using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    public string StackText => Count >= 64 ? (Count >= 1728 ? $"{Count / 1728.0:0.#} Shulker" : $"{Count / 64} Stacks{(Count % 64 > 0 ? " + " + Count % 64 : "")}") : "";
    public Bitmap? Icon { get; init; }
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
            if (project == null || !ModeLayer.IsChecked == true) return;
            if (e.Key == Avalonia.Input.Key.PageUp) LayerSlider.Value = Math.Min(LayerSlider.Maximum, LayerSlider.Value + 1);
            if (e.Key == Avalonia.Input.Key.PageDown) LayerSlider.Value = Math.Max(0, LayerSlider.Value - 1);
        };
        Opened += async (_, _) => await Startup();
        Closing += (_, _) => store.Dispose();
    }

    void ShowHover(string s) { HoverText.Text = s; HoverBox.IsVisible = s.Length > 0; }

    void ShowTab(int t)
    {
        Nav2D.IsChecked = t == 0; Nav3D.IsChecked = t == 1; NavMat.IsChecked = t == 2;
        Plan.IsVisible = t == 0; View3.IsVisible = t == 1; MatPage.IsVisible = t == 2; HoverBox.IsVisible = false;
        Bar3D.IsVisible = t == 1; if (t == 1) Dispatcher.UIThread.Post(() => View3.Focus());
        StatusBox.IsVisible = t == 1 && StatusText.Text?.Length > 0;
        ModeRow.IsVisible = t == 0; LayerRow.IsVisible = t == 0 && ModeLayer.IsChecked == true;
        ViewHint.Text = t switch
        {
            0 => "Ziehen = verschieben · Mausrad = zoomen · Bild auf/ab = Schicht wechseln",
            1 => View3.Fly
                ? "Flugmodus: WASD = fliegen · Leertaste/C = hoch/runter · Maus ziehen = umsehen · Umschalt = schneller · F oder Esc = beenden"
                : "Links ziehen = drehen · Rechts ziehen = verschieben · Mausrad = zoomen · WASD, Leertaste, C = bewegen · F = Flugmodus",
            _ => "Alle Blöcke bis zur eingestellten Höhe, sortiert nach Menge.",
        };
        if (t == 2) BuildMaterials();
    }

    void Busy(string? text) { BusyBox.IsVisible = text != null; if (text != null) BusyText.Text = text; }

    /* ---------- Start ---------- */
    async Task Startup()
    {
        Updater.CleanUp(); _ = CheckUpdate();
        RefreshList(); UpdateDbInfo();
        if (double.TryParse(store.Get("mouse"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms)) MouseSlider.Value = ms;
        _ = LoadTextures(null);
        var last = store.Get("last"); var list = store.List();
        var id = list.Any(p => p.Id == last) ? last : list.FirstOrDefault()?.Id;
        if (Environment.GetEnvironmentVariable("BLOCKPLAN_OPEN") is { Length: > 0 } want) id = list.FirstOrDefault(p => p.Name.StartsWith(want, StringComparison.OrdinalIgnoreCase))?.Id ?? id;
        if (id != null) await OpenProject(id);
        else ShowEmpty();
        switch (Environment.GetEnvironmentVariable("BLOCKPLAN_TAB")) { case "3d": ShowTab(1); break; case "mat": ShowTab(2); break; case "layer": ModeLayer.IsChecked = true; ShowTab(0); break; }
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
        ApplyProjectToViews(refit);
        ApplyView();
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
        View3.SetTop(cut);
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
            var p = await Task.Run(() => { var pr = SchematicReader.ToProject(s, r.X0, r.Y0, r.Z0, r.X1, r.Y1, r.Z1, r.Dewater, r.Plants, r.Rot); store.Save(pr); return pr; });
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

    void BuildMaterials()
    {
        if (project == null) { MatList.ItemsSource = null; return; }
        icons.Clear();
        var counts = project.CountBlocks((int)CutSlider.Value);
        var byBase = new Dictionary<int, long>();
        foreach (var (v, c) in counts) { int b = Blocks.BaseOf(v); byBase[b] = byBase.GetValueOrDefault(b) + c; }
        matAll = byBase.OrderByDescending(kv => kv.Value).Select(kv => new MatRow { Name = Blocks.DisplayName(kv.Key, true), Count = kv.Value, Icon = MatIcon(kv.Key) }).ToList();
        MatTitle.Text = "Materialliste · " + project.Name;
        MatSub.Text = $"{matAll.Count} Sorten, {matAll.Sum(m => m.Count):N0} Blöcke bis Y {project.OriginY + (int)CutSlider.Value}";
        FilterMat();
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
