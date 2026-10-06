using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Checkers.Core;

namespace Checkers.ChinookWorker;

/// <summary>Endgame database driver egdb64.dll (Cake / Chinook / Kingsrow WLD databases), see egdb_library\egdb.h.</summary>
public sealed unsafe class Egdb
{
    // EGDB_DRIVER: the first member is int lookup(EGDB_DRIVER*, EGDB_BITBOARD*, int color, int conditional)
    private readonly void** _driver;

    public int MaxPieces { get; }

    public Egdb(string dllPath, string directory, int cacheMb)
    {
        var lib = NativeLibrary.Load(dllPath);
        var identify = (delegate* unmanaged[Cdecl]<byte*, int*, int*, int>)NativeLibrary.GetExport(lib, "egdb_identify");
        var open = (delegate* unmanaged[Cdecl]<int, int, int, byte*, delegate* unmanaged[Cdecl]<byte*, void>, void**>)NativeLibrary.GetExport(lib, "egdb_open");
        int type, maxPieces;
        fixed (byte* dir = Encoding.ASCII.GetBytes(directory + "\0"))
        {
            if (identify(dir, &type, &maxPieces) != 0) throw new InvalidOperationException($"No endgame database in {directory}");
            _driver = open(0 /* EGDB_NORMAL bitboards */, maxPieces, cacheMb, dir, &IgnoreMessage);
        }
        if (_driver == null) throw new InvalidOperationException($"Cannot open the endgame database in {directory}");
        MaxPieces = maxPieces;
    }

    /// <summary>
    /// "win" / "draw" / "loss" for the side to move, null if unknown. The databases have no valid data for positions
    /// where either side can capture, so those are not probed.
    /// </summary>
    public string? Lookup(Position p)
    {
        if (p.PieceCount > MaxPieces || p.HasCapture(Side.Black) || p.HasCapture(Side.White)) return null;
        var bitboard = stackalloc uint[] { p.Black, p.White, p.Kings };
        var lookup = (delegate* unmanaged[Cdecl]<void**, uint*, int, int, int>)_driver[0];
        return lookup(_driver, bitboard, p.SideToMove == Side.Black ? 0 : 1, 0) switch { 1 => "win", 2 => "loss", 3 => "draw", _ => null };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void IgnoreMessage(byte* message) { }
}
