// Task Scheduler integration (per-user tasks in the \Caffeinate\ folder; no admin needed) and the
// windowless agent executable those tasks launch.

using System.Diagnostics;
using System.Security;
using System.Text;

namespace Caffeinate;

internal static class Tasks
{
    public const string Folder = @"\Caffeinate\";

    public static void Register(string name, string xml)
    {
        string file = Path.Combine(Path.GetTempPath(), $"caffeinate-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, Encoding.Unicode);
        try
        {
            var (code, output) = SchTasks("/create", "/tn", Folder + name, "/xml", file, "/f");
            if (code != 0) throw new InvalidOperationException($"could not create scheduled task {name}: {output.Trim()}");
        }
        finally { File.Delete(file); }
    }

    public static void Delete(string name) => SchTasks("/delete", "/tn", Folder + name, "/f");

    public static bool Exists(string name) => SchTasks("/query", "/tn", Folder + name).Code == 0;

    public static (int Code, string Output) SchTasks(params string[] args)
    {
        var psi = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    /// Account name for logon/unlock triggers (a trigger without one would need admin rights).
    public static string CurrentUser => $@"{Environment.UserDomainName}\{Environment.UserName}";

    public static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    public static string Local(DateTime t) => t.ToString("yyyy-MM-dd'T'HH:mm:ss");

    public static string Xml(string description, IEnumerable<string> triggers, string arguments, bool wakeToRun) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>{Esc(description)}</Description></RegistrationInfo>
          <Triggers>
            {string.Join("\n    ", triggers)}
          </Triggers>
          <Principals>
            <Principal id="Author"><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>true</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
            <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
            <WakeToRun>{(wakeToRun ? "true" : "false")}</WakeToRun>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec><Command>{Esc(Agent.Install())}</Command><Arguments>{Esc(arguments)}</Arguments></Exec>
          </Actions>
        </Task>
        """;

    public static string TimeTrigger(DateTime local) =>
        $"<TimeTrigger><StartBoundary>{Local(local)}</StartBoundary><Enabled>true</Enabled></TimeTrigger>";

    public static string WeeklyTrigger(DateTime firstLocal, Days days)
    {
        var names = Enum.GetValues<DayOfWeek>().Where(d => days.Includes(d)).Select(d => $"<{d}/>");
        return $"<CalendarTrigger><StartBoundary>{Local(firstLocal)}</StartBoundary><Enabled>true</Enabled>" +
               $"<ScheduleByWeek><DaysOfWeek>{string.Concat(names)}</DaysOfWeek><WeeksInterval>1</WeeksInterval>" +
               "</ScheduleByWeek></CalendarTrigger>";
    }

    public static string LogonTrigger() =>
        $"<LogonTrigger><Enabled>true</Enabled><UserId>{Esc(CurrentUser)}</UserId></LogonTrigger>";

    public static string UnlockTrigger() =>
        $"<SessionStateChangeTrigger><Enabled>true</Enabled><StateChange>SessionUnlock</StateChange>" +
        $"<UserId>{Esc(CurrentUser)}</UserId></SessionStateChangeTrigger>";

    public static string RepeatingTrigger(TimeSpan every) =>
        $"<TimeTrigger><Repetition><Interval>PT{(int)every.TotalMinutes}M</Interval>" +
        $"<StopAtDurationEnd>false</StopAtDurationEnd></Repetition>" +
        $"<StartBoundary>{Local(Clock.Now.AddMinutes(1))}</StartBoundary><Enabled>true</Enabled></TimeTrigger>";
}

/// The windowless twin of caffeinate.exe that scheduled tasks run: the same bytes with the PE subsystem
/// switched from console to GUI, so a task firing at 23:00 doesn't flash a console window.
internal static class Agent
{
    public static string PathName => Path.Combine(State.Home, "bin", "caffeinate-agent.exe");

    private static string? _installed;

    public static string Install()
    {
        if (_installed is not null) return _installed;
        string self = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate caffeinate.exe");
        if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(PathName), StringComparison.OrdinalIgnoreCase))
            return _installed = PathName;

        byte[] image = File.ReadAllBytes(self);
        SetGuiSubsystem(image);
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        if (!File.Exists(PathName) || !File.ReadAllBytes(PathName).AsSpan().SequenceEqual(image))
        {
            string tmp = PathName + ".new";
            File.WriteAllBytes(tmp, image);
            try { File.Move(tmp, PathName, overwrite: true); }
            catch (IOException) when (File.Exists(PathName))
            {
                // The agent is running right now; it is still compatible, so keep using it.
                File.Delete(tmp);
                Log.Write("agent is in use; kept the existing copy");
            }
        }
        return _installed = PathName;
    }

    private static void SetGuiSubsystem(byte[] image)
    {
        int pe = BitConverter.ToInt32(image, 0x3C);
        if (pe <= 0 || pe + 24 + 70 > image.Length || image[pe] != 'P' || image[pe + 1] != 'E')
            throw new InvalidOperationException("caffeinate.exe is not a valid PE image");
        int subsystem = pe + 24 + 68;           // same offset for PE32 and PE32+
        image[subsystem] = 2;                   // IMAGE_SUBSYSTEM_WINDOWS_GUI
        image[subsystem + 1] = 0;
    }
}

/// Runs `caffeinate --repair` at logon and every few minutes while any setting is journaled, so a
/// killed caffeinate can't leave e.g. "lid close: do nothing" behind.
internal static class RepairTask
{
    private const string Name = "repair";
    private static bool? _present;

    public static void Ensure()
    {
        if (_present == true) return;
        try
        {
            if (!Tasks.Exists(Name))
                Tasks.Register(Name, Tasks.Xml("Restores power settings left changed by a caffeinate that was killed.",
                    [Tasks.LogonTrigger(), Tasks.RepeatingTrigger(TimeSpan.FromMinutes(5))], "--repair", false));
            _present = true;
        }
        catch (Exception e) { Log.Write($"could not register repair task: {e.Message}"); }
    }

    public static void Remove()
    {
        if (_present == false) return;
        try { Tasks.Delete(Name); _present = false; }
        catch (Exception e) { Log.Write($"could not remove repair task: {e.Message}"); }
    }
}
