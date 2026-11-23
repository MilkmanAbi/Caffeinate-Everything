// caffeinate for Windows: prevent the system from sleeping on behalf of a utility, plus scheduling,
// screen-off, lid and Windows Update extensions. The macOS CLI is a strict subset:
//   caffeinate [-dimsu] [-t timeout] [-w pid] [utility arguments...]

using System.Reflection;

namespace Caffeinate;

internal static class Program
{
    private static int Main(string[] args)
    {
        Options o;
        try
        {
            o = Cli.Parse(args);
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine($"caffeinate: {e.Message}");
            Console.Error.WriteLine(Cli.Usage);
            return 1;
        }

        Updater.CleanupOld();
        if (o.Command is Command.Status or Command.List or Command.Version
            || (o.Command == Command.Hold && o.Utility.Length == 0))
            Updater.MaybeNotify();

        try
        {
            switch (o.Command)
            {
                case Command.Help: Console.WriteLine(Cli.Help); return 0;
                case Command.Version:
                    Console.WriteLine($"caffeinate {VersionText} (Windows, {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})");
                    return 0;
                case Command.CheckUpdate: return Updater.CheckCommand();
                case Command.Update: return Updater.UpdateCommand();
                case Command.UpdateCheck: return Updater.SetCheck(o.Target!);
                case Command.PostInstall: return Background(Updater.PostInstall);
                case Command.Status: return Status.Show();
                case Command.List: return Schedules.List();
                case Command.Remove: return Schedules.RemoveCommand(o.Target!);
                case Command.PauseUpdates: return Updates.PauseCommand(o.Target!);
                case Command.ResumeUpdates: return Updates.ResumeCommand();
                case Command.Restore: return Restore();
                case Command.Schedule: return Schedules.Create(o);
                case Command.RunSchedule: return Background(() => Schedules.RunScheduled(o.Internal.FirstOrDefault() ?? ""));
                case Command.Repair: return Background(o.Internal.Contains("--watch") ? Watch : RepairNow);
                case Command.Elevated: return Elevation.RunElevated(o.Internal);
                default: return Session.Run(o);
            }
        }
        catch (UsageException e)
        {
            Console.Error.WriteLine($"caffeinate: {e.Message}");
            return 1;
        }
        catch (Exception e)
        {
            Log.Write($"error: {e.Message}");
            Console.Error.WriteLine($"caffeinate: {e.Message}");
            return 1;
        }
    }

    public static string VersionText { get; } =
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";

    private static int Restore()
    {
        var restored = Journal.Repair();
        var live = Journal.Entries();
        if (restored.Count > 0) Console.WriteLine($"Restored: {string.Join(", ", restored)}.");
        if (live.Count > 0)
            Console.WriteLine($"Still changed by running caffeinate sessions (put back when they end): " +
                              string.Join(", ", live.Select(e => PlanSetting.ByKey(e.Key)?.Name ?? e.Key)) + ".");
        if (restored.Count == 0 && live.Count == 0) Console.WriteLine("Nothing to restore.");
        return 0;
    }

    private static int RepairNow()
    {
        var restored = Journal.Repair();
        if (restored.Count > 0) Log.Write($"repair task restored: {string.Join(", ", restored)}");
        if (!File.Exists(State.FilePath("journal.json"))) RepairTask.Remove();
        return 0;
    }

    /// Restore each journaled setting the moment its owning process exits (see RepairTask).
    private static int Watch()
    {
        using var changed = new EventWaitHandle(false, EventResetMode.AutoReset, RepairTask.ChangedEvent);
        while (true)
        {
            var mutex = new Mutex(false, RepairTask.WatcherMutex);
            bool owned;
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) { mutex.Dispose(); return 0; }   // another watcher is on it
            try
            {
                Log.Write("watcher started");
                while (true)
                {
                    var restored = Journal.Repair();
                    if (restored.Count > 0) Log.Write($"watcher restored: {string.Join(", ", restored)}");
                    var owners = Journal.Entries().SelectMany(e => e.Owners)
                        .GroupBy(o => (o.Pid, o.Started)).Select(g => g.First()).Take(62).ToList();
                    if (owners.Count == 0) break;
                    var handles = new List<WaitHandle> { changed };
                    var raw = new List<Microsoft.Win32.SafeHandles.SafeWaitHandle>();
                    foreach (var o in owners)
                    {
                        var h = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)o.Pid);
                        if (h.IsInvalid) { h.Dispose(); continue; }
                        raw.Add(h);
                        handles.Add(new ManualResetEvent(false) { SafeWaitHandle = h });
                    }
                    if (handles.Count == 1) { Thread.Sleep(500); continue; }   // owners vanished between calls
                    WaitHandle.WaitAny(handles.ToArray(), TimeSpan.FromMinutes(1));
                    foreach (var h in handles.Skip(1)) h.Dispose();
                }
            }
            finally
            {
                mutex.ReleaseMutex();
                mutex.Dispose();
            }
            // A session may have journaled something after our last look but before we let go of the
            // mutex (so its Ensure() didn't start a new watcher): check once more after releasing.
            if (!File.Exists(State.FilePath("journal.json"))) { Log.Write("watcher done"); return 0; }
        }
    }

    /// Scheduled-task entry points: no console, so log failures instead of printing them.
    private static int Background(Func<int> body)
    {
        Session.Quiet = true;
        try { return body(); }
        catch (Exception e)
        {
            Log.Write($"background run failed: {e}");
            return 1;
        }
    }
}
