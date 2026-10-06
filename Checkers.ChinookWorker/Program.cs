using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Checkers.Core;
using Checkers.ChinookWorker;

// Engine worker process, started by the Web API:
//   Checkers.ChinookWorker --engine <Kingsrow64.dll> --db <dir> --hash <MB> --dbcache <MB> --enginelog <dir> --pipe <name>
// Loads the engine and the endgame database once, then serves searches over the named pipe.
// The engine DLL prints to stdout itself, so the requests go over the pipe and our log goes to stderr.
var arg = args.Chunk(2).ToDictionary(a => a[0].TrimStart('-'), a => a[1]);
void Log(string message) => Console.Error.WriteLine($"[worker {Environment.ProcessId}] {message}");

// Asynchronous: a synchronous pipe handle cannot be written to while another thread is blocked reading it.
using var pipe = new NamedPipeClientStream(".", arg["pipe"], PipeDirection.InOut, PipeOptions.Asynchronous);
pipe.Connect(30_000);
var reader = new StreamReader(pipe);
var writer = new StreamWriter(pipe) { AutoFlush = true };

var sw = Stopwatch.StartNew();
var cbRoot = Path.GetDirectoryName(Path.GetDirectoryName(arg["engine"]))!;
Directory.SetCurrentDirectory(cbRoot); // Kingsrow finds its book and weights relative to the CheckerBoard folder
var engine = new CbEngine(arg["engine"], arg["enginelog"]);
engine.Command($"set hashsize {arg["hash"]}");
Egdb? egdb = null;
if (arg["db"] != "")
{
    // Without it Kingsrow reserves ~85% of the RAM for its database cache in every worker.
    engine.Command($"set dbmbytes {arg["dbcache"]}");
    engine.Command($"set dbpath {arg["db"]}");
    egdb = new Egdb(Path.Combine(cbRoot, "egdb64.dll"), arg["db"], int.Parse(arg["dbcache"]));
}
var searcher = new Searcher(engine, egdb);
// The first searches load the opening book, weights and database buffers.
searcher.Search(new("search", Position.Initial.ToFen(), 50), default);
if (egdb != null) searcher.Search(new("search", "W:WK27,30:BK14", 10), default);
Log($"{engine.Command("name")} ready in {sw.ElapsedMilliseconds} ms, database up to {egdb?.MaxPieces ?? 0} pieces");
writer.WriteLine("{}");

// Searches run on their own thread so that "stop" can be read while the engine is thinking.
var searches = new BlockingCollection<(WorkerRequest Request, CancellationToken Stop)>();
new Thread(() =>
{
    foreach (var (request, stop) in searches.GetConsumingEnumerable())
    {
        WorkerResponse response;
        try { response = new(searcher.Search(request, stop)); }
        catch (Exception e) { Log($"search failed: {e}"); response = new(null, e.Message); }
        writer.WriteLine(JsonSerializer.Serialize(response, Json.Options));
    }
}) { IsBackground = true }.Start();

CancellationTokenSource? current = null;
while (reader.ReadLine() is { } line) // ends when the Web API closes the pipe
{
    var request = JsonSerializer.Deserialize<WorkerRequest>(line, Json.Options)!;
    if (request.Cmd == "stop") current?.Cancel();
    else searches.Add((request, (current = new()).Token));
}
