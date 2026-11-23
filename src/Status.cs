// caffeinate --status

using System.Text;

namespace Caffeinate;

internal static class Status
{
    public static int Show()
    {
        var now = Clock.Now;
        var sb = new StringBuilder();

        var journal = Journal.Entries();   // also restores anything a killed caffeinate left behind
        var sessions = Sessions.Running();
        sb.AppendLine("Holding:");
        if (sessions.Count == 0) sb.AppendLine("  nothing");
        foreach (var s in sessions)
        {
            string since = $"since {TimeSpec.Format(s.StartedLocal, now)} ({TimeSpec.Format(now - s.StartedLocal)})";
            string until = s.UntilLocal is DateTime u ? $", until {TimeSpec.Format(u, now)}" : "";
            string who = s.Schedule is not null ? $"schedule {s.Schedule}" : $"caffeinate {s.CommandLine}".TrimEnd();
            sb.AppendLine($"  pid {s.Pid}: {who}");
            sb.AppendLine($"      {string.Join(", ", s.Holds)}; {since}{until}");
        }

        sb.AppendLine();
        sb.AppendLine("Schedules:");
        var schedules = Schedules.All();
        if (schedules.Count == 0) sb.AppendLine("  none");
        foreach (var s in schedules)
        {
            var next = s.Upcoming(now).FirstOrDefault();
            string nextText = next == default ? "" : $"; next: {Schedules.Lower(next.Action)} {TimeSpec.Format(next.When, now)}";
            sb.AppendLine($"  {s.Name}: {s.Description}{nextText}");
        }

        sb.AppendLine();
        var paused = Updates.PausedUntilUtc();
        sb.AppendLine(paused is DateTime p
            ? $"Windows Update: paused until {TimeSpec.Format(Clock.ToLocal(p), now)}"
            : "Windows Update: not paused");

        sb.AppendLine();
        sb.AppendLine("Power:");
        bool ac = Native.IsOnAcPower();
        string battery = Native.BatteryPercent() is int pct ? $" ({pct}% battery)" : "";
        string standby = PowerActions.ModernStandby ? "Modern Standby" : "S3 sleep";
        sb.AppendLine($"  {(ac ? "plugged in" : "on battery")}{battery}; {standby}" +
                      (PowerActions.CanHibernate ? ", hibernate available" : ", hibernate off"));
        try
        {
            MessageWindow.Ensure();
            sb.AppendLine($"  screen is {MessageWindow.DisplayState switch { 0 => "off", 1 => "on", 2 => "dimmed", _ => "unknown" }}");
        }
        catch (Exception e) { sb.AppendLine($"  screen state unavailable: {e.Message}"); }
        sb.AppendLine("                     plugged in       on battery");
        foreach (var setting in new[] { PlanSetting.DisplayTimeout, PlanSetting.SleepTimeout, PlanSetting.LidAction, PlanSetting.WakeTimers })
        {
            try
            {
                var (a, d) = PowerPlan.Read(setting);
                sb.AppendLine($"  {setting.Name,-18} {PowerPlan.Describe(setting, a),-16} {PowerPlan.Describe(setting, d)}");
            }
            catch (Exception e) { sb.AppendLine($"  {setting.Name}: {e.Message}"); }
        }

        if (journal.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Temporarily changed by caffeinate (put back when these sessions end):");
            foreach (var e in journal)
            {
                var setting = PlanSetting.ByKey(e.Key);
                string name = setting?.Name ?? e.Key;
                string was = setting is PlanSetting st
                    ? $"{PowerPlan.Describe(st, e.OriginalAc)} / {PowerPlan.Describe(st, e.OriginalDc)}"
                    : $"{e.OriginalAc} / {e.OriginalDc}";
                sb.AppendLine($"  {name}: normally {was} (pid {string.Join(", ", e.Owners.Select(o => o.Pid))})");
            }
        }

        Console.Write(sb.ToString());
        return 0;
    }
}
