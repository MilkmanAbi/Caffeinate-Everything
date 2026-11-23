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

        try
        {
            switch (o.Command)
            {
                case Command.Help: Console.WriteLine(Cli.Help); return 0;
                case Command.Version: Console.WriteLine($"caffeinate {Version} (Windows)"); return 0;
                case Command.Status: return Status.Show();
                case Command.List: return Schedules.List();
                case Command.Remove: return Schedules.RemoveCommand(o.Target!);
                case Command.PauseUpdates: return Updates.PauseCommand(o.Target!);
                case Command.ResumeUpdates: return Updates.ResumeCommand();
                case Command.Restore: return Restore();
                case Command.Schedule: return Schedules.Create(o);
                case Command.RunSchedule: return Background(() => Schedules.RunScheduled(o.Internal.FirstOrDefault() ?? ""));
                case Command.Repair: return Background(RepairNow);
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

    private static string Version =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "?";

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
