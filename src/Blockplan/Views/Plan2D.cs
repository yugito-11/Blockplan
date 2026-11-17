using System;
using System.Collections.Generic;
using System.Linq;
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
    WriteableBitmap? map; ushort[]? topV; short[]? topY; byte[]? topStatus; int buildGen;
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
            var px = new uint[WD]; var lv = Live; bool prog = ShowProgress && lv != null && (lv.Known || !lv.Chunks.IsEmpty);
            var stt = prog ? new byte[WD] : null;
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
                    if (stt != null)
                    {   // Baufortschritt: fehlt = hellblau, falsch = rot, gesetzt = wie geplant
                        byte st = lv!.Status(pr, x, ty[c], z, v); stt[c] = st;
                        uint Mix(uint a2, uint b2, double t) => 0xff000000 | (uint)(((a2 >> 16) & 255) * (1 - t) + ((b2 >> 16) & 255) * t) << 16 | (uint)(((a2 >> 8) & 255) * (1 - t) + ((b2 >> 8) & 255) * t) << 8 | (uint)((a2 & 255) * (1 - t) + (b2 & 255) * t);
                        if (st == 3) col = Mix(col, 0x7ec8ff, .6); else if (st == 2) col = Mix(col, 0xff3b3b, .65);
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
                map?.Dispose(); map = bm; topV = tv; topY = ty; topStatus = stt; InvalidateVisual();
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
        DrawSurroundings(ctx);
        ctx.FillRectangle(new SolidColorBrush(Color.FromRgb(0x1c, 0x26, 0x1f), Live != null && ShowSurroundings && !Live.Chunks.IsEmpty ? .35 : 1), area);
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
                    if (topStatus is { } ts && ts[c] is 2 or 3) ctx.FillRectangle(new SolidColorBrush(ts[c] == 3 ? Color.FromRgb(0x7e, 0xc8, 0xff) : Color.FromRgb(0xff, 0x3b, 0x3b), .5), r);
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
        DrawOverlays(ctx);
        DrawPlayers(ctx);
        // Norden-Pfeil
        var ft = new FormattedText("N ▲", System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.Bold), 12, Brushes.White);
        ctx.DrawText(ft, new Point(Bounds.Width / 2 - ft.Width / 2, 8));
    }

    /* ---------- Live: Spieler, Baufortschritt, Umgebung ---------- */
    public LiveClient? Live { get; set; }
    public bool ShowProgress { get; set; } = true;
    public bool ShowSurroundings { get; set; } = true;
    readonly Dictionary<(int, int), WriteableBitmap> chunkImg = new();
    DateTime lastLiveMap;

    /// <summary>Welt hat sich geändert: Karte höchstens einmal pro Sekunde neu berechnen.</summary>
    public void LiveChanged(List<(int cx, int cz)> chunks)
    {
        foreach (var k in chunks) if (chunkImg.Remove(k, out var b)) b.Dispose();
        if (ShowProgress && (DateTime.Now - lastLiveMap).TotalMilliseconds > 1000) { lastLiveMap = DateTime.Now; RebuildMap(); }
        else InvalidateVisual();
    }
    public void LiveReset() { foreach (var b in chunkImg.Values) b.Dispose(); chunkImg.Clear(); RebuildMap(); }

    WriteableBitmap ChunkImage(StandChunk c)
    {
        if (chunkImg.TryGetValue((c.Cx, c.Cz), out var b)) return b;
        var px = new uint[256]; var top = new int[256];
        for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
        {
            top[z * 16 + x] = -1;
            for (int y = c.H - 1; y >= 0; y--) { var v = c.At(x, y, z); if (v != 0 && v != LiveClient.Unloaded) { px[z * 16 + x] = atlas!.TopColor(v); top[z * 16 + x] = y; break; } }
        }
        for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++)
        {
            int i = z * 16 + x; if (top[i] < 0) continue; int h = top[i], hn = z > 0 ? top[i - 16] : h, hw = x > 0 ? top[i - 1] : h;
            double f = (1 + Math.Clamp((h - hn) + (h - hw) * .5, -3, 3) * .07) * .82; uint col = px[i];
            px[i] = 0xff000000 | (uint)Math.Clamp(((col >> 16) & 255) * f, 0, 255) << 16 | (uint)Math.Clamp(((col >> 8) & 255) * f, 0, 255) << 8 | (uint)Math.Clamp((col & 255) * f, 0, 255);
        }
        b = new WriteableBitmap(new PixelSize(16, 16), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var fb = b.Lock()) unsafe { var dst = (uint*)fb.Address; for (int z = 0; z < 16; z++) for (int x = 0; x < 16; x++) dst[z * fb.RowBytes / 4 + x] = px[z * 16 + x]; }
        return chunkImg[(c.Cx, c.Cz)] = b;
    }

    void DrawSurroundings(DrawingContext ctx)
    {
        var lv = Live; if (lv == null || !ShowSurroundings || p == null || atlas == null || lv.Chunks.IsEmpty) return;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            foreach (var c in lv.Chunks.Values)
            {
                double x = c.Cx * 16 - p.OriginX, z = c.Cz * 16 - p.OriginZ;
                var r = new Rect(S(x, z), new Size(16 * scale, 16 * scale));
                if (r.Right < 0 || r.Bottom < 0 || r.X > Bounds.Width || r.Y > Bounds.Height) continue;
                ctx.DrawImage(ChunkImage(c), new Rect(0, 0, 16, 16), r);
            }
    }

    void DrawPlayers(DrawingContext ctx)
    {
        var lv = Live; if (lv == null || p == null) return;
        foreach (var q in lv.Players.Where(q => q.Dim == lv.Dim))
        {
            var c = S(q.X - p.OriginX, q.Z - p.OriginZ); uint col = View3D.PlayerColor(q.Name);
            var br = new SolidColorBrush(Color.FromRgb((byte)(col >> 16), (byte)(col >> 8), (byte)col));
            // Blickrichtung (Minecraft: 0° = Süden, im Uhrzeigersinn)
            double a = q.Yaw * Math.PI / 180; var tip = c + new Point(-Math.Sin(a) * 14, Math.Cos(a) * 14);
            ctx.DrawLine(new Pen(br, 3), c, tip);
            ctx.DrawEllipse(br, new Pen(Brushes.White, 2), c, 7, 7);
            Tag(ctx, $"{q.Name}  Y {Math.Floor(q.Y)}", c + new Point(0, -20), br);
        }
    }

    /* ---------- Planungs-Ebenen: Baupläne, Hilfslinien, Punkte, Messung, Auswahl ---------- */
    public PlanData? Plan { get; set; }
    public bool ShowParts { get; set; } = true;
    public string? HighlightPart { get; set; }
    /// <summary>"" = verschieben, "point" = Punkt setzen, "area" = Bereich für Bauplan ziehen</summary>
    public string Mode { get; set; } = "";
    public event Action<int, int>? PointPlaced;
    public event Action<int, int, int, int>? AreaSelected;
    public event Action<string>? MeasureChanged;
    /// <summary>Im Modus "line": mit der linken Maustaste gezogene Linie (Projektkoordinaten).</summary>
    public event Action<int, int, int, int>? LineDrawn;
    (int x0, int z0, int x1, int z1)? meas, area, draw;
    bool measuring, selecting, drawing;

    Point S(double x, double z) => new(off.X + x * scale, off.Y + z * scale);
    static IBrush Br(string hex, double a = 1) { try { var c = Color.Parse(hex); return new SolidColorBrush(c, a); } catch { return new SolidColorBrush(Colors.White, a); } }

    void Tag(DrawingContext ctx, string text, Point at, IBrush border)
    {
        var t = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter", FontStyle.Normal, FontWeight.SemiBold), 11.5, Brushes.White);
        var r = new Rect(at.X - t.Width / 2 - 5, at.Y - t.Height / 2 - 2, t.Width + 10, t.Height + 4);
        ctx.FillRectangle(new SolidColorBrush(Color.FromArgb(230, 12, 18, 14)), r, 4); ctx.DrawRectangle(new Pen(border, 1), r, 4);
        ctx.DrawText(t, new Point(r.X + 5, r.Y + 2));
    }

    void DrawOverlays(DrawingContext ctx)
    {
        if (p == null) return;
        var pl = Plan;
        if (pl != null && ShowParts)
            foreach (var t in pl.Parts.Where(t => !t.Base))
            {
                var b = t.Box; if (!LayerMode && b[1] > top) continue; if (LayerMode && (layer < b[1] || layer > b[4])) continue;
                bool hi = t.Id == HighlightPart; var col = hi ? Br("#ffd23a") : Br("#ec5f9b", .8);
                var r = new Rect(S(b[0], b[2]), S(b[3] + 1, b[5] + 1));
                if (hi) ctx.FillRectangle(Br("#ffd23a", .18), r);
                ctx.DrawRectangle(new Pen(col, hi ? 2.5 : 1.2, hi ? null : new DashStyle(new double[] { 4, 3 }, 0)), r);
                if (r.Width > 60 || hi) Tag(ctx, pl.Label(t), r.Center, col);
            }
        if (pl != null)
        {
            foreach (var g in pl.Guides.Where(g => g.On))
            {
                bool here = !LayerMode || g.AtLayer(layer);   // in der Ebenen-Ansicht: außerhalb ihrer Höhe nur blass
                var col = Br(g.Color, here ? 1 : .3); var pen = new Pen(col, here ? 1.8 : 1.2);
                if (g.Type == "outline")
                {   // automatischer Umriss: die Randfelder selbst, in jeder Zoomstufe
                    var fb = Br(g.Color, here ? .75 : .25); var cs = g.Cells();
                    foreach (var (x, z) in cs) ctx.FillRectangle(fb, new Rect(S(x, z), new Size(Math.Max(scale, 1), Math.Max(scale, 1))));
                    if (cs.Count > 0 && g.Name.Length > 0) { var (fx, fz) = cs[0]; Tag(ctx, g.Name, S(fx + .5, fz + .5) + new Point(0, -14), col); }
                    continue;
                }
                if (g.Type == "grid")
                {   // Raster an festen Welt-Koordinaten (16 = Chunk-Grenzen)
                    if (scale >= 4 && here) foreach (var (x, z) in g.Cells(p)) ctx.FillRectangle(Br(g.Color, .3), new Rect(S(x, z), new Size(scale, scale)));
                    var gp = new Pen(col, here ? 1.4 : 1); bool lab = g.A * scale >= 46;
                    foreach (var x in g.GridLines(p.W, p.OriginX)) { ctx.DrawLine(gp, S(x, 0), S(x, p.D)); if (lab) Tag(ctx, $"X {p.OriginX + x}", new Point(S(x, 0).X, Math.Max(S(x, 0).Y, 0) + 12), col); }
                    foreach (var z in g.GridLines(p.D, p.OriginZ)) { ctx.DrawLine(gp, S(0, z), S(p.W, z)); if (lab) Tag(ctx, $"Z {p.OriginZ + z}", new Point(Math.Max(S(0, z).X, 0) + 30, S(0, z).Y), col); }
                    continue;
                }
                if (scale >= 4 && here) foreach (var (x, z) in g.Cells()) ctx.FillRectangle(Br(g.Color, .55), new Rect(S(x, z), new Size(scale, scale)));
                if (g.Type == "line")
                {
                    ctx.DrawLine(pen, S(g.Cx + .5, g.Cz + .5), S(g.X2 + .5, g.Z2 + .5));
                    int n = (int)Math.Round(Math.Sqrt((g.X2 - g.Cx) * (g.X2 - g.Cx) + (g.Z2 - g.Cz) * (g.Z2 - g.Cz))) + 1;
                    Tag(ctx, $"{n} Blöcke", S((g.Cx + g.X2) / 2 + .5, (g.Cz + g.Z2) / 2 + .5), col); continue;
                }
                double a = g.A, b = g.Type == "circle" ? g.A : g.Bv;
                if (g.Type == "rect") ctx.DrawRectangle(pen, new Rect(S(g.Cx - a / 2, g.Cz - b / 2), S(g.Cx + a / 2, g.Cz + b / 2)));
                else ctx.DrawEllipse(null, pen, S(g.Cx + .5, g.Cz + .5), a / 2 * scale, b / 2 * scale);
                var dash = new Pen(col, 1, new DashStyle(new double[] { 5, 4 }, 0));
                ctx.DrawLine(dash, S(g.Cx - a / 2 + .5, g.Cz + .5), S(g.Cx + a / 2 + .5, g.Cz + .5)); ctx.DrawLine(dash, S(g.Cx + .5, g.Cz - b / 2 + .5), S(g.Cx + .5, g.Cz + b / 2 + .5));
                ctx.FillRectangle(col, new Rect(S(g.Cx + .5, g.Cz + .5) - new Point(3, 3), new Size(6, 6)));
                if (a * scale > 50) Tag(ctx, g.Type == "rect" ? $"{a:0} Blöcke" : $"Ø {a:0} · r {a / 2:0.#}", S(g.Cx + .5, g.Cz + .5) + new Point(0, -14), col);
                if (b * scale > 50 && g.Type != "circle") Tag(ctx, g.Type == "rect" ? $"{b:0} Blöcke" : $"Ø {b:0} · r {b / 2:0.#}", S(g.Cx + .5, g.Cz + b / 4 + .5) + new Point(42, 0), col);
                if (g.Name.Length > 0 && a * scale > 40) Tag(ctx, g.Name, S(g.Cx + .5, g.Cz - b / 2 + .5) + new Point(0, -14), col);
            }
            foreach (var q in pl.Points)
            {
                var c = S(q.X + .5, q.Z + .5); ctx.DrawEllipse(Br("#ffd23a"), new Pen(Brushes.Black, 1.5), c, 6, 6);
                Tag(ctx, $"{q.Name} ({p.OriginX + q.X} | {p.OriginZ + q.Z})", c + new Point(0, -18), Br("#ffd23a"));
            }
        }
        if (meas is { } m)
        {
            var a = S(m.x0 + .5, m.z0 + .5); var b = S(m.x1 + .5, m.z1 + .5); var col = Br("#ff8fbf");
            ctx.FillRectangle(Br("#ff8fbf", .5), new Rect(S(m.x0, m.z0), new Size(scale, scale))); ctx.FillRectangle(Br("#ff8fbf", .5), new Rect(S(m.x1, m.z1), new Size(scale, scale)));
            ctx.DrawLine(new Pen(col, 2.5), a, b);
            Tag(ctx, MeasureText(m), new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2 - 14), col);
        }
        if (draw is { } dw)
        {
            var a = S(dw.x0 + .5, dw.z0 + .5); var b = S(dw.x1 + .5, dw.z1 + .5); var col = Br("#7ee08f");
            ctx.DrawLine(new Pen(col, 2.5), a, b); ctx.FillRectangle(col, new Rect(a - new Point(3, 3), new Size(6, 6)));
            Tag(ctx, MeasureText(dw), new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2 - 14), col);
        }
        if (area is { } ar)
        {
            var r = new Rect(S(Math.Min(ar.x0, ar.x1), Math.Min(ar.z0, ar.z1)), S(Math.Max(ar.x0, ar.x1) + 1, Math.Max(ar.z0, ar.z1) + 1));
            ctx.FillRectangle(Br("#ffd23a", .15), r); ctx.DrawRectangle(new Pen(Br("#ffd23a"), 2, new DashStyle(new double[] { 6, 4 }, 0)), r);
            Tag(ctx, $"{(int)Math.Round(r.Width / scale)} × {(int)Math.Round(r.Height / scale)}", r.Center, Br("#ffd23a"));
        }
    }

    static string MeasureText((int x0, int z0, int x1, int z1) m)
    {
        int dx = Math.Abs(m.x1 - m.x0) + 1, dz = Math.Abs(m.z1 - m.z0) + 1;
        int n = (int)Math.Round(Math.Sqrt((m.x1 - m.x0) * (m.x1 - m.x0) + (m.z1 - m.z0) * (m.z1 - m.z0))) + 1;
        return dx == 1 || dz == 1 ? $"{n} Blöcke" : $"{n} Blöcke  ({dx} × {dz})";
    }

    (int x, int z) CellAt(Point pos) => ((int)Math.Floor((pos.X - off.X) / scale), (int)Math.Floor((pos.Y - off.Y) / scale));
    public void ClearMeasure() { meas = null; MeasureChanged?.Invoke(""); InvalidateVisual(); }
    public void CenterOn(double x, double z) { off = new Point(Bounds.Width / 2 - (x + .5) * scale, Bounds.Height / 2 - (z + .5) * scale); if (scale < 8) { var c = new Point(Bounds.Width / 2, Bounds.Height / 2); off = c - (c - off) * (8 / scale); scale = 8; } InvalidateVisual(); }
    public (double x, double z) ViewCenter() => ((Bounds.Width / 2 - off.X) / scale, (Bounds.Height / 2 - off.Y) / scale);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e); Focus(); var pos = e.GetPosition(this); var pp = e.GetCurrentPoint(this);
        var (x, z) = CellAt(pos);
        if (pp.Properties.IsRightButtonPressed)
        {   // Messen mit rechter Maustaste
            meas = (x, z, x, z); measuring = true; e.Pointer.Capture(this); InvalidateVisual(); return;
        }
        if (Mode == "point" && p != null && x >= 0 && z >= 0 && x < p.W && z < p.D) { PointPlaced?.Invoke(x, z); return; }
        if (Mode == "area") { area = (x, z, x, z); selecting = true; e.Pointer.Capture(this); InvalidateVisual(); return; }
        if (Mode == "line") { draw = (x, z, x, z); drawing = true; e.Pointer.Capture(this); InvalidateVisual(); return; }
        dragFrom = pos; e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e); dragFrom = null; e.Pointer.Capture(null);
        if (measuring) { measuring = false; if (meas is { } m) MeasureChanged?.Invoke(MeasureText(m) + $"  ·  von X {p!.OriginX + m.x0} Z {p.OriginZ + m.z0} bis X {p.OriginX + m.x1} Z {p.OriginZ + m.z1}"); }
        if (drawing && draw is { } dl) { drawing = false; draw = null; InvalidateVisual(); LineDrawn?.Invoke(dl.x0, dl.z0, dl.x1, dl.z1); }
        if (selecting && area is { } a) { selecting = false; area = null; InvalidateVisual(); AreaSelected?.Invoke(Math.Min(a.x0, a.x1), Math.Min(a.z0, a.z1), Math.Max(a.x0, a.x1), Math.Max(a.z0, a.z1)); }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e); var pos = e.GetPosition(this);
        if (measuring && meas is { } m) { var (x, z) = CellAt(pos); meas = (m.x0, m.z0, x, z); InvalidateVisual(); return; }
        if (selecting && area is { } a) { var (x, z) = CellAt(pos); area = (a.x0, a.z0, x, z); InvalidateVisual(); return; }
        if (drawing && draw is { } dl)
        {   // Umschalt gedrückt: nur waagrecht, senkrecht oder genau diagonal
            var (x, z) = CellAt(pos);
            if (e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            {
                int dx = x - dl.x0, dz = z - dl.z0, ax = Math.Abs(dx), az = Math.Abs(dz);
                if (ax > 2 * az) z = dl.z0; else if (az > 2 * ax) x = dl.x0; else { int mm = Math.Max(ax, az); x = dl.x0 + Math.Sign(dx) * mm; z = dl.z0 + Math.Sign(dz) * mm; }
            }
            draw = (dl.x0, dl.z0, x, z); InvalidateVisual(); return;
        }
        if (dragFrom is { } f) { off += pos - f; dragFrom = pos; InvalidateVisual(); return; }
        if (p == null || topV == null) return;
        int cx = (int)Math.Floor((pos.X - off.X) / scale), cz = (int)Math.Floor((pos.Y - off.Y) / scale);
        if (cx < 0 || cz < 0 || cx >= p.W || cz >= p.D) { Hover?.Invoke(""); return; }
        long c = (long)cz * p.W + cx; int v = topV[c];
        Hover?.Invoke(v == 0 ? $"leer  ·  X {p.OriginX + cx}  Z {p.OriginZ + cz}" : $"{Blocks.DisplayName(v, true)}  ·  X {p.OriginX + cx}  Y {p.OriginY + topY![c]}  Z {p.OriginZ + cz}");
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e); var pos = e.GetPosition(this);
        double ns = Math.Clamp(scale * Math.Exp(Math.Clamp(e.Delta.Y, -3, 3) * 0.12), 0.05, 64);
        off = pos - (pos - off) * (ns / scale); scale = ns; InvalidateVisual(); e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e) { base.OnSizeChanged(e); if (!fitted) InvalidateVisual(); }
}
