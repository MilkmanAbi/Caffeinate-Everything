// A hold session: take power requests (and any temporary power-plan changes), then wait for the
// utility / -w pid / timeout / Ctrl+C, and undo everything on every exit path.

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Caffeinate;

/// A Win32 power request (the Windows equivalent of an IOPMAssertion). The kernel drops it if the process dies.
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
            int err = Marshal.GetLastWin32Error();
            Marshal.FreeHGlobal(_reason);
            throw new Win32Exception(err, "PowerCreateRequest failed");
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

/// The set of things a session holds, so they can be released and re-applied together (battery floor).
internal sealed class Holds(Options o) : IDisposable
{
    private readonly object _gate = new();
    private readonly PowerRequest _request = new("caffeinate command-line tool");
    private bool _applied, _lid, _dcSleep;

    private bool Display => o.D;
    private bool System => o.KeepsSystemAwake;

    public List<string> Describe()
    {
        var list = new List<string>();
        if (Display) list.Add("display");
        if (System) list.Add("system");
        if (o.S && !System) list.Add("system (on AC)");
        if (o.IgnoreLid) list.Add("lid ignored");
        if (o.ScreenOff) list.Add("screen off");
        if (o.HoldUpdates) list.Add("updates paused");
        if (o.BatteryFloor is int f) list.Add($"battery floor {f}%");
        return list;
    }

    public void Apply()
    {
        lock (_gate)
        {
            if (_applied) return;
            _applied = true; // set first: if a step below throws, Release() still undoes the earlier steps
            if (Display) _request.Set(Native.PowerRequestType.DisplayRequired);
            if (System) _request.Set(Native.PowerRequestType.SystemRequired);

            // Modern Standby on battery terminates system requests 5 minutes after the sleep timeout
            // (PowerSetRequest docs). With the battery sleep timeout at "never" that point never comes.
            if (System && PowerActions.ModernStandby)
                _dcSleep = TryJournal(PlanSetting.SleepTimeout, null, 0);

            // Closing the lid is a user-initiated sleep that ends all requests, unless its action is "do nothing".
            if (o.IgnoreLid)
            {
                Journal.Acquire(PlanSetting.LidAction, PowerPlan.LidDoNothing, PowerPlan.LidDoNothing);
                _lid = true;
            }
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            if (!_applied) return;
            _applied = false;
            TryRun(() => _request.Clear(Native.PowerRequestType.DisplayRequired), Display);
            TryRun(() => _request.Clear(Native.PowerRequestType.SystemRequired), System);
            if (_lid) TryRun(() => Journal.Release(PlanSetting.LidAction));
            if (_dcSleep) TryRun(() => Journal.Release(PlanSetting.SleepTimeout));
            _lid = _dcSleep = false;
        }
    }

    public bool Applied { get { lock (_gate) return _applied; } }

    /// For -s: hold the system request only while on AC power.
    public void SetAcOnlySystem(bool on)
    {
        lock (_gate)
        {
            if (on) _request.Set(Native.PowerRequestType.SystemRequired);
            else _request.Clear(Native.PowerRequestType.SystemRequired);
        }
    }

    public void Dispose()
    {
        Release();
        _request.Dispose();
    }

    private static bool TryJournal(PlanSetting s, uint? ac, uint? dc)
    {
        try { return Journal.Acquire(s, ac, dc); }
        catch (Exception e)
        {
            Session.Warn($"could not change the {s.Name}: {e.Message}");
            return false;
        }
    }

    private static void TryRun(Action a, bool when = true)
    {
        if (!when) return;
        try { a(); } catch (Exception e) { Log.Write($"release failed: {e.Message}"); }
    }
}

internal static class Session
{
    private static readonly ManualResetEvent Quit = new(false);
    private static volatile bool _runningUtility;
    private static int _interrupts;
    private static int _cleanedUp;
    private static readonly List<Action> Cleanups = [];
    private static readonly List<PosixSignalRegistration> SignalRegistrations = [];

    public static bool Quiet { get; set; }

    public static void Warn(string message)
    {
        Log.Write(message);
        if (!Quiet) Console.Error.WriteLine($"caffeinate: {message}");
    }

    public static void Info(string message)
    {
        if (!Quiet) Console.Error.WriteLine($"caffeinate: {message}");
    }

    /// Returns the exit code. 0 = held until the end, 130 = interrupted, otherwise the utility's code.
    public static int Run(Options o, string? scheduleName = null, DateTime? untilOverride = null)
    {
        // Like macOS: no assertion flags => prevent idle sleep.
        if (!o.AnyAssertion && !o.KeepsSystemAwake) { o.I = true; }

        Journal.Repair();

        // ---- When to stop (wall clock, so time asleep counts) ----
        DateTime? end = untilOverride;
        if (o.For is TimeSpan f) end = Clock.AddElapsed(Clock.Now, f);
        else if (o.Until is TimePoint u) end = u.Resolve(Clock.Now);
        else if (o.Timeout is long t) end = Clock.AddElapsed(Clock.Now, TimeSpan.FromSeconds(t));
        if (o.Utility.Length > 0) end = null; // -t is ignored with a utility, as on macOS

        // ---- Validate -w before taking anything ----
        SafeWaitHandle? pidHandle = null;
        if (o.WaitPid is int pid && o.Utility.Length == 0)
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

        InstallExitHandlers();
        using var holds = new Holds(o);
        Cleanups.Add(holds.Release);
        try
        {
            holds.Apply();

            Timer? acTimer = null;
            if (o.S && !o.KeepsSystemAwake)
            {
                bool held = false;
                void Sync()
                {
                    bool onAc = Native.IsOnAcPower();
                    if (onAc != held) { holds.SetAcOnlySystem(onAc); held = onAc; }
                }
                Sync();
                acTimer = new Timer(_ => { try { Sync(); } catch { } }, null, 5000, 5000);
                Cleanups.Add(() => acTimer.Dispose());
            }

            if (o.HoldUpdates)
            {
                var helper = Updates.StartSessionHold(end);
                Cleanups.Add(helper.Dispose);
            }

            if (o.U)
            {
                PowerActions.DeclareUserActivity();
                var userActive = new PowerRequest("caffeinate command-line tool (user active)");
                userActive.Set(Native.PowerRequestType.DisplayRequired);
                long secs = o.Timeout is > 0 ? o.Timeout.Value : 5;
                var timer = new Timer(_ => { try { userActive.Clear(Native.PowerRequestType.DisplayRequired); } catch { } },
                    null, TimeSpan.FromSeconds(secs), Timeout.InfiniteTimeSpan);
                Cleanups.Add(() => { timer.Dispose(); userActive.Dispose(); });
            }

            if (o.BatteryFloor is int floor)
            {
                var floorTimer = new Timer(_ => { try { CheckBatteryFloor(holds, floor); } catch { } }, null, 0, 30_000);
                Cleanups.Add(() => floorTimer.Dispose());
            }

            Sessions.Register(new SessionInfo
            {
                StartedLocal = Clock.Now,
                CommandLine = string.Join(' ', Environment.GetCommandLineArgs().Skip(1)),
                Holds = holds.Describe(),
                UntilLocal = end,
                Schedule = scheduleName,
            });
            Cleanups.Add(Sessions.Unregister);

            if (o.ScreenOff) ScreenOff.Run(Quit);

            if (o.Utility.Length > 0) return RunUtility(o.Utility);
            return WaitForEnd(end, pidHandle);
        }
        finally
        {
            Cleanup();
        }
    }

    private static bool _floorSuspended;

    private static void CheckBatteryFloor(Holds holds, int floor)
    {
        bool onAc = Native.IsOnAcPower();
        int? pct = Native.BatteryPercent();
        if (!_floorSuspended && !onAc && pct is int p && p <= floor)
        {
            holds.Release();
            _floorSuspended = true;
            Warn($"battery at {p}% (floor {floor}%): stopped holding; will resume when plugged in");
        }
        else if (_floorSuspended && onAc)
        {
            holds.Apply();
            _floorSuspended = false;
            Warn("power connected: holding again");
        }
    }

    private static int WaitForEnd(DateTime? end, SafeWaitHandle? pidHandle)
    {
        var handles = new List<WaitHandle> { Quit };
        if (pidHandle is not null) handles.Add(new ManualResetEvent(false) { SafeWaitHandle = pidHandle });
        var array = handles.ToArray();

        while (true)
        {
            // Re-check the wall clock at least every 30s: wait timeouts don't advance while the PC is asleep.
            int waitMs = Timeout.Infinite;
            if (end is DateTime e)
            {
                var left = e - Clock.Now;
                if (left <= TimeSpan.Zero) return 0;
                waitMs = (int)Math.Min(left.TotalMilliseconds, 30_000);
            }
            int which = WaitHandle.WaitAny(array, waitMs);
            if (which == 0) return 130;                         // interrupted, like a shell after SIGINT
            if (which == 1) return 0;                           // -w process exited
        }
    }

    private static void InstallExitHandlers()
    {
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

        // Closing the window / logoff / shutdown: Windows allows a few seconds, so restore synchronously here.
        // Keep the registrations referenced: a collected registration is silently unregistered.
        foreach (var sig in new[] { PosixSignal.SIGHUP, PosixSignal.SIGTERM })
            SignalRegistrations.Add(PosixSignalRegistration.Create(sig, _ => Cleanup()));
    }

    public static void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleanedUp, 1) == 1) return;
        for (int i = Cleanups.Count - 1; i >= 0; i--)
        {
            try { Cleanups[i](); }
            catch (Exception e) { Log.Write($"cleanup step failed: {e.Message}"); }
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
