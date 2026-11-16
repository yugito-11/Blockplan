using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Blockplan.Core;

namespace Blockplan.Views;

/// <summary>Kleine Bausteine für die Seiten (Karten, Überschriften, Blocksymbole).</summary>
static class UI
{
    /// <summary>Farbe aus dem hellen/dunklen Design (Fallback grau).</summary>
    public static IBrush Res(string key) =>
        Application.Current is { } app && app.TryGetResource(key, app.ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;

    public static Border Card(Control c, double pad = 16) => new() { Classes = { "card" }, Padding = new Thickness(pad), Child = c };
    public static TextBlock H(string t) => new() { Text = t.ToUpperInvariant(), Classes = { "h" } };
    public static TextBlock Title(string t) => new() { Text = t, FontSize = 21, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    public static TextBlock Note(string t) => new() { Text = t, Classes = { "note" } };
    public static Button Btn(string t, Action a, bool accent = false)
    {
        var b = new Button { Content = t, Padding = new Thickness(10, 4) }; if (accent) b.Classes.Add("accent");
        b.Click += (_, _) => a(); return b;
    }
    public static StackPanel Row(params Control[] c) { var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 }; foreach (var x in c) s.Children.Add(x); return s; }

    static readonly Dictionary<(Atlas, int), Bitmap> icons = new();
    public static Bitmap Tile(Atlas atlas, int tile)
    {
        if (icons.TryGetValue((atlas, tile), out var b)) return b;
        var px = atlas.Tiles[tile]; var wb = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var fb = wb.Lock()) for (int y = 0; y < 16; y++) System.Runtime.InteropServices.Marshal.Copy(px, y * 64, fb.Address + y * fb.RowBytes, 64);
        return icons[(atlas, tile)] = wb;
    }
    public static Bitmap Icon(Atlas atlas, int v) => Tile(atlas, Blocks.Get(v).Shape == 'm' ? atlas.Cross(v) : atlas.Faces(v)[0]);

    /// <summary>Materialtabelle: Symbol, Name, Anzahl, Stacks.</summary>
    public static Control Material(Atlas atlas, Dictionary<int, long> m)
    {
        var sp = new StackPanel { Spacing = 2 };
        if (m.Count == 0) { sp.Children.Add(Note("Keine Blöcke.")); return sp; }
        sp.Children.Add(Note($"{m.Count} Sorten, {m.Values.Sum():N0} Blöcke"));
        foreach (var (v, n) in m.OrderByDescending(kv => kv.Value))
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*,90,120"), Height = 26 };
            var img = new Image { Source = Icon(atlas, v), Width = 18, Height = 18 }; RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.None);
            g.Children.Add(img);
            var name = new TextBlock { Text = Blocks.DisplayName(v, true), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0) }; Grid.SetColumn(name, 1); g.Children.Add(name);
            var cnt = new TextBlock { Text = n.ToString("N0"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas,monospace") }; Grid.SetColumn(cnt, 2); g.Children.Add(cnt);
            var st = new TextBlock { Text = n >= 64 ? $"{n / 64} St.{(n % 64 > 0 ? " + " + n % 64 : "")}" : "", HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Classes = { "note" } }; Grid.SetColumn(st, 3); g.Children.Add(st);
            sp.Children.Add(g);
        }
        return sp;
    }
}

/// <summary>Eine Schicht eines Bauplans, Block für Block mit Texturen.</summary>
public sealed class PartLayerView : Control
{
    Project? p; Part? t; Atlas? atlas; public int Layer;
    public void Set(Project pr, Part part, Atlas a, int layer) { p = pr; t = part; atlas = a; Layer = layer; InvalidateVisual(); }
    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x1a, 0x15)), new Rect(Bounds.Size), 8);
        if (p == null || t == null || atlas == null) return;
        var b = t.Box; int w = b[3] - b[0] + 1, d = b[5] - b[2] + 1; var m = t.Mask;
        double s = Math.Max(1, Math.Floor(Math.Min((Bounds.Width - 20) / w, (Bounds.Height - 20) / d))), ox = (Bounds.Width - w * s) / 2, oz = (Bounds.Height - d * s) / 2;
        int y = Layer - b[1];
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            for (int z = 0; z < d; z++) for (int x = 0; x < w; x++)
            {
                var r = new Rect(ox + x * s, oz + z * s, s, s);
                long mi = ((long)y * d + z) * w + x; int v = mi < m.LongLength && m[mi] != 0 ? p.At(b[0] + x, Layer, b[2] + z) : 0;
                if (v == 0)
                {   // Schicht darunter blass zur Orientierung
                    long mb = ((long)(y - 1) * d + z) * w + x; int vb = y > 0 && mb < m.LongLength && m[mb] != 0 ? p.At(b[0] + x, Layer - 1, b[2] + z) : 0;
                    if (vb != 0) using (ctx.PushOpacity(.25)) ctx.DrawImage(UI.Tile(atlas, atlas.Faces(vb)[2]), new Rect(0, 0, 16, 16), r);
                    continue;
                }
                ctx.DrawImage(UI.Tile(atlas, Blocks.Get(v).Shape == 'm' ? atlas.Cross(v) : atlas.Faces(v)[2]), new Rect(0, 0, 16, 16), r);
            }
        if (s >= 8)
        {
            var pen = new Pen(new SolidColorBrush(Colors.Black, .25), 1);
            for (int x = 0; x <= w; x++) ctx.DrawLine(pen, new Point(ox + x * s, oz), new Point(ox + x * s, oz + d * s));
            for (int z = 0; z <= d; z++) ctx.DrawLine(pen, new Point(ox, oz + z * s), new Point(ox + w * s, oz + z * s));
        }
        ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(0xec, 0x5f, 0x9b)), 1.5), new Rect(ox, oz, w * s, d * s));
    }
}

/// <summary>Seite „Baupläne“: Kategorien und Bauteile, je Bauteil Schichten und Material.</summary>
public sealed class PartsPage : UserControl
{
    readonly Func<(Project? p, PlanData? pl, Atlas a)> ctx; readonly Action save; readonly Action<Part> show;
    public string? Selected; int layer;
    public PartsPage(Func<(Project?, PlanData?, Atlas)> context, Action onSave, Action<Part> showInPlan) { ctx = context; save = onSave; show = showInPlan; }

    public void Refresh()
    {
        var (p, pl, atlas) = ctx();
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("270,*"), Margin = new Thickness(20) };
        if (p == null || pl == null) { Content = UI.Card(UI.Note("Kein Projekt geöffnet.")); return; }
        var parts = pl.Parts.Where(t => !t.Base).ToList(); var cats = pl.Cats;
        // Liste links
        var list = new StackPanel { Spacing = 2 };
        list.Children.Add(UI.H("Baupläne"));
        list.Children.Add(UI.Btn("Übersicht", () => { Selected = null; Refresh(); }));
        foreach (var c in cats)
        {
            var ts = parts.Where(t => t.Cat == c.Id).OrderBy(t => t.No).ToList(); if (ts.Count == 0) continue;
            list.Children.Add(new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 10, 0, 2) });
            foreach (var t in ts)
            {
                var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(10, 4), Background = t.Id == Selected ? UI.Res("AccentBrush") : Brushes.Transparent };
                var lbl = new TextBlock { Text = pl.Label(t), TextTrimming = TextTrimming.CharacterEllipsis }; var cnt = new TextBlock { Text = t.N.ToString("N0"), Classes = { "note" }, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, [DockPanel.DockProperty] = Dock.Right };
                if (t.Id == Selected) { lbl.Foreground = Brushes.White; cnt.Foreground = Brushes.White; }
                b.Content = new DockPanel { Children = { cnt, lbl } };
                var id = t.Id; b.Click += (_, _) => { Selected = id; layer = -1; Refresh(); };
                list.Children.Add(b);
            }
        }
        var left = UI.Card(new ScrollViewer { Content = list }, 10); left.Margin = new Thickness(0, 0, 16, 0);
        grid.Children.Add(left);
        Control detail;
        var sel = parts.FirstOrDefault(t => t.Id == Selected);
        if (sel == null)
        {
            var sp = new StackPanel { Spacing = 12 };
            sp.Children.Add(UI.Title("Übersicht"));
            sp.Children.Add(UI.Note(parts.Count > 0 ? $"{parts.Count} Baupläne in {cats.Count(c => parts.Any(t => t.Cat == c.Id))} Kategorien." : "Noch keine Baupläne."));
            sp.Children.Add(UI.Note("Neuen Bauplan erstellen: im 2D-Plan rechts unter „Werkzeuge“ auf „Bauplan aus Bereich“ klicken und mit der Maus einen Rahmen ziehen. Danach wählst du Name, Kategorie und Höhe."));
            var cards = new WrapPanel();
            foreach (var c in cats)
            {
                var ts = parts.Where(t => t.Cat == c.Id).ToList(); if (ts.Count == 0) continue;
                var cc = UI.Card(new StackPanel { Spacing = 4, Children = { new TextBlock { Text = c.Name, FontWeight = FontWeight.SemiBold }, UI.Note($"{ts.Count} Bauteile · {ts.Sum(t => t.N):N0} Blöcke") } }, 12);
                cc.Margin = new Thickness(0, 0, 10, 10); cc.Width = 230; cards.Children.Add(cc);
            }
            sp.Children.Add(cards);
            detail = UI.Card(new ScrollViewer { Content = sp });
        }
        else
        {
            var b = sel.Box;
            if (layer < b[1] || layer > b[4])
            {   // mit der Schicht beginnen, in der die meisten Blöcke liegen
                var mk = sel.Mask; int w = b[3] - b[0] + 1, d = b[5] - b[2] + 1, best = 0; long bn = -1;
                for (int y = 0; y <= b[4] - b[1]; y++) { long n = 0; long o = (long)y * w * d; for (long k = 0; k < (long)w * d && o + k < mk.LongLength; k++) n += mk[o + k]; if (n > bn) { bn = n; best = y; } }
                layer = b[1] + best;
            }
            var sp = new StackPanel { Spacing = 12 };
            var name = new TextBox { Text = sel.Name.Length > 0 ? sel.Name : pl.Label(sel), FontSize = 18, FontWeight = FontWeight.SemiBold };
            name.LostFocus += (_, _) => { if (name.Text is { Length: > 0 } nt && nt != pl.Label(sel)) { sel.Name = nt; save(); Refresh(); } };
            var cat = new ComboBox { ItemsSource = cats.Select(c => c.Name).ToList(), SelectedIndex = cats.FindIndex(c => c.Id == sel.Cat), MinWidth = 160 };
            cat.SelectionChanged += (_, _) => { if (cat.SelectedIndex >= 0 && cats[cat.SelectedIndex].Id != sel.Cat) { sel.Cat = cats[cat.SelectedIndex].Id; save(); Refresh(); } };
            var del = new Button { Content = "Löschen", Foreground = UI.Res("DangerBrush") };
            del.Click += (_, _) => { if ((string?)del.Tag != "sure") { del.Tag = "sure"; del.Content = "Wirklich löschen?"; return; } pl.Remove("parts", sel); foreach (var s in pl.Stages) { var l = s.Parts; if (l.Remove(sel.Id)) s.Parts = l; } Selected = null; save(); Refresh(); };
            sp.Children.Add(name);
            sp.Children.Add(UI.Row(new TextBlock { Text = "Kategorie", VerticalAlignment = VerticalAlignment.Center }, cat, UI.Btn("Im 2D-Plan zeigen", () => show(sel)), del));
            sp.Children.Add(UI.Note($"{sel.N:N0} Blöcke · {b[3] - b[0] + 1}×{b[5] - b[2] + 1} Grundfläche, {b[4] - b[1] + 1} hoch · Startecke X {p.OriginX + b[0]}  Y {p.OriginY + b[1]}  Z {p.OriginZ + b[2]}"));
            // Schichten
            var lv = new PartLayerView { Height = 420 }; lv.Set(p, sel, atlas, layer);
            var lt = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas,monospace"), MinWidth = 150 };
            void SetL(int l) { layer = Math.Clamp(l, b[1], b[4]); lv.Set(p, sel, atlas, layer); lt.Text = $"Schicht {layer - b[1] + 1} von {b[4] - b[1] + 1} · Y {p.OriginY + layer}"; }
            SetL(layer);
            var sl = new Slider { Minimum = b[1], Maximum = Math.Max(b[1] + .001, b[4]), Value = layer, TickFrequency = 1, IsSnapToTickEnabled = true, Width = 260 };
            sl.PropertyChanged += (_, e) => { if (e.Property == RangeBase.ValueProperty) SetL((int)sl.Value); };
            sp.Children.Add(UI.Row(UI.Btn("◀", () => sl.Value = Math.Max(b[1], sl.Value - 1)), lt, UI.Btn("▶", () => sl.Value = Math.Min(b[4], sl.Value + 1)), sl));
            sp.Children.Add(lv);
            sp.Children.Add(UI.H("Material"));
            sp.Children.Add(UI.Material(atlas, pl.Material(new[] { sel })));
            detail = UI.Card(new ScrollViewer { Content = sp });
        }
        Grid.SetColumn(detail, 1); grid.Children.Add(detail);
        Content = grid;
    }
}

/// <summary>Seite „Etappen“: Bau in Abschnitte teilen, je Abschnitt Bauteile und Material.</summary>
public sealed class StagesPage : UserControl
{
    readonly Func<(Project? p, PlanData? pl, Atlas a)> ctx; readonly Action save;
    public string? Selected;
    public StagesPage(Func<(Project?, PlanData?, Atlas)> context, Action onSave) { ctx = context; save = onSave; }

    public void Refresh()
    {
        var (p, pl, atlas) = ctx();
        if (p == null || pl == null) { Content = UI.Card(UI.Note("Kein Projekt geöffnet.")); return; }
        var ss = pl.Stages; var parts = pl.Parts.Where(t => !t.Base).ToList();
        if (Selected == null || ss.All(s => s.Id != Selected)) Selected = ss.FirstOrDefault()?.Id;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("270,*"), Margin = new Thickness(20) };
        var list = new StackPanel { Spacing = 4 }; list.Children.Add(UI.H("Etappen"));
        for (int i = 0; i < ss.Count; i++)
        {
            var s = ss[i]; var ps = parts.Where(t => s.Parts.Contains(t.Id)).ToList();
            var b = new Button { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(10, 6), Background = s.Id == Selected ? UI.Res("AccentBrush") : Brushes.Transparent };
            var rt = new TextBlock { Text = s.Done ? "✓" : $"{ps.Count} Teile", [DockPanel.DockProperty] = Dock.Right };
            var lbl = new TextBlock { Text = $"{i + 1}. {s.Name}", TextDecorations = s.Done ? TextDecorations.Strikethrough : null };
            if (s.Id == Selected) { rt.Foreground = Brushes.White; lbl.Foreground = Brushes.White; }
            b.Content = new DockPanel { Children = { rt, lbl } };
            var id = s.Id; b.Click += (_, _) => { Selected = id; Refresh(); };
            list.Children.Add(b);
        }
        if (ss.Count == 0) list.Children.Add(UI.Note("Noch keine."));
        list.Children.Add(UI.Btn("+ Neue Etappe", () => { var n = pl.AddStage("Etappe " + (ss.Count + 1)); Selected = n.Id; save(); Refresh(); }, true));
        var left = UI.Card(new ScrollViewer { Content = list }, 10); left.Margin = new Thickness(0, 0, 16, 0); grid.Children.Add(left);
        var sel = ss.FirstOrDefault(s => s.Id == Selected);
        var sp = new StackPanel { Spacing = 12 };
        if (sel == null)
        {
            sp.Children.Add(UI.Title("Etappen"));
            sp.Children.Add(UI.Note("Teile den Bau in Abschnitte ein, z. B. „Fundament“, „Mauerring“, „Türme“. Jede Etappe bekommt Bauteile aus dem Reiter Baupläne, und du siehst, welches Material sie braucht."));
        }
        else
        {
            int idx = ss.IndexOf(sel);
            var name = new TextBox { Text = sel.Name, FontSize = 18, FontWeight = FontWeight.SemiBold, MinWidth = 260 };
            name.LostFocus += (_, _) => { if (name.Text is { Length: > 0 } nt && nt != sel.Name) { sel.Name = nt; save(); Refresh(); } };
            var del = new Button { Content = "Löschen", Foreground = UI.Res("DangerBrush") };
            del.Click += (_, _) => { if ((string?)del.Tag != "sure") { del.Tag = "sure"; del.Content = "Wirklich löschen?"; return; } pl.Remove("stages", sel); Selected = null; save(); Refresh(); };
            var up = UI.Btn("▲", () => { pl.Move("stages", sel, -1); save(); Refresh(); }); up.IsEnabled = idx > 0;
            var dn = UI.Btn("▼", () => { pl.Move("stages", sel, 1); save(); Refresh(); }); dn.IsEnabled = idx < ss.Count - 1;
            sp.Children.Add(UI.Row(name, up, dn, del));
            var done = new CheckBox { Content = "als fertig markieren", IsChecked = sel.Done };
            done.IsCheckedChanged += (_, _) => { sel.Done = done.IsChecked == true; save(); Refresh(); };
            var chosen = parts.Where(t => sel.Parts.Contains(t.Id)).ToList();
            sp.Children.Add(UI.Row(UI.Note($"{chosen.Count} Bauteile · {chosen.Sum(t => t.N):N0} Blöcke"), done));
            sp.Children.Add(UI.H("Bauteile"));
            if (parts.Count == 0) sp.Children.Add(UI.Note("Noch keine Baupläne. Erstelle sie im 2D-Plan mit „Bauplan aus Bereich“."));
            foreach (var c in pl.Cats)
            {
                var ts = parts.Where(t => t.Cat == c.Id).OrderBy(t => t.No).ToList(); if (ts.Count == 0) continue;
                var all = new CheckBox { Content = $"{c.Name} ({ts.Count})", FontWeight = FontWeight.SemiBold, IsChecked = ts.All(t => sel.Parts.Contains(t.Id)) };
                all.IsCheckedChanged += (_, _) => { var l = sel.Parts.Where(x => ts.All(t => t.Id != x)).ToList(); if (all.IsChecked == true) l.AddRange(ts.Select(t => t.Id)); sel.Parts = l; save(); Refresh(); };
                sp.Children.Add(all);
                var wrap = new WrapPanel { Margin = new Thickness(24, 0, 0, 0) };
                foreach (var t in ts)
                {
                    var cb = new CheckBox { Content = pl.Label(t), IsChecked = sel.Parts.Contains(t.Id), Margin = new Thickness(0, 0, 16, 0), MinWidth = 170 };
                    var tid = t.Id; cb.IsCheckedChanged += (_, _) => { var l = sel.Parts.Where(x => x != tid).ToList(); if (cb.IsChecked == true) l.Add(tid); sel.Parts = l; save(); Refresh(); };
                    wrap.Children.Add(cb);
                }
                sp.Children.Add(wrap);
            }
            sp.Children.Add(UI.H("Material dieser Etappe"));
            sp.Children.Add(UI.Material(atlas, pl.Material(chosen)));
        }
        var right = UI.Card(new ScrollViewer { Content = sp }); Grid.SetColumn(right, 1); grid.Children.Add(right);
        Content = grid;
    }
}

/// <summary>Seite „To-dos“: gemeinsame Aufgabenliste mit Bezug auf Etappen oder Baupläne.</summary>
public sealed class TodosPage : UserControl
{
    readonly Func<(Project? p, PlanData? pl, Atlas a)> ctx; readonly Action save; readonly Func<string> me; readonly Action<string> setMe; readonly Action<string> openRef;
    bool showDone;
    public TodosPage(Func<(Project?, PlanData?, Atlas)> context, Action onSave, Func<string> getName, Action<string> setName, Action<string> openReference)
    { ctx = context; save = onSave; me = getName; setMe = setName; openRef = openReference; }

    static string Ago(long t)
    {
        var s = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - t) / 1000;
        return s < 60 ? "gerade eben" : s < 3600 ? $"vor {s / 60} min" : s < 86400 ? $"vor {s / 3600} h" : DateTimeOffset.FromUnixTimeMilliseconds(t).LocalDateTime.ToString("d");
    }

    public void Refresh()
    {
        var (p, pl, _) = ctx();
        if (p == null || pl == null) { Content = UI.Card(UI.Note("Kein Projekt geöffnet.")); return; }
        var refs = pl.Stages.Select(s => ("s:" + s.Id, "Etappe: " + s.Name)).Concat(pl.Parts.Where(t => !t.Base).Take(400).Select(t => ("p:" + t.Id, pl.Label(t)))).ToList();
        string RefName(string r) => refs.FirstOrDefault(x => x.Item1 == r).Item2 ?? "";
        var sp = new StackPanel { Spacing = 12, MaxWidth = 900 };
        sp.Children.Add(UI.Title("Team-To-dos"));
        sp.Children.Add(UI.Note("Die Liste wird mit dem Projekt gespeichert und beim Export ins Artefakt mitgenommen."));
        var text = new TextBox { PlaceholderText = "Neue Aufgabe, z. B. „Sand für die Bucht sammeln“" };
        var who = new TextBox { PlaceholderText = "Für wen? (optional)", Width = 170 };
        var rf = new ComboBox { ItemsSource = new[] { "Kein Bezug" }.Concat(refs.Select(r => r.Item2)).ToList(), SelectedIndex = 0, Width = 220 };
        void Add()
        {
            if (string.IsNullOrWhiteSpace(text.Text)) return;
            pl.AddTodo(text.Text.Trim(), who.Text?.Trim() ?? "", rf.SelectedIndex > 0 ? refs[rf.SelectedIndex - 1].Item1 : "", me()); save(); Refresh();
        }
        text.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        var addRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
        addRow.Children.Add(text); Grid.SetColumn(who, 1); addRow.Children.Add(who); Grid.SetColumn(rf, 2); addRow.Children.Add(rf);
        var addB = UI.Btn("Hinzufügen", Add, true); Grid.SetColumn(addB, 3); addRow.Children.Add(addB);
        sp.Children.Add(addRow);
        var meBox = new TextBox { Text = me(), Width = 220, PlaceholderText = "Dein Name" };
        meBox.LostFocus += (_, _) => setMe(meBox.Text?.Trim() ?? "");
        sp.Children.Add(UI.Row(new TextBlock { Text = "Dein Name (wird bei neuen Aufgaben vermerkt)", VerticalAlignment = VerticalAlignment.Center, Classes = { "note" } }, meBox));
        var all = pl.Todos; var open = all.Where(t => !t.Done).ToList(); var done = all.Where(t => t.Done).ToList();
        Control Row(Todo t)
        {
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 4) };
            var cb = new CheckBox { IsChecked = t.Done, VerticalAlignment = VerticalAlignment.Center };
            cb.IsCheckedChanged += (_, _) => { t.Done = cb.IsChecked == true; t.DoneBy = t.Done ? me() : ""; t.J["doneAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); save(); Refresh(); };
            g.Children.Add(cb);
            var tx = new TextBlock { Text = t.Text, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, TextDecorations = t.Done ? TextDecorations.Strikethrough : null, Opacity = t.Done ? .6 : 1, FontSize = 14.5 };
            Grid.SetColumn(tx, 1); g.Children.Add(tx);
            if (t.Ref.Length > 0 && RefName(t.Ref) is { Length: > 0 } rn) { var rb = UI.Btn(rn, () => openRef(t.Ref)); rb.Classes.Add("link"); Grid.SetColumn(rb, 2); g.Children.Add(rb); }
            var x = UI.Btn("×", () => { pl.Remove("todos", t); save(); Refresh(); }); Grid.SetColumn(x, 3); g.Children.Add(x);
            var meta = UI.Note((t.Who.Length > 0 ? "für " + t.Who + " · " : "") + (t.By.Length > 0 ? "von " + t.By + " · " : "") + (t.At > 0 ? Ago(t.At) : "") + (t.Done && t.DoneBy.Length > 0 ? " · erledigt von " + t.DoneBy : ""));
            Grid.SetColumn(meta, 1); Grid.SetRow(meta, 1); Grid.SetColumnSpan(meta, 3); g.Children.Add(meta);
            return new Border { BorderBrush = UI.Res("LineBrush"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 4), Child = g };
        }
        var listP = new StackPanel();
        if (open.Count == 0) listP.Children.Add(UI.Note("Keine offenen Aufgaben."));
        foreach (var t in open) listP.Children.Add(Row(t));
        sp.Children.Add(listP);
        if (done.Count > 0)
        {
            sp.Children.Add(UI.Btn(showDone ? "Erledigte ausblenden" : $"{done.Count} erledigte anzeigen", () => { showDone = !showDone; Refresh(); }));
            if (showDone) foreach (var t in done) sp.Children.Add(Row(t));
        }
        Content = new Border { Margin = new Thickness(20), Child = UI.Card(new ScrollViewer { Content = sp }) };
    }
}
