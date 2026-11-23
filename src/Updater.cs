// Update checks and self-update from GitHub releases.
//
//   caffeinate --check-update     is there a newer release?
//   caffeinate --update           install it: the installer for installed copies (Inno Setup upgrades
//                                 in place), or an in-place swap for a standalone exe
//   caffeinate --update-check on|off   the once-a-day background check (default on)
//
// Downloads are for the PC's architecture (arm64 or x64, even if an x64 copy runs emulated on ARM)
// and are verified against the release's SHA256SUMS.txt before anything is run or replaced.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace Caffeinate;

internal sealed record ReleaseInfo(Version Version, string Tag, string Url, Dictionary<string, string> Assets);

internal sealed class UpdateState
{
    public bool Enabled { get; set; } = true;
    public DateTime LastCheckUtc { get; set; }
    public DateTime LastNoticeUtc { get; set; }
    public string? Latest { get; set; }
}

internal static class Updater
{
    public const string Repo = "MilkmanAbi/Caffeinate-Everything";
    private const string InnoAppId = "{716F32B2-0987-4131-857A-A69160FE1973}_is1";

    // Test hooks: point at a local server, and pretend to be an older version.
    private static string ApiBase => Environment.GetEnvironmentVariable("CAFFEINATE_UPDATE_API") is { Length: > 0 } u
        ? u.TrimEnd('/') : "https://api.github.com";

    public static Version Current { get; } =
        Version.TryParse(Environment.GetEnvironmentVariable("CAFFEINATE_PRETEND_VERSION"), out var pretend) ? pretend
        : Version.TryParse(Program.VersionText, out var v) ? v : new Version(0, 0);

    /// The PC's architecture, not this process's: an x64 copy running emulated on ARM updates to arm64.
    public static string Arch => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    private static string StatePath => State.FilePath("update.json");

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"caffeinate-windows/{Program.VersionText}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    public static ReleaseInfo FetchLatest(HttpClient http)
    {
        string json = http.GetStringAsync($"{ApiBase}/repos/{Repo}/releases/latest").GetAwaiter().GetResult();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new InvalidOperationException($"the latest release has an unexpected tag '{tag}'");
        var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (a.GetProperty("name").GetString() is string n && a.GetProperty("browser_download_url").GetString() is string url)
                assets[n] = url;
        }
        string page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";
        return new ReleaseInfo(version, tag, page, assets);
    }

    // ---------------------------------------------------------------- commands

    public static int CheckCommand()
    {
        using var http = Client(TimeSpan.FromSeconds(20));
        var r = FetchLatest(http);
        Remember(r.Version);
        if (r.Version <= Current)
        {
            Console.WriteLine($"caffeinate {Current} is up to date.");
            return 0;
        }
        Console.WriteLine($"caffeinate {r.Version} is available (you have {Current}): {r.Url}");
        Console.WriteLine("Run 'caffeinate --update' to install it.");
        return 0;
    }

    public static int UpdateCommand()
    {
        using var http = Client(TimeSpan.FromMinutes(5));
        Console.WriteLine("Checking for updates...");
        var r = FetchLatest(http);
        Remember(r.Version);
        if (r.Version <= Current)
        {
            Console.WriteLine($"caffeinate {Current} is up to date.");
            return 0;
        }

        string self = Environment.ProcessPath ?? throw new InvalidOperationException("cannot locate caffeinate.exe");
        bool installed = InstalledDir() is string dir &&
                         string.Equals(dir, Path.GetDirectoryName(self), StringComparison.OrdinalIgnoreCase);
        string asset = installed ? $"caffeinate-{r.Version}-windows-{Arch}-setup.exe" : $"caffeinate-windows-{Arch}.exe";
        if (!r.Assets.TryGetValue(asset, out var url))
            throw new InvalidOperationException($"release {r.Tag} has no {asset}; download it from {r.Url}");

        string work = Path.Combine(Path.GetTempPath(), $"caffeinate-update-{r.Version}");
        Directory.CreateDirectory(work);
        string file = Path.Combine(work, asset);
        Console.WriteLine($"Downloading caffeinate {r.Version} ({Arch}, {(installed ? "installer" : "standalone exe")})...");
        Download(http, url, file);
        Verify(http, r, asset, file);
        Console.WriteLine("Verified the download against the release's SHA256SUMS.txt.");

        if (installed)
        {
            // Inno Setup upgrades in place (same AppId), keeps the install location and per-user/all-users
            // choice, and closes running copies. This process exits right away so its exe can be replaced.
            Process.Start(new ProcessStartInfo(file)
            {
                UseShellExecute = true,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS",
            });
            Console.WriteLine($"Installing caffeinate {r.Version}; the installer finishes on its own in a few seconds.");
            Log.Write($"update: started installer for {r.Version}");
            return 0;
        }

        ReplaceSelf(self, file);
        Console.WriteLine($"Updated {self} to caffeinate {r.Version}.");
        Log.Write($"update: replaced standalone exe with {r.Version}");
        return 0;
    }

    public static int SetCheck(string value)
    {
        bool on = value.ToLowerInvariant() switch
        {
            "on" or "yes" or "true" or "1" => true,
            "off" or "no" or "false" or "0" => false,
            _ => throw new UsageException($"--update-check: use on or off, not '{value}'"),
        };
        var st = LoadState();
        st.Enabled = on;
        State.Save(StatePath, st, StateJson.Default.UpdateState);
        Console.WriteLine(on ? "caffeinate will check for new versions once a day." : "Automatic update checks are off.");
        return 0;
    }

    // ---------------------------------------------------------------- background notice

    /// At most once a day, look for a newer release in the background and print a one-line notice.
    /// Never blocks: a short-lived command may exit first; the result is cached for the next run.
    public static void MaybeNotify()
    {
        try
        {
            if (Session.Quiet || Console.IsErrorRedirected) return;
            if (Environment.GetEnvironmentVariable("CAFFEINATE_NO_UPDATE_CHECK") is { Length: > 0 }) return;
            var st = LoadState();
            if (!st.Enabled) return;

            if (Version.TryParse(st.Latest, out var known) && known > Current)
            {
                Notice(st, known);
                return;
            }
            if (DateTime.UtcNow - st.LastCheckUtc < TimeSpan.FromHours(24)) return;

            var t = new Thread(() =>
            {
                try
                {
                    using var http = Client(TimeSpan.FromSeconds(8));
                    var r = FetchLatest(http);
                    st.LastCheckUtc = DateTime.UtcNow;
                    st.Latest = r.Version.ToString();
                    State.Save(StatePath, st, StateJson.Default.UpdateState);
                    if (r.Version > Current) Notice(st, r.Version);
                }
                catch (Exception e)
                {
                    // Offline etc.: try again in an hour rather than on every run.
                    st.LastCheckUtc = DateTime.UtcNow - TimeSpan.FromHours(23);
                    try { State.Save(StatePath, st, StateJson.Default.UpdateState); } catch { }
                    Log.Write($"update check failed: {e.Message}");
                }
            }) { IsBackground = true, Name = "caffeinate-update-check" };
            t.Start();
        }
        catch { /* an update check must never break caffeinate */ }
    }

    private static void Notice(UpdateState st, Version latest)
    {
        if (DateTime.UtcNow - st.LastNoticeUtc < TimeSpan.FromHours(24)) return;
        st.LastNoticeUtc = DateTime.UtcNow;
        try { State.Save(StatePath, st, StateJson.Default.UpdateState); } catch { }
        Console.Error.WriteLine($"caffeinate: version {latest} is available (you have {Current}). " +
                                "Run 'caffeinate --update' to install it.");
    }

    private static void Remember(Version latest)
    {
        try
        {
            var st = LoadState();
            st.LastCheckUtc = DateTime.UtcNow;
            st.Latest = latest.ToString();
            State.Save(StatePath, st, StateJson.Default.UpdateState);
        }
        catch { }
    }

    private static UpdateState LoadState() => State.Load(StatePath, StateJson.Default.UpdateState) ?? new UpdateState();

    // ---------------------------------------------------------------- helpers

    /// Where the Inno Setup installer put caffeinate, if it did (per-user first, then all users).
    public static string? InstalledDir()
    {
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var key = hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{InnoAppId}");
            if (key?.GetValue("InstallLocation") is string s && s.Length > 0) return s.TrimEnd('\\');
        }
        return null;
    }

    private static void Download(HttpClient http, string url, string file)
    {
        using var src = http.GetStreamAsync(url).GetAwaiter().GetResult();
        using var dst = File.Create(file);
        src.CopyTo(dst);
    }

    private static void Verify(HttpClient http, ReleaseInfo r, string asset, string file)
    {
        if (!r.Assets.TryGetValue("SHA256SUMS.txt", out var sumsUrl))
            throw new InvalidOperationException("the release has no SHA256SUMS.txt, so the download can't be verified; not installing it");
        string sums = http.GetStringAsync(sumsUrl).GetAwaiter().GetResult();
        string? expected = sums.Split('\n')
            .Select(l => l.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && p[1].Trim().TrimStart('*') == asset)
            .Select(p => p[0].ToLowerInvariant()).FirstOrDefault();
        if (expected is null) throw new InvalidOperationException($"SHA256SUMS.txt has no entry for {asset}; not installing it");
        string actual;
        using (var f = File.OpenRead(file)) actual = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        if (actual != expected)
        {
            File.Delete(file);
            throw new InvalidOperationException($"checksum mismatch for {asset} (expected {expected}, got {actual}); not installing it");
        }
    }

    /// Windows lets you rename a running exe but not overwrite it: move ourselves aside, put the new one in place.
    private static void ReplaceSelf(string self, string newExe)
    {
        string old = self + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(self, old);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new InvalidOperationException(
                $"can't replace {self} ({e.Message}). Run this from an admin terminal, or use the installer from the release page.");
        }
        try { File.Move(newExe, self); }
        catch
        {
            File.Move(old, self);   // put the working copy back
            throw;
        }
    }

    /// Remove the previous exe left by a standalone self-update (it was still running at the time).
    public static void CleanupOld()
    {
        try
        {
            if (Environment.ProcessPath is string self && File.Exists(self + ".old")) File.Delete(self + ".old");
        }
        catch { }
    }

    /// Run by the installer after install/upgrade: refresh the windowless agent copy used by schedules.
    public static int PostInstall()
    {
        try { if (File.Exists(Agent.PathName)) Agent.Install(); }
        catch (Exception e) { Log.Write($"post-install: could not refresh the agent: {e.Message}"); }
        try { Journal.Repair(); } catch { }
        return 0;
    }
}
