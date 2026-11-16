using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Blockplan.Core;

namespace Blockplan.Views;

/// <summary>„Schematic laden“: Ausschnitt, Wasser entfernen, Drehen.</summary>
public sealed class SchemDialog : Window
{
    public sealed record Result(int X0, int Y0, int Z0, int X1, int Y1, int Z1, bool Dewater, bool Plants, int Rot, bool Desnow, bool SnowBlocks);

    public SchemDialog(Schem s)
    {
        Title = "Schematic laden"; Width = 520; SizeToContent = SizeToContent.Height; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(22), Spacing = 14 };
        root.Children.Add(new TextBlock { Text = s.Name, FontSize = 17, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });

        Border Stat(string big, string small) => new()
        {
            Background = (IBrush?)Application.Current!.FindResource("SunkBrush") ?? Brushes.Gray, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 8),
            Child = new StackPanel { Children = { new TextBlock { Text = big, FontSize = 17, FontFamily = new FontFamily("Consolas,monospace") }, new TextBlock { Text = small, FontSize = 11.5, Opacity = .7 } } }
        };
        var sum = new UniformGrid { Columns = 3 };
        sum.Children.Add(Stat($"{s.X1 - s.X0 + 1}×{s.Z1 - s.Z0 + 1}", "Grundfläche (X×Z)"));
        sum.Children.Add(Stat($"{s.Y1 - s.Y0 + 1}", "Ebenen"));
        sum.Children.Add(Stat(s.Count.ToString("N0"), $"Blöcke, {s.Types} Sorten"));
        foreach (Control c in sum.Children) c.Margin = new Thickness(0, 0, 8, 0);
        root.Children.Add(sum);
        root.Children.Add(new TextBlock { Text = $"Leere Ränder sind schon abgeschnitten (Datei: {s.W}×{s.D}×{s.H}). Für nur einen Teil die Grenzen ändern.", TextWrapping = TextWrapping.Wrap, FontSize = 12.5, Opacity = .75 });

        NumericUpDown N(int v, int max) => new() { Value = v, Minimum = 0, Maximum = max, FormatString = "0", Increment = 1 };
        var f = new[] { N(s.X0, s.W - 1), N(s.Z0, s.D - 1), N(s.Y0, s.H - 1), N(s.X1, s.W - 1), N(s.Z1, s.D - 1), N(s.Y1, s.H - 1) };
        string[] lab = { "X von", "Z von", "Y von", "X bis", "Z bis", "Y bis" };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,10,*,10,*"), RowDefinitions = new RowDefinitions("Auto,Auto,8,Auto,Auto") };
        for (int i = 0; i < 6; i++)
        {
            int col = (i % 3) * 2, row = i < 3 ? 0 : 3;
            var t = new TextBlock { Text = lab[i], FontSize = 12, Opacity = .7 }; Grid.SetColumn(t, col); Grid.SetRow(t, row); grid.Children.Add(t);
            Grid.SetColumn(f[i], col); Grid.SetRow(f[i], row + 1); grid.Children.Add(f[i]);
        }
        root.Children.Add(grid);
        var size = new TextBlock { FontSize = 12.5 };
        void Upd()
        {
            long w = (long)(f[3].Value - f[0].Value + 1 ?? 1), d = (long)(f[4].Value - f[1].Value + 1 ?? 1), h = (long)(f[5].Value - f[2].Value + 1 ?? 1);
            size.Text = $"Ausschnitt {w}×{d}×{h} = {w * d * h / 1e6:0.#} Mio. Felder";
        }
        foreach (var x in f) x.ValueChanged += (_, _) => Upd(); Upd();
        root.Children.Add(size);

        var dw = new CheckBox { Content = $"Wasser entfernen ({s.Water:N0} Wasserblöcke, {s.Waterlogged:N0} geflutete)", IsChecked = s.Water > 0, IsEnabled = s.Water + s.Waterlogged > 0 };
        var pl = new CheckBox { Content = $"auch Seegras und Kelp ({s.WaterPlants:N0})", IsVisible = s.WaterPlants > 0, Margin = new Thickness(26, 0, 0, 0) };
        root.Children.Add(dw); root.Children.Add(pl);
        var sn = new CheckBox { Content = $"Schnee entfernen ({s.Snow:N0} Schneedecken, {s.Snowy:N0} verschneite Blöcke)", IsChecked = false, IsEnabled = s.Snow + s.Snowy + s.SnowBlocks > 0 };
        var sb = new CheckBox { Content = $"auch Schneeblöcke ({s.SnowBlocks:N0})", IsVisible = s.SnowBlocks > 0, Margin = new Thickness(26, 0, 0, 0) };
        root.Children.Add(sn); root.Children.Add(sb);
        var rot = new ComboBox { ItemsSource = new[] { "Nicht drehen", "90° im Uhrzeigersinn", "180°", "90° gegen den Uhrzeigersinn" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        root.Children.Add(new DockPanel { Children = { new TextBlock { Text = "Drehen", VerticalAlignment = VerticalAlignment.Center, Width = 70 }, rot } });

        var ok = new Button { Content = "Laden", Classes = { "accent" }, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        var cancel = new Button { Content = "Abbrechen", MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        root.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } });
        cancel.Click += (_, _) => Close(null);
        ok.Click += (_, _) =>
        {
            int I(int k) => (int)(f[k].Value ?? 0);
            int x0 = Math.Min(I(0), I(3)), x1 = Math.Max(I(0), I(3)), z0 = Math.Min(I(1), I(4)), z1 = Math.Max(I(1), I(4)), y0 = Math.Min(I(2), I(5)), y1 = Math.Max(I(2), I(5));
            Close(new Result(x0, y0, z0, x1, y1, z1, dw.IsChecked == true, pl.IsChecked == true, rot.SelectedIndex, sn.IsChecked == true, sb.IsChecked == true));
        };
        Content = root;
    }
}
