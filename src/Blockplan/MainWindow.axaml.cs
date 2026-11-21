using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Blockplan.Core;
using Blockplan.Views;

namespace Blockplan;

public sealed class MatRow
{
    public required string Name { get; init; }
    public long Count { get; init; }
    public string CountText => Count.ToString("N0");
    public string StackText { get; init; } = "";
    public Bitmap? Icon { get; init; }
    /// <summary>Grundblock (für „Ersetzen“); 0 = Item ohne eigenen Block.</summary>
    public int Block { get; init; }
    public bool CanReplace => Replace != null;
    public System.Windows.Input.ICommand? Replace { get; init; }
}

/// <summary>Kleiner Befehl für Knöpfe in Listen.</summary>
public sealed class Cmd(Action run) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? p) => true;
    public void Execute(object? p) => run();
}

public partial class MainWindow : Window
{
    readonly Store store = new();
    Project? project;
    Atlas atlas = new(null);
    McAssets? mc;
    bool loadingUi;
    List<MatRow> matAll = new();

    public MainWindow()
    {
        InitializeComponent();
        Nav2D.Click += (_, _) => ShowTab(0); Nav3D.Click += (_, _) => ShowTab(1); NavMat.Click += (_, _) => ShowTab(2);
        BtnOpen.Click += async (_, _) => await OpenSchematic();
        BtnImport.Click += async (_, _) => await ImportArtifact();
        BtnJar.Click += async (_, _) => await PickJar();
        BtnCheckUpdate.Click += async (_, _) => await CheckUpdate(true);
        BtnUpdate.Click += (_, _) => { if (updateFile == null) return; try { store.Dispose(); Updater.ApplyAndRestart(updateFile); } catch (Exception ex) { UpdateText.Text = "Update fehlgeschlagen: " + ex.Message; } };
        BtnBrowserJar.Click += async (_, _) => await DownloadInBrowser();
        BtnDelete.Click += async (_, _) => await DeleteProject();
        BtnInstall.Click += async (_, _) =>
        {
            if (!OperatingSystem.IsWindows()) return;
            BtnInstall.IsEnabled = false; BtnInstall.Content = "Installiere …";
            var err = await Task.Run(() => Installer.Install(InstallDesktop.IsChecked == true));
            if (err != null) { BtnInstall.IsEnabled = true; BtnInstall.Content = "Jetzt installieren"; await Message("Installieren fehlgeschlagen", err); return; }
            store.Dispose(); Environment.Exit(0);   // die installierte Kopie läuft schon
        };
        BtnOpenInstalled.Click += async (_, _) =>
        {
            var err = Updater.ExePath is { } me ? Installer.Launch("--installed-from \"" + me + "\"") : "nur mit der Blockplan.exe";
            if (err != null) { await Message("Öffnen fehlgeschlagen", err); return; }
            store.Dispose(); Environment.Exit(0);
        };
        BtnSnow.Click += async (_, _) => await RemoveSnow();
        BtnFit.Click += (_, _) => { Plan.ResetView(); View3.ResetView(); };
        ProjSel.SelectionChanged += async (_, _) => { if (!loadingUi && ProjSel.SelectedItem is ProjectInfo pi && pi.Id != project?.Id) await OpenProject(pi.Id); };
        ModeTop.IsCheckedChanged += (_, _) => ApplyView(); ModeLayer.IsCheckedChanged += (_, _) => ApplyView();
        LayerSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) ApplyView(); };
        CutSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) ApplyView(); };
        LayerDown.Click += (_, _) => LayerSlider.Value = Math.Max(0, LayerSlider.Value - 1);
        LayerUp.Click += (_, _) => LayerSlider.Value = Math.Min(LayerSlider.Maximum, LayerSlider.Value + 1);
        ProjName.LostFocus += (_, _) => { if (project != null && !loadingUi && ProjName.Text is { Length: > 0 } t && t != project.Name) { project.Name = t; SaveProject(); RefreshList(); } };
        foreach (var n in new[] { OrX, OrY, OrZ }) n.ValueChanged += (_, _) => { if (project == null || loadingUi) return; project.OriginX = (int)(OrX.Value ?? 0); project.OriginY = (int)(OrY.Value ?? 64); project.OriginZ = (int)(OrZ.Value ?? 0); SaveProject(); };
        MatSearch.TextChanged += (_, _) => FilterMat();
        MatScope.SelectionChanged += (_, _) => { if (!matScopeBusy && MatPage.IsVisible) BuildMaterials(); };
        MatMode.SelectionChanged += (_, _) => { if (MatPage.IsVisible) BuildMaterials(); };
        BtnMatCopy.Click += async (_, _) => await CopyMaterials();
        BtnExport.Click += async (_, _) => await ExportSchem();
        BtnUndo.Click += async (_, _) => await UndoReplace();
        Plan.Hover += ShowHover; View3.Hover += ShowHover;
        FlyBtn.Click += (_, _) => { View3.SetFly(FlyBtn.IsChecked == true); View3.Focus(); };
        View3.FlyChanged += on => { FlyBtn.IsChecked = on; ShowTab(1); };
        SpeedSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { View3.Speed = (float)SpeedSlider.Value; SpeedVal.Text = $"{SpeedSlider.Value:0} Blöcke/s"; } };
        SpeedVal.Text = "8 Blöcke/s";
        MouseSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { View3.MouseSens = (float)MouseSlider.Value; MouseVal.Text = $"{MouseSlider.Value * 100:0} %"; if (!loadingUi) store.Set("mouse", MouseSlider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); } };
        MouseVal.Text = "100 %";
        View3.Status += s => { StatusText.Text = s; StatusBox.IsVisible = s.Length > 0 && View3.IsVisible; };
        AddHandler(DragDrop.DropEvent, OnDrop); DragDrop.SetAllowDrop(this, true);
        KeyDown += (_, e) =>
        {
            if (project != null && tab == 1 && StepBtn.IsChecked == true && e.Key is Avalonia.Input.Key.PageUp or Avalonia.Input.Key.PageDown)
            { StepBy(e.Key == Avalonia.Input.Key.PageUp ? 1 : -1); e.Handled = true; return; }
            if (project == null || !ModeLayer.IsChecked == true) return;
            if (e.Key == Avalonia.Input.Key.PageUp) LayerSlider.Value = Math.Min(LayerSlider.Maximum, LayerSlider.Value + 1);
            if (e.Key == Avalonia.Input.Key.PageDown) LayerSlider.Value = Math.Max(0, LayerSlider.Value - 1);
        };
        InitPlanning(); InitLive(); Init3DTools(); InitNavAndTimelapse(); InitBau();
        Opened += async (_, _) => await Startup();
        Closing += (_, _) => store.Dispose();
    }

    /* ---------- 3D-Werkzeuge: Bau-Anleitung, Schnitt, Nacht ---------- */
    long[] layerCount = Array.Empty<long>();
    DispatcherTimer? stepTimer, cutTimer;

    void Init3DTools()
    {
        StepBtn.Click += (_, _) => SetStepMode(StepBtn.IsChecked == true);
        StepFirst.Click += (_, _) => StepTo(FirstLayer());
        StepLast.Click += (_, _) => { StepPlay.IsChecked = false; StepTo(LastLayer()); };
        StepPrev.Click += (_, _) => StepBy(-1); StepNext.Click += (_, _) => StepBy(1);
        StepPlay.Click += (_, _) =>
        {
            stepTimer?.Stop();
            if (StepPlay.IsChecked != true) { StepPlay.Content = "▶ Abspielen"; return; }
            StepPlay.Content = "⏸ Pause";
            if ((int)CutSlider.Value >= LastLayer()) StepTo(FirstLayer());
            stepTimer = new DispatcherTimer(TimeSpan.FromSeconds(StepSpeed.Value), DispatcherPriority.Background, (_, _) =>
            {
                if ((int)CutSlider.Value >= LastLayer()) { StepPlay.IsChecked = false; StepPlay.Content = "▶ Abspielen"; stepTimer!.Stop(); return; }
                StepBy(1);
            });
            stepTimer.Start();
        };
        StepSpeed.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && stepTimer != null) stepTimer.Interval = TimeSpan.FromSeconds(StepSpeed.Value); };
        ToolTip.SetTip(StepSpeed, "Geschwindigkeit beim Abspielen");

        CutAxis.SelectionChanged += (_, _) =>
        {
            if (project == null) return;
            int ax = CutAxis.SelectedIndex; bool on = ax > 0;
            CutPos.IsVisible = CutPosRow.IsVisible = CutOther.IsVisible = on;
            if (on) { loadingUi = true; CutPos.Maximum = Math.Max(1, (ax == 1 ? project.W : project.D) - 1); CutPos.Value = CutPos.Maximum / 2; loadingUi = false; }
            ApplyCut();
        };
        CutPos.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !loadingUi) { CutPosText(); cutTimer?.Stop(); cutTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(140), DispatcherPriority.Background, (_, _) => { cutTimer!.Stop(); ApplyCut(); }); cutTimer.Start(); } };
        CutOther.IsCheckedChanged += (_, _) => ApplyCut();

        NightBtn.Click += (_, _) =>
        {
            bool on = NightBtn.IsChecked == true; SpawnChk.IsVisible = on; LightInfo.Text = on ? "Berechne Licht …" : "";
            View3.SetNight(on, SpawnChk.IsChecked == true);
        };
        SpawnChk.IsCheckedChanged += (_, _) => { View3.ShowSpawns(SpawnChk.IsChecked == true); UpdateLightInfo(); };
        View3.LightReady += UpdateLightInfo;

        WalkBtn.Click += (_, _) => { View3.SetWalk(WalkBtn.IsChecked == true); View3.Focus(); };
        View3.WalkChanged += on =>
        {
            WalkBtn.IsChecked = on; if (on) { FlyBtn.IsChecked = false; ShowTab(1); }
            ViewHint.Text = on ? "Laufen: WASD = gehen · Maus ziehen = umsehen · Leertaste = springen · Strg = rennen · Umschalt = schleichen · an Leitern: W = hoch · G oder Esc = beenden"
                               : "Links ziehen = drehen · Rechts ziehen = verschieben · Mausrad = zoomen · WASD, Leertaste, C = bewegen · F = Flugmodus · G = Laufen · Hilfslinie anklicken = bearbeiten";
            if (!on) ShowHover("");
        };
        View3.WalkMoved += () => ShowHover("Du stehst bei " + View3.WalkPos);
        PhotoBtn.Click += async (_, _) => await TakePhoto();
        ShaderBtn.Click += (_, _) =>
        {
            bool on = ShaderBtn.IsChecked == true;
            View3.SetShaders(on); SunSlider.IsVisible = on; store.Set("shaders", on ? "1" : "0");
            if (on && !View3.ShadersAvailable) DispatcherTimer.RunOnce(() => { if (!View3.ShadersAvailable) { ShowHover("Deine Grafikkarte unterstützt die Shader leider nicht."); } }, TimeSpan.FromSeconds(1));
        };
        SunSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty) { View3.SunAngle = (float)SunSlider.Value; if (!loadingUi) store.Set("sun", SunSlider.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)); } };
    }

    async Task TakePhoto()
    {
        if (project == null) return;
        var w = new Window { Title = "Foto", Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var size = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, SelectedIndex = 1, ItemsSource = new[] { "Wie das Fenster (doppelt scharf)", "Full HD – 1920 × 1080", "4K – 3840 × 2160", "Quadratisch – 2048 × 2048" } };
        var time = new ComboBox { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, SelectedIndex = 1, ItemsSource = new[] { "Wie die Ansicht", "Mittag (Sonne von oben)", "Abendsonne", "Nacht (mit Fackellicht)" } };
        var fog = new CheckBox { Content = "Dunst in der Ferne", IsChecked = false };
        var guides = new CheckBox { Content = "Hilfslinien mit aufs Bild", IsChecked = false };
        var players = new CheckBox { Content = "Spieler mit aufs Bild", IsChecked = true };
        var ok = new Button { Content = "Aufnehmen …", Classes = { "accent" }, MinWidth = 110, IsDefault = true }; var cancel = new Button { Content = "Abbrechen", MinWidth = 90, IsCancel = true };
        bool go = false; ok.Click += (_, _) => { go = true; w.Close(); }; cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 10, Children =
            {
                new TextBlock { Text = "Foto vom Bau", FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = "Das Bild zeigt genau den Ausschnitt der 3D-Ansicht. Richte vorher die Kamera aus.", Classes = { "note" }, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Größe", Classes = { "note" } }, size,
                new TextBlock { Text = "Licht", Classes = { "note" } }, time,
                fog, guides, players,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, ok } },
            }
        };
        await w.ShowDialog(this);
        if (!go) return;
        double sc = RenderScaling;
        var (pw, ph) = size.SelectedIndex switch
        {
            0 => ((int)(View3.Bounds.Width * sc * 2), (int)(View3.Bounds.Height * sc * 2)),
            2 => (3840, 2160), 3 => (2048, 2048), _ => (1920, 1080),
        };
        var pt = time.SelectedIndex switch { 0 => View3D.PhotoTime.AsView, 2 => View3D.PhotoTime.Evening, 3 => View3D.PhotoTime.Night, _ => View3D.PhotoTime.Noon };
        if (pt == View3D.PhotoTime.Night && !View3.Night)
        {   // Licht erst berechnen
            NightBtn.IsChecked = true; SpawnChk.IsVisible = true; View3.SetNight(true, false); SpawnChk.IsChecked = false;
            var tcs = new TaskCompletionSource(); void Done() { tcs.TrySetResult(); } View3.LightReady += Done;
            Busy("Berechne Licht …"); await Task.WhenAny(tcs.Task, Task.Delay(60000)); View3.LightReady -= Done; await Task.Delay(800);
        }
        Busy("Nehme das Foto auf …");
        var shot = await View3.CapturePhoto(new View3D.PhotoOptions(pw, ph, pt, fog.IsChecked == true, guides.IsChecked == true, players.IsChecked == true));
        Busy(null);
        if (shot is not { } img) { await Message("Foto nicht möglich", "Die Grafikkarte konnte das Bild in dieser Größe nicht zeichnen. Versuch eine kleinere Größe."); return; }
        if (Environment.GetEnvironmentVariable("BLOCKPLAN_PHOTO") is { Length: > 0 } testPath) { await Task.Run(() => SavePng(testPath, img.rgba, img.w, img.h)); return; }   // zum Testen
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Foto speichern", SuggestedFileName = string.Join("_", project.Name.Split(Path.GetInvalidFileNameChars())) + ".png", DefaultExtension = "png",
            FileTypeChoices = new[] { new FilePickerFileType("PNG-Bild") { Patterns = new[] { "*.png" } } },
        });
        if (f == null || f.TryGetLocalPath() is not { } path) return;
        await Task.Run(() => SavePng(path, img.rgba, img.w, img.h));
        ShowHover($"Foto gespeichert: {Path.GetFileName(path)} ({img.w} × {img.h})");
    }

    internal static void SavePng(string path, byte[] rgba, int w, int h)
    {
        var info = new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bm = new SkiaSharp.SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bm.GetPixels(), rgba.Length);
        using var data = bm.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var fs = File.Create(path); data.SaveTo(fs);
    }

    /* ---------- Bauen im Spiel (Mod im eigenen Spiel) ---------- */
    bool bauBusy;

    void InitBau()
    {
        BtnBauSend.Click += async (_, _) => await SendPlanToGame();
        BauShow.IsCheckedChanged += (_, _) => { if (!bauBusy) _ = SendBauView(); };
        BauLayer.IsCheckedChanged += (_, _) => { if (!bauBusy) _ = SendBauView(); };
        // Schicht im 2D-Plan wechseln = Ebene im Spiel (wenn „Nur eine Ebene“ an ist)
        LayerSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !bauBusy && BauLayer.IsChecked == true && live?.Bau != null) _ = SendBauView(); };
    }

    async Task SendBauView()
    {
        var lv = live; var p = project; if (lv == null || p == null) return;
        int layer = (int)LayerSlider.Value - bauOffsetY;
        await lv.SendView(BauShow.IsChecked == true, BauLayer.IsChecked == true, Math.Max(0, layer));
    }

    int bauOffsetY;   // Höhe des gesendeten Teils über der Projekt-Unterkante

    async Task SendPlanToGame()
    {
        var p = project; var lv = live; if (p == null || lv == null) return;
        var parts = BauScope.SelectedIndex > 0 && BauScope.SelectedIndex - 1 < bauScopes.Count ? BauScopeParts(bauScopes[BauScope.SelectedIndex - 1]) : null;
        BtnBauSend.IsEnabled = false; BtnBauSend.Content = "Sende …";
        string json = await Task.Run(() =>
        {
            if (parts == null || parts.Count == 0) { bauOffsetY = 0; return GameJson(p, 0, 0, 0, p.W - 1, p.H - 1, p.D - 1, null); }
            int x0 = parts.Min(t => t.Box[0]), y0 = parts.Min(t => t.Box[1]), z0 = parts.Min(t => t.Box[2]), x1 = parts.Max(t => t.Box[3]), y1 = parts.Max(t => t.Box[4]), z1 = parts.Max(t => t.Box[5]);
            bauOffsetY = y0;
            return GameJson(p, x0, y0, z0, x1, y1, z1, (x, y, z) => parts.Any(t => InPart(t, x, y, z)));
        });
        var err = await lv.SendPlan(json);
        BtnBauSend.IsEnabled = true; BtnBauSend.Content = "Plan ins Spiel";
        if (err != null) { await Message("Plan nicht gesendet", err); return; }
        await SendBauView();
    }

    /// <summary>Teil des Projekts als Projekt-JSON mit Welt-Startecke (für die Mod).</summary>
    static string GameJson(Project p, int x0, int y0, int z0, int x1, int y1, int z1, Func<int, int, int, bool>? keep)
    {
        int W = x1 - x0 + 1, H = y1 - y0 + 1, D = z1 - z0 + 1;
        var sub = new Project { Name = p.Name, W = W, H = H, D = D, Cells = new ushort[(long)W * H * D], OriginX = p.OriginX + x0, OriginY = p.OriginY + y0, OriginZ = p.OriginZ + z0 };
        for (int y = 0; y < H; y++) for (int z = 0; z < D; z++) for (int x = 0; x < W; x++)
        {
            int v = p.At(x0 + x, y0 + y, z0 + z); if (v == 0 || (keep != null && !keep(x0 + x, y0 + y, z0 + z))) continue;
            sub.Cells[sub.Index(x, y, z)] = (ushort)v;
        }
        return sub.ToJson();
    }

    List<string> bauScopes = new();
    void FillBauScopes()
    {
        var keys = new List<string>(); var labels = new List<string> { "Ganzes Projekt" };
        if (plan != null)
        {
            foreach (var st in plan.Stages) { keys.Add("s:" + st.Id); labels.Add("Etappe: " + st.Name); }
            foreach (var t in plan.Parts.Where(x => !x.Base)) { keys.Add("p:" + t.Id); labels.Add("Bauplan: " + plan.Label(t)); }
        }
        bauScopes = keys; BauScope.ItemsSource = labels; BauScope.SelectedIndex = 0;
    }

    List<Part>? BauScopeParts(string key)
    {
        if (plan == null) return null; var parts = plan.Parts;
        if (key.StartsWith("s:") && plan.Stages.FirstOrDefault(x => x.Id == key[2..]) is { } st) { var ids = st.Parts.ToHashSet(); return parts.Where(t => ids.Contains(t.Id)).ToList(); }
        if (key.StartsWith("p:")) return parts.Where(x => x.Id == key[2..]).ToList();
        return null;
    }

    void UpdateBauUi()
    {
        var b = live?.Bau; var p = project;
        if (b == null) { BauText.Text = ""; return; }
        bauBusy = true;
        BauShow.IsChecked = b.Show; BauLayer.IsChecked = b.LayerMode;
        if (b.LayerMode && p != null) { int ly = b.Layer + bauOffsetY; if ((int)LayerSlider.Value != ly && ly < p.H) LayerSlider.Value = ly; }   // Ebene im Spiel → 2D-Plan
        bauBusy = false;
        long inv = b.Inventory.Values.Sum(), lag = b.Storage.Values.Sum();
        BauText.Text = b.Plan.Length == 0 ? "Im Spiel ist noch kein Plan geladen."
            : $"Im Spiel: „{b.Plan}“ · {(b.LayerMode ? $"Ebene Y {(p?.OriginY ?? 0) + b.Layer + bauOffsetY}" : "ganzer Plan")}\n"
              + (b.Scanned ? $"{b.Placed:N0} von {b.Total:N0} gesetzt ({(double)b.Placed / Math.Max(1, b.Total):P0})" : "zählt …")
              + $"\n{b.Chests} Lagerkiste{(b.Chests == 1 ? "" : "n")} ({lag:N0} Items) · Inventar {inv:N0} Items";
        if (MatPage.IsVisible) BuildMaterials();
    }

    void UpdateLightInfo()
    {
        if (!View3.Night) { LightInfo.Text = ""; return; }
        int n = View3.SpawnCount;
        LightInfo.Text = SpawnChk.IsChecked != true ? "" : n == 0 ? "Überall hell genug ✓" : $"{n:N0}{(n >= 200_000 ? "+" : "")} dunkle Stellen";
        ToolTip.SetTip(LightInfo, n == 0 ? "Keine Stelle im Bau, an der nachts Monster erscheinen können." : "An den roten Kreuzen ist es so dunkel (Blocklicht 0), dass dort nachts Monster erscheinen können. Fackeln oder Laternen helfen.");
    }

    void CutPosText()
    {
        if (project == null) return; int ax = CutAxis.SelectedIndex, pos = (int)CutPos.Value;
        CutPosVal.Text = ax == 1 ? $"X {project.OriginX + pos}" : ax == 2 ? $"Z {project.OriginZ + pos}" : "";
    }

    void ApplyCut()
    {
        if (project == null) return;
        CutPosText();
        View3.SetClip(CutAxis.SelectedIndex, (int)CutPos.Value, CutOther.IsChecked == true);
    }

    void ComputeLayerCounts()
    {
        var p = project; if (p == null) { layerCount = Array.Empty<long>(); return; }
        long WD = (long)p.W * p.D; var lc = new long[p.H];
        for (int y = 0; y < p.H; y++) { long n = 0, b = y * WD; for (long i = 0; i < WD; i++) if (p.Cells[b + i] != 0) n++; lc[y] = n; }
        layerCount = lc;
    }
    int FirstLayer() { for (int y = 0; y < layerCount.Length; y++) if (layerCount[y] > 0) return y; return 0; }
    int LastLayer() { for (int y = layerCount.Length - 1; y >= 0; y--) if (layerCount[y] > 0) return y; return 0; }

    void SetStepMode(bool on)
    {
        if (project == null) { StepBtn.IsChecked = false; return; }
        StepBar.IsVisible = on && tab == 1;
        if (!on) { stepTimer?.Stop(); StepPlay.IsChecked = false; StepPlay.Content = "▶ Abspielen"; CutSlider.Value = CutSlider.Maximum; ApplyView(); return; }
        if (layerCount.Length != project.H) ComputeLayerCounts();
        StepTo(FirstLayer());
    }

    void StepBy(int d)
    {
        if (project == null || layerCount.Length == 0) return;
        int y = (int)CutSlider.Value + d;
        while (y > 0 && y < layerCount.Length - 1 && layerCount[y] == 0) y += d;   // leere Ebenen überspringen
        StepTo(Math.Clamp(y, FirstLayer(), LastLayer()));
    }

    void StepTo(int y)
    {
        CutSlider.Value = y;
        if (!loadingUi) LayerSlider.Value = y;   // 2D-Plan zeigt dieselbe Ebene
        ApplyView();
    }

    void UpdateStepUi()
    {
        var p = project; if (p == null) return;
        if (layerCount.Length != p.H) ComputeLayerCounts();
        int y = (int)CutSlider.Value, first = FirstLayer(), last = LastLayer();
        int idx = 0, total = 0; for (int k = 0; k < layerCount.Length; k++) if (layerCount[k] > 0) { total++; if (k <= y) idx++; }
        StepText.Text = $"Ebene {idx} von {total} · Y {p.OriginY + y} · {(y < layerCount.Length ? layerCount[y] : 0):N0} Blöcke";
        StepPrev.IsEnabled = StepFirst.IsEnabled = y > first; StepNext.IsEnabled = y < last;
        // Material dieser Ebene (als Items)
        var st = new Dictionary<int, long>(); long WD = (long)p.W * p.D, b = y * WD;
        if (y < p.H) for (long i = 0; i < WD; i++) { int v = p.Cells[b + i]; if (v != 0) st[v] = st.GetValueOrDefault(v) + 1; }
        var from = new Dictionary<string, int>(); foreach (var v in st.Keys) foreach (var (it, _) in Items.Of(v)) from.TryAdd(it, Blocks.BaseOf(v));
        StepMat.Children.Clear();
        var items = Items.FromStates(st).OrderByDescending(kv => kv.Value).ToList();
        foreach (var (it, n) in items.Take(14))
        {
            int src = from.GetValueOrDefault(it); var img = new Image { Width = 18, Height = 18, Source = MatIcon(Items.BlockFor(it, src)) };
            RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.None);
            var chip = new Border { Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 2), Margin = new Thickness(0, 0, 6, 4),
                Child = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 5, Children = { img, new TextBlock { Text = $"{n:N0} {ItemName(it, src)}", Foreground = Brushes.White, FontSize = 11.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } } };
            ToolTip.SetTip(chip, Items.StackText(it, n) is { Length: > 0 } stx ? stx : $"{n} Stück");
            StepMat.Children.Add(chip);
        }
        if (items.Count > 14) StepMat.Children.Add(new TextBlock { Text = $"+ {items.Count - 14} weitere", Foreground = Brushes.White, FontSize = 11.5, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center });
    }

    void ShowHover(string s) { HoverText.Text = s; HoverBox.IsVisible = s.Length > 0; }

    int tab;
    void ShowTab(int t)
    {
        tab = t;
        Nav2D.IsChecked = t == 0; Nav3D.IsChecked = t == 1; NavMat.IsChecked = t == 2; NavParts.IsChecked = t == 3; NavStages.IsChecked = t == 4; NavTodos.IsChecked = t == 5;
        Plan.IsVisible = t == 0; View3.IsVisible = t == 1; MatPage.IsVisible = t == 2; PartsHost.IsVisible = t == 3; StagesHost.IsVisible = t == 4; TodosHost.IsVisible = t == 5;
        HoverBox.IsVisible = false; MeasureBox.IsVisible = t == 0 && MeasureText.Text?.Length > 0;
        Bar3D.IsVisible = t == 1; if (t == 1) Dispatcher.UIThread.Post(() => View3.Focus());
        TimeBar.IsVisible = t == 1 && tlOpen;
        StepBar.IsVisible = t == 1 && StepBtn.IsChecked == true; CutCard.IsVisible = t == 1;
        StatusBox.IsVisible = t == 1 && StatusText.Text?.Length > 0;
        ModeRow.IsVisible = t == 0; LayerRow.IsVisible = t == 0 && ModeLayer.IsChecked == true;
        ToolsCard.IsVisible = t == 0; GuidesCard.IsVisible = t is 0 or 1; LiveCard.IsVisible = t is 0 or 1;
        ViewHint.Text = t switch
        {
            0 => "Ziehen = verschieben · Mausrad = zoomen · rechte Maustaste ziehen = messen · Bild auf/ab = Schicht wechseln",
            1 => View3.Fly
                ? "Flugmodus: WASD = fliegen · Leertaste/C = hoch/runter · Maus ziehen = umsehen · Umschalt = schneller · F oder Esc = beenden"
                : "Links ziehen = drehen · Rechts ziehen = verschieben · Mausrad = zoomen · WASD, Leertaste, C = bewegen · F = Flugmodus · G = Laufen · Hilfslinie anklicken = bearbeiten",
            2 => "Alle Blöcke bis zur eingestellten Höhe, sortiert nach Menge.",
            3 => "Baupläne sind Teile deines Baus. Neue erstellst du im 2D-Plan mit „Bauplan aus Bereich“.",
            4 => "Etappen fassen Baupläne zu Bauabschnitten zusammen, mit Material je Abschnitt.",
            _ => "Aufgaben fürs Team, auf Wunsch mit Bezug zu einer Etappe oder einem Bauplan.",
        };
        if (t == 2) { FillMatScopes(); BuildMaterials(); }
        if (t == 3) partsPage.Refresh(); if (t == 4) stagesPage.Refresh(); if (t == 5) todosPage.Refresh();
    }

    /* ---------- Baupläne, Etappen, To-dos, Werkzeuge, Hilfslinien ---------- */
    PlanData? plan;
    PartsPage partsPage = null!; StagesPage stagesPage = null!; TodosPage todosPage = null!;
    DispatcherTimer? saveTimer;

    /// <summary>Speichern gesammelt nach kurzer Pause (große Projekte brauchen dafür etwas Zeit).</summary>
    void ScheduleSave()
    {
        UpdateCounts(); View3.SetGuides(plan, Guides3DChk.IsChecked == true);
        saveTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) => { saveTimer!.Stop(); SaveProject(); });
        saveTimer.Stop(); saveTimer.Start();
    }

    void UpdateCounts()
    {
        var ss = plan?.Stages ?? new(); var td = plan?.Todos ?? new();
        NavStages.Content = ss.Count > 0 ? $"⟋  Etappen  ({ss.Count(x => x.Done)}/{ss.Count})" : "⟋  Etappen";
        int open = td.Count(x => !x.Done); NavTodos.Content = open > 0 ? $"✓  Team-To-dos  ({open})" : "✓  Team-To-dos";
        int np = plan?.Parts.Count(x => !x.Base) ?? 0; NavParts.Content = np > 0 ? $"◫  Baupläne  ({np})" : "◫  Baupläne";
    }

    /* ---------- Live-Server ---------- */
    LiveClient? live; DateTime lastPlayersUi, lastProgress; bool progressBusy;

    void InitLive()
    {
        LiveLink.Text = store.Get("liveLink") ?? "";
        BtnLive.Click += (_, _) => ConnectLive();
        BtnLiveStop.Click += (_, _) => { recorder?.Flush(); recorder = null; live?.Stop(); live = null; Plan.Live = null; View3.SetLive(null, false, false); Plan.LiveReset(); UpdateLiveUi(); };
        LiveProgChk.IsCheckedChanged += (_, _) => ApplyLiveView();
        LiveSurrChk.IsCheckedChanged += (_, _) => ApplyLiveView();
        Guides3DChk.IsCheckedChanged += (_, _) => View3.SetGuides(plan, Guides3DChk.IsChecked == true);
        Closing += (_, _) => { lock (recLock) recorder?.Flush(); live?.Dispose(); };
    }

    void ConnectLive()
    {
        if (project == null) return;
        var link = LiveLink.Text?.Trim() ?? "";
        store.Set("liveLink", link);
        if (live == null)
        {
            live = new LiveClient();
            live.StatusChanged += () => Dispatcher.UIThread.Post(UpdateLiveUi);
            live.BauChanged += () => Dispatcher.UIThread.Post(UpdateBauUi);
            live.PlayersChanged += () => Dispatcher.UIThread.Post(() =>
            {
                Plan.InvalidateVisual(); View3.SetPlayers();
                if ((DateTime.Now - lastPlayersUi).TotalMilliseconds > 700) { lastPlayersUi = DateTime.Now; RenderLivePlayers(); }
            });
            live.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                if (live == null) return;
                var (all, cells, chunks) = live.TakeChanges();
                if (tlOpen) { if (all) Plan.LiveReset(); Plan.LiveChanged(chunks); }
                else { View3.LiveChanged(all, cells, chunks); Plan.LiveChanged(chunks); if (all) Plan.LiveReset(); }
                UpdateProgress(); Record(all, cells);
            });
        }
        live.Connect(link, project);
        recorder?.Flush(); recorder = null;
        var rp = project; Task.Run(() => { var r = new TimelapseRecorder(store, rp); Dispatcher.UIThread.Post(() => { if (project == rp) { recorder = r; UpdateTlInfo(); } }); });
        Plan.Live = live; ApplyLiveView(); UpdateLiveUi();
    }

    void ApplyLiveView()
    {
        Plan.ShowProgress = LiveProgChk.IsChecked == true; Plan.ShowSurroundings = LiveSurrChk.IsChecked == true; Plan.LiveReset();
        View3.SetLive(live, LiveProgChk.IsChecked == true, LiveSurrChk.IsChecked == true);
        UpdateProgress(true);
    }

    void UpdateLiveUi()
    {
        var st = live?.State ?? "aus";
        LiveStatus.Text = st switch
        {
            "live" => $"Live · {live!.Players.Count} Spieler online",
            "aus" => "Nicht verbunden.",
            _ => live!.Message,
        };
        LiveDot.Background = new SolidColorBrush(st switch { "live" => Color.Parse("#5fc27e"), "fehler" => Color.Parse("#ec6f66"), "aus" => Colors.Gray, _ => Color.Parse("#dc9a52") });
        BtnLiveStop.IsEnabled = live != null && st != "aus"; BtnLive.Content = live != null && st != "aus" ? "Neu verbinden" : "Verbinden";
        NavBox.IsVisible = live != null && live.Known;
        BauBox.IsVisible = live != null && st != "aus";
        RenderLivePlayers();
    }

    void RenderLivePlayers()
    {
        LivePlayers.Children.Clear();
        var lv = live; if (lv == null || project == null || lv.State == "aus") return;
        LivePlayers.Children.Add(new TextBlock { Text = $"Spieler online · {lv.Players.Count}", Classes = { "note" }, FontWeight = FontWeight.SemiBold });
        foreach (var q in lv.Players)
        {
            bool same = q.Dim == lv.Dim; int gx = (int)Math.Floor(q.X) - project.OriginX, gy = (int)Math.Floor(q.Y) - project.OriginY, gz = (int)Math.Floor(q.Z) - project.OriginZ;
            bool inside = same && gx >= 0 && gz >= 0 && gy >= 0 && gx < project.W && gz < project.D && gy < project.H;
            string where = !same ? q.Dim.Replace("minecraft:", "") : inside ? $"in der Baustelle · Ebene {gy + 1}" : $"{Math.Round(Math.Sqrt((gx - project.W / 2.0) * (gx - project.W / 2.0) + (gz - project.D / 2.0) * (gz - project.D / 2.0)))} Blöcke entfernt";
            uint col = View3D.PlayerColor(q.Name);
            var row = new DockPanel { Margin = new Thickness(0, 2) };
            var show = new Button { Content = "Zeigen", Padding = new Thickness(8, 2), FontSize = 12 }; DockPanel.SetDock(show, Dock.Right);
            var qq = q; show.Click += (_, _) => { if (tab == 1) View3.LookAt(qq.X - project.OriginX - project.W / 2.0, qq.Y - project.OriginY, qq.Z - project.OriginZ - project.D / 2.0); else { ShowTab(0); Plan.CenterOn(qq.X - project.OriginX, qq.Z - project.OriginZ); } };
            var dot = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb((byte)(col >> 16), (byte)(col >> 8), (byte)col)), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(show); row.Children.Add(dot);
            row.Children.Add(new StackPanel { Children = { new TextBlock { Text = q.Name, FontWeight = FontWeight.SemiBold }, new TextBlock { Text = $"X {Math.Floor(q.X)}  Y {Math.Floor(q.Y)}  Z {Math.Floor(q.Z)} · {where}", Classes = { "note" }, TextWrapping = TextWrapping.Wrap } } });
            LivePlayers.Children.Add(row);
        }
    }

    /// <summary>Baufortschritt zählen (im Hintergrund, höchstens alle 2 Sekunden).</summary>
    void UpdateProgress(bool force = false)
    {
        var lv = live; var p = project;
        if (lv == null || p == null || LiveProgChk.IsChecked != true) { LiveProgress.Text = ""; return; }
        if (progressBusy || (!force && (DateTime.Now - lastProgress).TotalSeconds < 2)) return;
        progressBusy = true; lastProgress = DateTime.Now; int cut = (int)CutSlider.Value;
        Task.Run(() =>
        {
            long ok = 0, bad = 0, miss = 0, unk = 0; long WD = (long)p.W * p.D; int H = Math.Min(cut, p.H - 1) + 1;
            Parallel.For(0, H, () => (0L, 0L, 0L, 0L), (y, _, acc) =>
            {
                for (int z = 0; z < p.D; z++) for (int x = 0; x < p.W; x++)
                {
                    int v = p.Cells[y * WD + (long)z * p.W + x]; if (v == 0) continue;
                    switch (lv.Status(p, x, y, z, v)) { case 1: acc.Item1++; break; case 2: acc.Item2++; break; case 3: acc.Item3++; break; default: acc.Item4++; break; }
                }
                return acc;
            }, acc => { Interlocked.Add(ref ok, acc.Item1); Interlocked.Add(ref bad, acc.Item2); Interlocked.Add(ref miss, acc.Item3); Interlocked.Add(ref unk, acc.Item4); });
            long n = ok + bad + miss + unk;
            Dispatcher.UIThread.Post(() =>
            {
                progressBusy = false;
                LiveProgress.Text = n == 0 ? "" : $"{ok:N0} von {n:N0} gesetzt ({(double)ok / n:P0})" + (bad > 0 ? $" · {bad:N0} falsch" : "") + (unk > 0 ? $" · {unk:N0} noch nicht gelesen" : "");
            });
        });
    }

    /* ---------- Was fehlt noch? (Navigator) ---------- */
    List<MissingSpot> navSpots = new(); int navIdx;

    void InitNavAndTimelapse()
    {
        BtnNavFind.Click += async (_, _) => await FindMissing();
        BtnNavPrev.Click += (_, _) => { if (navSpots.Count > 0) { navIdx = (navIdx - 1 + navSpots.Count) % navSpots.Count; ShowSpot(); } };
        BtnNavNext.Click += (_, _) => { if (navSpots.Count > 0) { navIdx = (navIdx + 1) % navSpots.Count; ShowSpot(); } };
        BtnNavShow.Click += (_, _) => ShowSpot();
        BtnTimelapse.Click += async (_, _) => await OpenTimelapse();
        BtnTlClose.Click += (_, _) => CloseTimelapse();
        TlSlider.PropertyChanged += (_, e) => { if (e.Property == Slider.ValueProperty && !tlSliderBusy) TlShow(); };
        TlPlay.Click += (_, _) => TlTogglePlay();
        BtnTlVideo.Click += async (_, _) => await TlVideo();
    }

    async Task FindMissing()
    {
        var p = project; var lv = live; if (p == null || lv == null || !lv.Known) return;
        BtnNavFind.IsEnabled = false; BtnNavFind.Content = "Suche …"; int cut = (int)CutSlider.Value;
        var (spots, miss, wrong, cut2) = await Task.Run(() => Navigator.Find(p, lv, cut));
        BtnNavFind.IsEnabled = true; BtnNavFind.Content = "Neu suchen";
        navSpots = spots; navIdx = 0; NavRow.IsVisible = spots.Count > 0;
        if (spots.Count == 0) { NavText.Text = "Alles gebaut – nichts fehlt und nichts ist falsch. 🎉"; return; }
        NavText.Tag = $"Insgesamt {miss:N0} fehlend{(wrong > 0 ? $", {wrong:N0} falsch" : "")} an {spots.Count:N0}{(cut2 ? "+" : "")} Stellen. Falsche Blöcke kommen zuerst, dann von unten nach oben.";
        ShowSpot();
    }

    void ShowSpot()
    {
        var p = project; if (p == null || navSpots.Count == 0) return;
        var sp = navSpots[navIdx];
        NavIdx.Text = $"{navIdx + 1} / {navSpots.Count}";
        string what = $"{sp.MainCount:N0} × {Blocks.DisplayName(sp.MainBlock, true)}{(sp.Count > sp.MainCount ? $" (+{sp.Count - sp.MainCount:N0} andere)" : "")}";
        NavText.Text = (sp.Wrong > 0 ? $"Falsch: {sp.Wrong:N0} Block{(sp.Wrong == 1 ? "" : "e")}" + (sp.Missing > 0 ? $", fehlt: {sp.Missing:N0}" : "") : "Fehlt") + $" – {what}\nbei X {p.OriginX + sp.X}  Y {p.OriginY + sp.Y}  Z {p.OriginZ + sp.Z}\n" + (NavText.Tag as string ?? "");
        if (tab == 1) View3.LookAt(sp.X - p.W / 2.0 + .5, sp.Y, sp.Z - p.D / 2.0 + .5);
        else
        {
            if (tab != 0) ShowTab(0);
            if (ModeLayer.IsChecked == true) LayerSlider.Value = sp.MinY;
            Plan.CenterOn(sp.X, sp.Z);
        }
    }

    /* ---------- Zeitraffer ---------- */
    TimelapseRecorder? recorder; readonly object recLock = new();
    Timeline? timeline; bool[]? tlState; int tlPos; long tlT; bool tlOpen, tlSliderBusy; DispatcherTimer? tlTimer;

    void Record(bool all, List<(int x, int y, int z)> cells)
    {
        var rec = recorder; var lv = live; if (rec == null || lv == null || !lv.Known) return;
        Task.Run(() => { lock (recLock) rec.Update(lv, all ? null : cells); Dispatcher.UIThread.Post(UpdateTlInfo); });
    }

    void UpdateTlInfo()
    {
        long n = recorder?.Recorded ?? 0;
        if (recorder != null) TlInfo.Text = n > 0 ? $"Aufnahme läuft · {n:N0} Änderungen gespeichert" : "Aufnahme läuft – sobald gebaut wird, entsteht der Zeitraffer.";
        BtnTimelapse.IsEnabled = n > 0 || (timelineCount > 0);
    }
    long timelineCount;

    void LoadTimelineInfo()
    {
        var p = project; if (p == null) return;
        Task.Run(() => { var tl = Timeline.Load(store, p); Dispatcher.UIThread.Post(() =>
        {
            if (project != p) return; timelineCount = tl.Events.Length;
            if (recorder == null) TlInfo.Text = tl.Events.Length > 0 ? $"{tl.Events.Length:N0} Änderungen vom {DateTimeOffset.FromUnixTimeMilliseconds(tl.Start).LocalDateTime:d} bis {DateTimeOffset.FromUnixTimeMilliseconds(tl.End).LocalDateTime:d} aufgezeichnet." : "Solange Live verbunden ist, merkt sich Blockplan, wann welcher Block gesetzt wurde.";
            UpdateTlInfo();
        }); });
    }

    async Task OpenTimelapse()
    {
        var p = project; if (p == null) return;
        lock (recLock) recorder?.Flush();
        Busy("Lade den Bauverlauf …");
        var tl = await Task.Run(() => Timeline.Load(store, p));
        Busy(null);
        if (tl.Events.Length == 0) { await Message("Noch kein Zeitraffer", "Es wurde noch nichts aufgezeichnet. Verbinde dich live mit dem Server, während gebaut wird."); return; }
        timeline = tl; tlOpen = true; ShowTab(1); TimeBar.IsVisible = true; StepBtn.IsChecked = false; SetStepMode(false);
        tlSliderBusy = true; TlSlider.Value = 0; tlSliderBusy = false;
        tlState = new bool[tl.Cells]; tlPos = 0; tlT = long.MinValue;
        TlShow();
    }

    void CloseTimelapse()
    {
        tlTimer?.Stop(); TlPlay.IsChecked = false; TlPlay.Content = "▶ Abspielen";
        tlOpen = false; TimeBar.IsVisible = false; timeline = null; tlState = null;
        View3.SetHidden(null);
    }

    long TlTimeAt(double frac) { var tl = timeline!; return tl.Start + (long)((tl.End - tl.Start) * Math.Clamp(frac, 0, 1)); }

    /// <summary>Zustand zum Zeitpunkt des Schiebers zeigen.</summary>
    void TlShow()
    {
        var tl = timeline; var p = project; if (tl == null || p == null || tlState == null) return;
        long t = TlTimeAt(TlSlider.Value / TlSlider.Maximum);
        if (t < tlT) { tlState = new bool[tl.Cells]; tlPos = 0; }   // zurückspulen: von vorn
        tlPos = tl.Advance(tlState, tlPos, t); tlT = t;
        var st = (bool[])tlState.Clone();
        View3.SetHidden(i => !st[i]);
        long built = Timeline.PlacedCount(st), total = p.Cells.LongCount(c => c != 0);
        TlText.Text = $"{DateTimeOffset.FromUnixTimeMilliseconds(t).LocalDateTime:dd.MM.yyyy HH:mm} · {built:N0} von {total:N0} gebaut ({(double)built / Math.Max(1, total):P0})";
    }

    void TlTogglePlay()
    {
        tlTimer?.Stop();
        if (TlPlay.IsChecked != true) { TlPlay.Content = "▶ Abspielen"; return; }
        TlPlay.Content = "⏸ Pause";
        if (TlSlider.Value >= TlSlider.Maximum) TlSlider.Value = 0;
        double secs = TlDur.SelectedIndex switch { 0 => 10, 2 => 60, _ => 20 }; var start = DateTime.Now; double from = TlSlider.Value;
        tlTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) =>
        {
            double v = from + (DateTime.Now - start).TotalSeconds / secs * TlSlider.Maximum;
            if (v >= TlSlider.Maximum) { TlSlider.Value = TlSlider.Maximum; TlPlay.IsChecked = false; TlPlay.Content = "▶ Abspielen"; tlTimer!.Stop(); return; }
            TlSlider.Value = v;
        });
        tlTimer.Start();
    }

    async Task TlVideo()
    {
        var tl = timeline; var p = project; if (tl == null || p == null) return;
        tlTimer?.Stop(); TlPlay.IsChecked = false; TlPlay.Content = "▶ Abspielen";
        string? path = Environment.GetEnvironmentVariable("BLOCKPLAN_VIDEO");   // zum Testen
        if (path == null)
        {
            var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Zeitraffer als Video speichern", SuggestedFileName = string.Join("_", (p.Name + " Zeitraffer").Split(Path.GetInvalidFileNameChars())) + ".avi", DefaultExtension = "avi",
                FileTypeChoices = new[] { new FilePickerFileType("Video (AVI)") { Patterns = new[] { "*.avi" } } },
            });
            if (f == null || f.TryGetLocalPath() is not { } pth) return;
            path = pth;
        }
        int fps = 12, frames = (TlDur.SelectedIndex switch { 0 => 10, 2 => 60, _ => 20 }) * fps;
        if (Environment.GetEnvironmentVariable("BLOCKPLAN_VIDEO_FRAMES") is { } vf && int.TryParse(vf, out var nf)) frames = nf;
        const int vw = 1280, vh = 720;
        try
        {
            using var avi = new AviWriter(path, vw, vh, fps);
            var st = new bool[tl.Cells]; int pos = 0;
            for (int k = 0; k <= frames; k++)
            {
                Busy($"Video: Bild {k + 1} von {frames + 1} …");
                long t = TlTimeAt(k / (double)frames); pos = tl.Advance(st, pos, t);
                var snap = (bool[])st.Clone();
                var nb = View3.NextBuild(); View3.SetHidden(i => !snap[i]);
                await Task.WhenAny(nb, Task.Delay(30000));
                var shot = await View3.CapturePhoto(new View3D.PhotoOptions(vw, vh, View3D.PhotoTime.AsView, true, false, false));
                if (shot is not { } img) throw new Exception("Die Grafikkarte konnte das Bild nicht zeichnen.");
                long built = Timeline.PlacedCount(snap);
                var label = $"{DateTimeOffset.FromUnixTimeMilliseconds(t).LocalDateTime:dd.MM.yyyy HH:mm}  ·  {built:N0} Blöcke";
                var jpg = await Task.Run(() => FrameJpeg(img.rgba, img.w, img.h, label, p.Name));
                avi.AddFrame(jpg);
            }
            Busy(null);
            TlShow();
            ShowHover($"Video gespeichert: {Path.GetFileName(path)} ({frames + 1} Bilder)");
        }
        catch (Exception e) { Busy(null); await Message("Video fehlgeschlagen", e.Message); }
    }

    /// <summary>Bild mit Datum unten links als JPEG.</summary>
    static byte[] FrameJpeg(byte[] rgba, int w, int h, string label, string title)
    {
        var info = new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Rgba8888, SkiaSharp.SKAlphaType.Unpremul);
        using var bm = new SkiaSharp.SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(rgba, 0, bm.GetPixels(), rgba.Length);
        using (var cv = new SkiaSharp.SKCanvas(bm))
        using (var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, 26))
        using (var bg = new SkiaSharp.SKPaint { Color = new SkiaSharp.SKColor(13, 18, 15, 200), IsAntialias = true })
        using (var fg = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White, IsAntialias = true })
        {
            string text = title + "  ·  " + label;
            float tw = font.MeasureText(text);
            cv.DrawRoundRect(new SkiaSharp.SKRect(20, h - 66, 20 + tw + 32, h - 20), 10, 10, bg);
            cv.DrawText(text, 36, h - 33, font, fg);
        }
        using var data = bm.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 88);
        return data.ToArray();
    }

    void InitPlanning()
    {
        (Project?, PlanData?, Atlas) Ctx() => (project, plan, atlas);
        partsPage = new PartsPage(Ctx, ScheduleSave, ShowPartInPlan); PartsHost.Content = partsPage;
        stagesPage = new StagesPage(Ctx, ScheduleSave); StagesHost.Content = stagesPage;
        todosPage = new TodosPage(Ctx, ScheduleSave, () => store.Get("who") ?? "", n => store.Set("who", n), OpenRef); TodosHost.Content = todosPage;
        NavParts.Click += (_, _) => ShowTab(3); NavStages.Click += (_, _) => ShowTab(4); NavTodos.Click += (_, _) => ShowTab(5);
        ToolArea.Click += (_, _) => { ToolPoint.IsChecked = false; GDrawLine.IsChecked = false; Plan.Mode = ToolArea.IsChecked == true ? "area" : ""; ViewHint.Text = Plan.Mode == "area" ? "Ziehe im Plan einen Rahmen um den Teil, der ein Bauplan werden soll." : ViewHint.Text; };
        ToolPoint.Click += (_, _) => { ToolArea.IsChecked = false; GDrawLine.IsChecked = false; Plan.Mode = ToolPoint.IsChecked == true ? "point" : ""; ViewHint.Text = Plan.Mode == "point" ? "Klicke im Plan auf die Stelle für den Punkt." : ViewHint.Text; };
        ShowPartsChk.IsCheckedChanged += (_, _) => { Plan.ShowParts = ShowPartsChk.IsChecked == true; Plan.InvalidateVisual(); };
        Plan.MeasureChanged += t => { MeasureText.Text = t; MeasureBox.IsVisible = t.Length > 0 && tab == 0; };
        BtnMeasureClear.Click += (_, _) => Plan.ClearMeasure();
        Plan.PointPlaced += async (x, z) =>
        {
            if (plan == null) return;
            var name = await Ask("Punkt setzen", "Name des Punkts", $"Punkt {plan.Points.Count + 1}");
            ToolPoint.IsChecked = false; Plan.Mode = "";
            if (name == null) return;
            plan.AddPoint(name, x, z); ScheduleSave(); RenderPoints(); Plan.InvalidateVisual();
        };
        Plan.AreaSelected += async (x0, z0, x1, z1) => { ToolArea.IsChecked = false; Plan.Mode = ""; await CreatePart(x0, z0, x1, z1); };
        GAddEllipse.Click += (_, _) => AddGuide("ellipse"); GAddCircle.Click += (_, _) => AddGuide("circle");
        GAddRect.Click += (_, _) => AddGuide("rect"); GAddLine.Click += (_, _) => AddGuide("line");
        View3.GuideEdited += (g, final) =>
        {
            ScheduleSave(); Plan.InvalidateVisual();
            if (final) RenderGuides();
            ViewHint.Text = GuideHint(g);
        };
        View3.GuideDeleted += g => { plan?.Remove("guides", g); ScheduleSave(); RenderGuides(); Plan.InvalidateVisual(); };
        View3.GuideSelected += g => { RenderGuides(); if (g != null) ViewHint.Text = GuideHint(g); else ViewHint.Text = "Links ziehen = drehen · Rechts ziehen = verschieben · Mausrad = zoomen · WASD, Leertaste, C = bewegen · F = Flugmodus · Hilfslinie anklicken = bearbeiten"; };
        GAddOutline.Click += (_, _) => AddAutoGuide("outline"); GAddGrid.Click += (_, _) => AddAutoGuide("grid");
        GDrawLine.Click += (_, _) =>
        {
            ToolArea.IsChecked = ToolPoint.IsChecked = false; Plan.Mode = GDrawLine.IsChecked == true ? "line" : "";
            if (Plan.Mode == "line") ViewHint.Text = "Ziehe im Plan mit der linken Maustaste eine Linie. Umschalt = nur gerade oder diagonal. Nochmal auf den Knopf = fertig.";
        };
        Plan.LineDrawn += (x0, z0, x1, z1) =>
        {
            if (plan == null || project == null || (x0 == x1 && z0 == z1)) return;
            var g = plan.AddGuide("line", x0, z0, 1); g.X2 = x1; g.Z2 = z1;
            if (ModeLayer.IsChecked == true) g.Y = (int)LayerSlider.Value;
            ScheduleSave(); RenderGuides(); Plan.InvalidateVisual();
        };
    }

    void ShowPartInPlan(Part t)
    {
        var b = t.Box; Plan.HighlightPart = t.Id; ShowTab(0);
        Dispatcher.UIThread.Post(() => Plan.CenterOn((b[0] + b[3]) / 2.0, (b[2] + b[5]) / 2.0));
    }

    void OpenRef(string r)
    {
        var k = r.Split(':', 2); if (k.Length < 2) return;
        if (k[0] == "s") { stagesPage.Selected = k[1]; ShowTab(4); } else { partsPage.Selected = k[1]; ShowTab(3); }
    }

    void RenderPoints()
    {
        PointsList.Children.Clear();
        var ps = plan?.Points ?? new();
        if (ps.Count == 0) { PointsList.Children.Add(new TextBlock { Text = "Noch keine Punkte.", Classes = { "note" } }); return; }
        foreach (var q in ps)
        {
            var row = new DockPanel();
            var del = new Button { Content = "×", Padding = new Thickness(6, 0) }; DockPanel.SetDock(del, Dock.Right);
            del.Click += (_, _) => { plan!.Remove("points", q); ScheduleSave(); RenderPoints(); Plan.InvalidateVisual(); };
            var go = new Button { Content = $"{q.Name}  ·  X {project!.OriginX + q.X} Z {project.OriginZ + q.Z}", Background = Brushes.Transparent, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            go.Click += (_, _) => Plan.CenterOn(q.X, q.Z);
            row.Children.Add(del); row.Children.Add(go); PointsList.Children.Add(row);
        }
    }

    void AddGuide(string type)
    {
        if (plan == null || project == null) return;
        var (cx, cz) = Plan.ViewCenter();
        var g = plan.AddGuide(type, Math.Clamp(cx, 0, project.W), Math.Clamp(cz, 0, project.D), type == "line" ? 20 : 31);
        if (ModeLayer.IsChecked == true) g.Y = (int)LayerSlider.Value;   // neue Hilfslinie startet auf der gezeigten Ebene
        ScheduleSave(); RenderGuides(); Plan.InvalidateVisual();
    }

    string GuideHint(Guide g)
    {
        string where = project == null ? "" : g.Type == "line" ? $"X {project.OriginX + g.Cx:0} Z {project.OriginZ + g.Cz:0} → X {project.OriginX + g.X2:0} Z {project.OriginZ + g.Z2:0}" : $"Mitte X {project.OriginX + g.Cx:0} Z {project.OriginZ + g.Cz:0}";
        string y = project == null ? "" : $"Y {project.OriginY + g.Y} bis {project.OriginY + g.Y + g.H - 1}";
        string keys = g.Type == "outline" ? "Bild↑/↓ = Höhe · Entf = löschen · Esc = fertig" : "Ziehen = verschieben · Bild↑/↓ = Höhe · + / − = Größe (Umschalt = 5er) · Entf = löschen · Esc = fertig";
        return $"„{g.Name}“ ausgewählt · {(g.Type == "outline" ? "" : where + " · ")}{y}\n{keys}";
    }

    void AddAutoGuide(string type)
    {
        if (plan == null || project == null) return;
        bool layerMode = ModeLayer.IsChecked == true; int ly = (int)LayerSlider.Value;
        var g = plan.AddGuide(type, 0, 0, 16);
        if (type == "outline")
        {
            g.Y = layerMode ? ly : 0; g.H = 1; g.AllLayers = !layerMode;   // Draufsicht: Umriss vom ganzen Bau, als Linie am Boden
            g.Name = layerMode ? $"Umriss Y {project.OriginY + ly}" : "Umriss";
            if (g.ComputeOutline(project) == 0) { plan.Remove("guides", g); ViewHint.Text = "Auf dieser Ebene gibt es keine Blöcke – kein Umriss."; return; }
        }
        else { g.Y = layerMode ? ly : 0; g.H = 1; g.Name = "Raster 16"; }
        ScheduleSave(); RenderGuides(); Plan.InvalidateVisual();
    }

    void RenderGuides()
    {
        GuidesList.Children.Clear();
        if (plan == null || project == null) return;
        int ox = project.OriginX, oz = project.OriginZ;
        var colors = new[] { "#7ee08f", "#ffd23a", "#5ab4ff", "#ff6b9a", "#c08bff", "#ff9f43" };
        foreach (var g in plan.Guides)
        {
            var box = new StackPanel { Spacing = 6 };
            var name = new TextBox { Text = g.Name, MinWidth = 0 };
            name.LostFocus += (_, _) => { g.Name = name.Text ?? ""; ScheduleSave(); Plan.InvalidateVisual(); };
            var on = new CheckBox { IsChecked = g.On }; on.IsCheckedChanged += (_, _) => { g.On = on.IsChecked == true; ScheduleSave(); Plan.InvalidateVisual(); };
            var del = new Button { Content = "×", Padding = new Thickness(6, 0) }; del.Click += (_, _) => { plan.Remove("guides", g); ScheduleSave(); RenderGuides(); Plan.InvalidateVisual(); };
            var col = new ComboBox { ItemsSource = colors.Select(c => new Border { Width = 18, Height = 14, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.Parse(c)) }).ToList(), SelectedIndex = Math.Max(0, Array.IndexOf(colors, g.Color)), Width = 64 };
            col.SelectionChanged += (_, _) => { if (col.SelectedIndex >= 0) { g.Color = colors[col.SelectedIndex]; ScheduleSave(); Plan.InvalidateVisual(); } };
            var head = new DockPanel(); DockPanel.SetDock(on, Dock.Left); DockPanel.SetDock(col, Dock.Left); DockPanel.SetDock(del, Dock.Right);
            head.Children.Add(on); head.Children.Add(col); head.Children.Add(del); head.Children.Add(name);
            box.Children.Add(head);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*") };
            int row = 0;
            void F(string label, double val, Action<double> set, int column)
            {
                if (column == 0) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var sp = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 4) };
                sp.Children.Add(new TextBlock { Text = label, Classes = { "note" } });
                var n = new NumericUpDown { Value = (decimal)Math.Round(val, 1), FormatString = "0.#", ShowButtonSpinner = false };
                n.ValueChanged += (_, _) => { if (n.Value is { } v) { set((double)v); ScheduleSave(); Plan.InvalidateVisual(); } };
                sp.Children.Add(n); Grid.SetColumn(sp, column * 2); Grid.SetRow(sp, row); grid.Children.Add(sp);
                if (column == 1) row++;
            }
            if (g.Type == "outline")
            {
                var info = new TextBlock { Text = $"{g.Cells().Count:N0} Randfelder, automatisch aus {(g.AllLayers ? "dem ganzen Bau" : "den Ebenen in seiner Höhe")}", Classes = { "note" }, TextWrapping = TextWrapping.Wrap };
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto)); Grid.SetColumnSpan(info, 3); Grid.SetRow(info, row++); grid.Children.Add(info);
            }
            else if (g.Type == "grid")
            {
                F("Abstand (Blöcke)", g.A, v => { g.A = Math.Max(2, Math.Round(v)); g.Name = $"Raster {g.A:0}"; }, 0); row++;
            }
            else if (g.Type == "line")
            {
                F("Start X", ox + g.Cx, v => g.Cx = v - ox, 0); F("Start Z", oz + g.Cz, v => g.Cz = v - oz, 1);
                F("Ende X", ox + g.X2, v => g.X2 = v - ox, 0); F("Ende Z", oz + g.Z2, v => g.Z2 = v - oz, 1);
            }
            else
            {
                F("Mitte X", ox + g.Cx, v => g.Cx = v - ox, 0); F("Mitte Z", oz + g.Cz, v => g.Cz = v - oz, 1);
                if (g.Type == "circle") { F("Durchmesser", g.A, v => g.A = Math.Max(1, v), 0); row++; }
                else { F(g.Type == "rect" ? "Breite (X)" : "Länge (X)", g.A, v => g.A = Math.Max(1, v), 0); F(g.Type == "rect" ? "Tiefe (Z)" : "Breite (Z)", g.Bv, v => g.Bv = Math.Max(1, v), 1); }
            }
            // Höhe: Unterkante als echte Welt-Höhe, dazu die Höhe in Blöcken
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            void H(string label, double val, double min, Action<int> set, int column)
            {
                var sp = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 4) };
                sp.Children.Add(new TextBlock { Text = label, Classes = { "note" } });
                var n = new NumericUpDown { Value = (decimal)val, Minimum = (decimal)min, Increment = 1, FormatString = "0" };
                n.ValueChanged += (_, _) => { if (n.Value is { } v) { set((int)v); ScheduleSave(); Plan.InvalidateVisual(); } };
                sp.Children.Add(n); Grid.SetColumn(sp, column * 2); Grid.SetRow(sp, row); grid.Children.Add(sp);
            }
            int oy = project.OriginY;
            void Re() { if (g.Type == "outline") { g.ComputeOutline(project); } }   // Umriss folgt der Höhe automatisch
            H("Ab Höhe Y", oy + g.Y, -64, v => { g.Y = v - oy; Re(); }, 0); H("Höhe (Blöcke)", g.H, 1, v => { g.H = v; Re(); }, 1); row++;
            box.Children.Add(grid);
            var lay = new Button { Content = "Auf aktuelle Ebene setzen", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            ToolTip.SetTip(lay, "Unterkante auf die Ebene, die gerade im 2D-Plan gezeigt wird");
            lay.Click += (_, _) => { g.Y = (int)LayerSlider.Value; if (g.Type == "outline") { g.H = 1; g.AllLayers = false; g.ComputeOutline(project); } ScheduleSave(); RenderGuides(); Plan.InvalidateVisual(); };
            box.Children.Add(lay);
            bool sel = g.Id == View3.SelectedGuide;   // in 3D ausgewählt: Karte hervorheben
            GuidesList.Children.Add(new Border { BorderBrush = sel ? new SolidColorBrush(Color.Parse(g.Color)) : UI.Res("LineBrush"), BorderThickness = new Thickness(sel ? 2.5 : 1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Child = box });
        }
        if (plan.Guides.Count == 0) GuidesList.Children.Add(new TextBlock { Text = "Noch keine Hilfslinien. Sie erscheinen in der Mitte der Ansicht.", Classes = { "note" }, TextWrapping = TextWrapping.Wrap });
    }

    async Task CreatePart(int x0, int z0, int x1, int z1)
    {
        if (plan == null || project == null) return;
        var cats = plan.Cats.Where(c => !c.Base).ToList();
        var w = new Window { Title = "Bauplan erstellen", Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var name = new TextBox { Text = "" , PlaceholderText = "Name, z. B. „Nordturm“" };
        var cat = new ComboBox { ItemsSource = cats.Select(c => c.Name).Append("Neue Kategorie …").ToList(), SelectedIndex = cats.Count > 0 ? 0 : cats.Count, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var newCat = new TextBox { PlaceholderText = "Name der neuen Kategorie", IsVisible = cats.Count == 0, Text = cats.Count == 0 ? "Bauteile" : "" };
        cat.SelectionChanged += (_, _) => newCat.IsVisible = cat.SelectedIndex == cats.Count;
        int cut = (int)CutSlider.Value;
        var y0 = new NumericUpDown { Value = project.OriginY, Minimum = project.OriginY, Maximum = project.OriginY + project.H - 1, FormatString = "0" };
        var y1 = new NumericUpDown { Value = project.OriginY + cut, Minimum = project.OriginY, Maximum = project.OriginY + project.H - 1, FormatString = "0" };
        var ok = new Button { Content = "Erstellen", Classes = { "accent" }, MinWidth = 90 }; var cancel = new Button { Content = "Abbrechen", MinWidth = 90 };
        bool go = false; ok.Click += (_, _) => { go = true; w.Close(); }; cancel.Click += (_, _) => w.Close();
        w.Content = new StackPanel
        {
            Margin = new Thickness(22), Spacing = 10, Children =
            {
                new TextBlock { Text = "Bauplan erstellen", FontSize = 17, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = $"Bereich {x1 - x0 + 1}×{z1 - z0 + 1} ab X {project.OriginX + x0} Z {project.OriginZ + z0}. Es zählen nur Felder mit Blöcken.", Classes = { "note" }, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Name", Classes = { "note" } }, name,
                new TextBlock { Text = "Kategorie", Classes = { "note" } }, cat, newCat,
                new TextBlock { Text = "Höhe von Y bis Y", Classes = { "note" } },
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,8,*"), Children = { y0, new Border { [Grid.ColumnProperty] = 2, Child = y1 } } },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, ok } },
            }
        };
        await w.ShowDialog(this);
        if (!go) return;
        string catId = cat.SelectedIndex >= 0 && cat.SelectedIndex < cats.Count ? cats[cat.SelectedIndex].Id : plan.AddCat(string.IsNullOrWhiteSpace(newCat.Text) ? "Bauteile" : newCat.Text.Trim()).Id;
        int ya = (int)(y0.Value ?? project.OriginY) - project.OriginY, yb = (int)(y1.Value ?? project.OriginY) - project.OriginY;
        var part = plan.AddPart(catId, name.Text?.Trim() ?? "", x0, Math.Min(ya, yb), z0, x1, Math.Max(ya, yb), z1);
        if (part == null) { await Message("Kein Bauplan erstellt", "In diesem Bereich sind keine Blöcke."); return; }
        ScheduleSave(); Plan.HighlightPart = part.Id; Plan.InvalidateVisual();
        partsPage.Selected = part.Id;
    }

    async Task<string?> Ask(string title, string label, string value)
    {
        var w = new Window { Title = title, Width = 380, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var tb = new TextBox { Text = value }; string? res = null;
        var ok = new Button { Content = "OK", Classes = { "accent" }, MinWidth = 80 }; var cancel = new Button { Content = "Abbrechen", MinWidth = 80 };
        ok.Click += (_, _) => { res = tb.Text?.Trim(); w.Close(); }; cancel.Click += (_, _) => w.Close();
        tb.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { res = tb.Text?.Trim(); w.Close(); } };
        w.Content = new StackPanel { Margin = new Thickness(20), Spacing = 10, Children = { new TextBlock { Text = label }, tb, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, ok } } } };
        w.Opened += (_, _) => { tb.Focus(); tb.SelectAll(); };
        await w.ShowDialog(this);
        return string.IsNullOrEmpty(res) ? null : res;
    }

    void Busy(string? text) { BusyBox.IsVisible = text != null; if (text != null) BusyText.Text = text; }

    /* ---------- Start ---------- */
    async Task Startup()
    {
        Updater.CleanUp(); _ = CheckUpdate();
        // nicht installiert (z. B. aus „Downloads“ gestartet): Installieren anbieten
        InstallBox.IsVisible = Updater.ExePath != null && !Installer.IsInstalled;
        if (Installer.Exists && !Installer.IsInstalled)
        {
            InstallText.Text = "Blockplan ist schon installiert – du hast gerade die heruntergeladene Datei geöffnet. Öffne die installierte Version (diese Datei wird dann gelöscht) oder ersetze sie.";
            BtnInstall.Content = "Installation ersetzen"; BtnOpenInstalled.IsVisible = true; BtnOpenInstalled.Classes.Add("accent"); BtnInstall.Classes.Remove("accent");
        }
        Installer.RefreshVersion();
        RefreshList(); UpdateDbInfo();
        if (double.TryParse(store.Get("sun"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sa)) SunSlider.Value = sa;
        if (store.Get("shaders") == "1" || Environment.GetEnvironmentVariable("BLOCKPLAN_SHADERS") == "1") { ShaderBtn.IsChecked = true; SunSlider.IsVisible = true; View3.SetShaders(true); }
        if (double.TryParse(store.Get("mouse"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms)) MouseSlider.Value = ms;
        _ = LoadTextures(null);
        var last = store.Get("last"); var list = store.List();
        var id = list.Any(p => p.Id == last) ? last : list.FirstOrDefault()?.Id;
        if (Environment.GetEnvironmentVariable("BLOCKPLAN_OPEN") is { Length: > 0 } want) id = list.FirstOrDefault(p => p.Name.StartsWith(want, StringComparison.OrdinalIgnoreCase))?.Id ?? id;
        if (id != null) await OpenProject(id);
        else ShowEmpty();
        if (Environment.GetEnvironmentVariable("BLOCKPLAN_LIVE") is { Length: > 0 } ll) { LiveLink.Text = ll; ConnectLive(); }   // zum Testen
        switch (Environment.GetEnvironmentVariable("BLOCKPLAN_TAB")) { case "3d": ShowTab(1); break; case "mat": ShowTab(2); break; case "parts": ShowTab(3); break; case "stages": ShowTab(4); break; case "todos": ShowTab(5); break; case "layer": ModeLayer.IsChecked = true; ShowTab(0); break; }
    }

    void ShowEmpty()
    {
        ProjName.Text = ""; ProjSize.Text = "Noch kein Projekt. Öffne links eine Schematic (.schem, .litematic, .nbt) oder importiere deine Projekte aus dem Artefakt.";
        Plan.SetProject(null, atlas, 0, true); View3.SetProject(null, atlas, 0, true);
    }

    void RefreshList()
    {
        loadingUi = true;
        var list = store.List(); ProjSel.ItemsSource = list;
        ProjSel.SelectedItem = list.FirstOrDefault(p => p.Id == project?.Id);
        loadingUi = false; UpdateDbInfo();
    }

    void UpdateDbInfo()
    {
        try { long len = new FileInfo(store.File).Length + (File.Exists(store.File + "-wal") ? new FileInfo(store.File + "-wal").Length : 0); DbInfo.Text = $"Blockplan {Updater.CurrentText} · {store.List().Count} Projekte in der eigenen Datenbank ({len / 1024.0 / 1024:0.0} MB):\n{store.File}"; }
        catch { DbInfo.Text = store.File; }
    }

    /* ---------- Texturen ---------- */
    async Task LoadTextures(string? jar)
    {
        var prog = new Progress<string>(s => TexInfo.Text = s);
        // Reihenfolge: gewählte Datei, sonst alle gefundenen Spieldateien (neueste zuerst), zuletzt Download von Mojang
        var tries = jar != null ? new List<string?> { jar } : McAssets.FindJars().Select(j => (string?)j).Append(null).ToList();
        McAssets.Log($"Texturen: Minecraft-Ordner {McAssets.MinecraftDir} (vorhanden: {Directory.Exists(McAssets.MinecraftDir)}), Kandidaten: {string.Join(", ", tries.Select(t => t ?? "Download"))}");
        var errors = new List<string>();
        foreach (var t in tries)
        {
            try
            {
                var path = t ?? await McAssets.DownloadAsync(prog);
                var m = await Task.Run(() => McAssets.Load(path, prog));
                if (m.Tex.Count < 100 || m.Faces.Count < 100) throw new InvalidDataException($"In {Path.GetFileName(path)} sind keine Block-Texturen ({m.Tex.Count}).");
                await m.LoadGermanAsync(); m.ApplyNames();
                mc = m; atlas = new Atlas(m);
                store.Set("jar", path);
                TexInfo.Text = $"Original-Texturen aus Minecraft {m.Version} ({m.Tex.Count}).";
                McAssets.Log($"Texturen geladen aus {path}: {m.Tex.Count} Bilder, {m.Faces.Count} Blöcke");
                ApplyProjectToViews(false);
                if (tab == 3) partsPage.Refresh(); else if (tab == 4) stagesPage.Refresh(); else if (tab == 5) todosPage.Refresh();
                return;
            }
            catch (Exception e)
            {
                errors.Add((t == null ? "Download" : Path.GetFileName(t)) + ": " + e.Message);
                McAssets.Log("Texturen fehlgeschlagen (" + (t ?? "Download") + "): " + e);
            }
        }
        TexInfo.Text = "Blockplan findet kein Minecraft Java auf diesem PC und kommt selbst nicht zu Mojang durch (" + string.Join(" · ", errors) +
            "). Klick auf „Im Browser laden“: Die Datei client.jar wird dann gefunden, sobald sie in Downloads liegt.";
        BtnBrowserJar.IsVisible = true;
    }

    /* ---------- Selbst-Update ---------- */
    string? updateFile;
    bool updating, firstCheck = true; string? readyVersion; DispatcherTimer? updateTimer;

    /// <summary>Beim Start, danach alle 10 Minuten und auf Knopfdruck: neue Version auf GitHub?</summary>
    async Task CheckUpdate(bool manual = false)
    {
        if (updateTimer == null && Updater.ExePath != null)
        {
            updateTimer = new DispatcherTimer(TimeSpan.FromMinutes(10), DispatcherPriority.Background, async (_, _) => await CheckUpdate());
            updateTimer.Start();
        }
        if (Updater.ExePath == null) { if (manual) UpdateNote.Text = "Updates gibt es nur in der Blockplan.exe."; return; }
        if (updating) return;
        updating = true; if (manual) UpdateNote.Text = "Suche nach Updates …";
        try
        {
            if (firstCheck) { firstCheck = false; await Task.Delay(2500); }   // Start nicht ausbremsen
            var r = await Updater.CheckAsync();
            if (r == null) { if (manual) UpdateNote.Text = $"Du hast die neueste Version ({Updater.CurrentText})."; return; }
            if (r.Version == readyVersion) { if (manual) UpdateNote.Text = $"Version {r.Version} ist bereit – links auf „Jetzt neu starten“."; return; }
            UpdateBox.IsVisible = true; BtnUpdate.IsVisible = false; UpdateText.Text = $"Neue Version {r.Version} wird geladen …";
            updateFile = await Updater.DownloadAsync(r, new Progress<int>(p => UpdateText.Text = $"Neue Version {r.Version} wird geladen … {p} %"));
            readyVersion = r.Version;
            UpdateText.Text = $"Version {r.Version} ist bereit (du hast {Updater.CurrentText}).";
            BtnUpdate.IsVisible = true; if (manual) UpdateNote.Text = "";
        }
        catch (Exception e)
        {
            McAssets.Log("Update: " + e.Message);
            if (manual) UpdateNote.Text = "Keine Verbindung zu GitHub: " + e.Message;
            else if (UpdateBox.IsVisible && readyVersion == null) UpdateText.Text = "Update konnte nicht geladen werden: " + e.Message;
        }
        finally { updating = false; }
    }

    DispatcherTimer? jarWatch;
    async Task DownloadInBrowser()
    {
        try { await Launcher.LaunchUriAsync(new Uri(McAssets.FallbackClientUrl)); }
        catch { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(McAssets.FallbackClientUrl) { UseShellExecute = true }); }
        TexInfo.Text = "Warte auf client.jar im Download-Ordner …";
        jarWatch?.Stop();
        jarWatch = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (_, _) =>
        {
            var jars = McAssets.FindJars(); if (jars.Count == 0) return;
            jarWatch!.Stop(); BtnBrowserJar.IsVisible = false;
            // in den eigenen Ordner kopieren, damit es auch nach dem Aufräumen von Downloads klappt
            var dest = Path.Combine(Store.Dir, "minecraft-26.3.jar");
            try { if (!File.Exists(dest)) File.Copy(jars[0], dest); } catch { dest = jars[0]; }
            await LoadTextures(dest);
        });
        jarWatch.Start();
    }

    async Task PickJar()
    {
        var f = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Minecraft-Spieldatei wählen (z. B. .minecraft\\versions\\26.3\\26.3.jar)",
            FileTypeFilter = new[] { new FilePickerFileType("Minecraft-Spieldatei oder Ressourcenpaket") { Patterns = new[] { "*.jar", "*.zip" } } },
        });
        if (f.Count > 0 && f[0].TryGetLocalPath() is { } path) await LoadTextures(path);
    }

    /* ---------- Projekte ---------- */
    async Task OpenProject(string id)
    {
        var info = store.List().FirstOrDefault(p => p.Id == id);
        Busy($"Lade {info?.Name ?? "Projekt"} …");
        try
        {
            var p = await Task.Run(() => store.Load(id));
            if (p == null) { Busy(null); return; }
            SetProject(p, true);
        }
        catch (Exception e) { await Message("Projekt konnte nicht geladen werden", e.Message); }
        finally { Busy(null); }
    }

    void SetProject(Project p, bool refit)
    {
        project = p; store.Set("last", p.Id);
        loadingUi = true;
        ProjName.Text = p.Name; OrX.Value = p.OriginX; OrY.Value = p.OriginY; OrZ.Value = p.OriginZ;
        int maxY = Math.Max(0, p.MaxY());
        CutSlider.Maximum = Math.Max(1, p.H - 1); CutSlider.Value = p.H - 1;
        LayerSlider.Maximum = Math.Max(1, p.H - 1); LayerSlider.Value = Math.Min(maxY, Math.Max(0, maxY / 2));
        long cnt = p.Cells.LongCount(c => c != 0);
        ProjSize.Text = $"{p.W}×{p.D} Grundfläche, {p.H} hoch · {cnt:N0} Blöcke";
        loadingUi = false;
        RefreshList();
        plan = new PlanData(p); Plan.Plan = plan; Plan.HighlightPart = null;
        View3.SetGuides(plan, Guides3DChk.IsChecked == true);
        if (live != null && live.State != "aus") Dispatcher.UIThread.Post(ConnectLive);   // anderer Baubereich: neu beobachten
        ApplyProjectToViews(refit);
        ApplyView();
        UpdateSnowInfo(); UpdateCounts(); RenderPoints(); RenderGuides();
        layerCount = Array.Empty<long>(); FillMatScopes(); FillBauScopes();
        if (tlOpen) CloseTimelapse();
        navSpots = new(); NavRow.IsVisible = false; NavText.Text = ""; BtnNavFind.Content = "Fehlende Stellen suchen";
        timelineCount = 0; LoadTimelineInfo();
        if (StepBtn.IsChecked == true) SetStepMode(true);
        if (CutAxis.SelectedIndex > 0) { CutAxis.SelectedIndex = 0; }
        if (tab == 3) partsPage.Refresh(); if (tab == 4) stagesPage.Refresh(); if (tab == 5) todosPage.Refresh();
        if (MatPage.IsVisible) BuildMaterials();
    }

    void ApplyProjectToViews(bool refit)
    {
        if (project == null) return;
        int cut = (int)CutSlider.Value;
        Plan.SetProject(project, atlas, cut, refit);
        Plan.SetView(ModeLayer.IsChecked == true, (int)LayerSlider.Value, cut);
        View3.SetProject(project, atlas, cut, refit);
        if (MatPage.IsVisible) BuildMaterials();
    }

    void ApplyView()
    {
        if (loadingUi) return;
        int cut = (int)CutSlider.Value, ly = (int)LayerSlider.Value;
        if (project != null) { CutVal.Text = $"Y {project.OriginY + cut}"; LayerVal.Text = $"Y {project.OriginY + ly}"; }
        LayerRow.IsVisible = Plan.IsVisible && ModeLayer.IsChecked == true;
        if (project == null) return;
        Plan.SetView(ModeLayer.IsChecked == true, ly, cut);
        View3.SetTop(cut, StepBtn.IsChecked == true ? cut : -1);
        if (StepBtn.IsChecked == true) UpdateStepUi();
        if (MatPage.IsVisible) BuildMaterials();
    }

    void SaveProject()
    {
        var p = project; if (p == null) return;
        Task.Run(() => { try { store.Save(p); } catch { } });
    }

    async Task OpenSchematic()
    {
        var f = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Schematic öffnen",
            FileTypeFilter = new[] { new FilePickerFileType("Schematic") { Patterns = new[] { "*.schem", "*.litematic", "*.nbt", "*.schematic" } } },
        });
        if (f.Count > 0 && f[0].TryGetLocalPath() is { } path) await LoadSchematic(path);
    }

    async Task LoadSchematic(string path)
    {
        Busy("Lese " + Path.GetFileName(path) + " …");
        Schem s;
        try { s = await Task.Run(() => SchematicReader.Load(path, t => Dispatcher.UIThread.Post(() => BusyText.Text = t))); }
        catch (Exception e) { Busy(null); await Message("Die Schematic konnte nicht gelesen werden", e.Message); return; }
        Busy(null);
        var r = await new SchemDialog(s).ShowDialog<SchemDialog.Result?>(this);
        if (r == null) return;
        Busy("Übernehme Blöcke …");
        try
        {
            var p = await Task.Run(() => { var pr = SchematicReader.ToProject(s, r.X0, r.Y0, r.Z0, r.X1, r.Y1, r.Z1, r.Dewater, r.Plants, r.Rot, r.Desnow, r.SnowBlocks); store.Save(pr); return pr; });
            s.Cells = Array.Empty<ushort>(); GC.Collect();
            SetProject(p, true);
        }
        catch (Exception e) { await Message("Fehler beim Übernehmen", e.Message); }
        finally { Busy(null); }
    }

    async Task ImportArtifact()
    {
        var f = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Projekte aus dem Blockplan-Artefakt importieren (.json)", AllowMultiple = true,
            FileTypeFilter = new[] { new FilePickerFileType("Blockplan-Projekt") { Patterns = new[] { "*.json", "*.blockplan" } } },
        });
        await ImportFiles(f.Select(x => x.TryGetLocalPath()).Where(x => x != null).Select(x => x!).ToList());
    }

    async Task ImportFiles(List<string> paths)
    {
        Project? last = null; var errors = new List<string>();
        foreach (var path in paths)
        {
            Busy("Importiere " + Path.GetFileName(path) + " …");
            try { last = await Task.Run(() => { var p = Project.FromJson(File.ReadAllText(path)); store.Save(p); return p; }); }
            catch (Exception e) { errors.Add(Path.GetFileName(path) + ": " + e.Message); }
        }
        Busy(null);
        if (last != null) SetProject(last, true);
        if (errors.Count > 0) await Message("Nicht alles konnte importiert werden", string.Join("\n", errors));
    }

    async void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
        if (files == null || files.Count == 0) return;
        var json = files.Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".blockplan", StringComparison.OrdinalIgnoreCase)).ToList();
        if (json.Count > 0) await ImportFiles(json);
        var schem = files.FirstOrDefault(f => !json.Contains(f));
        if (schem != null) await LoadSchematic(schem);
    }

    async Task DeleteProject()
    {
        if (project == null) return;
        if (!await Confirm("Projekt löschen?", $"„{project.Name}“ wird aus der Datenbank gelöscht. Das kann nicht rückgängig gemacht werden.")) return;
        store.Delete(project.Id); project = null;
        var next = store.List().FirstOrDefault();
        RefreshList();
        if (next != null) await OpenProject(next.Id); else ShowEmpty();
    }

    /* ---------- Schnee aus einem geladenen Projekt entfernen ---------- */
    void UpdateSnowInfo()
    {
        if (project == null) { SnowRow.IsVisible = false; return; }
        long cover = 0, blocks = 0, snowy = 0;
        foreach (var (v, c) in project.CountBlocks()) { int k = SchematicReader.SnowKind(v); if (k == 1) cover += c; else if (k == 2) blocks += c; else if (k == 3) snowy += c; }
        SnowRow.IsVisible = cover + blocks + snowy > 0;
        SnowInfo.Text = $"Im Projekt: {cover:N0} Schneedecken, {snowy:N0} verschneite Blöcke, {blocks:N0} Schneeblöcke.";
        SnowBlocksChk.IsVisible = blocks > 0;
    }

    async Task RemoveSnow()
    {
        var p = project; if (p == null) return;
        bool blocks = SnowBlocksChk.IsChecked == true;
        Busy("Entferne Schnee …");
        long n = await Task.Run(() => { var c = p.Replace(v => SchematicReader.Desnow(v, blocks)); store.Save(p); return c; });
        Busy(null);
        SetProject(p, false);
        ProjSize.Text += $" · {n:N0} Schneeblöcke entfernt bzw. aufgetaut";
    }

    /* ---------- Materialliste ---------- */
    readonly Dictionary<int, Bitmap> icons = new();
    Bitmap MatIcon(int v)
    {
        if (icons.TryGetValue(v, out var b)) return b;
        var tile = Blocks.Get(v).Shape == 'm' ? atlas.Cross(v) : atlas.Faces(v)[0];
        var px = atlas.Tiles[tile]; var wb = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var fb = wb.Lock()) for (int y = 0; y < 16; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * 64, fb.Address + y * fb.RowBytes, 64);
        return icons[v] = wb;
    }

    /// <summary>Umfang der Materialliste: "" ganzes Projekt, "s:id" Etappe, "p:id" Bauplan.</summary>
    List<(string key, string label)> matScopes = new();
    bool matScopeBusy;

    void FillMatScopes()
    {
        matScopeBusy = true;
        var cur = MatScope.SelectedIndex >= 0 && MatScope.SelectedIndex < matScopes.Count ? matScopes[MatScope.SelectedIndex].key : "";
        matScopes = new() { ("", "Ganzes Projekt (bis zur eingestellten Höhe)") };
        if (plan != null)
        {
            foreach (var st in plan.Stages) matScopes.Add(("s:" + st.Id, "Etappe: " + st.Name + (st.Done ? " ✓" : "")));
            var openStages = plan.Stages.Where(x => !x.Done).ToList();
            if (openStages.Count > 0 && plan.Stages.Count > 1) matScopes.Add(("open", "Alle offenen Etappen"));
            foreach (var t in plan.Parts.Where(x => !x.Base)) matScopes.Add(("p:" + t.Id, "Bauplan: " + plan.Label(t)));
        }
        MatScope.ItemsSource = matScopes.Select(x => x.label).ToList();
        int idx = matScopes.FindIndex(x => x.key == cur); MatScope.SelectedIndex = idx < 0 ? 0 : idx;
        matScopeBusy = false;
    }

    /// <summary>Blockzustände im gewählten Umfang.</summary>
    (Dictionary<int, long> states, string what) MatStates()
    {
        var p = project!; var key = MatScope.SelectedIndex >= 0 && MatScope.SelectedIndex < matScopes.Count ? matScopes[MatScope.SelectedIndex].key : "";
        if (plan != null && key.Length > 0)
        {
            var parts = plan.Parts;
            if (key == "open") { var ids = plan.Stages.Where(x => !x.Done).SelectMany(x => x.Parts).ToHashSet(); return (plan.States(parts.Where(t => ids.Contains(t.Id))), "alle offenen Etappen"); }
            if (key.StartsWith("s:") && plan.Stages.FirstOrDefault(x => x.Id == key[2..]) is { } st) { var ids = st.Parts.ToHashSet(); return (plan.States(parts.Where(t => ids.Contains(t.Id))), "Etappe „" + st.Name + "“"); }
            if (key.StartsWith("p:") && parts.FirstOrDefault(x => x.Id == key[2..]) is { } pt) return (plan.States(new[] { pt }), "Bauplan „" + plan.Label(pt) + "“");
        }
        return (p.CountBlocks((int)CutSlider.Value), $"bis Y {p.OriginY + (int)CutSlider.Value}");
    }

    string ItemName(string item, int block)
    {
        if (mc != null && mc.ItemNamesDe.TryGetValue(item, out var n)) return n;
        if (Blocks.Find(item) is var b and > 0) return Blocks.DisplayName(b, true);
        if (block > 0) return Blocks.DisplayName(block, true);
        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(item.Replace('_', ' '));
    }

    void BuildMaterials()
    {
        if (project == null) { MatList.ItemsSource = null; return; }
        icons.Clear();
        if (MatScope.ItemsSource == null || matScopes.Count == 0) FillMatScopes();
        var (states, what) = MatStates();
        bool asItems = MatMode.SelectedIndex == 0;
        if (asItems)
        {
            // Items wie im Inventar; Symbol vom ersten Block, aus dem das Item kommt
            var from = new Dictionary<string, int>();
            foreach (var v in states.Keys) foreach (var (it, _) in Items.Of(v)) from.TryAdd(it, Blocks.BaseOf(v));
            var items = Items.FromStates(states);
            matAll = items.OrderByDescending(kv => kv.Value).Select(kv =>
            {
                int src = from.GetValueOrDefault(kv.Key), blk = Items.BlockFor(kv.Key, src);
                var bau = live?.Bau; string have = "";
                if (bau != null && bau.Plan.Length > 0)
                {
                    int inv = bau.Inventory.GetValueOrDefault(kv.Key), lag = bau.Storage.GetValueOrDefault(kv.Key);
                    if (inv + lag > 0) have = $"  ·  da {inv + lag:N0}" + (lag > 0 ? $" (Lager {lag:N0})" : "");
                }
                return new MatRow { Name = ItemName(kv.Key, src), Count = kv.Value, StackText = Items.StackText(kv.Key, kv.Value) + have, Icon = MatIcon(blk), Block = src, Replace = src > 0 ? new Cmd(() => _ = ReplaceBlocks(src)) : null };
            }).ToList();
            var (slots, shulker, chests, dbl) = Items.Space(items);
            MatSpaceText.Text = $"Platz: {slots:N0} Stacks  ·  {shulker:0.#} Shulker-Kisten  ·  {dbl:0.#} Doppelkisten  ·  {Math.Ceiling(slots / 36.0):N0} volle Inventare";
            MatSub.Text = $"{matAll.Count} Items, {matAll.Sum(m => m.Count):N0} Stück · {what}. Wandfackeln zählen als Fackeln, doppelte Stufen als 2, Wasser und obere Türhälften nicht.";
        }
        else
        {
            var byBase = new Dictionary<int, long>();
            foreach (var (v, c) in states) { int b = Blocks.BaseOf(v); byBase[b] = byBase.GetValueOrDefault(b) + c; }
            matAll = byBase.OrderByDescending(kv => kv.Value).Select(kv => new MatRow { Name = Blocks.DisplayName(kv.Key, true), Count = kv.Value, StackText = Items.StackText(Blocks.Get(kv.Key).Key, kv.Value), Icon = MatIcon(kv.Key), Block = kv.Key, Replace = new Cmd(() => _ = ReplaceBlocks(kv.Key)) }).ToList();
            MatSpaceText.Text = $"{matAll.Sum(m => m.Count):N0} Blöcke im Plan";
            MatSub.Text = $"{matAll.Count} Sorten · {what}";
        }
        MatTitle.Text = "Materialliste · " + project.Name;
        FilterMat();
    }

    /// <summary>Baupläne im gewählten Umfang; null = ganzes Projekt.</summary>
    List<Part>? ScopeParts()
    {
        var key = MatScope.SelectedIndex >= 0 && MatScope.SelectedIndex < matScopes.Count ? matScopes[MatScope.SelectedIndex].key : "";
        if (plan == null || key.Length == 0) return null;
        var parts = plan.Parts;
        if (key == "open") { var ids = plan.Stages.Where(x => !x.Done).SelectMany(x => x.Parts).ToHashSet(); return parts.Where(t => ids.Contains(t.Id)).ToList(); }
        if (key.StartsWith("s:") && plan.Stages.FirstOrDefault(x => x.Id == key[2..]) is { } st) { var ids = st.Parts.ToHashSet(); return parts.Where(t => ids.Contains(t.Id)).ToList(); }
        if (key.StartsWith("p:")) return parts.Where(x => x.Id == key[2..]).ToList();
        return null;
    }

    static bool InPart(Part t, int x, int y, int z)
    {
        var b = t.Box; if (x < b[0] || x > b[3] || y < b[1] || y > b[4] || z < b[2] || z > b[5]) return false;
        int w = b[3] - b[0] + 1, d = b[5] - b[2] + 1; long mi = ((long)(y - b[1]) * d + (z - b[2])) * w + (x - b[0]);
        var m = t.Mask; return mi < m.LongLength && m[mi] != 0;
    }

    async Task ExportSchem()
    {
        var p = project; if (p == null) return;
        var parts = ScopeParts(); var (_, what) = MatStates();
        int x0 = 0, y0 = 0, z0 = 0, x1 = p.W - 1, y1 = Math.Min((int)CutSlider.Value, p.H - 1), z1 = p.D - 1;
        Func<int, int, int, bool>? keep = null;
        if (parts != null)
        {
            if (parts.Count == 0) { await Message("Nichts zu exportieren", "In dieser Etappe sind noch keine Baupläne."); return; }
            x0 = parts.Min(t => t.Box[0]); y0 = parts.Min(t => t.Box[1]); z0 = parts.Min(t => t.Box[2]);
            x1 = parts.Max(t => t.Box[3]); y1 = parts.Max(t => t.Box[4]); z1 = parts.Max(t => t.Box[5]);
            var ps = parts; keep = (x, y, z) => ps.Any(t => InPart(t, x, y, z));
        }
        var safe = string.Join("_", (p.Name + (parts != null ? " " + what.Replace("„", "").Replace("“", "") : "")).Split(Path.GetInvalidFileNameChars()));
        var f = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Als Schematic speichern", SuggestedFileName = safe + ".litematic", DefaultExtension = "litematic",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Litematica (.litematic)") { Patterns = new[] { "*.litematic" } },
                new FilePickerFileType("WorldEdit / Sponge (.schem)") { Patterns = new[] { "*.schem" } },
            },
        });
        if (f == null || f.TryGetLocalPath() is not { } path) return;
        bool lite = !path.EndsWith(".schem", StringComparison.OrdinalIgnoreCase);
        if (lite && !path.EndsWith(".litematic", StringComparison.OrdinalIgnoreCase)) path += ".litematic";
        Busy("Speichere Schematic …");
        try
        {
            int dv = mc?.DataVersion > 0 ? mc.DataVersion : 4440;
            var (bytes, n) = await Task.Run(() => { long c; var b = lite ? SchemExport.BuildLitematic(p, x0, y0, z0, x1, y1, z1, keep, dv, out c) : SchemExport.Build(p, x0, y0, z0, x1, y1, z1, keep, dv, out c); return (b, c); });
            await File.WriteAllBytesAsync(path, bytes);
            Busy(null);
            await Message("Schematic gespeichert", $"{n:N0} Blöcke ({x1 - x0 + 1}×{y1 - y0 + 1}×{z1 - z0 + 1}) in {Path.GetFileName(path)}.\n\n" + (lite
                ? "Litematica: Datei in den Ordner .minecraft\\schematics legen, im Spiel M + Ladebildschirm öffnen, laden und platzieren. Die Ecke unten im Nordwesten ist der Ursprung."
                : $"WorldEdit: Datei in den Ordner schematics legen, dann //schem load {Path.GetFileNameWithoutExtension(path)} und //paste. Die Ecke unten im Nordwesten ist der Einfügepunkt."));
        }
        catch (Exception e) { Busy(null); await Message("Speichern fehlgeschlagen", e.Message); }
    }

    sealed record Cand(int Block, string Name, Bitmap Icon);
    (string projectId, ushort[] cells, string text)? undo;

    async Task ReplaceBlocks(int from)
    {
        var p = project; if (p == null || from <= 0) return;
        var parts = ScopeParts(); var (_, what) = MatStates(); int cut = Math.Min((int)CutSlider.Value, p.H - 1);
        var all = Enumerable.Range(1, Blocks.Count - 1).Where(i => Blocks.All[i] is { } b && b.Props == null && i != from).ToList();
        List<Cand> Cands(string q) => all.Select(i => (i, n: Blocks.DisplayName(i, true)))
            .Where(t => q.Length == 0 || t.n.Contains(q, StringComparison.OrdinalIgnoreCase) || Blocks.Get(t.i).Key.Contains(q.Replace(' ', '_'), StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.n, StringComparer.CurrentCulture).Take(300).Select(t => new Cand(t.i, t.n, MatIcon(t.i))).ToList();
        var w = new Window { Title = "Blöcke ersetzen", Width = 460, Height = 600, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var search = new TextBox { PlaceholderText = "Neuen Block suchen, z. B. Fichte …" };
        var list = new ListBox { ItemsSource = Cands(""), Height = 330 };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<Cand>((c, _) =>
        {
            var img = new Image { Width = 20, Height = 20, Source = c?.Icon }; RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.None);
            return new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { img, new TextBlock { Text = c?.Name, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } };
        });
        search.TextChanged += (_, _) => list.ItemsSource = Cands(search.Text?.Trim() ?? "");
        var keepForm = new CheckBox { Content = "Form und Ausrichtung übernehmen (Treppe → Treppe, Stufe → Stufe)", IsChecked = true };
        var ok = new Button { Content = "Ersetzen", Classes = { "accent" }, MinWidth = 90, IsEnabled = false }; var cancel = new Button { Content = "Abbrechen", MinWidth = 90 };
        list.SelectionChanged += (_, _) => ok.IsEnabled = list.SelectedItem is Cand;
        bool go = false; ok.Click += (_, _) => { go = true; w.Close(); }; cancel.Click += (_, _) => w.Close();
        var fromImg = new Image { Width = 24, Height = 24, Source = MatIcon(from) }; RenderOptions.SetBitmapInterpolationMode(fromImg, BitmapInterpolationMode.None);
        w.Content = new StackPanel
        {
            Margin = new Thickness(20), Spacing = 10, Children =
            {
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { fromImg, new TextBlock { Text = $"„{Blocks.DisplayName(from, true)}“ ersetzen durch …", FontSize = 16, FontWeight = FontWeight.SemiBold, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center } } },
                new TextBlock { Text = "Gilt für: " + what + ". Treppen, Stufen, Zäune usw. aus demselben Material werden mitgenommen, wenn es sie im neuen Material auch gibt.", Classes = { "note" }, TextWrapping = TextWrapping.Wrap },
                search, list, keepForm,
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { cancel, ok } },
            }
        };
        w.Opened += (_, _) => search.Focus();
        await w.ShowDialog(this);
        if (!go || list.SelectedItem is not Cand to) return;

        var map = BlockReplace.Map(from, to.Block, keepForm.IsChecked == true);
        Busy("Ersetze Blöcke …");
        var before = (ushort[])p.Cells.Clone();
        long n = await Task.Run(() =>
        {
            long c = 0;
            for (int y = 0; y <= (parts == null ? cut : p.H - 1); y++) for (int z = 0; z < p.D; z++) for (int x = 0; x < p.W; x++)
            {
                long i = p.Index(x, y, z); int v = p.Cells[i]; if (v == 0) continue;
                int nv = map(v); if (nv == v) continue;
                if (parts != null && !parts.Any(t => InPart(t, x, y, z))) continue;
                p.Cells[i] = (ushort)nv; c++;
            }
            if (c > 0) store.Save(p);
            return c;
        });
        Busy(null);
        if (n == 0) { await Message("Nichts ersetzt", "Im gewählten Teil gibt es diesen Block nicht."); return; }
        undo = (p.Id, before, $"{n:N0} × {Blocks.DisplayName(from, true)} → {to.Name}");
        BtnUndo.IsVisible = true; ToolTip.SetTip(BtnUndo, "Rückgängig: " + undo.Value.text);
        SetProject(p, false);
        MatSub.Text = $"Ersetzt: {undo.Value.text}. " + MatSub.Text;
    }

    async Task UndoReplace()
    {
        var p = project; if (p == null || undo is not { } u || u.projectId != p.Id) { BtnUndo.IsVisible = false; return; }
        p.Cells = u.cells; undo = null; BtnUndo.IsVisible = false;
        Busy("Stelle wieder her …"); await Task.Run(() => store.Save(p)); Busy(null);
        SetProject(p, false);
    }

    async Task CopyMaterials()
    {
        if (project == null) return;
        var (_, what) = MatStates();
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Material für {project.Name} – {what}");
        foreach (var m in matAll) sb.AppendLine($"{m.Count,8:N0}  {m.Name}{(m.StackText.Length > 0 ? "  (" + m.StackText + ")" : "")}");
        sb.AppendLine(MatSpaceText.Text);
        if (Clipboard != null) await Clipboard.SetTextAsync(sb.ToString());
        BtnMatCopy.Content = "Kopiert ✓";
        DispatcherTimer.RunOnce(() => BtnMatCopy.Content = "Liste kopieren", TimeSpan.FromSeconds(2));
    }

    void FilterMat()
    {
        var q = MatSearch.Text?.Trim() ?? "";
        MatList.ItemsSource = q.Length == 0 ? matAll : matAll.Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /* ---------- kleine Dialoge ---------- */
    async Task Message(string title, string text)
    {
        var w = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var ok = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, MinWidth = 80, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        ok.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Thickness(22), Spacing = 14, Children = { new TextBlock { Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold }, new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, ok } };
        await w.ShowDialog(this);
    }

    async Task<bool> Confirm(string title, string text)
    {
        var w = new Window { Title = title, Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, CanResize = false };
        var yes = new Button { Content = "Löschen", Classes = { "accent" }, MinWidth = 90, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        var no = new Button { Content = "Abbrechen", MinWidth = 90, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
        bool r = false; yes.Click += (_, _) => { r = true; w.Close(); }; no.Click += (_, _) => w.Close();
        w.Content = new StackPanel { Margin = new Thickness(22), Spacing = 14, Children = { new TextBlock { Text = title, FontSize = 16, FontWeight = Avalonia.Media.FontWeight.SemiBold }, new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Children = { no, yes } } } };
        await w.ShowDialog(this);
        return r;
    }
}
