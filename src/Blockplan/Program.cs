using Avalonia;
using System;

namespace Blockplan;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        var de = new System.Globalization.CultureInfo("de-DE");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = de; System.Globalization.CultureInfo.CurrentCulture = de;
        if (args.Length >= 2 && args[0] == "--selftest") return Core.SelfTest.Run(args[1]);
        if (args.Length >= 1 && args[0] == "--modeltest") return Core.ModelTest.Run();
        if (args.Length >= 1 && args[0] == "--checkupdate")
        {
            var r = Core.Updater.CheckAsync().GetAwaiter().GetResult();
            Console.WriteLine($"aktuell {Core.Updater.CurrentText} → " + (r == null ? "kein Update" : $"Update {r.Version}, {r.Size} Bytes, {r.Url}"));
            return 0;
        }
        if (args.Length >= 3 && args[0] == "--export")
        {   // Projekt (.json) als .litematic oder .schem speichern (zum Testen)
            var p = Core.Project.FromJson(System.IO.File.ReadAllText(args[1])); long n;
            var b = args[2].EndsWith(".schem") ? Core.SchemExport.Build(p, 0, 0, 0, p.W - 1, p.H - 1, p.D - 1, null, 4440, out n) : Core.SchemExport.BuildLitematic(p, 0, 0, 0, p.W - 1, p.H - 1, p.D - 1, null, 4440, out n);
            System.IO.File.WriteAllBytes(args[2], b); Console.WriteLine($"{n} Blöcke, {p.W}x{p.H}x{p.D}"); return 0;
        }
        if (args.Length >= 2 && args[0] == "--import-schem")
        {   // Schematic ohne Dialog vollständig übernehmen (zum Testen)
            using var st = new Core.Store();
            foreach (var f in args[1..]) { var sc = Core.SchematicReader.Load(f); var p = Core.SchematicReader.ToProject(sc, sc.X0, sc.Y0, sc.Z0, sc.X1, sc.Y1, sc.Z1, false, false, 0); st.Save(p); Console.WriteLine($"importiert: {p.Name}, {sc.Banners.Count} Banner mit Mustern: {string.Join(" ; ", sc.Banners.Values)}"); }
            return 0;
        }
        if (args.Length >= 2 && args[0] == "--import")
        {   // Projekte aus dem Artefakt ohne Fenster in die Datenbank übernehmen
            using var st = new Core.Store();
            foreach (var f in args[1..]) { var p = Core.Project.FromJson(System.IO.File.ReadAllText(f)); st.Save(p); Console.WriteLine("importiert: " + p.Name); }
            return 0;
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // nur zum Testen ohne Grafikkarte (Software-OpenGL), unter Windows ohne Bedeutung
            .With(Environment.GetEnvironmentVariable("BLOCKPLAN_SOFTGL") == "1" ? new X11PlatformOptions { GlxRendererBlacklist = new System.Collections.Generic.List<string>() } : new X11PlatformOptions())
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTextWriter(Console.Error, Avalonia.Logging.LogEventLevel.Warning);
}
