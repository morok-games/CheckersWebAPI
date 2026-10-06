using System.Diagnostics;
using System.Text.RegularExpressions;
using Checkers.Core;

namespace Checkers.ChinookWorker;

/// <summary>One search: endgame database probe, engine search with time / depth / stop limits, move verification.</summary>
public sealed class Searcher(CbEngine engine, Egdb? egdb)
{
    private bool? _book;

    public SearchResult Search(WorkerRequest request, CancellationToken stop)
    {
        var position = Position.Parse(request.Fen);
        var moves = position.Moves();
        if (moves.Count == 0) throw new InvalidOperationException("No legal moves.");

        var wdl = egdb?.Lookup(position);
        if (moves.Count == 1) return new(moves[0].ToString(), [moves[0].ToString()], null, wdl, 0, 0, wdl != null);

        if (_book != request.UseBook) engine.Command($"set book {((_book = request.UseBook).Value ? 1 : 0)}");
        engine.Command($"set gamehist {position.ToFen()}");

        // The CheckerBoard API has only a time limit. A monitor thread stops the engine when asked to, or when the
        // depth limit is reached, keeping the last completed iteration (Kingsrow iterates 1, 3, 5, ... in microseconds,
        // hence busy waiting when a depth limit is set). The stop flag is re-asserted because getmove clears it.
        var board = ToBoard(position);
        var sw = Stopwatch.StartNew();
        string? lastComplete = null;
        bool atDepth = false;
        using var done = new ManualResetEventSlim();
        var monitor = new Thread(() =>
        {
            for (string last = ""; !done.IsSet;)
            {
                if (stop.IsCancellationRequested || atDepth) engine.Stop();
                else if (engine.Info is var text && text != last)
                {
                    last = text;
                    int depth = int.Parse(Match(text, @"depth (\d+)") ?? "0");
                    bool complete = text.Contains("value=") && text.Contains("pv ");
                    if (complete) lastComplete = text;
                    atDepth = request.MaxDepth > 0 && (depth > request.MaxDepth || complete && depth >= request.MaxDepth);
                }
                if (request.MaxDepth > 0 && !atDepth) Thread.SpinWait(20); else done.Wait(1);
            }
        });
        engine.ResetStop();
        monitor.Start();
        // A database position is already decided, a very short search is enough to pick the winning line.
        engine.GetMove(board, position.SideToMove == Side.Black ? 2 : 1, Math.Max(wdl != null ? Math.Min(request.TimeMs, 10) : request.TimeMs, 1) / 1000.0);
        done.Set();
        monitor.Join();

        var after = FromBoard(board, position.SideToMove == Side.Black ? Side.White : Side.Black);
        var best = moves.FirstOrDefault(m => position.Apply(m) == after)
                   ?? throw new InvalidOperationException($"Engine returned an illegal position {after.ToFen()}.");

        // Status line: "value=134,  depth 15/15.7/27,  0.0s,  7322 kN/s,  pv 14x23 27x18 16x23", value is black's view.
        var info = atDepth && lastComplete != null ? lastComplete : engine.Info;
        int? value = Match(info, @"value[=<>](-?\d+)") is { } v ? int.Parse(v) : null;
        var pv = new List<string> { best.ToString() };
        var tokens = Match(info, @"pv ([\dx\- ]+)")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (tokens.Length > 0 && TryFind(position, tokens[0])?.ToString() == best.ToString())
        {
            var p = position.Apply(best);
            foreach (var token in tokens.Skip(1))
            {
                if (TryFind(p, token) is not { } m) break;
                pv.Add(m.ToString());
                p = p.Apply(m);
            }
        }
        return new(best.ToString(), [.. pv], position.SideToMove == Side.Black ? value : -value, wdl,
            int.Parse(Match(info, @"depth (\d+)") ?? "0"),
            (long)(double.Parse(Match(info, @"([\d.]+) kN/s") ?? "0", System.Globalization.CultureInfo.InvariantCulture) * sw.ElapsedMilliseconds),
            wdl != null);
    }

    // The status line may be cut off in the middle of a move.
    private static Move? TryFind(Position p, string move)
    {
        try { return p.Find(move); }
        catch (PdnException) { return null; }
    }

    private static string? Match(string text, string pattern) => Regex.Match(text, pattern) is { Success: true } m ? m.Groups[1].Value : null;

    // CheckerBoard board[x][y] as x*8+y: 1 = white, 2 = black, 4 = man, 8 = king.
    private static int[] ToBoard(Position p)
    {
        var board = new int[64];
        foreach (var s in Position.Squares(p.Black | p.White))
        {
            var (x, y) = Position.Coords(s);
            board[x * 8 + y] = ((p.Black & Position.Bit(s)) != 0 ? 2 : 1) | ((p.Kings & Position.Bit(s)) != 0 ? 8 : 4);
        }
        return board;
    }

    private static Position FromBoard(int[] board, Side sideToMove)
    {
        uint black = 0, white = 0, kings = 0;
        for (int s = 1; s <= 32; s++)
        {
            var (x, y) = Position.Coords(s);
            int piece = board[x * 8 + y];
            if ((piece & 2) != 0) black |= Position.Bit(s);
            if ((piece & 1) != 0) white |= Position.Bit(s);
            if ((piece & 8) != 0) kings |= Position.Bit(s);
        }
        return new(black, white, kings, sideToMove);
    }
}
