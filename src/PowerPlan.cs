// Read/write power-plan settings on the active scheme (no admin needed for the current user's plan).

using System.Runtime.InteropServices;

namespace Caffeinate;

internal readonly record struct PlanSetting(string Key, string Name, Guid Subgroup, Guid Setting)
{
    private static readonly Guid SubSleep = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");

    public static readonly PlanSetting LidAction = new("lid", "lid close action",
        new("4f971e89-eebd-4455-a8de-9e59040e7347"), new("5ca83367-6e45-459f-a27b-476b1d01c936"));
    public static readonly PlanSetting DisplayTimeout = new("display", "screen timeout",
        new("7516b95f-f776-4464-8c53-06167f40cc99"), new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e"));
    public static readonly PlanSetting SleepTimeout = new("sleep", "sleep timeout",
        SubSleep, new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da"));
    public static readonly PlanSetting HibernateTimeout = new("hibernate", "hibernate timeout",
        SubSleep, new("9d7815a6-7ee4-497e-8888-515a05f02364"));
    public static readonly PlanSetting WakeTimers = new("waketimers", "wake timers",
        SubSleep, new("bd3b718a-0680-4d9d-8ab2-e1d2b4ac806d"));

    public static readonly PlanSetting[] All = [LidAction, DisplayTimeout, SleepTimeout, HibernateTimeout, WakeTimers];

    public static PlanSetting? ByKey(string key) => All.Cast<PlanSetting?>().FirstOrDefault(s => s!.Value.Key == key);
}

internal static class PowerPlan
{
    public const uint LidDoNothing = 0, LidSleep = 1, LidHibernate = 2, LidShutDown = 3;
    public const uint WakeTimersDisabled = 0, WakeTimersEnabled = 1, WakeTimersImportantOnly = 2;

    public static Guid ActiveScheme()
    {
        uint rc = PowerGetActiveScheme(IntPtr.Zero, out IntPtr p);
        if (rc != 0) throw new InvalidOperationException($"cannot read the active power plan (error {rc})");
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    public static (uint Ac, uint Dc) Read(PlanSetting s, Guid? scheme = null)
    {
        Guid sch = scheme ?? ActiveScheme(), sub = s.Subgroup, set = s.Setting;
        uint rc1 = PowerReadACValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, out uint ac);
        uint rc2 = PowerReadDCValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, out uint dc);
        if (rc1 != 0 || rc2 != 0)
            throw new InvalidOperationException($"cannot read the {s.Name} setting (error {(rc1 != 0 ? rc1 : rc2)})");
        return (ac, dc);
    }

    public static void Write(PlanSetting s, uint ac, uint dc, Guid? scheme = null)
    {
        Guid active = ActiveScheme();
        Guid sch = scheme ?? active, sub = s.Subgroup, set = s.Setting;
        uint rc = PowerWriteACValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, ac);
        if (rc == 0) rc = PowerWriteDCValueIndex(IntPtr.Zero, ref sch, ref sub, ref set, dc);
        // Re-applying the active scheme makes the new values take effect immediately.
        if (rc == 0 && sch == active) rc = PowerSetActiveScheme(IntPtr.Zero, ref sch);
        if (rc != 0)
            throw new InvalidOperationException(rc == 5
                ? $"not allowed to change the {s.Name} (a policy may be locking the power plan)"
                : $"cannot change the {s.Name} (error {rc})");
    }

    public static string Describe(PlanSetting s, uint v) => s.Key switch
    {
        "lid" => v switch
        {
            LidDoNothing => "do nothing", LidSleep => "sleep", LidHibernate => "hibernate",
            LidShutDown => "shut down", _ => $"value {v}",
        },
        "waketimers" => v switch
        {
            WakeTimersDisabled => "disabled", WakeTimersEnabled => "enabled",
            WakeTimersImportantOnly => "important only", _ => $"value {v}",
        },
        _ => v == 0 ? "never" : TimeSpec.Format(TimeSpan.FromSeconds(v)),
    };

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);
}
