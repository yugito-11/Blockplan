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
