using System.Diagnostics;
using Checkers.Core;

namespace Checkers.Api;

public sealed record SuggestMoveRequest(string? GameId, GameState? State, string? Level, Limits? Limits);
public sealed record GameState(string? Notation, string? Position);
public sealed record Limits(int? MaxDepth, int? SoftTimeMs, int? HardTimeMs);

/// <param name="ScoreOrWDL">Engine score for the side to move (100 = one man); with a tablebase hit 1 = win, 0 = draw, -1 = loss.</param>
public sealed record SuggestMoveResponse(string Engine, string BestMove, string[] Pv, int? ScoreOrWDL, int Depth, long Nodes,
    string PositionKey, SuggestInfo Info);
public sealed record SuggestInfo(bool TablebaseHit, long TimeMs, bool Cached);

public sealed record MoveRequest(string? Position, string? Move);

/// <summary>An error answered with the given HTTP status (422, 500, 503, 504).</summary>
public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// POST /v1/move/suggest, as in the specification:
/// 1. parse and normalize the PDN; 2-3. database probe / search with the limits of the level (in the worker);
/// 4. verify the move is legal, otherwise try the next PV move or fail with 500; 5. LRU cache by canonical position.
/// </summary>
public sealed class MoveService(EngineAdapter engine, LruCache<string, SuggestMoveResponse> cache, IConfiguration config, ILogger<MoveService> log)
{
    // Upper bounds per level (request limits can only lower them): max depth, soft time, opening book.
    // strong uses 450 ms so that the whole answer stays under 600 ms.
    private static readonly Dictionary<string, (int Depth, int TimeMs, bool Book)> Levels = new()
    {
        ["weak"] = (8, 100, false), // no randomness
        ["medium"] = (12, 250, true),
        ["strong"] = (18, 450, true),
    };

    public async Task<SuggestMoveResponse> SuggestAsync(SuggestMoveRequest r, string requestId, CancellationToken aborted)
    {
        var sw = Stopwatch.StartNew();
        if (r.GameId != "checkers-8x8" || r.State?.Notation != "PDN")
            throw new ApiException(422, "Expected gameId 'checkers-8x8' and state.notation 'PDN'.");
        if (r.Limits is { MaxDepth: < 0 or > 64 } or { SoftTimeMs: < 1 or > 60000 } or { HardTimeMs: < 1 or > 60000 })
            throw new ApiException(422, "Limits out of range.");
        (int Depth, int TimeMs, bool Book)? level = r.Level == null ? null
            : Levels.TryGetValue(r.Level, out var l) ? l : throw new ApiException(422, $"Unknown level '{r.Level}'.");

        int depth = level is { } lv ? Math.Min(r.Limits?.MaxDepth is int d and > 0 ? d : lv.Depth, lv.Depth) : r.Limits?.MaxDepth ?? 0;
        int softTime = level is { } lt ? Math.Min(r.Limits?.SoftTimeMs ?? lt.TimeMs, lt.TimeMs)
            : r.Limits?.SoftTimeMs ?? config.GetValue("Limits:DefaultSoftTimeMs", 300);
        int hardTime = r.Limits?.HardTimeMs ?? config.GetValue("Limits:DefaultHardTimeMs", 1200);

        engine.SetPosition(r.State.Position); // PdnException -> 422
        var position = engine.Position;
        if (position.Moves().Count == 0) throw new ApiException(422, $"{position.SideToMove} has no legal moves.");

        var positionKey = "pdn:" + position.ToFen();
        var cacheKey = $"{positionKey}|{r.Level}|{depth}|{softTime}";
        if (cache.TryGet(cacheKey, out var cached))
            return Logged(cached with { Info = cached.Info with { TimeMs = sw.ElapsedMilliseconds, Cached = true } }, requestId);

        SearchResult result;
        using (var hardTimeout = CancellationTokenSource.CreateLinkedTokenSource(aborted))
        {
            hardTimeout.CancelAfter(hardTime);
            try { result = await engine.SearchAsync(new(depth, softTime, level?.Book ?? false), hardTimeout.Token); }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
            {
                throw new ApiException(504, $"The engine did not answer within hardTimeMs = {hardTime} ms.");
            }
        }

        var best = result.Pv.Prepend(result.BestMove).Select(m => TryFind(position, m)).FirstOrDefault(m => m != null)?.ToString()
                   ?? throw new ApiException(500, $"Engine returned an illegal move '{result.BestMove}'.");
        var response = new SuggestMoveResponse(config["Engine:Type"] ?? "chinook", best,
            result.Pv.FirstOrDefault() == best ? result.Pv : [best],
            result.TablebaseHit ? result.Wdl switch { "win" => 1, "loss" => -1, _ => 0 } : result.Score,
            result.Depth, result.Nodes, positionKey, new(result.TablebaseHit, sw.ElapsedMilliseconds, false));
        cache.Set(cacheKey, response);
        return Logged(response, requestId);
    }

    private SuggestMoveResponse Logged(SuggestMoveResponse r, string requestId)
    {
        log.LogInformation("move.suggest {RequestId} {TimeMs} {Depth} {Nodes} {TablebaseHit}",
            requestId, r.Info.TimeMs, r.Depth, r.Nodes, r.Info.TablebaseHit);
        return r;
    }

    private static Move? TryFind(Position position, string move)
    {
        try { return position.Find(move); }
        catch (PdnException) { return null; }
    }
}

/// <summary>Thread-safe LRU cache with a capacity and a time-to-live per entry.</summary>
public sealed class LruCache<TKey, TValue>(int capacity, TimeSpan ttl) where TKey : notnull
{
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, DateTime Expires)>> _map = [];
    private readonly LinkedList<(TKey Key, TValue Value, DateTime Expires)> _order = new(); // most recent first

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_map)
        {
            if (_map.TryGetValue(key, out var node) && node.Value.Expires > DateTime.UtcNow)
            {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        lock (_map)
        {
            if (_map.Remove(key, out var old)) _order.Remove(old);
            else if (_map.Count >= capacity && _order.Last is { } last) { _order.RemoveLast(); _map.Remove(last.Value.Key); }
            _map[key] = _order.AddFirst((key, value, DateTime.UtcNow + ttl));
        }
    }
}
