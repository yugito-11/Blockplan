using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Blockplan.Core;

/// <summary>Ein Stück 3D-Geometrie. Je Ecke 6 Zahlen: x, y, z, u, v, td (Kachel*8 + Richtung).</summary>
public sealed class MeshChunk
{
    public float[] V = Array.Empty<float>();
    public uint[] I = Array.Empty<uint>();
}

/// <summary>
/// Baut die 3D-Ansicht: volle, undurchsichtige Blöcke nur mit sichtbaren Seiten, gleiche Nachbarflächen
/// zu großen Rechtecken zusammengefasst (Textur wiederholt sich im Shader). Andere Formen als einfache Quader,
/// Pflanzen als gekreuzte Flächen. Läuft parallel in Bereichen zu 32×32 Spalten.
/// </summary>
public static class Mesher
{
    const int R = 32;
    // Richtungen: 0 +x, 1 -x, 2 +y, 3 -y, 4 +z, 5 -z, 6 gekreuzt
    static readonly int[] NA = { 0, 0, 1, 1, 2, 2 };
    static readonly int[][] AX = { new[] { 2, 1 }, new[] { 2, 1 }, new[] { 0, 2 }, new[] { 0, 2 }, new[] { 0, 1 }, new[] { 0, 1 } };
    static readonly int[] SG = { 1, -1, 1, -1, 1, -1 };

    sealed record Box(float X0, float Y0, float Z0, float X1, float Y1, float Z1);
    static readonly Box Full = new(0, 0, 0, 1, 1, 1);
    static readonly Regex plants = new("torch|sapling|flower|tulip|grass|fern|bush|mushroom|roots|sprouts|_fan|coral$|kelp|seagrass|cane|bamboo$|vine");

    /// <summary>0 Luft, 1 voll undurchsichtig, 2 Quader, 3 gekreuzt</summary>
    static (byte kind, Box[] boxes) Classify(int v, McAssets? mc)
    {
        var b = Blocks.Get(v); var B = Blocks.Get(Blocks.BaseOf(v)); var pr = b.Props ?? new Dictionary<string, string>();
        string P(string k, string d) => pr.TryGetValue(k, out var x) ? x : d;
        switch (b.Shape)
        {
            case 's':
                if (P("type", "bottom") == "double") return (1, new[] { Full });
                return (2, new[] { P("type", "bottom") == "top" ? new Box(0, .5f, 0, 1, 1, 1) : new Box(0, 0, 0, 1, .5f, 1) });
            case 't':
            {
                bool top = P("half", "bottom") == "top";
                var slab = top ? new Box(0, .5f, 0, 1, 1, 1) : new Box(0, 0, 0, 1, .5f, 1);
                float y0 = top ? 0 : .5f, y1 = top ? .5f : 1;
                var q = P("facing", "north") switch
                {
                    "south" => new Box(0, y0, .5f, 1, y1, 1), "east" => new Box(.5f, y0, 0, 1, y1, 1),
                    "west" => new Box(0, y0, 0, .5f, y1, 1), _ => new Box(0, y0, 0, 1, y1, .5f),
                };
                return (2, new[] { slab, q });
            }
            case 'h': return (2, new[] { P("half", "bottom") == "top" ? new Box(0, .875f, 0, 1, 1, 1) : new Box(0, 0, 0, 1, .125f, 1) });
            case 'p':
            {
                if (B.Key.EndsWith("_door")) { var f = P("facing", "north"); return (2, new[] { f is "north" or "south" ? new Box(0, 0, .405f, 1, 1, .595f) : new Box(.405f, 0, 0, .595f, 1, 1) }); }
                float s = B.Key.EndsWith("pane") || B.Key.EndsWith("bars") ? .0625f : B.Key.EndsWith("_wall") ? .25f : .125f;
                return (2, new[] { new Box(.5f - s, 0, .5f - s, .5f + s, 1, .5f + s) });
            }
            case 'm':
                if (mc?.FacesOf(v) != null && mc.CrossOf(v) == null && !plants.IsMatch(B.Key)) return (2, new[] { new Box(.25f, 0, .25f, .75f, .5f, .75f) });
                return (3, Array.Empty<Box>());
            default:
                if (b.SeeThrough || Blocks.IsCutout(v) || Blocks.IsGlassLike(v) || Blocks.IsWater(v)) return (2, new[] { Full });
                return (1, new[] { Full });
        }
    }

    public static List<MeshChunk> Build(Project p, int top, Atlas atlas, CancellationToken ct = default)
    {
        int n = Blocks.Count; var kind = new byte[n]; var boxes = new Box[n][]; var ft = new int[n][]; var cr = new int[n]; var bas = new int[n];
        foreach (var v in p.CountBlocks(top).Keys)
        {
            var (k, bx) = Classify(v, atlas.Mc); kind[v] = k; boxes[v] = bx; bas[v] = Blocks.BaseOf(v);
            if (k == 3) cr[v] = atlas.Cross(v); else ft[v] = atlas.Faces(v);
        }
        int W0 = p.W, D0 = p.D, H = Math.Min(top, p.H - 1) + 1; long SY = (long)W0 * D0; var cells = p.Cells;
        float ox = -W0 / 2f, oz = -D0 / 2f;
        bool Opaque(long i) => kind[cells[i]] == 1;
        var regs = new List<(int x0, int z0)>();
        for (int z0 = 0; z0 < D0; z0 += R) for (int x0 = 0; x0 < W0; x0 += R) regs.Add((x0, z0));
        var outp = new MeshChunk?[regs.Count];
        Parallel.For(0, regs.Count, new ParallelOptions { CancellationToken = ct }, ri =>
        {
            var (x0, z0) = regs[ri];
            int W = Math.Min(R, W0 - x0), D = Math.Min(R, D0 - z0); int nn = W * D * H;
            var cv = new ushort[nn]; var ex = new byte[nn]; bool any = false;
            var V = new List<float>(); var I = new List<uint>();
            void Quad(float[] c, float[] uv, int td, bool flip)
            {
                uint b0 = (uint)(V.Count / 6);
                for (int k = 0; k < 4; k++) { V.Add(c[k * 3]); V.Add(c[k * 3 + 1]); V.Add(c[k * 3 + 2]); V.Add(uv[k * 2]); V.Add(uv[k * 2 + 1]); V.Add(td); }
                if (flip) { I.Add(b0); I.Add(b0 + 2); I.Add(b0 + 1); I.Add(b0); I.Add(b0 + 3); I.Add(b0 + 2); }
                else { I.Add(b0); I.Add(b0 + 1); I.Add(b0 + 2); I.Add(b0); I.Add(b0 + 2); I.Add(b0 + 3); }
            }
            for (int y = 0; y < H; y++) for (int z = 0; z < D; z++)
            {
                if (ct.IsCancellationRequested) return;
                int gz = z0 + z; long i = ((long)y * D0 + gz) * W0 + x0; int li = (y * D + z) * W;
                for (int x = 0; x < W; x++, i++, li++)
                {
                    int v = cells[i]; if (v == 0) continue; int k = kind[v]; if (k == 0) continue;
                    int gx = x0 + x;
                    // Nachbarn: undurchsichtig? / gleicher Block?
                    bool nPX = gx + 1 < W0 && Opaque(i + 1), nNX = gx > 0 && Opaque(i - 1);
                    bool nPY = y + 1 < H && Opaque(i + SY), nNY = y > 0 && Opaque(i - SY);
                    bool nPZ = gz + 1 < D0 && Opaque(i + W0), nNZ = gz > 0 && Opaque(i - W0);
                    if (k == 1)
                    {
                        int bits = (nPX ? 0 : 1) | (nNX ? 0 : 2) | (nPY ? 0 : 4) | (nNY ? 0 : 8) | (nPZ ? 0 : 16) | (nNZ ? 0 : 32);
                        if (bits != 0) { cv[li] = (ushort)v; ex[li] = (byte)bits; any = true; }
                        continue;
                    }
                    float bx = gx + ox, bz = gz + oz;
                    if (k == 3)
                    {
                        float s = .5f * .7071f * 1.13f, cx = bx + .5f, cz = bz + .5f; int td = cr[v] * 8 + 6;
                        Quad(new[] { cx - s, y, cz - s, cx + s, y, cz + s, cx + s, y + 1f, cz + s, cx - s, y + 1f, cz - s }, new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, td, false);
                        Quad(new[] { cx - s, y, cz + s, cx + s, y, cz - s, cx + s, y + 1f, cz - s, cx - s, y + 1f, cz + s }, new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, td, false);
                        any = true; continue;
                    }
                    var f = ft[v]; int bv = bas[v];
                    bool Same(long j, bool inb) => inb && bas[cells[j]] == bv && kind[cells[j]] == 2 && boxes[cells[j]].Length == 1 && boxes[cells[j]][0] == Full;
                    bool isFull = boxes[v].Length == 1 && boxes[v][0] == Full;
                    foreach (var q in boxes[v])
                    {
                        float X0 = bx + q.X0, X1 = bx + q.X1, Y0 = y + q.Y0, Y1 = y + q.Y1, Z0 = bz + q.Z0, Z1 = bz + q.Z1;
                        // +x
                        if (!(q.X1 == 1 && (nPX || (isFull && Same(i + 1, gx + 1 < W0)))))
                            Quad(new[] { X1, Y0, Z0, X1, Y0, Z1, X1, Y1, Z1, X1, Y1, Z0 }, new[] { q.Z0, q.Y0, q.Z1, q.Y0, q.Z1, q.Y1, q.Z0, q.Y1 }, f[0] * 8 + 0, true);
                        if (!(q.X0 == 0 && (nNX || (isFull && Same(i - 1, gx > 0)))))
                            Quad(new[] { X0, Y0, Z0, X0, Y0, Z1, X0, Y1, Z1, X0, Y1, Z0 }, new[] { q.Z0, q.Y0, q.Z1, q.Y0, q.Z1, q.Y1, q.Z0, q.Y1 }, f[1] * 8 + 1, false);
                        if (!(q.Y1 == 1 && (nPY || (isFull && Same(i + SY, y + 1 < H)))))
                            Quad(new[] { X0, Y1, Z0, X1, Y1, Z0, X1, Y1, Z1, X0, Y1, Z1 }, new[] { q.X0, q.Z0, q.X1, q.Z0, q.X1, q.Z1, q.X0, q.Z1 }, f[2] * 8 + 2, true);
                        if (!(q.Y0 == 0 && (nNY || (isFull && Same(i - SY, y > 0)))))
                            Quad(new[] { X0, Y0, Z0, X1, Y0, Z0, X1, Y0, Z1, X0, Y0, Z1 }, new[] { q.X0, q.Z0, q.X1, q.Z0, q.X1, q.Z1, q.X0, q.Z1 }, f[3] * 8 + 3, false);
                        if (!(q.Z1 == 1 && (nPZ || (isFull && Same(i + W0, gz + 1 < D0)))))
                            Quad(new[] { X0, Y0, Z1, X1, Y0, Z1, X1, Y1, Z1, X0, Y1, Z1 }, new[] { q.X0, q.Y0, q.X1, q.Y0, q.X1, q.Y1, q.X0, q.Y1 }, f[4] * 8 + 4, false);
                        if (!(q.Z0 == 0 && (nNZ || (isFull && Same(i - W0, gz > 0)))))
                            Quad(new[] { X0, Y0, Z0, X1, Y0, Z0, X1, Y1, Z0, X0, Y1, Z0 }, new[] { q.X0, q.Y0, q.X1, q.Y0, q.X1, q.Y1, q.X0, q.Y1 }, f[5] * 8 + 5, true);
                    }
                    any = true;
                }
            }
            if (!any) return;
            // Zusammenfassen gleicher Flächen je Richtung und Schicht
            int[] dims = { W, H, D };
            for (int d = 0; d < 6; d++)
            {
                int na = NA[d], ua = AX[d][0], va = AX[d][1], bit = 1 << d, NU = dims[ua], NV = dims[va];
                var mask = new int[NU * NV]; var c = new int[3];
                for (int sl = 0; sl < dims[na]; sl++)
                {
                    bool has = false; c[na] = sl;
                    for (int vv = 0; vv < NV; vv++) { c[va] = vv; for (int uu = 0; uu < NU; uu++) { c[ua] = uu; int li = (c[1] * D + c[2]) * W + c[0]; int m = (ex[li] & bit) != 0 ? ft[cv[li]][d] + 1 : 0; mask[vv * NU + uu] = m; if (m != 0) has = true; } }
                    if (!has) continue;
                    for (int vv = 0; vv < NV; vv++)
                        for (int uu = 0; uu < NU;)
                        {
                            int k = mask[vv * NU + uu]; if (k == 0) { uu++; continue; }
                            int w = 1; while (uu + w < NU && mask[vv * NU + uu + w] == k) w++;
                            int h = 1;
                            for (; vv + h < NV; h++) { bool okRow = true; for (int q2 = 0; q2 < w; q2++) if (mask[(vv + h) * NU + uu + q2] != k) { okRow = false; break; } if (!okRow) break; }
                            for (int r = 0; r < h; r++) Array.Clear(mask, (vv + r) * NU + uu, w);
                            float pl = sl + (SG[d] > 0 ? 1 : 0); var cs = new float[12]; var uv = new float[8];
                            int[,] du = { { 0, 0 }, { w, 0 }, { w, h }, { 0, h } };
                            for (int e = 0; e < 4; e++)
                            {
                                var qq = new float[3]; qq[na] = pl; qq[ua] = uu + du[e, 0]; qq[va] = vv + du[e, 1];
                                cs[e * 3] = x0 + qq[0] + ox; cs[e * 3 + 1] = qq[1]; cs[e * 3 + 2] = z0 + qq[2] + oz;
                                uv[e * 2] = du[e, 0]; uv[e * 2 + 1] = du[e, 1];
                            }
                            Quad(cs, uv, (k - 1) * 8 + d, d == 0 || d == 2 || d == 5);
                            uu += w;
                        }
                }
            }
            if (I.Count > 0) outp[ri] = new MeshChunk { V = V.ToArray(), I = I.ToArray() };
        });
        return outp.Where(m => m != null).Select(m => m!).ToList();
    }
}
