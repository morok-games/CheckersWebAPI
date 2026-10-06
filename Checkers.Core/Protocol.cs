using System.Text.Json;

namespace Checkers.Core;

// Web API <-> Checkers.ChinookWorker process: one JSON object per line over a named pipe.
// The worker sends "{}" when it is ready, then one WorkerResponse per search; "stop" has no response.

public sealed record WorkerRequest(string Cmd, string? Fen = null, int TimeMs = 0, int MaxDepth = 0, bool UseBook = false);

public sealed record WorkerResponse(SearchResult? Result, string? Error = null);

/// <param name="Score">Engine evaluation for the side to move (100 = one man), if known.</param>
/// <param name="Wdl">"win", "draw" or "loss" for the side to move when the position is in the endgame database.</param>
public sealed record SearchResult(string BestMove, string[] Pv, int? Score, string? Wdl, int Depth, long Nodes, bool TablebaseHit);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
