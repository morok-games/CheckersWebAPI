using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Checkers.Core;
using Microsoft.Extensions.Options;

namespace Checkers.Api;

/// <summary>"Engine" section of appsettings.json.</summary>
public sealed class EngineOptions
{
    public string Type { get; set; } = "chinook";
    public string Path { get; set; } = "";      // Kingsrow64.dll
    public string Databases { get; set; } = ""; // endgame database folder
    public int Workers { get; set; } = 2;
    public int HashMb { get; set; } = 128;
    public int DatabaseCacheMb { get; set; } = 256;
}

public sealed record SearchLimits(int MaxDepth, int SoftTimeMs, bool UseBook);

/// <summary>Engine adapter from the specification: setPosition(pdn), search(limits).</summary>
public sealed class EngineAdapter(EnginePool pool)
{
    public Position Position { get; private set; } = Position.Initial;

    public void SetPosition(string? pdn) => Position = Position.Parse(pdn);

    /// <summary>softTimeMs is enforced by the engine; cancelling <paramref name="ct"/> (hardTimeMs) stops it.</summary>
    public Task<SearchResult> SearchAsync(SearchLimits limits, CancellationToken ct) =>
        pool.SearchAsync(new("search", Position.ToFen(), limits.SoftTimeMs, limits.MaxDepth, limits.UseBook), ct);
}

/// <summary>
/// Long-lived engine worker processes (Checkers.ChinookWorker), started and warmed up with the application.
/// Round robin routing with an async lock per worker; a worker that exits is restarted.
/// </summary>
public sealed class EnginePool(IOptions<EngineOptions> options, IServiceScopeFactory scopes, ILogger<EnginePool> log) : IHostedService
{
    private Worker?[] _workers = [];
    private int _next = -1;
    private bool _stopping;

    public int ReadyWorkers => _workers.Count(w => w?.Alive == true);

    public async Task StartAsync(CancellationToken ct)
    {
        _workers = new Worker?[options.Value.Workers];
        await Task.WhenAll(Enumerable.Range(0, _workers.Length).Select(StartWorkerAsync));
        await WarmUpAsync();
    }

    public Task StopAsync(CancellationToken ct)
    {
        _stopping = true;
        Parallel.ForEach(_workers, w => w?.Dispose());
        return Task.CompletedTask;
    }

    public async Task<SearchResult> SearchAsync(WorkerRequest request, CancellationToken ct)
    {
        for (int attempt = 0; attempt < _workers.Length; attempt++)
        {
            var worker = _workers[(int)((uint)Interlocked.Increment(ref _next) % _workers.Length)];
            if (worker?.Alive != true) continue;
            await worker.Lock.WaitAsync(ct);
            try
            {
                var response = await worker.SearchAsync(request, ct);
                return response.Result ?? throw new ApiException(500, response.Error ?? "Engine error.");
            }
            finally { worker.Lock.Release(); }
        }
        throw new ApiException(503, "No engine worker is running.");
    }

    private async Task StartWorkerAsync(int index)
    {
        try
        {
            var worker = await Worker.StartAsync(options.Value, index, log);
            worker.OnExit(() =>
            {
                if (_stopping) return;
                log.LogWarning("Engine worker {Index} exited, restarting", index);
                _ = StartWorkerAsync(index);
            });
            _workers[index] = worker;
            log.LogInformation("Engine worker {Index} ready", index);
        }
        catch (Exception e)
        {
            log.LogError("Engine worker {Index} failed to start: {Error} Retrying in 10 s.", index, e.Message);
            if (!_stopping) _ = Task.Delay(10_000).ContinueWith(_ => StartWorkerAsync(index));
        }
    }

    /// <summary>
    /// A few real requests through the whole stack, so the first client request does not pay for JIT compilation
    /// (needed for "tablebase position in under 50 ms"). The application accepts requests only after this.
    /// </summary>
    private async Task WarmUpAsync()
    {
        const string midgame = "B:W17,21,22,23,25,26,28,30,31:B1,3,5,6,9,10,11,12,15";
        for (int round = 0; round < _workers.Length && ReadyWorkers > 0; round++)
            foreach (var (fen, level) in new[] { ("B:WK2:BK18,K23", "strong"), ("W:W13,14:BK1,K4", "medium"), (midgame, "strong") })
            {
                using var scope = scopes.CreateScope();
                var request = new SuggestMoveRequest("checkers-8x8", new("PDN", fen), level, new(null, 50 + round, null));
                try { await scope.ServiceProvider.GetRequiredService<MoveService>().SuggestAsync(request, "warmup", default); }
                catch (Exception e) { log.LogWarning("Warm-up request failed: {Error}", e.Message); }
            }
    }

    private sealed class Worker(Process process, NamedPipeServerStream pipe) : IDisposable
    {
        private readonly StreamReader _reader = new(pipe);
        private readonly StreamWriter _writer = new(pipe) { AutoFlush = true };

        public SemaphoreSlim Lock { get; } = new(1, 1);
        public bool Alive => !process.HasExited;

        public void OnExit(Action action) => process.Exited += (_, _) => action();

        public static async Task<Worker> StartAsync(EngineOptions o, int index, ILogger log)
        {
            if (!File.Exists(o.Path)) throw new FileNotFoundException($"Engine DLL not found: {o.Path} (Engine:Path).");
            var pipeName = $"checkers-{Environment.ProcessId}-{index}-{Guid.NewGuid():N}";
            var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var psi = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "ChinookWorker", "Checkers.ChinookWorker.exe"))
            {
                RedirectStandardOutput = true, // engine DLL chatter, discarded
                RedirectStandardError = true,  // worker log
                CreateNoWindow = true,
            };
            foreach (var a in new[] { "--engine", o.Path, "--db", o.Databases, "--hash", $"{o.HashMb}", "--dbcache", $"{o.DatabaseCacheMb}",
                         "--enginelog", Path.Combine(AppContext.BaseDirectory, "logs", "engine"), "--pipe", pipeName })
                psi.ArgumentList.Add(a);

            var process = Process.Start(psi)!;
            process.EnableRaisingEvents = true;
            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrEmpty(e.Data)) log.LogInformation("worker {Index}: {Line}", index, e.Data); };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            try
            {
                var connect = pipe.WaitForConnectionAsync(timeout.Token);
                if (await Task.WhenAny(connect, process.WaitForExitAsync(timeout.Token)) != connect) throw new IOException("exited");
                var worker = new Worker(process, pipe);
                if (await worker._reader.ReadLineAsync(timeout.Token) == null) throw new IOException("closed"); // "{}" = ready
                return worker;
            }
            catch
            {
                pipe.Dispose();
                if (!process.WaitForExit(3000)) process.Kill();
                process.WaitForExit();
                throw new InvalidOperationException($"the worker exited during start-up, exit code 0x{process.ExitCode:X8} " +
                                                    "(for a crash inside the engine DLL see the Windows Application event log).");
            }
        }

        /// <summary>On cancellation sends "stop" and waits for the answer, so the worker is free again; kills it if it hangs.</summary>
        public async Task<WorkerResponse> SearchAsync(WorkerRequest request, CancellationToken ct)
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request, Json.Options));
            var read = _reader.ReadLineAsync();
            try
            {
                var line = await read.WaitAsync(ct) ?? throw new ApiException(503, "The engine worker stopped.");
                return JsonSerializer.Deserialize<WorkerResponse>(line, Json.Options)!;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                await _writer.WriteLineAsync(JsonSerializer.Serialize(new WorkerRequest("stop"), Json.Options));
                try { await read.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (TimeoutException) { process.Kill(); }
                throw;
            }
        }

        public void Dispose()
        {
            pipe.Dispose(); // the worker exits when the pipe closes
            if (!process.WaitForExit(3000)) process.Kill();
        }
    }
}
