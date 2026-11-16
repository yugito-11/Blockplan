using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Blockplan.Core;

namespace Blockplan.Views;

/// <summary>2D-Plan: Draufsicht (oberster Block bis zur Höhengrenze) oder eine einzelne Schicht.</summary>
public sealed class Plan2D : Control
{
    Project? p; Atlas? atlas;
    public bool LayerMode { get; private set; }
    int layer, top;
    double scale = 4; Point off; bool fitted;
    Point? dragFrom;
    WriteableBitmap? map; ushort[]? topV; short[]? topY; int buildGen;
    readonly Dictionary<int, WriteableBitmap> tileBmp = new();
    public event Action<string>? Hover;

    public Plan2D() { ClipToBounds = true; Focusable = true; }

    public void SetProject(Project? project, Atlas a, int topCut, bool refit)
    {
        p = project; atlas = a; top = topCut; layer = Math.Min(layer, Math.Max(0, (p?.H ?? 1) - 1));
        foreach (var b in tileBmp.Values) b.Dispose(); tileBmp.Clear();
        if (refit) fitted = false;
        RebuildMap();
    }

    public void SetView(bool layerMode, int layerY, int topCut)
    {
        bool ch = layerMode != LayerMode || layerY != layer || topCut != top;
        LayerMode = layerMode; layer = layerY; top = topCut;
        if (ch) RebuildMap();
    }

    void Fit()
    {
        if (p == null || Bounds.Width < 10) return;
        scale = Math.Max(0.05, Math.Min((Bounds.Width - 40) / p.W, (Bounds.Height - 40) / p.D));
        off = new Point((Bounds.Width - p.W * scale) / 2, (Bounds.Height - p.D * scale) / 2); fitted = true;
    }

    public void ResetView() { fitted = false; InvalidateVisual(); }

    /// <summary>Kartenbild (1 Pixel je Block) im Hintergrund berechnen.</summary>
    void RebuildMap()
    {
        var pr = p; var a = atlas; if (pr == null || a == null) { map = null; InvalidateVisual(); return; }
        int gen = Interlocked.Increment(ref buildGen); bool lm = LayerMode; int ly = layer, tc = Math.Min(top, pr.H - 1);
        Task.Run(() =>
        {
            int W = pr.W, D = pr.D; long WD = (long)W * D; var tv = new ushort[WD]; var ty = new short[WD];
            if (lm) { long b = ly * WD; for (long c = 0; c < WD; c++) { tv[c] = pr.Cells[b + c]; ty[c] = (short)ly; } }
            else
                Parallel.For(0, D, z =>
                {
                    for (int x = 0; x < W; x++)
                    {
                        long c = (long)z * W + x; ty[c] = -1;
                        for (int y = tc; y >= 0; y--) { var v = pr.Cells[y * WD + c]; if (v != 0) { tv[c] = v; ty[c] = (short)y; break; } }
                    }
                });
            var px = new uint[WD];
            Parallel.For(0, D, z =>
            {
                for (int x = 0; x < W; x++)
                {
                    long c = (long)z * W + x; var v = tv[c]; if (v == 0) { px[c] = lm && ly > 0 && pr.Cells[(ly - 1) * WD + c] != 0 ? 0x30ffffffu : 0; continue; }
                    uint col = a.TopColor(v);
                    if (!lm)
                    {
                        int h = ty[c], hn = z > 0 ? ty[c - W] : h, hw = x > 0 ? ty[c - 1] : h;
                        double f = 1 + Math.Clamp((h - hn) + (h - hw) * .5, -3, 3) * .07;
                        col = 0xff000000 | (uint)Math.Clamp(((col >> 16) & 255) * f, 0, 255) << 16 | (uint)Math.Clamp(((col >> 8) & 255) * f, 0, 255) << 8 | (uint)Math.Clamp((col & 255) * f, 0, 255);
                    }
                    px[c] = col;
                }
            });
            Dispatcher.UIThread.Post(() =>
            {
                if (gen != buildGen) return;
                var bm = new WriteableBitmap(new PixelSize(W, D), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
                using (var fb = bm.Lock())
                    unsafe { var dst = (uint*)fb.Address; for (int z = 0; z < D; z++) { var row = dst + (long)z * fb.RowBytes / 4; for (int x = 0; x < W; x++) row[x] = px[(long)z * W + x]; } }
                map?.Dispose(); map = bm; topV = tv; topY = ty; InvalidateVisual();
            });
        });
    }

    WriteableBitmap Tile(int t)
    {
        if (tileBmp.TryGetValue(t, out var b)) return b;
        var src = atlas!.Tiles[t]; b = new WriteableBitmap(new PixelSize(Atlas.T, Atlas.T), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (var fb = b.Lock()) for (int y = 0; y < Atlas.T; y++) System.Runtime.InteropServices.Marshal.Copy(src, y * Atlas.T * 4, fb.Address + y * fb.RowBytes, Atlas.T * 4);
        return tileBmp[t] = b;
    }

    public override void Render(DrawingContext ctx)
    {
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x12, 0x1a, 0x15)), new Rect(Bounds.Size));
        if (p == null || map == null) return;
        if (!fitted) Fit();
        var area = new Rect(off.X, off.Y, p.W * scale, p.D * scale);
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1c, 0x26, 0x1f)), area);
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
        {
            if (scale < 7 || topV == null) ctx.DrawImage(map, new Rect(0, 0, p.W, p.D), area);
            else
            {
                int x0 = Math.Max(0, (int)((-off.X) / scale)), z0 = Math.Max(0, (int)((-off.Y) / scale));
                int x1 = Math.Min(p.W - 1, (int)((Bounds.Width - off.X) / scale)), z1 = Math.Min(p.D - 1, (int)((Bounds.Height - off.Y) / scale));
                var dark = new SolidColorBrush(Colors.Black); long WD = (long)p.W * p.D;
                for (int z = z0; z <= z1; z++) for (int x = x0; x <= x1; x++)
                {
                    long c = (long)z * p.W + x; int v = topV[c]; var r = new Rect(off.X + x * scale, off.Y + z * scale, scale + .5, scale + .5);
                    if (v == 0)
                    {
                        if (LayerMode && layer > 0 && p.Cells[(layer - 1) * WD + c] is var below && below != 0)
                            using (ctx.PushOpacity(.28)) ctx.DrawImage(Tile(atlas!.Faces(below)[2]), new Rect(0, 0, 16, 16), r);
                        continue;
                    }
                    var b = Blocks.Get(v); int tile = b.Shape == 'm' ? atlas!.Cross(v) : atlas!.Faces(v)[2];
                    var dr = b.Shape is 'p' or 'm' ? r.Deflate(scale * (b.Shape == 'p' ? .3 : .2)) : r;
                    ctx.DrawImage(Tile(tile), new Rect(0, 0, 16, 16), dr);
                    if (!LayerMode)
                    {
                        int h = topY![c], hn = z > 0 ? topY[c - p.W] : h, hw = x > 0 ? topY[c - 1] : h; double f = Math.Clamp((h - hn) + (h - hw) * .5, -3, 3) * .07;
                        if (f != 0) ctx.FillRectangle(new SolidColorBrush(f > 0 ? Colors.White : Colors.Black, Math.Min(f > 0 ? .35 : .5, Math.Abs(f))), r);
                    }
                    if (scale >= 14 && b.Shape == 't' && b.Props != null)
                    {
                        b.Props.TryGetValue("facing", out var fc); var half = new Rect(r.X, r.Y, r.Width, r.Height);
                        half = fc switch { "east" => new Rect(r.X + r.Width / 2, r.Y, r.Width / 2, r.Height), "west" => new Rect(r.X, r.Y, r.Width / 2, r.Height), "south" => new Rect(r.X, r.Y + r.Height / 2, r.Width, r.Height / 2), _ => new Rect(r.X, r.Y, r.Width, r.Height / 2) };
                        ctx.FillRectangle(new SolidColorBrush(Colors.Black, .38), half);
                    }
                }
            }
        }
        // Gitter und Chunk-Linien
        if (scale >= 10)
        {
            var thin = new Pen(new SolidColorBrush(Colors.Black, .22), 1); var chunk = new Pen(new SolidColorBrush(Color.FromRgb(0xec, 0x5f, 0x9b), .55), 1);
            int x0 = Math.Max(0, (int)((-off.X) / scale)), z0 = Math.Max(0, (int)((-off.Y) / scale));
            int x1 = Math.Min(p.W, (int)((Bounds.Width - off.X) / scale) + 1), z1 = Math.Min(p.D, (int)((Bounds.Height - off.Y) / scale) + 1);
            for (int x = x0; x <= x1; x++) { double X = Math.Round(off.X + x * scale) + .5; ctx.DrawLine((p.OriginX + x) % 16 == 0 ? chunk : thin, new Point(X, Math.Max(0, off.Y)), new Point(X, Math.Min(Bounds.Height, off.Y + p.D * scale))); }
            for (int z = z0; z <= z1; z++) { double Z = Math.Round(off.Y + z * scale) + .5; ctx.DrawLine((p.OriginZ + z) % 16 == 0 ? chunk : thin, new Point(Math.Max(0, off.X), Z), new Point(Math.Min(Bounds.Width, off.X + p.W * scale), Z)); }
        }
        ctx.DrawRectangle(new Pen(new SolidColorBrush(Color.FromRgb(0xec, 0x5f, 0x9b)), 1.5), area);
        // Norden-Pfeil
        var ft = new FormattedText("N ▲", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), 12, Brushes.White);
        ctx.DrawText(ft, new Point(Bounds.Width / 2 - ft.Width / 2, 8));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); Focus(); dragFrom = e.GetPosition(this); e.Pointer.Capture(this); }
    protected override void OnPointerReleased(PointerReleasedEventArgs e) { base.OnPointerReleased(e); dragFrom = null; e.Pointer.Capture(null); }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var pos = e.GetPosition(this);
        if (dragFrom is { } f) { off += pos - f; dragFrom = pos; InvalidateVisual(); return; }
        if (p == null || topV == null) return;
        int x = (int)Math.Floor((pos.X - off.X) / scale), z = (int)Math.Floor((pos.Y - off.Y) / scale);
        if (x < 0 || z < 0 || x >= p.W || z >= p.D) { Hover?.Invoke(""); return; }
        long c = (long)z * p.W + x; int v = topV[c];
        Hover?.Invoke(v == 0 ? $"leer  ·  X {p.OriginX + x}  Z {p.OriginZ + z}" : $"{Blocks.DisplayName(v, true)}  ·  X {p.OriginX + x}  Y {p.OriginY + topY![c]}  Z {p.OriginZ + z}");
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); var pos = e.GetPosition(this);
        double ns = Math.Clamp(scale * Math.Exp(Math.Clamp(e.Delta.Y, -3, 3) * 0.12), 0.05, 64);
        off = pos - (pos - off) * (ns / scale); scale = ns; InvalidateVisual(); e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); if (!fitted) InvalidateVisual(); }
}
