using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Blockplan.Core;

/// <summary>
/// Selbst-Update über GitHub-Releases: neue Blockplan.exe herunterladen, die laufende Datei umbenennen
/// (das erlaubt Windows auch während sie läuft), die neue an ihre Stelle legen und neu starten.
/// Dateien, die das Programm selbst lädt, bekommen keine Browser- oder SmartScreen-Warnung.
/// </summary>
public static class Updater
{
    const string Api = "https://api.github.com/repos/yugito-11/blockplan/releases/latest";

    public static Version Current => Version.TryParse(Environment.GetEnvironmentVariable("BLOCKPLAN_FAKEVER"), out var f) ? f // nur zum Testen
        : Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Current.Build}";

    /// <summary>Nur als echte Windows-.exe (nicht beim Entwickeln mit „dotnet Blockplan.dll“).</summary>
    public static string? ExePath
    {
        get
        {
            var p = Environment.ProcessPath;
            return OperatingSystem.IsWindows() && p != null && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                   && !Path.GetFileName(p).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) ? p : null;
        }
    }

    /// <summary>Reste eines früheren Updates entfernen.</summary>
    public static void CleanUp()
    {
        try { if (ExePath is { } exe) { var old = exe + ".old"; if (File.Exists(old)) File.Delete(old); } } catch { }
    }

    public sealed record Release(string Version, string Url, long Size);

    public static async Task<Release?> CheckAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Blockplan/" + CurrentText);
        var j = JsonNode.Parse(await http.GetStringAsync(Api))!;
        var tag = ((string?)j["tag_name"] ?? "").TrimStart('v');
        if (!Version.TryParse(tag, out var v) || v <= new Version(Current.Major, Current.Minor, Current.Build)) return null;
        foreach (var a in j["assets"]!.AsArray())
            if ((string?)a!["name"] == "Blockplan.exe") return new Release(tag, (string)a["browser_download_url"]!, (long?)a["size"] ?? 0);
        return null;
    }

    /// <summary>Lädt die neue Version neben die laufende Datei (Blockplan.exe.new).</summary>
    public static async Task<string> DownloadAsync(Release r, IProgress<int>? prog)
    {
        var exe = ExePath ?? throw new InvalidOperationException("Kein Update beim Entwickeln.");
        var dest = exe + ".new";
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Blockplan/" + CurrentText);
        using var resp = await http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var fs = File.Create(dest))
        {
            var buf = new byte[1 << 16]; long got = 0; int n;
            while ((n = await src.ReadAsync(buf)) > 0) { await fs.WriteAsync(buf.AsMemory(0, n)); got += n; if (r.Size > 0) prog?.Report((int)(got * 100 / r.Size)); }
        }
        if (r.Size > 0 && new FileInfo(dest).Length != r.Size) { File.Delete(dest); throw new IOException("Download unvollständig."); }
        return dest;
    }

    /// <summary>Neue Datei einsetzen und neu starten.</summary>
    public static void ApplyAndRestart(string newFile)
    {
        var exe = ExePath!;
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        File.Move(newFile, exe);
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        Environment.Exit(0);
    }
}
