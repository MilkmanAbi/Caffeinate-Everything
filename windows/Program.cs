// caffeinate for Windows: prevent the system from sleeping on behalf of a utility.
// Mirrors the macOS CLI:  caffeinate [-dimsu] [-t timeout] [-w pid] [utility arguments...]

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Caffeinate;

internal static class Program
{
    private const string Usage =
        "usage: caffeinate [-dimsu] [-t timeout] [-w pid] [utility arguments...]";

    private const string Help = Usage + @"

  -d          Prevent the display from sleeping.
  -i          Prevent the system from idle sleeping (default if no assertion flag is given).
  -m          Prevent the disk from idle sleeping (Windows: same as -i, see README).
  -s          Prevent the system from sleeping. Only held while on AC power.
  -u          Declare that the user is active: wakes the display and keeps it on
              for the -t timeout, or 5 seconds if no timeout is given.
  -t timeout  Hold the assertions for <timeout> seconds. Ignored when a utility is given.
  -w pid      Hold the assertions until process <pid> exits.

With a utility, the assertions are held until it exits, and caffeinate exits with its code.
With nothing else, caffeinate runs until interrupted (Ctrl+C).";

    private static readonly ManualResetEvent Quit = new(false);
    private static volatile bool _runningUtility;
    private static int _interrupts;

    private static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"caffeinate: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string[] args)
    {
        // ---- getopt("dimsut:w:") with BSD semantics: stop at first non-option, honour "--" ----
        bool d = false, i = false, m = false, s = false, u = false;
        long? timeout = null;
        int? waitPid = null;

        int argi = 0;
        while (argi < args.Length)
        {
            string a = args[argi];
            if (a == "--") { argi++; break; }
            if (a == "-h" || a == "--help") { Console.WriteLine(Help); return 0; }
            if (a.Length < 2 || a[0] != '-') break;

            for (int j = 1; j < a.Length; j++)
            {
                char c = a[j];
                switch (c)
                {
                    case 'd': d = true; break;
                    case 'i': i = true; break;
                    case 'm': m = true; break;
                    case 's': s = true; break;
                    case 'u': u = true; break;
                    case 't':
                    case 'w':
                    {
                        string val;
                        if (j + 1 < a.Length) val = a[(j + 1)..];
                        else if (argi + 1 < args.Length) val = args[++argi];
                        else return UsageError($"option requires an argument -- {c}");

                        if (c == 't')
                        {
                            if (!long.TryParse(val, out long t) || t < 0)
                                return UsageError($"invalid timeout: {val}");
                            timeout = t;
                        }
                        else
                        {
                            if (!int.TryParse(val, out int p) || p <= 0)
                                return UsageError($"invalid pid: {val}");
                            waitPid = p;
                        }
                        j = a.Length; // rest of this arg was consumed as the value
                        break;
                    }
                    default:
                        return UsageError($"illegal option -- {c}");
                }
            }
            argi++;
        }
        string[] utility = args[argi..];

        // Like macOS: no assertion flags => prevent idle sleep.
        if (!d && !i && !m && !s && !u) i = true;

        // ---- Validate -w before taking any assertions ----
        SafeWaitHandle? pidHandle = null;
        if (waitPid is int pid && utility.Length == 0)
        {
            pidHandle = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)pid);
            if (pidHandle.IsInvalid)
            {
                // Capture before touching Console: its lazy init makes P/Invokes that clobber the last error.
                string why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                Console.Error.WriteLine($"caffeinate: cannot wait for pid {pid}: {why}");
                return 1;
            }
        }

        // ---- Take the assertions ----
        using var main = new PowerRequest("caffeinate command-line tool");
        if (d) main.Set(Native.PowerRequestType.DisplayRequired);
        if (i || m) main.Set(Native.PowerRequestType.SystemRequired);

        Timer? acTimer = null;
        if (s && !(i || m))
        {
            // -s is only honoured on AC power. Re-check periodically and follow the power source.
            bool held = false;
            void Sync()
            {
                bool onAc = Native.IsOnAcPower();
                if (onAc && !held) { main.Set(Native.PowerRequestType.SystemRequired); held = true; }
                else if (!onAc && held) { main.Clear(Native.PowerRequestType.SystemRequired); held = false; }
            }
            Sync();
            acTimer = new Timer(_ => { try { Sync(); } catch { /* keep running */ } }, null, 5000, 5000);
        }

        PowerRequest? userActive = null;
        Timer? userActiveTimer = null;
        if (u)
        {
            DeclareUserActivity();
            userActive = new PowerRequest("caffeinate command-line tool (user active)");
            userActive.Set(Native.PowerRequestType.DisplayRequired);
            long secs = timeout is > 0 ? timeout.Value : 5;
            var ua = userActive;
            userActiveTimer = new Timer(_ => { try { ua.Clear(Native.PowerRequestType.DisplayRequired); } catch { } },
                null, TimeSpan.FromSeconds(secs), Timeout.InfiniteTimeSpan);
        }

        Console.CancelKeyPress += (_, e) =>
        {
            // With a utility, the child receives Ctrl+C too, so keep holding while it shuts down.
            // A second Ctrl+C (or Ctrl+Break) stops caffeinate anyway, for utilities that ignore it.
            e.Cancel = true;
            if (!_runningUtility
                || e.SpecialKey == ConsoleSpecialKey.ControlBreak
                || Interlocked.Increment(ref _interrupts) >= 2)
            {
                Quit.Set();
            }
        };

        try
        {
            if (utility.Length > 0)
                return RunUtility(utility);

            var handles = new List<WaitHandle> { Quit };
            if (pidHandle is not null)
                handles.Add(new ManualResetEvent(false) { SafeWaitHandle = pidHandle });

            int waitMs = Timeout.Infinite;
            if (timeout is long t)
                waitMs = (int)Math.Min(t * 1000, int.MaxValue - 1);

            int which = WaitHandle.WaitAny(handles.ToArray(), waitMs);
            return which == 0 ? 130 : 0; // 130 = interrupted, like a shell after SIGINT
        }
        finally
        {
            acTimer?.Dispose();
            userActiveTimer?.Dispose();
            userActive?.Dispose();
        }
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine($"caffeinate: {message}");
        Console.Error.WriteLine(Usage);
        return 1;
    }

    // ---------------------------------------------------------------- -u

    private static void DeclareUserActivity()
    {
        // Reset the system and display idle timers.
        Native.SetThreadExecutionState(Native.ES_SYSTEM_REQUIRED | Native.ES_DISPLAY_REQUIRED);

        // A zero-distance relative mouse move counts as user input: it wakes a display that is
        // already off and updates the last-input time, without moving the pointer.
        var input = new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            mi = new Native.MOUSEINPUT { dwFlags = Native.MOUSEEVENTF_MOVE },
        };
        uint sent = Native.SendInput(1, [input], Marshal.SizeOf<Native.INPUT>());
        if (sent != 1)
        {
            string why = new Win32Exception(Marshal.GetLastWin32Error()).Message;
            Console.Error.WriteLine($"caffeinate: warning: could not declare user activity: {why}");
        }
    }

    // ---------------------------------------------------------------- utility

    private static int RunUtility(string[] utility)
    {
        string name = utility[0];
        string? path = ResolveCommand(name);
        if (path is null)
        {
            Console.Error.WriteLine($"caffeinate: {name}: command not found");
            return 127;
        }

        var psi = new System.Diagnostics.ProcessStartInfo { UseShellExecute = false };
        string ext = Path.GetExtension(path);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            // Batch files must go through cmd.exe. /s strips the outer quotes, keeping the inner ones intact.
            var sb = new StringBuilder();
            sb.Append(QuoteForCmd(path));
            foreach (var arg in utility.AsSpan(1)) sb.Append(' ').Append(QuoteForCmd(arg));
            psi.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            psi.Arguments = $"/d /s /c \"{sb}\"";
        }
        else
        {
            psi.FileName = path;
            foreach (var arg in utility.AsSpan(1)) psi.ArgumentList.Add(arg);
        }

        _runningUtility = true;
        using var proc = System.Diagnostics.Process.Start(psi)
                         ?? throw new InvalidOperationException($"failed to start {name}");
        using var exited = new ManualResetEvent(false)
        {
            SafeWaitHandle = new SafeWaitHandle(proc.Handle, ownsHandle: false),
        };
        if (WaitHandle.WaitAny([exited, Quit]) == 1)
        {
            // Forced stop: leave the utility alone (it may be the user's app) but stop holding.
            Console.Error.WriteLine(
                $"caffeinate: interrupted; {name} (pid {proc.Id}) is still running but no longer kept awake");
            return 130;
        }
        proc.WaitForExit();
        return proc.ExitCode;
    }

    /// Resolve like a shell does: explicit paths as-is, bare names via PATH + PATHEXT.
    /// A bare name without a PATHEXT extension never matches an extensionless file
    /// (e.g. the POSIX `npm` shim that sits next to npm.cmd).
    private static string? ResolveCommand(string name)
    {
        string[] exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string nameExt = Path.GetExtension(name);
        bool hasExecExt = nameExt.Length > 0 && exts.Any(e => e.Equals(nameExt, StringComparison.OrdinalIgnoreCase));

        IEnumerable<string> candidatesFor(string basePath) =>
            hasExecExt ? [basePath] : exts.Select(e => basePath + e.ToLowerInvariant());

        bool hasDir = name.IndexOfAny(['\\', '/', ':']) >= 0;
        if (hasDir)
            return candidatesFor(Path.GetFullPath(name)).FirstOrDefault(File.Exists);

        string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string dir in dirs)
        {
            string basePath;
            try { basePath = Path.Combine(dir.Trim('"'), name); } catch { continue; }
            string? hit = candidatesFor(basePath).FirstOrDefault(File.Exists);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static string QuoteForCmd(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '&', '|', '<', '>', '^', '(', ')', ',', ';', '=']) < 0)
            return arg;
        return "\"" + arg.Replace("\"", "\"\"") + "\"";
    }
}

/// A Win32 power request (the Windows equivalent of an IOPMAssertion).
/// The kernel drops it automatically if the process dies.
internal sealed class PowerRequest : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly IntPtr _reason;

    public PowerRequest(string reason)
    {
        _reason = Marshal.StringToHGlobalUni(reason);
        var ctx = new Native.REASON_CONTEXT
        {
            Version = Native.POWER_REQUEST_CONTEXT_VERSION,
            Flags = Native.POWER_REQUEST_CONTEXT_SIMPLE_STRING,
            SimpleReasonString = _reason,
        };
        _handle = Native.PowerCreateRequest(ref ctx);
        if (_handle.IsInvalid)
        {
            Marshal.FreeHGlobal(_reason);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "PowerCreateRequest failed");
        }
    }

    public void Set(Native.PowerRequestType type)
    {
        if (!Native.PowerSetRequest(_handle, type))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"PowerSetRequest({type}) failed");
    }

    public void Clear(Native.PowerRequestType type)
    {
        if (!Native.PowerClearRequest(_handle, type))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"PowerClearRequest({type}) failed");
    }

    public void Dispose()
    {
        _handle.Dispose();
        Marshal.FreeHGlobal(_reason);
    }
}

internal static class Native
{
    public const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    public const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;

    public enum PowerRequestType
    {
        DisplayRequired = 0,
        SystemRequired = 1,
        AwayModeRequired = 2,
        ExecutionRequired = 3,
    }

    // REASON_CONTEXT: the union's largest member (Detailed) is 24 bytes on 64-bit; pad to match.
    [StructLayout(LayoutKind.Sequential)]
    public struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        public IntPtr SimpleReasonString;
        private IntPtr _pad1;
        private IntPtr _pad2;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeFileHandle PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PowerSetRequest(SafeFileHandle handle, PowerRequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PowerClearRequest(SafeFileHandle handle, PowerRequestType type);

    public const uint ES_SYSTEM_REQUIRED = 0x00000001;
    public const uint ES_DISPLAY_REQUIRED = 0x00000002;

    [DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);

    [StructLayout(LayoutKind.Sequential)]
    public struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    /// ACLineStatus: 0 = battery, 1 = AC, 255 = unknown (desktops; treat as AC).
    public static bool IsOnAcPower() => !GetSystemPowerStatus(out var st) || st.ACLineStatus != 0;

    public const uint SYNCHRONIZE = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern SafeWaitHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    public const uint INPUT_MOUSE = 0;
    public const uint MOUSEEVENTF_MOVE = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    // MOUSEINPUT is the largest member of the INPUT union, so this layout is 40 bytes on 64-bit.
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public MOUSEINPUT mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
