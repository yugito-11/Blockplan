using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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
        InitPlanning(); InitLive();
        Opened += async (_, _) => await Startup();
        Closing += (_, _) => store.Dispose();
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
        StatusBox.IsVisible = t == 1 && StatusText.Text?.Length > 0;
        ModeRow.IsVisible = t == 0; LayerRow.IsVisible = t == 0 && ModeLayer.IsChecked == true;
        ToolsCard.IsVisible = GuidesCard.IsVisible = t == 0; LiveCard.IsVisible = t is 0 or 1;
        ViewHint.Text = t switch
        {
            0 => "Ziehen = verschieben · Mausrad = zoomen · rechte Maustaste ziehen = messen · Bild auf/ab = Schicht wechseln",
            1 => View3.Fly
                ? "Flugmodus: WASD = fliegen · Leertaste/C = hoch/runter · Maus ziehen = umsehen · Umschalt = schneller · F oder Esc = beenden"
                : "Links ziehen = drehen · Rechts ziehen = verschieben · Mausrad = zoomen · WASD, Leertaste, C = bewegen · F = Flugmodus",
            2 => "Alle Blöcke bis zur eingestellten Höhe, sortiert nach Menge.",
            3 => "Baupläne sind Teile deines Baus. Neue erstellst du im 2D-Plan mit „Bauplan aus Bereich“.",
            4 => "Etappen fassen Baupläne zu Bauabschnitten zusammen, mit Material je Abschnitt.",
            _ => "Aufgaben fürs Team, auf Wunsch mit Bezug zu einer Etappe oder einem Bauplan.",
        };
        if (t == 2) BuildMaterials();
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
        BtnLiveStop.Click += (_, _) => { live?.Stop(); live = null; Plan.Live = null; View3.SetLive(null, false, false); Plan.LiveReset(); UpdateLiveUi(); };
        LiveProgChk.IsCheckedChanged += (_, _) => ApplyLiveView();
        LiveSurrChk.IsCheckedChanged += (_, _) => ApplyLiveView();
        Guides3DChk.IsCheckedChanged += (_, _) => View3.SetGuides(plan, Guides3DChk.IsChecked == true);
        Closing += (_, _) => live?.Dispose();
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
            live.PlayersChanged += () => Dispatcher.UIThread.Post(() =>
            {
                Plan.InvalidateVisual(); View3.SetPlayers();
                if ((DateTime.Now - lastPlayersUi).TotalMilliseconds > 700) { lastPlayersUi = DateTime.Now; RenderLivePlayers(); }
            });
            live.Changed += () => Dispatcher.UIThread.Post(() =>
            {
                if (live == null) return;
                var (all, cells, chunks) = live.TakeChanges();
                View3.LiveChanged(all, cells, chunks); Plan.LiveChanged(chunks);
                if (all) Plan.LiveReset();
                UpdateProgress();
            });
        }
        live.Connect(link, project);
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

    void InitPlanning()
    {
        (Project?, PlanData?, Atlas) Ctx() => (project, plan, atlas);
        partsPage = new PartsPage(Ctx, ScheduleSave, ShowPartInPlan); PartsHost.Content = partsPage;
        stagesPage = new StagesPage(Ctx, ScheduleSave); StagesHost.Content = stagesPage;
        todosPage = new TodosPage(Ctx, ScheduleSave, () => store.Get("who") ?? "", n => store.Set("who", n), OpenRef); TodosHost.Content = todosPage;
        NavParts.Click += (_, _) => ShowTab(3); NavStages.Click += (_, _) => ShowTab(4); NavTodos.Click += (_, _) => ShowTab(5);
        ToolArea.Click += (_, _) => { ToolPoint.IsChecked = false; Plan.Mode = ToolArea.IsChecked == true ? "area" : ""; ViewHint.Text = Plan.Mode == "area" ? "Ziehe im Plan einen Rahmen um den Teil, der ein Bauplan werden soll." : ViewHint.Text; };
        ToolPoint.Click += (_, _) => { ToolArea.IsChecked = false; Plan.Mode = ToolPoint.IsChecked == true ? "point" : ""; ViewHint.Text = Plan.Mode == "point" ? "Klicke im Plan auf die Stelle für den Punkt." : ViewHint.Text; };
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
            if (g.Type == "line")
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
            H("Ab Höhe Y", oy + g.Y, -64, v => g.Y = v - oy, 0); H("Höhe (Blöcke)", g.H, 1, v => g.H = v, 1); row++;
            box.Children.Add(grid);
            var lay = new Button { Content = "Auf aktuelle Ebene setzen", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch, HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center };
            ToolTip.SetTip(lay, "Unterkante auf die Ebene, die gerade im 2D-Plan gezeigt wird");
            lay.Click += (_, _) => { g.Y = (int)LayerSlider.Value; ScheduleSave(); RenderGuides(); Plan.InvalidateVisual(); };
            box.Children.Add(lay);
            GuidesList.Children.Add(new Border { BorderBrush = UI.Res("LineBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Child = box });
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
        RefreshList(); UpdateDbInfo();
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
