using System.Runtime.InteropServices;
using System.Text;

namespace Checkers.ChinookWorker;

/// <summary>CheckerBoard API engine DLL (Kingsrow64.dll): getmove / enginecommand, see CheckerBoard cb_interface.h.</summary>
public sealed unsafe class CbEngine
{
    // int getmove(int board[8][8], int color, double maxtime, char str[1024], int *playnow, int info, int moreinfo, CBmove *move)
    private readonly delegate* unmanaged<int*, int, double, byte*, int*, int, int, int*, int> _getMove;
    // int enginecommand(const char *command, char reply[1024])
    private readonly delegate* unmanaged<byte*, byte*, int> _command;
    // Shared with the engine during getmove and touched from other threads, so they live at fixed addresses.
    private readonly int* _playNow = (int*)NativeMemory.AllocZeroed(sizeof(int));
    private readonly byte* _info = (byte*)NativeMemory.AllocZeroed(1024);

    /// <param name="logFolder">Returned to the engine as "My Documents", where Kingsrow writes its log.</param>
    public CbEngine(string dllPath, string logFolder)
    {
        // egdb64.dll (imported by the engine) lives in the CheckerBoard folder, one level above engines\.
        NativeLibrary.Load(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(dllPath))!, "egdb64.dll"));
        var lib = NativeLibrary.Load(dllPath);
        RedirectDocuments(lib, logFolder);
        _getMove = (delegate* unmanaged<int*, int, double, byte*, int*, int, int, int*, int>)NativeLibrary.GetExport(lib, "getmove");
        _command = (delegate* unmanaged<byte*, byte*, int>)NativeLibrary.GetExport(lib, "enginecommand");
    }

    public string Command(string command)
    {
        var reply = stackalloc byte[1024];
        reply[0] = 0;
        fixed (byte* c = Encoding.ASCII.GetBytes(command + "\0")) _command(c, reply);
        return Marshal.PtrToStringAnsi((IntPtr)reply)!;
    }

    /// <summary>Searches; <paramref name="board"/> (board[x][y] as x*8+y) is replaced by the position after the move.</summary>
    public void GetMove(int[] board, int color, double seconds)
    {
        var move = stackalloc int[67]; // struct CBmove, not used for English checkers
        _info[0] = 0;
        fixed (int* b = board) _getMove(b, color, seconds, _info, _playNow, 2 /* CB_EXACT_TIME */, 0, move);
    }

    /// <summary>The engine's status line; readable while a search is running.</summary>
    public string Info => Marshal.PtrToStringAnsi((IntPtr)_info)!;

    public void Stop() => Volatile.Write(ref *_playNow, 1);

    public void ResetStop() => Volatile.Write(ref *_playNow, 0);

    // ---- Kingsrow writes its log to "<My Documents>\Ed Gilbert\Kingsrow\" and crashes (C runtime fail-fast) when it
    // cannot. IIS app pool identities have no Documents folder, so the engine's import of SHGetFolderPathA is redirected.

    private static byte[] s_documents = [];
    private static delegate* unmanaged<IntPtr, int, IntPtr, uint, byte*, int> s_shGetFolderPath;

    private static void RedirectDocuments(IntPtr module, string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "Ed Gilbert", "Kingsrow"));
        s_documents = Encoding.Default.GetBytes(Path.GetFullPath(folder) + "\0");

        // Find the import address table slot of SHELL32!SHGetFolderPathA (PE32+ import directory).
        var b = (byte*)module;
        var optionalHeader = b + *(int*)(b + 0x3C) + 24;
        for (var desc = (uint*)(b + *(uint*)(optionalHeader + 120)); desc[3] != 0; desc += 5) // IMAGE_IMPORT_DESCRIPTOR
        {
            if (!string.Equals(Marshal.PtrToStringAnsi((IntPtr)(b + desc[3])), "SHELL32.dll", StringComparison.OrdinalIgnoreCase)) continue;
            var names = (ulong*)(b + (desc[0] != 0 ? desc[0] : desc[4]));
            var slots = (IntPtr*)(b + desc[4]);
            for (int i = 0; names[i] != 0; i++)
            {
                if ((names[i] >> 63) != 0 || Marshal.PtrToStringAnsi((IntPtr)(b + (uint)names[i] + 2)) != "SHGetFolderPathA") continue;
                VirtualProtect(&slots[i], (nuint)sizeof(IntPtr), 0x04 /* PAGE_READWRITE */, out var old);
                s_shGetFolderPath = (delegate* unmanaged<IntPtr, int, IntPtr, uint, byte*, int>)slots[i];
                slots[i] = (IntPtr)(delegate* unmanaged<IntPtr, int, IntPtr, uint, byte*, int>)&GetFolderPath;
                VirtualProtect(&slots[i], (nuint)sizeof(IntPtr), old, out _);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static int GetFolderPath(IntPtr hwnd, int csidl, IntPtr token, uint flags, byte* path)
    {
        if ((csidl & 0xFF) != 0x05 /* CSIDL_PERSONAL */) return s_shGetFolderPath(hwnd, csidl, token, flags, path);
        s_documents.CopyTo(new Span<byte>(path, 260));
        return 0;
    }

    [DllImport("kernel32")]
    private static extern bool VirtualProtect(void* address, nuint size, uint protect, out uint oldProtect);
}
