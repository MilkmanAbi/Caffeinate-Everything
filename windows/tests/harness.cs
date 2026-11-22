// Test harness: launches a command the way different environments would, then delivers real
// console signals to it (the same ones a terminal sends on Ctrl+C / Ctrl+Break / closing the window).
// Compiled with the in-box .NET Framework csc.exe (C# 5), see run-tests.ps1.
//
//   harness <mode> <action> <delayMs> <waitMs> <command line...>
//     mode:   console   new hidden console (like a terminal window / double-click)
//             group     new console + CREATE_NEW_PROCESS_GROUP (Ctrl+C disabled, as some launchers do)
//             detached  no console at all (GUI launchers, scheduled tasks)
//     action: none | ctrlc | ctrlc2 (two presses) | break | close (click the window's X)
//   harness ignore <ms>    sleep while ignoring Ctrl+C (stands in for a utility that won't die)
//
// Prints "EXITED code=<n> ms=<n>" or "STILL RUNNING" (then kills the process).

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

static class Harness
{
    const uint CREATE_NEW_CONSOLE = 0x10, DETACHED_PROCESS = 0x8, CREATE_NEW_PROCESS_GROUP = 0x200;
    const uint CTRL_C_EVENT = 0, CTRL_BREAK_EVENT = 1, WM_CLOSE = 0x10;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int pid, tid; }

    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32")] static extern bool TerminateProcess(IntPtr h, uint code);
    [DllImport("kernel32")] static extern bool FreeConsole();
    [DllImport("kernel32", SetLastError = true)] static extern bool AttachConsole(int pid);
    [DllImport("kernel32")] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    delegate bool CtrlHandler(uint ctrlType);
    [DllImport("kernel32")] static extern bool SetConsoleCtrlHandler(CtrlHandler handler, bool add);
    static readonly CtrlHandler Swallow = delegate { return true; };
    [DllImport("kernel32", SetLastError = true)] static extern bool GenerateConsoleCtrlEvent(uint ev, uint group);
    [DllImport("kernel32")] static extern IntPtr GetConsoleWindow();
    [DllImport("user32")] static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);

    static int Main(string[] a)
    {
        if (a.Length == 2 && a[0] == "ignore")
        {
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            Thread.Sleep(int.Parse(a[1]));
            return 0;
        }

        string mode = a[0], action = a[1];
        int delay = int.Parse(a[2]), wait = int.Parse(a[3]);
        string cmd = string.Join(" ", a, 4, a.Length - 4);

        uint flags = 0;
        if (mode == "console") flags = CREATE_NEW_CONSOLE;
        else if (mode == "group") flags = CREATE_NEW_CONSOLE | CREATE_NEW_PROCESS_GROUP;
        else if (mode == "detached") flags = DETACHED_PROCESS;

        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf(si);
        si.dwFlags = 1;      // STARTF_USESHOWWINDOW
        si.wShowWindow = 0;  // SW_HIDE: keep test consoles off the screen
        PROCESS_INFORMATION pi;
        Console.Out.Flush(); // bind stdout before we detach from our own console below
        if (!CreateProcess(null, new StringBuilder(cmd), IntPtr.Zero, IntPtr.Zero, false, flags,
                IntPtr.Zero, null, ref si, out pi))
        {
            Console.WriteLine("CreateProcess failed: " + Marshal.GetLastWin32Error());
            return 2;
        }
        Console.WriteLine("pid=" + pi.pid);

        Thread.Sleep(delay);
        if (WaitForSingleObject(pi.hProcess, 0) == 0)
        {
            uint early; GetExitCodeProcess(pi.hProcess, out early);
            Console.WriteLine("EXITED-EARLY code=" + (int)early);
            return 0;
        }

        var sw = Stopwatch.StartNew();
        if (action != "none")
        {
            FreeConsole();
            if (!AttachConsole(pi.pid))
            {
                Console.WriteLine("AttachConsole failed: " + Marshal.GetLastWin32Error());
            }
            else
            {
                // Don't let the signal hit the harness itself. NULL only covers Ctrl+C, so also
                // install a handler that swallows Ctrl+Break.
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                SetConsoleCtrlHandler(Swallow, true);
                if (action == "ctrlc") GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                else if (action == "ctrlc2")
                {
                    GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                    Thread.Sleep(700);
                    GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0);
                }
                // Ctrl+Break can target a process group (Ctrl+C cannot), so aim it at the child's
                // group: broadcasting it would also kill this harness. Use with mode "group".
                else if (action == "break") GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, (uint)pi.pid);
                else if (action == "close") PostMessage(GetConsoleWindow(), WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                FreeConsole();
            }
        }

        if (WaitForSingleObject(pi.hProcess, (uint)wait) == 0)
        {
            uint code; GetExitCodeProcess(pi.hProcess, out code);
            Console.WriteLine("EXITED code=" + (int)code + " ms=" + sw.ElapsedMilliseconds);
            return 0;
        }
        Console.WriteLine("STILL RUNNING after " + wait + "ms (killed)");
        TerminateProcess(pi.hProcess, 99);
        return 1;
    }
}
