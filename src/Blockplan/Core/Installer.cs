using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Threading;

namespace Blockplan.Core;

/// <summary>
/// Installation ohne Administratorrechte, wie bei vielen Programmen üblich:
/// Programm nach %LOCALAPPDATA%\Programs\Blockplan, Startmenü- und Desktop-Verknüpfung,
/// Eintrag unter Windows „Apps“ (dort auch deinstallieren). Die Projekte bleiben in %LOCALAPPDATA%\Blockplan.
/// </summary>
public static class Installer
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Blockplan");
    public static string Target => Path.Combine(Dir, "Blockplan.exe");
    static string StartMenuLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Blockplan.lnk");
    static string DesktopLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Blockplan.lnk");
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Blockplan";

    /// <summary>Läuft gerade die installierte Kopie?</summary>
    public static bool IsInstalled => Updater.ExePath is { } p && string.Equals(Path.GetFullPath(p), Path.GetFullPath(Target), StringComparison.OrdinalIgnoreCase);

    /// <summary>Gibt es schon eine Installation (auch wenn gerade eine andere Datei läuft)?</summary>
    public static bool Exists => OperatingSystem.IsWindows() && File.Exists(Target);

    /// <summary>Installieren und die installierte Kopie starten. Gibt eine Fehlermeldung zurück oder null.</summary>
    [SupportedOSPlatform("windows")]
    public static string? Install(bool desktop)
    {
        var src = Updater.ExePath; if (src == null) return "Installieren geht nur mit der Blockplan.exe.";
        try
        {
            Directory.CreateDirectory(Dir);
            // läuft dort schon eine alte Version? Datei umbenennen geht auch während sie läuft
            if (File.Exists(Target)) { var old = Target + ".old"; try { if (File.Exists(old)) File.Delete(old); } catch { } try { File.Move(Target, old); } catch { } }
            File.Copy(src, Target, true);
            Shortcut(StartMenuLink, Target);
            if (desktop) Shortcut(DesktopLink, Target);
            Register();
            // installierte Kopie starten; sie räumt die heruntergeladene Datei weg
            Process.Start(new ProcessStartInfo(Target) { UseShellExecute = true, ArgumentList = { "--installed-from", src } });
            return null;
        }
        catch (Exception e) { McAssets.Log("Installieren: " + e); return e.Message; }
    }

    /// <summary>Verknüpfung (.lnk) über die Windows-Shell anlegen.</summary>
    static void Shortcut(string link, string target)
    {
        string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var ps = $"$s=(New-Object -ComObject WScript.Shell).CreateShortcut({Q(link)});$s.TargetPath={Q(target)};$s.WorkingDirectory={Q(Path.GetDirectoryName(target)!)};$s.IconLocation={Q(target + ",0")};$s.Description='Bauplaner für Minecraft';$s.Save()";
        var p = Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", ps }, CreateNoWindow = true, UseShellExecute = false });
        p?.WaitForExit(20000);
    }

    /// <summary>Eintrag unter Einstellungen → Apps (nur für diesen Benutzer).</summary>
    [SupportedOSPlatform("windows")]
    static void Register()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(UninstallKey);
        k.SetValue("DisplayName", "Blockplan");
        k.SetValue("DisplayVersion", Updater.CurrentText);
        k.SetValue("Publisher", "Blockplan");
        k.SetValue("DisplayIcon", Target + ",0");
        k.SetValue("InstallLocation", Dir);
        k.SetValue("UninstallString", "\"" + Target + "\" --uninstall");
        k.SetValue("NoModify", 1); k.SetValue("NoRepair", 1);
        try { k.SetValue("EstimatedSize", (int)(new FileInfo(Target).Length / 1024)); } catch { }
    }

    /// <summary>Nach einem Update die Version unter „Apps“ nachziehen.</summary>
    public static void RefreshVersion()
    {
        if (!OperatingSystem.IsWindows() || !IsInstalled) return;
        try { using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKey, true); k?.SetValue("DisplayVersion", Updater.CurrentText); } catch { }
    }

    /// <summary>Von der installierten Kopie aufgerufen: heruntergeladene Datei löschen (sobald sie beendet ist).</summary>
    public static void RemoveDownloaded(string path)
    {
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(Target), StringComparison.OrdinalIgnoreCase)) return;
        new Thread(() =>
        {
            for (int i = 0; i < 40; i++) { try { if (!File.Exists(path)) return; File.Delete(path); return; } catch { Thread.Sleep(500); } }
        }) { IsBackground = true }.Start();
    }

    /// <summary>Deinstallieren (aus „Apps“): Verknüpfungen, Eintrag und Programmordner entfernen. Projekte bleiben.</summary>
    [SupportedOSPlatform("windows")]
    public static void Uninstall()
    {
        try { File.Delete(StartMenuLink); } catch { }
        try { File.Delete(DesktopLink); } catch { }
        try { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        // laufende Datei kann sich nicht selbst löschen: kurz warten lassen und dann den Ordner entfernen
        var cmd = $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{Dir}\"";
        Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
    }
}
