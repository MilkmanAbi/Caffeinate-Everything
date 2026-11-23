// Sleep/wake schedules.
//
//   --sleep WINDOW   sleep at the start, wake at the end
//   --wake WINDOW    wake at the start, hold (with the given hold flags) until the end, then sleep
//   --at "WHEN ACT"  single events
//
// A --wake window that starts now runs in the terminal. --sleep that starts now sleeps right away
// (saving only the wake). Everything else is saved under schedules\NAME.json and registered as
// Task Scheduler tasks (\Caffeinate\NAME, plus \Caffeinate\NAME.wake with "wake the computer").
// When a task fires, the windowless agent runs `--run-schedule NAME` and works out what is due.
//
// Modern Standby can't be woken into a usable state by a timer (Microsoft: the always-on timer
// "wakes SoC but cannot turn on display"), so when a sleep is followed by a scheduled wake on such a
// PC, caffeinate hibernates instead: resuming from hibernate on a timer is a full wake.

using System.Globalization;
using System.Text;

namespace Caffeinate;

internal sealed class ScheduledEvent
{
    public ActionKind Action { get; set; }
    public TimeOnly? Time { get; set; }       // repeating: time of day
    public Days Days { get; set; }            // repeating: the days it happens on
    public DateTime? AtLocal { get; set; }    // one-off
    public bool Done { get; set; }            // one-off already handled
}

internal sealed class HoldWindow
{
    public TimeOnly? Start { get; set; }      // repeating
    public TimeOnly? End { get; set; }
    public Days Days { get; set; }            // days the window starts on
    public DateTime? StartLocal { get; set; } // one-off
    public DateTime? EndLocal { get; set; }
}

internal sealed class Schedule
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string CommandLine { get; set; } = "";
    public DateTime CreatedLocal { get; set; }
    public Days? Repeat { get; set; }
    public List<ScheduledEvent> Events { get; set; } = [];
    public List<HoldWindow> Holds { get; set; } = [];
    public List<string> HoldArgs { get; set; } = [];

    public bool HasWake => Events.Any(e => e.Action == ActionKind.Wake);

    /// Upcoming occurrences of each event, soonest first.
    public IEnumerable<(DateTime When, ActionKind Action)> Upcoming(DateTime now, int perEvent = 1)
    {
        var list = new List<(DateTime, ActionKind)>();
        foreach (var e in Events)
        {
            if (e.AtLocal is DateTime at) { if (!e.Done && at > now.AddMinutes(-10)) list.Add((at, e.Action)); continue; }
            int found = 0;
            for (int d = 0; d < 8 && found < perEvent; d++)
            {
                var day = now.Date.AddDays(d);
                var t = Clock.SkipGap(day + e.Time!.Value.ToTimeSpan());
                if (t > now && e.Days.Includes(day.DayOfWeek)) { list.Add((t, e.Action)); found++; }
            }
        }
        return list.OrderBy(x => x.Item1);
    }

    /// The hold window we are inside of right now, if any (returns its end).
    public DateTime? ActiveHoldEnd(DateTime now)
    {
        foreach (var h in Holds)
        {
            if (h.StartLocal is DateTime s && h.EndLocal is DateTime e)
            {
                if (s <= now && now < e) return e;
                continue;
            }
            for (int back = 0; back <= 1; back++)
            {
                var day = now.Date.AddDays(-back);
                if (!h.Days.Includes(day.DayOfWeek)) continue;
                var start = Clock.SkipGap(day + h.Start!.Value.ToTimeSpan());
                var end = Clock.SkipGap(day + h.End!.Value.ToTimeSpan());
                if (end <= start) end = Clock.SkipGap(day.AddDays(1) + h.End!.Value.ToTimeSpan());
                if (start <= now && now < end) return end;
            }
        }
        return null;
    }
}

internal sealed class WakeTimerState
{
    public Guid Scheme { get; set; }
    public uint OriginalAc { get; set; }
    public uint OriginalDc { get; set; }
    public List<string> Schedules { get; set; } = [];
}

internal static class Schedules
{
    private static string Dir => State.Dir("schedules");
    private static string FileFor(string name) => Path.Combine(Dir, name + ".json");

    public static Schedule? Load(string name) => State.Load(FileFor(name), StateJson.Default.Schedule);

    public static List<Schedule> All() =>
        Directory.GetFiles(Dir, "*.json")
            .Select(f => State.Load(f, StateJson.Default.Schedule))
            .Where(s => s is not null).Select(s => s!).OrderBy(s => s.Name).ToList();

    // ---------------------------------------------------------------- create

    public static int Create(Options o)
    {
        DateTime now = Clock.Now;
        var caps = PowerActions.Caps;
        var s = new Schedule
        {
            CreatedLocal = now,
            Repeat = o.Repeat,
            HoldArgs = o.HoldArgs,
            CommandLine = string.Join(' ', Environment.GetCommandLineArgs().Skip(1)),
        };
        var notes = new List<string>();

        // ---- --wake that starts now: run here, then sleep at the end ----
        if (o.WakeWindow is TimeWindow ww && o.Repeat is null)
        {
            var (start, end, inProgress) = ww.Resolve(now);
            if (inProgress || start <= now.AddSeconds(2))
            {
                if (end is null) throw new UsageException("--wake now needs an end (e.g. --wake 3h or --wake now-18:00)");
                if (o.DryRun)
                {
                    Console.WriteLine($"Hold now until {TimeSpec.Format(end.Value, now)} ({Describe(o.HoldArgs)}), then sleep.");
                    return 0;
                }
                int code = Session.Run(Cli.Parse([.. o.HoldArgs]), untilOverride: end);
                if (code != 0) return code;
                Session.Info("window over: sleeping");
                PowerActions.Sleep();
                return 0;
            }
        }

        // ---- build events ----
        string desc;
        if (o.SleepWindow is TimeWindow sw)
        {
            bool hasEnd = sw.End is not null || sw.Length is not null;
            ActionKind sleepKind = ActionKind.Sleep;
            if (hasEnd && caps.ModernStandby)
            {
                if (PowerActions.CanHibernate)
                {
                    sleepKind = ActionKind.Hibernate;
                    notes.Add("This PC uses Modern Standby, which can't be woken on a timer, so it will hibernate instead of sleep (resume takes a few seconds longer).");
                }
                else notes.Add("This PC uses Modern Standby and hibernation is off, so the scheduled wake may not happen. An admin can run: powercfg /hibernate on");
            }
            AddWindow(s, sw, o.Repeat, now, sleepKind, ActionKind.Wake, hold: false);
            desc = $"asleep {sw.Text}";
        }
        else if (o.WakeWindow is TimeWindow w)
        {
            AddWindow(s, w, o.Repeat, now, ActionKind.Wake, ActionKind.Sleep, hold: w.End is not null || w.Length is not null);
            desc = $"awake {w.Text}" + (o.HoldArgs.Count > 0 ? $" ({Describe(o.HoldArgs)})" : "");
        }
        else
        {
            foreach (var at in o.At)
            {
                if (o.Repeat is Days days) s.Events.Add(new ScheduledEvent { Action = at.Action, Time = at.When.Time, Days = days });
                else s.Events.Add(new ScheduledEvent { Action = at.Action, AtLocal = at.When.Resolve(now) });
            }
            desc = string.Join(", ", o.At.Select(a => $"{Lower(a.Action)} at {a.When.Text}"));
        }
        if (o.Repeat is Days r) desc += $", {r.Describe()}";
        if (caps.ModernStandby && s.HasWake && !s.Events.Any(e => e.Action == ActionKind.Hibernate))
            notes.Add("On Modern Standby PCs a timer can't fully wake the PC from sleep (Windows keeps the screen off). " +
                      "The wake works if the PC is awake, hibernated, or was put to sleep by a caffeinate --sleep schedule.");
        s.Description = desc;
        s.Name = o.Name ?? UniqueName(AutoName(o));

        // ---- anything due right now? (only one-offs; e.g. --sleep 2h = sleep now, wake in 2h) ----
        var dueNow = s.Repeat is null
            ? s.Events.Where(e => !e.Done && e.AtLocal <= now.AddSeconds(2)).ToList()
            : [];
        if (dueNow.Count > 1) throw new UsageException("more than one action is due right now");
        foreach (var e in dueNow) e.Done = true;

        if (o.DryRun)
        {
            PrintPlan(s, now, dueNow.FirstOrDefault(), notes, dryRun: true);
            return 0;
        }

        if (Load(s.Name) is not null && o.Name is not null)
            throw new UsageException($"a schedule named '{s.Name}' already exists (remove it first: caffeinate --remove {s.Name})");

        bool pending = s.Events.Any(e => !e.Done) || s.Holds.Count > 0;
        if (pending)
        {
            if (s.HasWake && s.Events.Any(e => e.Action == ActionKind.Wake && !e.Done))
                notes.AddRange(WakeTimers.Ensure(s.Name));
            State.Save(FileFor(s.Name), s, StateJson.Default.Schedule);
            try { RegisterTasks(s); }
            catch
            {
                Remove(s.Name, quiet: true);
                throw;
            }
        }

        PrintPlan(s, now, dueNow.FirstOrDefault(), notes, dryRun: false);
        if (dueNow.FirstOrDefault() is ScheduledEvent due) Execute(due.Action, s, late: TimeSpan.Zero, immediate: true);
        return 0;
    }

    private static void AddWindow(Schedule s, TimeWindow w, Days? repeat, DateTime now, ActionKind atStart, ActionKind atEnd, bool hold)
    {
        if (repeat is Days days)
        {
            var start = w.Start.Time;
            TimeOnly? end = null;
            bool crossesMidnight = false;
            if (w.Length is TimeSpan len)
            {
                if (len >= TimeSpan.FromDays(1)) throw new UsageException("a repeating window must be shorter than a day");
                end = start.Add(len, out int wrapped);
                crossesMidnight = wrapped > 0;
            }
            else if (w.End is { IsClock: true } endPoint)
            {
                end = endPoint.Time;
                crossesMidnight = endPoint.Time <= start;
            }
            s.Events.Add(new ScheduledEvent { Action = atStart, Time = start, Days = days });
            if (end is TimeOnly e)
            {
                s.Events.Add(new ScheduledEvent { Action = atEnd, Time = e, Days = crossesMidnight ? days.NextDay() : days });
                if (hold) s.Holds.Add(new HoldWindow { Start = start, End = e, Days = days });
            }
            return;
        }

        var (st, en, _) = w.Resolve(now);
        s.Events.Add(new ScheduledEvent { Action = atStart, AtLocal = st });
        if (en is DateTime end2)
        {
            s.Events.Add(new ScheduledEvent { Action = atEnd, AtLocal = end2 });
            if (hold) s.Holds.Add(new HoldWindow { StartLocal = st, EndLocal = end2 });
        }
    }

    private static Days NextDay(this Days d) =>
        (Days)((((int)d << 1) & 0x7F) | (((int)d & (int)Days.Sat) != 0 ? (int)Days.Sun : 0));

    private static string AutoName(Options o)
    {
        string Clean(string t) => new string(t.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (o.SleepWindow is TimeWindow s) return "sleep-" + Clean(s.Text);
        if (o.WakeWindow is TimeWindow w) return "wake-" + Clean(w.Text);
        return "at-" + Clean(o.At[0].When.Text) + "-" + Lower(o.At[0].Action);
    }

    private static string UniqueName(string baseName)
    {
        baseName = baseName.Length > 30 ? baseName[..30] : baseName;
        if (baseName.Length == 0) baseName = "schedule";
        string name = baseName;
        for (int i = 2; Load(name) is not null; i++) name = $"{baseName}-{i}";
        return name;
    }

    private static void RegisterTasks(Schedule s)
    {
        var normal = new List<string>();
        var wake = new List<string>();
        foreach (var e in s.Events.Where(e => !e.Done))
        {
            string trig = e.AtLocal is DateTime at ? Tasks.TimeTrigger(at)
                : Tasks.WeeklyTrigger(Clock.Now.Date + e.Time!.Value.ToTimeSpan(), e.Days);
            (e.Action == ActionKind.Wake ? wake : normal).Add(trig);
        }
        if (s.Holds.Count > 0)
        {
            // Re-enter an active window after a restart, sign-in or unlock.
            normal.Add(Tasks.LogonTrigger());
            normal.Add(Tasks.UnlockTrigger());
        }

        string args = $"--run-schedule {s.Name}";
        if (normal.Count > 0) Tasks.Register(s.Name, Tasks.Xml($"caffeinate schedule: {s.Description}", normal, args, false));
        if (wake.Count > 0) Tasks.Register(s.Name + ".wake", Tasks.Xml($"caffeinate schedule (wake): {s.Description}", wake, args, true));
    }

    // ---------------------------------------------------------------- list / remove

    public static int List()
    {
        var all = All();
        if (all.Count == 0) { Console.WriteLine("No saved schedules."); return 0; }
        var now = Clock.Now;
        foreach (var s in all)
        {
            Console.WriteLine($"{s.Name}");
            Console.WriteLine($"    {s.Description}");
            var next = s.Upcoming(now).Take(2).ToList();
            if (next.Count > 0)
                Console.WriteLine("    next: " + string.Join(", ", next.Select(n => $"{Lower(n.Action)} {TimeSpec.Format(n.When, now)}")));
            if (s.ActiveHoldEnd(now) is DateTime end)
                Console.WriteLine($"    holding now, until {TimeSpec.Format(end, now)}");
        }
        return 0;
    }

    public static int RemoveCommand(string target)
    {
        var names = target.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? All().Select(s => s.Name).ToList()
            : [target];
        if (names.Count == 0) { Console.WriteLine("No saved schedules."); return 0; }
        foreach (var n in names)
        {
            if (Load(n) is null)
            {
                Console.Error.WriteLine($"caffeinate: no schedule named '{n}' (see caffeinate --list)");
                return 1;
            }
            foreach (var msg in Remove(n, quiet: false)) Console.WriteLine(msg);
            Console.WriteLine($"Removed schedule {n}.");
        }
        return 0;
    }

    public static List<string> Remove(string name, bool quiet)
    {
        Tasks.Delete(name);
        Tasks.Delete(name + ".wake");
        try { File.Delete(FileFor(name)); } catch { }
        var notes = WakeTimers.Release(name);
        Log.Write($"removed schedule {name}");
        return quiet ? [] : notes;
    }

    // ---------------------------------------------------------------- the agent

    public static int RunScheduled(string name)
    {
        Session.Quiet = true;
        var s = Load(name);
        if (s is null)
        {
            Log.Write($"schedule {name} no longer exists; removing its tasks");
            Tasks.Delete(name);
            Tasks.Delete(name + ".wake");
            return 0;
        }

        var now = Clock.Now;
        var due = new List<(ScheduledEvent Event, DateTime When)>();
        foreach (var e in s.Events)
        {
            if (Occurrence(e, now) is DateTime when && when <= now.AddSeconds(60) && now - when <= TimeSpan.FromMinutes(10))
                due.Add((e, when));
        }

        foreach (var (e, when) in due.OrderBy(d => d.When))
        {
            if (when > now) Thread.Sleep(when - now);       // a trigger can fire a few seconds early
            if (e.AtLocal is not null) e.Done = true;
            Execute(e.Action, s, Clock.Now - when, immediate: false);
        }
        if (due.Count > 0 && s.Repeat is null) SaveOrFinish(s);

        // Inside a wake window (just started, or re-entered after restart/sign-in/unlock)? Hold until its end.
        if (s.ActiveHoldEnd(Clock.Now) is DateTime end)
        {
            using var mutex = new Mutex(false, $@"Local\Caffeinate.Hold.{name}", out _);
            bool owned;
            try { owned = mutex.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) return 0; // already holding this window
            try
            {
                Log.Write($"schedule {name}: holding until {end:yyyy-MM-dd HH:mm}");
                return Session.Run(Cli.Parse([.. s.HoldArgs]), name, end);
            }
            finally { mutex.ReleaseMutex(); }
        }
        return 0;
    }

    /// The most recent occurrence of an event at or before now (+60s), or null.
    private static DateTime? Occurrence(ScheduledEvent e, DateTime now)
    {
        if (e.AtLocal is DateTime at) return e.Done ? null : at;
        for (int back = -1; back <= 1; back++)
        {
            var day = now.Date.AddDays(-back);
            var t = Clock.SkipGap(day + e.Time!.Value.ToTimeSpan());
            if (e.Days.Includes(day.DayOfWeek) && t <= now.AddSeconds(60) && t >= now.AddMinutes(-10)) return t;
        }
        return null;
    }

    private static void SaveOrFinish(Schedule s)
    {
        bool more = s.Events.Any(e => !e.Done) || s.Holds.Any(h => h.EndLocal > Clock.Now);
        if (more) State.Save(FileFor(s.Name), s, StateJson.Default.Schedule);
        else Remove(s.Name, quiet: true);
    }

    public static void Execute(ActionKind action, Schedule s, TimeSpan late, bool immediate)
    {
        Log.Write($"schedule {s.Name}: {Lower(action)} (late {late.TotalSeconds:F0}s)");
        try
        {
            switch (action)
            {
                case ActionKind.Wake:
                    if (late > TimeSpan.FromMinutes(10)) return;
                    PowerActions.WakeFor(TimeSpan.FromMinutes(2));
                    break;
                case ActionKind.ScreenOff:
                    ScreenOff.Run();
                    break;
                case ActionKind.Sleep:
                case ActionKind.Hibernate:
                    if (!immediate && !ReadyToSleep(s.Name, late)) return;
                    if (action == ActionKind.Hibernate) PowerActions.Hibernate(); else PowerActions.Sleep();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"schedule {s.Name}: {Lower(action)} failed: {ex.Message}");
            if (immediate) throw;
        }
    }

    /// A scheduled sleep shouldn't yank the PC away from someone using it, or from a running caffeinate.
    private static bool ReadyToSleep(string name, TimeSpan late)
    {
        if (late > TimeSpan.FromMinutes(10))
        {
            Log.Write($"schedule {name}: sleep skipped, {late.TotalMinutes:F0} min late (PC was off or asleep)");
            return false;
        }
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(15);
        while (true)
        {
            var holders = Sessions.Running().Where(x => x.Schedule is null).ToList();
            if (holders.Count > 0)
            {
                Log.Write($"schedule {name}: sleep skipped, caffeinate pid {holders[0].Pid} is holding ({holders[0].CommandLine})");
                return false;
            }
            if (Native.UserIdle() >= TimeSpan.FromMinutes(2)) return true;
            if (DateTime.UtcNow > deadline)
            {
                Log.Write($"schedule {name}: sleep skipped, the PC was in use for 15 minutes");
                return false;
            }
            Thread.Sleep(15_000);
        }
    }

    // ---------------------------------------------------------------- output

    private static void PrintPlan(Schedule s, DateTime now, ScheduledEvent? dueNow, List<string> notes, bool dryRun)
    {
        var sb = new StringBuilder();
        sb.AppendLine(dryRun ? $"Would create schedule '{s.Name}': {s.Description}" : $"Schedule '{s.Name}': {s.Description}");
        if (dueNow is not null) sb.AppendLine($"  now: {Lower(dueNow.Action)}");
        foreach (var (when, action) in s.Upcoming(now, perEvent: s.Repeat is null ? 1 : 2).Take(6))
            sb.AppendLine($"  {Lower(action),-10} {TimeSpec.Format(when, now)}");
        if (s.Holds.Count > 0 && s.HoldArgs.Count > 0) sb.AppendLine($"  while awake: {Describe(s.HoldArgs)}");
        foreach (var n in notes) sb.AppendLine($"  note: {n}");
        if (!dryRun && (s.Events.Any(e => !e.Done) || s.Holds.Count > 0))
            sb.AppendLine($"  (runs in the background, also after a restart; remove with: caffeinate --remove {s.Name})");
        Console.Write(sb.ToString());
    }

    private static string Describe(IEnumerable<string> holdArgs) => string.Join(' ', holdArgs) is { Length: > 0 } a ? a : "-i";

    public static string Lower(ActionKind a) => a switch
    {
        ActionKind.ScreenOff => "screen-off",
        _ => a.ToString().ToLowerInvariant(),
    };
}

/// "Allow wake timers" must be Enabled for scheduled wakes. Turned on when the first schedule with a wake
/// is saved, and put back when the last one is removed (unless the user changed it meanwhile).
internal static class WakeTimers
{
    private static string PathName => State.FilePath("waketimers.json");

    public static List<string> Ensure(string schedule)
    {
        var notes = new List<string>();
        using var _ = State.Lock();
        var st = State.Load(PathName, StateJson.Default.WakeTimerState);
        var scheme = PowerPlan.ActiveScheme();
        var (ac, dc) = PowerPlan.Read(PlanSetting.WakeTimers, scheme);
        if (st is null)
        {
            if (ac == PowerPlan.WakeTimersEnabled && dc == PowerPlan.WakeTimersEnabled) return notes;
            st = new WakeTimerState { Scheme = scheme, OriginalAc = ac, OriginalDc = dc };
        }
        if (!st.Schedules.Contains(schedule)) st.Schedules.Add(schedule);
        State.Save(PathName, st, StateJson.Default.WakeTimerState);
        if (ac != PowerPlan.WakeTimersEnabled || dc != PowerPlan.WakeTimersEnabled)
        {
            PowerPlan.Write(PlanSetting.WakeTimers, PowerPlan.WakeTimersEnabled, PowerPlan.WakeTimersEnabled, scheme);
            notes.Add($"turned on 'Allow wake timers' (was: plugged in {PowerPlan.Describe(PlanSetting.WakeTimers, ac)}, " +
                      $"on battery {PowerPlan.Describe(PlanSetting.WakeTimers, dc)}); it is put back when the last wake schedule is removed.");
        }
        return notes;
    }

    public static List<string> Release(string schedule)
    {
        var notes = new List<string>();
        using var _ = State.Lock();
        var st = State.Load(PathName, StateJson.Default.WakeTimerState);
        if (st is null || !st.Schedules.Remove(schedule)) return notes;
        // Only schedules that still exist count.
        st.Schedules.RemoveAll(n => Schedules.Load(n) is null);
        if (st.Schedules.Count > 0)
        {
            State.Save(PathName, st, StateJson.Default.WakeTimerState);
            return notes;
        }
        try
        {
            var (ac, dc) = PowerPlan.Read(PlanSetting.WakeTimers, st.Scheme);
            uint newAc = ac == PowerPlan.WakeTimersEnabled ? st.OriginalAc : ac;
            uint newDc = dc == PowerPlan.WakeTimersEnabled ? st.OriginalDc : dc;
            if (newAc != ac || newDc != dc)
            {
                PowerPlan.Write(PlanSetting.WakeTimers, newAc, newDc, st.Scheme);
                notes.Add("put 'Allow wake timers' back to how it was.");
            }
        }
        catch (Exception e) { Log.Write($"could not restore wake timers: {e.Message}"); }
        File.Delete(PathName);
        return notes;
    }
}
