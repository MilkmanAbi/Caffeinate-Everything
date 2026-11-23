// Command-line parsing: macOS getopt("dimsut:w:") semantics for the short flags, plus long options.

using System.Globalization;

namespace Caffeinate;

internal sealed class UsageException(string message) : Exception(message);

internal enum Command
{
    Hold, Schedule, List, Remove, PauseUpdates, ResumeUpdates, Status, Restore, Version, Help,
    // internal, used by scheduled tasks and the elevated helper
    RunSchedule, Repair, Elevated,
}

internal enum ActionKind { Sleep, Hibernate, Wake, ScreenOff }

internal sealed record AtEvent(TimePoint When, ActionKind Action);

internal sealed class Options
{
    public Command Command = Command.Hold;

    // macOS flags
    public bool D, I, M, S, U;
    public long? Timeout;
    public int? WaitPid;
    public string[] Utility = [];

    // hold extensions
    public TimeSpan? For;
    public TimePoint? Until;
    public bool ScreenOff, IgnoreLid, HoldUpdates;
    public int? BatteryFloor;

    // schedules
    public TimeWindow? SleepWindow, WakeWindow;
    public List<AtEvent> At = [];
    public Days? Repeat;
    public string? Name;
    public bool DryRun;

    // command arguments
    public string? Target;
    public string[] Internal = [];

    /// The flags that define a hold, re-serialized for saved schedules.
    public List<string> HoldArgs = [];

    public bool AnyAssertion => D || I || M || S || U;
    public bool KeepsSystemAwake => I || M || ScreenOff || IgnoreLid;
    public bool HasSchedule => SleepWindow is not null || WakeWindow is not null || At.Count > 0;
}

internal static class Cli
{
    // Same one-line usage as macOS caffeinate (plus -m, which macOS also accepts).
    public const string Usage = "usage: caffeinate [-dimsu] [-t timeout] [-w pid] [utility arguments...]";

    // macOS-style: the usage line on top, then the Windows extensions further down.
    public static readonly string Help = Usage + new string('\n', 16) + """
        Extended options (caffeinate for Windows)

          Holding
            --for DURATION        hold for a duration: 90m, 2h30m, 1d
            --until TIME          hold until a time: 18:30, 7am, "tomorrow 09:00"
            --screen-off          turn the screen off now but keep the PC fully running
                                  (Wi-Fi, downloads, builds); any key or touch turns it back on
            --ignore-lid          keep running with the lid closed (restored afterwards)
            --battery-floor PCT   on battery, stop holding at PCT% (resumes when plugged in)
            --hold-updates        pause Windows Update while holding [asks for admin once]

          Schedules
            --sleep WINDOW        asleep during WINDOW, then wake:      --sleep 23:00-07:00
            --wake WINDOW         awake during WINDOW, then sleep:      --wake 09:00-17:30
            --at "WHEN ACTION"    one event (repeatable); ACTION is sleep, hibernate, wake
                                  or screen-off:                        --at "23:30 sleep"
            --repeat DAYS         daily, weekdays, weekends, or mon,wed,fri
            --name NAME           name the schedule
            --dry-run             show what would happen, change nothing

            WINDOW is START-END, "START to END", START+DURATION, or DURATION (from now).
            A --wake window starting now runs in this terminal (Ctrl+C cancels); everything
            else is saved and runs in the background, also after a restart. Hold flags given
            with --wake (-d, --ignore-lid, ...) apply during the window.

          Manage
            --status              sessions, schedules, Windows Update and power settings
            --list                list saved schedules
            --remove NAME|all     delete saved schedules
            --pause-updates FOR   pause Windows Update: 7d, or a date (max 35 days) [admin]
            --resume-updates      resume Windows Update [admin]
            --restore             undo settings left changed by a caffeinate that was killed
            --version             print the version
        """;
    private static readonly string[] LongWithValue =
        ["for", "until", "battery-floor", "sleep", "wake", "at", "repeat", "name", "remove", "pause-updates"];

    private static readonly string[] LongFlags =
        ["screen-off", "ignore-lid", "hold-updates", "dry-run", "status", "list", "resume-updates", "restore",
         "version", "help"];

    public static Options Parse(string[] args)
    {
        var o = new Options();

        // Internal entry points (scheduled tasks, repair task, UAC helper) take the rest verbatim.
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--run-schedule": o.Command = Command.RunSchedule; o.Internal = args[1..]; return o;
                case "--repair": o.Command = Command.Repair; o.Internal = args[1..]; return o;
                case "--elevated": o.Command = Command.Elevated; o.Internal = args[1..]; return o;
            }
        }

        var commands = new List<Command>();
        int i = 0;
        while (i < args.Length)
        {
            string a = args[i];
            if (a == "--") { i++; break; }
            if (a == "-h") { commands.Add(Command.Help); i++; continue; }

            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                string name = a[2..], value = "";
                int eq = name.IndexOf('=');
                if (eq >= 0) { value = name[(eq + 1)..]; name = name[..eq]; }
                bool needsValue = LongWithValue.Contains(name);
                if (!needsValue && !LongFlags.Contains(name))
                    throw new UsageException($"unrecognized option '--{name}'");
                if (needsValue && eq < 0)
                {
                    if (i + 1 >= args.Length) throw new UsageException($"option '--{name}' requires an argument");
                    value = args[++i];
                }
                else if (!needsValue && eq >= 0)
                    throw new UsageException($"option '--{name}' doesn't take an argument");

                ApplyLong(o, name, value, commands);
                i++;
                continue;
            }

            if (a.Length < 2 || a[0] != '-') break;

            for (int j = 1; j < a.Length; j++)
            {
                char c = a[j];
                switch (c)
                {
                    case 'd': o.D = true; o.HoldArgs.Add("-d"); break;
                    case 'i': o.I = true; o.HoldArgs.Add("-i"); break;
                    case 'm': o.M = true; o.HoldArgs.Add("-m"); break;
                    case 's': o.S = true; o.HoldArgs.Add("-s"); break;
                    case 'u': o.U = true; o.HoldArgs.Add("-u"); break;
                    case 't':
                    case 'w':
                    {
                        string val;
                        if (j + 1 < a.Length) val = a[(j + 1)..];
                        else if (i + 1 < args.Length) val = args[++i];
                        else throw new UsageException($"option requires an argument -- {c}");

                        if (c == 't')
                        {
                            if (!long.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out long t))
                                throw new UsageException($"invalid timeout: {val}");
                            o.Timeout = t;
                        }
                        else
                        {
                            if (!int.TryParse(val, NumberStyles.None, CultureInfo.InvariantCulture, out int p) || p <= 0)
                                throw new UsageException($"invalid pid: {val}");
                            o.WaitPid = p;
                        }
                        j = a.Length;
                        break;
                    }
                    default:
                        throw new UsageException($"illegal option -- {c}");
                }
            }
            i++;
        }
        o.Utility = args[i..];

        if (commands.Distinct().Count() > 1)
            throw new UsageException("give only one of --status, --list, --remove, --pause-updates, --resume-updates, --restore, --version");
        if (commands.Count > 0) o.Command = commands[0];
        else if (o.HasSchedule) o.Command = Command.Schedule;

        Validate(o);
        return o;
    }

    private static void ApplyLong(Options o, string name, string value, List<Command> commands)
    {
        try
        {
            switch (name)
            {
                case "for": o.For = TimeSpec.ParseDuration(value); o.HoldArgs.Add($"--for={value}"); break;
                case "until": o.Until = TimeSpec.ParsePoint(value); break;
                case "screen-off": o.ScreenOff = true; o.HoldArgs.Add("--screen-off"); break;
                case "ignore-lid": o.IgnoreLid = true; o.HoldArgs.Add("--ignore-lid"); break;
                case "hold-updates": o.HoldUpdates = true; o.HoldArgs.Add("--hold-updates"); break;
                case "battery-floor":
                    if (!int.TryParse(value.TrimEnd('%'), NumberStyles.None, CultureInfo.InvariantCulture, out int pct)
                        || pct < 1 || pct > 99)
                        throw new UsageException($"invalid battery floor '{value}' (use a percentage from 1 to 99)");
                    o.BatteryFloor = pct;
                    o.HoldArgs.Add($"--battery-floor={pct}");
                    break;
                case "sleep":
                    if (o.SleepWindow is not null) throw new UsageException("--sleep given twice");
                    o.SleepWindow = TimeSpec.ParseWindow(value);
                    break;
                case "wake":
                    if (o.WakeWindow is not null) throw new UsageException("--wake given twice");
                    o.WakeWindow = TimeSpec.ParseWindow(value);
                    break;
                case "at": o.At.Add(ParseAt(value)); break;
                case "repeat": o.Repeat = TimeSpec.ParseDays(value); break;
                case "name": o.Name = ValidateName(value); break;
                case "dry-run": o.DryRun = true; break;
                case "status": commands.Add(Command.Status); break;
                case "list": commands.Add(Command.List); break;
                case "remove": commands.Add(Command.Remove); o.Target = value; break;
                case "pause-updates": commands.Add(Command.PauseUpdates); o.Target = value; break;
                case "resume-updates": commands.Add(Command.ResumeUpdates); break;
                case "restore": commands.Add(Command.Restore); break;
                case "version": commands.Add(Command.Version); break;
                case "help": commands.Add(Command.Help); break;
            }
        }
        catch (TimeSpecException e) { throw new UsageException($"--{name}: {e.Message}"); }
    }

    private static AtEvent ParseAt(string value)
    {
        var parts = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            // "WHEN ACTION" or "ACTION WHEN"
            if (TryAction(parts[^1], out var a1)) return new AtEvent(TimeSpec.ParsePoint(string.Join(' ', parts[..^1])), a1);
            if (TryAction(parts[0], out var a2)) return new AtEvent(TimeSpec.ParsePoint(string.Join(' ', parts[1..])), a2);
        }
        throw new UsageException($"--at '{value}': expected \"TIME ACTION\", e.g. \"23:30 sleep\" (actions: sleep, hibernate, wake, screen-off)");
    }

    private static bool TryAction(string s, out ActionKind a)
    {
        switch (s.ToLowerInvariant())
        {
            case "sleep": a = ActionKind.Sleep; return true;
            case "hibernate": a = ActionKind.Hibernate; return true;
            case "wake": a = ActionKind.Wake; return true;
            case "screen-off": case "screenoff": a = ActionKind.ScreenOff; return true;
            default: a = default; return false;
        }
    }

    private static string ValidateName(string name)
    {
        if (name.Length is 0 or > 40 || name.Equals("all", StringComparison.OrdinalIgnoreCase) || name == "repair"
            || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_')))
            throw new UsageException($"invalid name '{name}' (letters, digits, - and _; up to 40 characters)");
        return name;
    }

    private static void Validate(Options o)
    {
        bool holdExtras = o.For is not null || o.Until is not null || o.ScreenOff || o.IgnoreLid || o.HoldUpdates
                          || o.BatteryFloor is not null;

        if (o.Command is not (Command.Hold or Command.Schedule))
        {
            if (o.AnyAssertion || holdExtras || o.Timeout is not null || o.WaitPid is not null || o.Utility.Length > 0
                || o.HasSchedule || o.Repeat is not null || o.Name is not null)
                throw new UsageException("this command can't be combined with other options");
            return;
        }

        int timeLimits = (o.Timeout is not null ? 1 : 0) + (o.For is not null ? 1 : 0) + (o.Until is not null ? 1 : 0);
        if (timeLimits > 1) throw new UsageException("use only one of -t, --for and --until");
        if (o.ScreenOff && (o.D || o.U)) throw new UsageException("--screen-off contradicts -d and -u (they keep the screen on)");
        if ((o.For is not null || o.Until is not null) && o.Utility.Length > 0)
            throw new UsageException("--for/--until can't be combined with a utility (it holds until the utility exits)");

        if (o.Command == Command.Schedule)
        {
            if (o.Utility.Length > 0) throw new UsageException("a schedule can't run a utility");
            if (timeLimits > 0 || o.WaitPid is not null)
                throw new UsageException("-t, -w, --for and --until can't be combined with --sleep/--wake/--at (the window sets the time)");
            if (o.SleepWindow is not null && o.WakeWindow is not null)
                throw new UsageException("use either --sleep or --wake (use --at for anything more complex)");
            if ((o.SleepWindow ?? o.WakeWindow) is not null && o.At.Count > 0)
                throw new UsageException("combine --at with --sleep/--wake by writing the window as --at events");
            if (o.Repeat is not null)
            {
                if (o.SleepWindow is { IsRecurrable: false } || o.WakeWindow is { IsRecurrable: false }
                    || o.At.Any(e => !e.When.IsClock))
                    throw new UsageException("--repeat needs clock times (like 09:00 or 7pm), not dates, 'now' or +durations");
            }
            if (o.SleepWindow is not null && (o.AnyAssertion || holdExtras))
                throw new UsageException("hold flags (-d, --ignore-lid, ...) only apply to --wake windows");
        }
        else
        {
            if (o.Repeat is not null) throw new UsageException("--repeat needs --sleep, --wake or --at");
            if (o.Name is not null) throw new UsageException("--name needs --sleep, --wake or --at");
            if (o.DryRun && o.Command == Command.Hold && !o.HasSchedule)
                throw new UsageException("--dry-run only applies to schedules");
        }
    }
}
