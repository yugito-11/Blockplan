using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Blockplan.Core;

namespace Blockplan.Views;

/// <summary>„An Bautrupp senden“: Etappen, Baupläne oder Ebenen als Aufgaben in eine Bautrupp-Gruppe schreiben.</summary>
public sealed class BautruppDialog : Window
{
    readonly Project p; readonly PlanData pl;
    readonly Func<string, string?> get; readonly Action<string, string?> set;
    readonly TextBox db = new() { PlaceholderText = "https://…firebasedatabase.app oder Einladungs-Link" };
    readonly TextBox group = new() { PlaceholderText = "Gruppencode" };
    readonly TextBox me = new() { PlaceholderText = "Dein Minecraft-Name" };
    readonly TextBox name = new();
    readonly ComboBox mode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly NumericUpDown layerH = new() { Value = 4, Minimum = 1, Maximum = 64, FormatString = "0", Increment = 1, Width = 130 };
    readonly ComboBox art = new() { ItemsSource = new[] { "Sammeln + Platzieren", "Platzieren", "Nur sammeln" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly ComboBox dim = new() { ItemsSource = new[] { "Oberwelt", "Nether", "End" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    readonly CheckBox place = new(), order = new() { Content = "In Reihenfolge freischalten (nächste Etappe erst, wenn die vorige fertig ist)", IsChecked = true };
    readonly CheckBox skipDone = new() { Content = "Fertige Etappen weglassen", IsChecked = true };
    readonly CheckBox rest = new() { Content = "Übrige Blöcke als Material eintragen", IsChecked = true };
    readonly WrapPanel players = new();
    readonly TextBlock playersNote = new() { Classes = { "note" }, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock preview = new() { TextWrapping = TextWrapping.Wrap };
    readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    readonly Button send = new() { Content = "Senden", Classes = { "accent" }, MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly HashSet<string> who = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Bautrupp.Mode> modes = new();
    DispatcherTimer? previewTimer;

    public BautruppDialog(Project project, PlanData plan, Func<string, string?> getSetting, Action<string, string?> setSetting)
    {
        p = project; pl = plan; get = getSetting; set = setSetting;
        Title = "An Bautrupp senden"; Width = 560; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        db.Text = get("btDb") ?? ""; group.Text = get("btGroup") ?? ""; me.Text = get("who") ?? ""; name.Text = p.Name;
        int ns = pl.Stages.Count, np = pl.Parts.Count(t => !t.Base);
        var labels = new List<string>();
        if (ns > 0) { modes.Add(Bautrupp.Mode.Stages); labels.Add($"Etappen ({ns}) – je Etappe eine Aufgabe"); }
        if (np > 0) { modes.Add(Bautrupp.Mode.Parts); labels.Add($"Baupläne ({np}) – je Bauplan eine Aufgabe"); }
        modes.Add(Bautrupp.Mode.Layers); labels.Add("Ebenen – von unten nach oben");
        mode.ItemsSource = labels; mode.SelectedIndex = 0;
        bool hasPlace = p.OriginX != 0 || p.OriginZ != 0 || p.OriginY != 64;
        place.Content = $"Baustelle in der Welt bei X {p.OriginX}  Y {p.OriginY}  Z {p.OriginZ} – die Mod zählt Block für Block nach Plan";
        place.IsChecked = hasPlace;

        TextBlock L(string t) => new() { Text = t, Classes = { "note" }, Margin = new Thickness(0, 4, 0, 0) };
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 6 };
        root.Children.Add(new TextBlock { Text = "An Bautrupp senden", FontSize = 17, FontWeight = FontWeight.SemiBold });
        root.Children.Add(new TextBlock { Text = "Legt in eurer Bautrupp-Gruppe ein neues Projekt mit Aufgaben an. Jede Blockart wird ein Ziel; Mod und Webseite zählen dann automatisch mit.", Classes = { "note" }, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(L("Datenbank-Adresse (aus Bautrupp → Einstellungen)")); root.Children.Add(db);
        var g2 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        g2.Children.Add(new StackPanel { Children = { L("Gruppencode"), group } });
        g2.Children.Add(new StackPanel { [Grid.ColumnProperty] = 2, Children = { L("Dein Name"), me } });
        root.Children.Add(g2);
        root.Children.Add(L("Name des Projekts in Bautrupp")); root.Children.Add(name);
        root.Children.Add(L("Aufteilen nach")); root.Children.Add(mode);
        var layerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = "Ebenen je Aufgabe", VerticalAlignment = VerticalAlignment.Center }, layerH } };
        root.Children.Add(layerRow);
        var g3 = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*") };
        g3.Children.Add(new StackPanel { Children = { L("Ziel"), art } });
        g3.Children.Add(new StackPanel { [Grid.ColumnProperty] = 2, Children = { L("Dimension"), dim } });
        root.Children.Add(g3);
        foreach (var c in new[] { place, order, skipDone, rest }) { c.Margin = new Thickness(0, 2, 0, 0); if (c.Content is string s) c.Content = new TextBlock { Text = s, TextWrapping = TextWrapping.Wrap }; root.Children.Add(c); }
        root.Children.Add(L("Für wen (keiner ausgewählt = alle)")); root.Children.Add(players); root.Children.Add(playersNote);
        root.Children.Add(new Border { Background = UI.Res("SunkBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8), Margin = new Thickness(0, 8, 0, 0), Child = preview });
        root.Children.Add(status);
        var close = new Button { Content = "Schließen", MinWidth = 100, HorizontalContentAlignment = HorizontalAlignment.Center };
        close.Click += (_, _) => Close();
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0), Children = { close, send } });
        Content = new ScrollViewer { Content = root, MaxHeight = 860 };

        void ModeChanged()
        {
            var m = CurMode();
            layerRow.IsVisible = m == Bautrupp.Mode.Layers;
            skipDone.IsVisible = m == Bautrupp.Mode.Stages;
            order.IsVisible = m != Bautrupp.Mode.Parts;
            Schedule();
        }
        mode.SelectionChanged += (_, _) => ModeChanged();
        art.SelectionChanged += (_, _) => Schedule();
        layerH.ValueChanged += (_, _) => Schedule();
        foreach (var c in new[] { place, order, skipDone, rest }) c.IsCheckedChanged += (_, _) => Schedule();
        db.LostFocus += (_, _) => { if (Bautrupp.ParseLink(db.Text ?? "", out var d, out var g)) { db.Text = d; group.Text = g; } LoadPlayers(); };
        group.LostFocus += (_, _) => LoadPlayers();
        send.Click += async (_, _) => await Send();
        Opened += (_, _) => { ModeChanged(); LoadPlayers(); };
    }

    Bautrupp.Mode CurMode() => modes[Math.Clamp(mode.SelectedIndex, 0, modes.Count - 1)];

    Bautrupp.Options Opts() => new()
    {
        Mode = CurMode(), LayerHeight = (int)(layerH.Value ?? 4),
        Art = art.SelectedIndex switch { 1 => "platzieren", 2 => "sammeln", _ => "kombi" },
        Dim = dim.SelectedIndex switch { 1 => "nether", 2 => "end", _ => "overworld" },
        WithPlace = place.IsChecked == true, Order = order.IsChecked == true, SkipDone = skipDone.IsChecked == true, RestAsMaterial = rest.IsChecked == true,
        Who = string.Join(",", who), Me = me.Text?.Trim() ?? "", Name = string.IsNullOrWhiteSpace(name.Text) ? p.Name : name.Text.Trim(),
    };

    /// <summary>Vorschau gesammelt nach kurzer Pause, da große Projekte etwas Rechenzeit brauchen.</summary>
    void Schedule()
    {
        previewTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => { previewTimer!.Stop(); Preview(); });
        previewTimer.Stop(); previewTimer.Start();
    }

    void Preview()
    {
        var o = Opts(); var gs = Bautrupp.Groups(p, pl, o);
        if (gs.Count == 0)
        {
            preview.Text = o.Mode == Bautrupp.Mode.Stages ? "Keine Etappe mit Bauteilen (oder alle schon fertig)." : "Keine Blöcke gefunden.";
            send.IsEnabled = false; return;
        }
        long blocks = 0; var items = new HashSet<string>();
        foreach (var g in gs) foreach (var c in g.Cells) foreach (var (id, n) in Bautrupp.Items(p.Cells[c])) { blocks += n; items.Add(id); }
        var lines = gs.Take(8).Select((g, i) => $"{i + 1}. {g.Title} – {g.Cells.Count:N0} Blöcke").ToList();
        if (gs.Count > 8) lines.Add($"… und {gs.Count - 8} weitere");
        preview.Text = $"{gs.Count} Aufgaben · {items.Count} Blockarten · {blocks:N0} Blöcke\n" + string.Join("\n", lines)
            + (o.WithPlace ? "" : "\nOhne Baustelle zählt Bautrupp nur, was gesammelt wird.");
        send.IsEnabled = true;
    }

    bool Conn(out string d, out string g)
    {
        d = Bautrupp.NormUrl(db.Text ?? ""); g = Bautrupp.NormGroup(group.Text ?? "");
        if (Bautrupp.ParseLink(db.Text ?? "", out var ld, out var lg)) { d = ld; g = lg; }
        return d.StartsWith("http") && g.Length > 0;
    }

    string loaded = "";
    async void LoadPlayers()
    {
        if (!Conn(out var d, out var g)) { playersNote.Text = "Erst Adresse und Gruppencode eintragen."; return; }
        if (loaded == d + "|" + g) return;
        loaded = d + "|" + g; playersNote.Text = "Lade Spieler …";
        try
        {
            var list = await Bautrupp.Players(d, g);
            players.Children.Clear();
            foreach (var n in list)
            {
                var cb = new CheckBox { Content = n, IsChecked = who.Contains(n), Margin = new Thickness(0, 0, 14, 0) };
                cb.IsCheckedChanged += (_, _) => { if (cb.IsChecked == true) who.Add(n); else who.Remove(n); };
                players.Children.Add(cb);
            }
            playersNote.Text = list.Count == 0 ? "Verbunden, aber noch keine Spieler in der Gruppe." : "";
            set("btDb", d); set("btGroup", g);
        }
        catch (Exception e) { loaded = ""; playersNote.Text = "Keine Verbindung: " + e.Message; }
    }

    async System.Threading.Tasks.Task Send()
    {
        if (!Conn(out var d, out var g)) { status.Text = "Bitte Datenbank-Adresse und Gruppencode eintragen."; return; }
        var o = Opts();
        if (o.Me.Length == 0) { status.Text = "Bitte deinen Namen eintragen (so steht es im Verlauf)."; return; }
        set("btDb", d); set("btGroup", g); set("who", o.Me);
        send.IsEnabled = false; status.Text = "Sende …";
        try
        {
            var payload = await System.Threading.Tasks.Task.Run(() => Bautrupp.Build(p, pl, o));
            await Bautrupp.Send(d, g, payload);
            // merken, welches Bautrupp-Projekt dazugehört (für späteren Abgleich)
            var sent = p.Extra["bautrupp"] as JsonArray ?? new JsonArray(); p.Extra["bautrupp"] = sent;
            sent.Add(new JsonObject { ["db"] = d, ["gruppe"] = g, ["projekt"] = payload.ProjectId, ["name"] = o.Name, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            Sent = true;
            status.Text = $"Fertig: Projekt „{o.Name}“ mit {payload.Tasks} Aufgaben und {payload.Goals} Zielen" + (payload.Materials > 0 ? $", {payload.Materials} Materialien" : "") + " angelegt. In Minecraft mit J öffnen.";
        }
        catch (Exception e) { status.Text = "Fehlgeschlagen: " + e.Message; send.IsEnabled = true; }
    }

    /// <summary>Wurde etwas gesendet? Dann hat sich das Projekt geändert (Extra["bautrupp"]).</summary>
    public bool Sent { get; private set; }
}
