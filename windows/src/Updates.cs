// Windows Update pause, via the same registry values the Settings app writes
// (HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings). Writing needs admin, so those steps run in a
// short-lived elevated copy of caffeinate (one UAC prompt); reading works unprivileged.

using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Caffeinate;

internal static class Updates
{
    private const string KeyPath = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";
    private const string Expiry = "PauseUpdatesExpiryTime";
    private static readonly string[] StartNames = ["PauseUpdatesStartTime", "PauseFeatureUpdatesStartTime", "PauseQualityUpdatesStartTime"];
    private static readonly string[] EndNames = [Expiry, "PauseFeatureUpdatesEndTime", "PauseQualityUpdatesEndTime"];
    private static readonly string[] AllNames = [.. StartNames, .. EndNames];
    public const int MaxDays = 35;
    private const string Fmt = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    // ---------------------------------------------------------------- read (no admin)

    public static DateTime? PausedUntilUtc()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        if (key?.GetValue(Expiry) is not string s) return null;
        if (!DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return null;
        return t > DateTime.UtcNow ? t : null;
    }

    private static Dictionary<string, string?> Snapshot()
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return AllNames.ToDictionary(n => n, n => key?.GetValue(n) as string);
    }

    // ---------------------------------------------------------------- commands (unprivileged side)

    public static int PauseCommand(string spec)
    {
        DateTime endUtc;
        try
        {
            if (TimeSpec.LooksLikeDuration(spec))
                endUtc = DateTime.UtcNow + TimeSpec.ParseDuration(spec, allowBareSeconds: false);
            else
                endUtc = Clock.ToUtc(TimeSpec.ParsePoint(spec).Resolve(Clock.Now));
        }
        catch (TimeSpecException e) { throw new UsageException($"--pause-updates: {e.Message} (e.g. 7d, 14d or 2026-12-01)"); }

        if (endUtc <= DateTime.UtcNow.AddMinutes(1)) throw new UsageException("--pause-updates: that time is in the past");
        if (endUtc > DateTime.UtcNow.AddDays(MaxDays))
            throw new UsageException($"--pause-updates: Windows allows pausing for at most {MaxDays} days");

        Elevation.Run(["updates", "pause", endUtc.ToString(Fmt, CultureInfo.InvariantCulture)]);
        var now = PausedUntilUtc();
        if (now is null) throw new InvalidOperationException("Windows Update did not accept the pause");
        Console.WriteLine($"Windows Update paused until {TimeSpec.Format(Clock.ToLocal(now.Value), Clock.Now)}.");
        return 0;
    }

    public static int ResumeCommand()
    {
        if (PausedUntilUtc() is null)
        {
            Console.WriteLine("Windows Update isn't paused.");
            return 0;
        }
        Elevation.Run(["updates", "resume"]);
        Console.WriteLine(PausedUntilUtc() is null ? "Windows Update resumed." : "caffeinate: Windows Update still reports a pause");
        return PausedUntilUtc() is null ? 0 : 1;
    }

    /// --hold-updates: an elevated helper pauses updates for this session and restores the previous
    /// state when this process exits (it watches our process handle, so even a crash restores it).
    public static IDisposable StartSessionHold(DateTime? endLocal)
    {
        string id = Guid.NewGuid().ToString("N");
        var ready = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\Caffeinate.UpdReady.{id}");
        var stop = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\Caffeinate.UpdStop.{id}");
        var done = new EventWaitHandle(false, EventResetMode.ManualReset, $@"Local\Caffeinate.UpdDone.{id}");
        string end = endLocal is DateTime e ? Clock.ToUtc(e).ToString(Fmt, CultureInfo.InvariantCulture) : "none";
        Session.Info("asking for admin rights to pause Windows Update...");
        var helper = Elevation.Start(["updates", "hold", Owner.Me.Pid.ToString(CultureInfo.InvariantCulture),
            Owner.Me.Started.ToString(CultureInfo.InvariantCulture), end, id]);
        // The elevated helper hands off to a detached copy of itself and exits, so success = "ready" is set;
        // helper exit without "ready" = failure. (WaitAny reports the lowest signalled index, so ready wins.)
        using var exited = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(helper.Handle, false) };
        int which = WaitHandle.WaitAny([ready, exited], TimeSpan.FromSeconds(90));
        if (which != 0 && !ready.WaitOne(0))
        {
            string why = which == WaitHandle.WaitTimeout ? "timed out" : helper.Error();
            helper.Dispose();
            throw new InvalidOperationException("the Windows Update helper failed: " + why);
        }
        var until = PausedUntilUtc();
        Session.Info(until is DateTime u
            ? $"Windows Update paused until {TimeSpec.Format(Clock.ToLocal(u), Clock.Now)} (lifted when caffeinate exits)"
            : "Windows Update pause requested");
        return new HoldHandle(stop, ready, done, helper);
    }

    private sealed class HoldHandle(EventWaitHandle stop, EventWaitHandle ready, EventWaitHandle done, Elevation.Helper helper) : IDisposable
    {
        public void Dispose()
        {
            stop.Set();
            done.WaitOne(TimeSpan.FromSeconds(2)); // it restores in milliseconds; it also does so if we die
            helper.Dispose();
            stop.Dispose();
            ready.Dispose();
            done.Dispose();
        }
    }

    // ---------------------------------------------------------------- elevated side

    public static int Elevated(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case "pause":
                var end = DateTime.Parse(args[1], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
                Write(DateTime.UtcNow, end);
                Log.Write($"paused Windows Update until {args[1]}");
                return 0;
            case "resume":
                Clear();
                Log.Write("resumed Windows Update");
                return 0;
            case "hold":
                return Detach(args);
            case "hold-detached":
                return Hold(int.Parse(args[1], CultureInfo.InvariantCulture), long.Parse(args[2], CultureInfo.InvariantCulture),
                    args[3], args[4]);
            default:
                throw new InvalidOperationException("unknown updates operation");
        }
    }

    /// Re-launch as "hold-detached" and exit once it is ready. The detached copy's parent (this process) is
    /// gone, so "End process tree" on caffeinate doesn't kill it: it still sees caffeinate exit and restores.
    private static int Detach(string[] args)
    {
        string id = args[4];
        using var ready = EventWaitHandle.OpenExisting($@"Local\Caffeinate.UpdReady.{id}");
        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "--elevated", "updates", "hold-detached" }.Concat(args[1..])) psi.ArgumentList.Add(a);
        using var child = System.Diagnostics.Process.Start(psi)!;
        using var childExit = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(child.Handle, false) };
        int which = WaitHandle.WaitAny([ready, childExit], TimeSpan.FromSeconds(60));
        return which == 0 ? 0 : 2;
    }

    private static int Hold(int ownerPid, long ownerStarted, string endArg, string id)
    {
        if (Native.ProcessStartTime(ownerPid) != ownerStarted) return 3;
        using var owner = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)ownerPid);
        if (owner.IsInvalid) return 3;
        using var ready = EventWaitHandle.OpenExisting($@"Local\Caffeinate.UpdReady.{id}");
        using var stop = EventWaitHandle.OpenExisting($@"Local\Caffeinate.UpdStop.{id}");
        using var done = EventWaitHandle.OpenExisting($@"Local\Caffeinate.UpdDone.{id}");
        using var ownerWait = new ManualResetEvent(false) { SafeWaitHandle = owner };

        DateTime? end = endArg == "none" ? null
            : DateTime.Parse(endArg, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        // A short lease, renewed every 5 minutes: if caffeinate and this helper are both killed, the pause
        // still lapses on its own within 15 minutes.
        DateTime Lease()
        {
            var lease = DateTime.UtcNow.AddMinutes(15);
            return end is DateTime e && e.AddMinutes(5) < lease ? e.AddMinutes(5) : lease;
        }

        var prior = Snapshot();
        DateTime start = DateTime.UtcNow;
        string? written = null;
        DateTime? priorEnd = PausedUntilUtc();
        if (priorEnd is null || priorEnd < Lease())
        {
            Write(start, Lease());
            written = Read(Expiry);
            Log.Write($"session hold: paused Windows Update until {written} for pid {ownerPid}");
        }
        else Log.Write($"session hold: Windows Update already paused until {priorEnd:o}; leaving it");
        ready.Set();

        while (WaitHandle.WaitAny([ownerWait, stop], TimeSpan.FromMinutes(5)) == WaitHandle.WaitTimeout)
        {
            if (written is not null && Read(Expiry) == written)
            {
                Write(start, Lease());   // renew the lease while the session lasts
                written = Read(Expiry);
            }
        }

        // Restore only our own write: if someone changed the pause meanwhile, leave theirs.
        if (written is not null && Read(Expiry) == written)
        {
            using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
            foreach (var (name, value) in prior)
            {
                if (value is null) key.DeleteValue(name, throwOnMissingValue: false);
                else key.SetValue(name, value, RegistryValueKind.String);
            }
            Log.Write("session hold: restored the previous Windows Update pause state");
        }
        done.Set();
        return 0;
    }

    private static string? Read(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        return key?.GetValue(name) as string;
    }

    private static void Write(DateTime startUtc, DateTime endUtc)
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        string s = startUtc.ToString(Fmt, CultureInfo.InvariantCulture), e = endUtc.ToString(Fmt, CultureInfo.InvariantCulture);
        foreach (var n in StartNames) key.SetValue(n, s, RegistryValueKind.String);
        foreach (var n in EndNames) key.SetValue(n, e, RegistryValueKind.String);
    }

    private static void Clear()
    {
        using var key = Registry.LocalMachine.CreateSubKey(KeyPath, writable: true);
        foreach (var n in AllNames) key.DeleteValue(n, throwOnMissingValue: false);
    }
}

/// Run a copy of caffeinate elevated through UAC, only for the step that needs it.
internal static class Elevation
{
    public static bool IsElevated
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public sealed class Helper : IDisposable
    {
        public IntPtr Handle { get; init; }
        public string ResultFile { get; init; } = "";

        public bool WaitForExit(TimeSpan timeout)
        {
            using var w = new ManualResetEvent(false) { SafeWaitHandle = new SafeWaitHandle(Handle, false) };
            return w.WaitOne(timeout);
        }

        public int ExitCode => Native.GetExitCode(Handle);

        public string Error()
        {
            try { if (File.Exists(ResultFile)) return File.ReadAllText(ResultFile).Trim(); } catch { }
            return $"exit code {ExitCode}";
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) Native.CloseHandle(Handle);
            try { File.Delete(ResultFile); } catch { }
        }
    }

    /// Run an elevated operation and wait for it; throws with the helper's error message on failure.
    public static void Run(string[] args)
    {
        using var h = Start(args);
        h.WaitForExit(Timeout.InfiniteTimeSpan);
        if (h.ExitCode != 0) throw new InvalidOperationException(h.Error());
    }

    public static Helper Start(string[] args)
    {
        string result = Path.Combine(Path.GetTempPath(), $"caffeinate-{Guid.NewGuid():N}.result");
        string exe = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate caffeinate.exe");
        string parameters = string.Join(' ', new[] { "--elevated", "--result", result }.Concat(args).Select(Quote));

        IntPtr verb = Marshal.StringToHGlobalUni(IsElevated ? "open" : "runas");
        IntPtr file = Marshal.StringToHGlobalUni(exe);
        IntPtr param = Marshal.StringToHGlobalUni(parameters);
        try
        {
            var info = new Native.SHELLEXECUTEINFOW
            {
                cbSize = Marshal.SizeOf<Native.SHELLEXECUTEINFOW>(),
                fMask = Native.SEE_MASK_NOCLOSEPROCESS | Native.SEE_MASK_NOASYNC | Native.SEE_MASK_FLAG_NO_UI,
                lpVerb = verb, lpFile = file, lpParameters = param,
                nShow = 0, // SW_HIDE: the helper's console window stays hidden
            };
            if (!Native.ShellExecuteExW(ref info))
            {
                int err = Marshal.GetLastWin32Error();
                throw new InvalidOperationException(err == 1223
                    ? "the admin (UAC) prompt was declined"
                    : $"could not start the admin helper: {new Win32Exception(err).Message}");
            }
            if (info.hProcess == IntPtr.Zero) throw new InvalidOperationException("the admin helper did not start");
            return new Helper { Handle = info.hProcess, ResultFile = result };
        }
        finally
        {
            Marshal.FreeHGlobal(verb);
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(param);
        }
    }

    /// Entry point of the elevated copy: `--elevated --result FILE <op...>`.
    public static int RunElevated(string[] args)
    {
        string? result = args.Length >= 2 && args[0] == "--result" ? args[1] : null;
        string[] op = result is null ? args : args[2..];
        try
        {
            if (!IsElevated) throw new InvalidOperationException("admin rights are required");
            return op.FirstOrDefault() switch
            {
                "updates" => Updates.Elevated(op[1..]),
                _ => throw new InvalidOperationException("unknown elevated operation"),
            };
        }
        catch (Exception e)
        {
            Log.Write($"elevated helper failed: {e.Message}");
            if (result is not null) try { File.WriteAllText(result, e.Message); } catch { }
            return 1;
        }
    }

    private static string Quote(string s) =>
        s.Length > 0 && s.IndexOfAny([' ', '\t', '"']) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";
}
