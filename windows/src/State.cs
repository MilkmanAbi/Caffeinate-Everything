// Persistent state under %LOCALAPPDATA%\Caffeinate (override with CAFFEINATE_HOME for tests):
//   journal.json        power-plan settings caffeinate changed, their originals, and which processes own them
//   sessions\*.json     running caffeinate sessions (for --status)
//   schedules\*.json    saved sleep/wake schedules
//   bin\caffeinate-agent.exe   windowless copy used by scheduled tasks
//   caffeinate.log      log of background activity (agent, repairs, elevated helper)

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Caffeinate;

internal sealed class Owner
{
    public int Pid { get; set; }
    public long Started { get; set; }

    public static Owner Me { get; } = new()
    {
        Pid = Environment.ProcessId,
        Started = Native.ProcessStartTime(Environment.ProcessId) ?? 0,
    };

    public bool IsAlive => Native.ProcessStartTime(Pid) is long s && s == Started;
    public bool Same(Owner o) => o.Pid == Pid && o.Started == Started;
}

internal sealed class SettingEntry
{
    public string Key { get; set; } = "";
    public Guid Scheme { get; set; }
    public uint OriginalAc { get; set; }
    public uint OriginalDc { get; set; }
    public uint AppliedAc { get; set; }
    public uint AppliedDc { get; set; }
    public List<Owner> Owners { get; set; } = [];
}

internal sealed class JournalData
{
    public List<SettingEntry> Settings { get; set; } = [];
}

internal sealed class SessionInfo
{
    public int Pid { get; set; }
    public long Started { get; set; }
    public DateTime StartedLocal { get; set; }
    public string CommandLine { get; set; } = "";
    public List<string> Holds { get; set; } = [];
    public DateTime? UntilLocal { get; set; }
    public string? Schedule { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(JournalData))]
[JsonSerializable(typeof(SessionInfo))]
[JsonSerializable(typeof(Schedule))]
[JsonSerializable(typeof(WakeTimerState))]
internal sealed partial class StateJson : JsonSerializerContext;

internal static class State
{
    public static string Home { get; } = Environment.GetEnvironmentVariable("CAFFEINATE_HOME") is { Length: > 0 } h
        ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Caffeinate");

    public static string Dir(string name)
    {
        string d = Path.Combine(Home, name);
        Directory.CreateDirectory(d);
        return d;
    }

    public static string FilePath(string name)
    {
        Directory.CreateDirectory(Home);
        return Path.Combine(Home, name);
    }

    /// Cross-process lock for read-modify-write of the state files (same logon session).
    public static IDisposable Lock()
    {
        var m = new Mutex(false, @"Local\Caffeinate.State");
        try
        {
            if (!m.WaitOne(TimeSpan.FromSeconds(10)))
                throw new InvalidOperationException("timed out waiting for another caffeinate to release its state lock");
        }
        catch (AbandonedMutexException) { /* previous holder died; we own it now */ }
        return new Releaser(m);
    }

    private sealed class Releaser(Mutex m) : IDisposable
    {
        public void Dispose() { m.ReleaseMutex(); m.Dispose(); }
    }

    public static T? Load<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) where T : class
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null; }
        catch (Exception e) when (e is JsonException or IOException)
        {
            Log.Write($"ignoring unreadable state file {path}: {e.Message}");
            return null;
        }
    }

    /// Write atomically so a crash mid-write never leaves a truncated file.
    public static void Save<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(value, type));
        File.Move(tmp, path, overwrite: true);
    }
}

internal static class Log
{
    public static void Write(string message)
    {
        try
        {
            string path = State.FilePath("caffeinate.log");
            var fi = new FileInfo(path);
            if (fi.Exists && fi.Length > 1_000_000) File.Move(path, path + ".old", overwrite: true);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{Environment.ProcessId}] {message}{Environment.NewLine}");
        }
        catch { /* logging must never break anything */ }
    }
}

/// Power-plan settings changed on behalf of running caffeinate processes.
/// Shared between processes: the first changes the setting, the last one out restores it.
internal static class Journal
{
    private static string PathName => State.FilePath("journal.json");

    /// Apply new AC/DC values (null leaves that side alone). Returns false if nothing needed changing.
    public static bool Acquire(PlanSetting s, uint? ac, uint? dc)
    {
        using var _ = State.Lock();
        var j = LoadAndPrune();
        var entry = j.Settings.FirstOrDefault(e => e.Key == s.Key);
        if (entry is not null)
        {
            if (!entry.Owners.Any(o => o.Same(Owner.Me))) entry.Owners.Add(Owner.Me);
            Save(j);
            return true;
        }

        Guid scheme = PowerPlan.ActiveScheme();
        var (curAc, curDc) = PowerPlan.Read(s, scheme);
        uint wantAc = ac ?? curAc, wantDc = dc ?? curDc;
        if (wantAc == curAc && wantDc == curDc) return false;

        // Journal first, then change: a crash between the two leaves an entry whose restore is a no-op.
        j.Settings.Add(new SettingEntry
        {
            Key = s.Key, Scheme = scheme,
            OriginalAc = curAc, OriginalDc = curDc, AppliedAc = wantAc, AppliedDc = wantDc,
            Owners = [Owner.Me],
        });
        Save(j);
        try
        {
            PowerPlan.Write(s, wantAc, wantDc, scheme);
        }
        catch
        {
            j.Settings.RemoveAll(e => e.Key == s.Key);
            Save(j);
            throw;
        }
        Log.Write($"changed {s.Name}: AC {curAc}->{wantAc}, DC {curDc}->{wantDc}");
        return true;
    }

    public static void Release(PlanSetting s)
    {
        using var _ = State.Lock();
        var j = LoadAndPrune();
        var entry = j.Settings.FirstOrDefault(e => e.Key == s.Key);
        if (entry is null) return;
        entry.Owners.RemoveAll(o => o.Same(Owner.Me));
        if (entry.Owners.Count == 0)
        {
            Restore(entry);
            j.Settings.Remove(entry);
        }
        Save(j);
    }

    /// Restore settings whose owners have all exited. Returns what was restored.
    public static List<string> Repair()
    {
        if (!File.Exists(PathName)) return [];
        using var _ = State.Lock();
        var before = State.Load(PathName, StateJson.Default.JournalData)?.Settings.Select(e => e.Key).ToList() ?? [];
        var j = LoadAndPrune();
        Save(j);
        return before.Except(j.Settings.Select(e => e.Key))
            .Select(k => PlanSetting.ByKey(k)?.Name ?? k).ToList();
    }

    public static List<SettingEntry> Entries()
    {
        if (!File.Exists(PathName)) return [];
        using var _ = State.Lock();
        int before = State.Load(PathName, StateJson.Default.JournalData)?.Settings.Count ?? 0;
        var j = LoadAndPrune();
        if (j.Settings.Count != before) Save(j);
        return j.Settings;
    }

    private static JournalData LoadAndPrune()
    {
        var j = State.Load(PathName, StateJson.Default.JournalData) ?? new JournalData();
        foreach (var e in j.Settings.ToList())
        {
            e.Owners.RemoveAll(o => !o.IsAlive);
            if (e.Owners.Count == 0)
            {
                Log.Write($"owner(s) of {e.Key} exited without restoring it; restoring now");
                Restore(e);
                j.Settings.Remove(e);
            }
        }
        return j;
    }

    private static void Restore(SettingEntry e)
    {
        if (PlanSetting.ByKey(e.Key) is not PlanSetting s) return;
        try
        {
            var (ac, dc) = PowerPlan.Read(s, e.Scheme);
            // Only undo our own change: if the user changed the setting since, leave their value alone.
            uint newAc = ac == e.AppliedAc ? e.OriginalAc : ac;
            uint newDc = dc == e.AppliedDc ? e.OriginalDc : dc;
            if (newAc != ac || newDc != dc)
            {
                PowerPlan.Write(s, newAc, newDc, e.Scheme);
                Log.Write($"restored {s.Name}: AC {ac}->{newAc}, DC {dc}->{newDc}");
            }
        }
        catch (Exception ex) { Log.Write($"failed to restore {s.Name}: {ex.Message}"); }
    }

    private static void Save(JournalData j)
    {
        if (j.Settings.Count == 0)
        {
            File.Delete(PathName);
            RepairTask.Remove();
        }
        else
        {
            State.Save(PathName, j, StateJson.Default.JournalData);
            RepairTask.Ensure();
        }
    }
}

/// Running sessions, so --status can list them.
internal static class Sessions
{
    private static string FileFor(Owner o) => Path.Combine(State.Dir("sessions"), $"{o.Pid}-{o.Started}.json");

    public static void Register(SessionInfo info)
    {
        try
        {
            info.Pid = Owner.Me.Pid;
            info.Started = Owner.Me.Started;
            State.Save(FileFor(Owner.Me), info, StateJson.Default.SessionInfo);
        }
        catch (Exception e) { Log.Write($"could not record session: {e.Message}"); }
    }

    public static void Unregister()
    {
        try { File.Delete(FileFor(Owner.Me)); } catch { }
    }

    public static List<SessionInfo> Running()
    {
        var list = new List<SessionInfo>();
        string dir = Path.Combine(State.Home, "sessions");
        if (!Directory.Exists(dir)) return list;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var s = State.Load(f, StateJson.Default.SessionInfo);
            if (s is not null && new Owner { Pid = s.Pid, Started = s.Started }.IsAlive) list.Add(s);
            else try { File.Delete(f); } catch { }
        }
        return list.OrderBy(s => s.StartedLocal).ToList();
    }
}
