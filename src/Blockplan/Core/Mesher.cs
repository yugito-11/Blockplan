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
    /// <summary>Halb durchsichtig (Wasser, Eis, farbiges Glas …): wird nach dem Rest mit Überblendung gezeichnet.</summary>
    public bool Translucent;
    /// <summary>Bereich (32×32 Spalten) im Projekt, damit man ihn einzeln neu bauen kann.</summary>
    public int Region;
}

/// <summary>Zusätze für den Live-Modus und für Umgebungs-Chunks.</summary>
public sealed class MeshOptions
{
    /// <summary>Nur diese Bereiche bauen (null = alle).</summary>
    public HashSet<int>? Regions;
    /// <summary>Baufortschritt je Feld (x, y, z, Plan-Block): 1 gesetzt, 2 falsch, 3 fehlt, 4 unbekannt.</summary>
    public Func<int, int, int, int, byte>? Status;
    /// <summary>Statt Projektmitte: eigene Lage in der Szene (für Umgebungs-Chunks).</summary>
    public bool UseOffset; public float OffX, OffY, OffZ;
    /// <summary>Kennzeichen aller Flächen (3 = Hilfslinie/Farbe, sonst 0).</summary>
    public float Flag;
    /// <summary>Bau-Anleitung: diese Ebene hervorheben (-1 = keine).</summary>
    public int HighlightY = -1;
    /// <summary>Schnitt: 0 aus, 1 entlang X (Nord-Süd-Schnitt), 2 entlang Z; sichtbar bleibt die Seite unter bzw. über ClipPos.</summary>
    public int ClipAxis; public int ClipPos; public bool ClipKeepHigh;
    /// <summary>Licht je Feld (Himmel*16 + Block, 0–255) für die Nachtansicht; null = überall Tageslicht.</summary>
    public byte[]? Light;
    /// <summary>Zeitraffer: diese Felder sind (noch) nicht gebaut und werden weggelassen.</summary>
    public Func<long, bool>? Hidden;
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
    static readonly Box[] WaterTop = { new(0, 0, 0, 1, .875f, 1) };
    /// <summary>Zahlen je Ecke: x, y, z, u, v, td (Kachel*8 + Richtung), Kennzeichen (0 normal, 1 fehlt, 2 falsch, 3 Farbe,
    /// 4 hervorgehobene Ebene, 5 Spawn-Markierung, 6 Wasser, 7 leuchtet selbst), Licht (Himmel*16 + Block).</summary>
    public const int Stride = 8;
    /// <summary>Licht ohne Berechnung: voller Himmel, kein Blocklicht.</summary>
    public const float DayLight = 240;
    static readonly float[] Unit = { 0, 1, 1, 1, 1, 0, 0, 0 };

    /// <summary>Kachel überwiegend halb durchsichtig (nicht nur Löcher wie bei Glas oder Laub)?</summary>
    static bool IsSemi(byte[] px)
    {
        int semi = 0, n = px.Length / 4;
        for (int i = 0; i < n; i++) { byte a = px[i * 4 + 3]; if (a > 20 && a < 235) semi++; }
        return semi > n / 2;
    }
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

    /// <summary>Anzahl Bereiche (32×32 Spalten) eines Projekts und Bereich einer Spalte.</summary>
    public static int RegionsX(int w) => (w + R - 1) / R;
    /// <summary>Helleres von zwei Lichtwerten, je Himmel und Block getrennt.</summary>
    static float MaxL(int a, int b) => Math.Max(a & 0xF0, b & 0xF0) | Math.Max(a & 15, b & 15);
    public static int RegionOf(int w, int x, int z) => (z / R) * RegionsX(w) + x / R;

    public static List<MeshChunk> Build(Project p, int top, Atlas atlas, CancellationToken ct = default, MeshOptions? opt = null)
    {
        int n = Blocks.Count; var kind = new byte[n]; var boxes = new Box[n][]; var ft = new int[n][]; var cr = new int[n]; var bas = new int[n]; var trans = new bool[n];
        var opq = new bool[n]; var fam = new byte[n]; var emis = new bool[n];   // leuchtende Blöcke (für den Shader)
        var bank = atlas.Mc != null && atlas.Mc.BlockStates.Count > 0 ? new ModelBank(atlas) : null;
        foreach (var v in p.CountBlocks(top).Keys)
        {
            if (Entities.IsInvisible(v)) { kind[v] = 0; continue; }   // Barriere, Licht … sieht man im Spiel nicht
            var (k, bx) = Classify(v, atlas.Mc); kind[v] = k; boxes[v] = bx; bas[v] = Blocks.BaseOf(v); emis[v] = Lighting.Emission(v) >= 10;
            opq[v] = k == 1 && !Entities.IsEntity(v);   // Truhen sind kleiner als ein Block: Nachbarn nicht verdecken
            if (k == 3) cr[v] = atlas.Cross(v); else ft[v] = atlas.Faces(v);
            if (k == 2) trans[v] = Blocks.IsWater(v) || IsSemi(atlas.Tiles[ft[v][0]]);
            if (bank != null && !Blocks.IsWater(v) && !Blocks.IsLava(v))
            {
                // Echte Modelle: einfache volle Würfel bleiben zusammengefasst, alles andere als Modell
                fam[v] = (byte)ModelBank.Family(v);
                var sc = k == 1 ? bank.SimpleCube(v) : null;
                if (sc != null) ft[v] = sc;
                else if (bank.Get(v, 0).Length > 0 || fam[v] != 0) { kind[v] = 5; opq[v] = false; }   // Modell (Fackel, Schild …) verdeckt die Nachbarn nicht
            }
        }
        int W0 = p.W, D0 = p.D, H = Math.Min(top, p.H - 1) + 1; long SY = (long)W0 * D0; var cells = p.Cells;
        float ox = opt?.UseOffset == true ? opt.OffX : -W0 / 2f, oz = opt?.UseOffset == true ? opt.OffZ : -D0 / 2f, offY = opt?.OffY ?? 0, baseFl = opt?.Flag ?? 0;
        var status = opt?.Status;
        int hiY = opt?.HighlightY ?? -1, clipAxis = opt?.ClipAxis ?? 0, clipPos = opt?.ClipPos ?? 0; bool clipHigh = opt?.ClipKeepHigh ?? false;
        var light = opt?.Light; var hidden = opt?.Hidden;
        // Schnitt: Spalten auf der weggeschnittenen Seite gibt es nicht
        bool Vis(int x, int z) { if (clipAxis == 0) return true; int c = clipAxis == 1 ? x : z; return clipHigh ? c >= clipPos : c <= clipPos; }
        bool VisI(long i) => (clipAxis == 0 || Vis((int)(i % W0), (int)(i / W0 % D0))) && (hidden == null || !hidden(i));
        // Licht eines Feldes (außerhalb: Himmel)
        int Lt(long i, bool inb) => !inb ? 240 : light == null ? 240 : light[i];
        // Baufortschritt: fehlende oder falsche Blöcke verdecken nichts (sie werden durchscheinend gezeigt)
        byte St(long i) { int x = (int)(i % W0), z = (int)(i / W0 % D0), y = (int)(i / SY); return status!(x, y, z, cells[i]); }
        bool Ghost(long i) => status != null && cells[i] != 0 && St(i) is 2 or 3;
        bool Opaque(long i) => opq[cells[i]] && (status == null || !Ghost(i)) && ((clipAxis == 0 && hidden == null) || VisI(i));
        var regs = new List<(int x0, int z0)>(); int rx = RegionsX(W0);
        for (int z0 = 0; z0 < D0; z0 += R) for (int x0 = 0; x0 < W0; x0 += R) regs.Add((x0, z0));
        var todoR = opt?.Regions != null ? opt.Regions.Where(r => r >= 0 && r < regs.Count).ToList() : Enumerable.Range(0, regs.Count).ToList();
        var outp = new MeshChunk?[regs.Count * 2];
        Parallel.For(0, todoR.Count, new ParallelOptions { CancellationToken = ct }, ti =>
        {
            int ri = todoR[ti];
            var (x0, z0) = regs[ri];
            int W = Math.Min(R, W0 - x0), D = Math.Min(R, D0 - z0); int nn = W * D * H;
            var cv = new ushort[nn]; var ex = new byte[nn]; bool any = false;
            var Vo = new List<float>(); var Io = new List<uint>(); var Vt = new List<float>(); var It = new List<uint>();
            bool curT = false; float curFl = baseFl, curL = DayLight;
            var lx = light == null ? null : new byte[nn * 6];   // Licht je Fläche der vollen Würfel (für das Zusammenfassen)
            void Quad(float[] c, float[] uv, int td, bool flip)
            {
                var V = curT ? Vt : Vo; var I = curT ? It : Io;
                uint b0 = (uint)(V.Count / Stride);
                for (int k = 0; k < 4; k++) { V.Add(c[k * 3]); V.Add(c[k * 3 + 1] + offY); V.Add(c[k * 3 + 2]); V.Add(uv[k * 2]); V.Add(uv[k * 2 + 1]); V.Add(td); V.Add(curFl); V.Add(curL); }
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
                    if (clipAxis != 0 && !Vis(gx, gz)) continue;
                    if (hidden != null && hidden(i)) continue;
                    float cellFl = y == hiY ? 4 : baseFl == 0 && emis[v] ? 7 : baseFl; curFl = cellFl;
                    // Licht: eigenes Feld (für Modelle und Teilblöcke) bzw. das hellste Nachbarfeld
                    if (light != null)
                    {
                        int own = light[i], best = own;
                        if (gx + 1 < W0) best = Math.Max(best, light[i + 1]); if (gx > 0) best = Math.Max(best, light[i - 1]);
                        if (y + 1 < H) best = Math.Max(best, light[i + SY]); else best = Math.Max(best, 240);
                        if (gz + 1 < D0) best = Math.Max(best, light[i + W0]); if (gz > 0) best = Math.Max(best, light[i - W0]);
                        curL = k == 1 ? MaxL(best, own) : MaxL(own, best & 0xF0);
                    }
                    // Nachbarn: undurchsichtig? / gleicher Block?
                    bool nPX = gx + 1 < W0 && Opaque(i + 1), nNX = gx > 0 && Opaque(i - 1);
                    bool nPY = y + 1 < H && Opaque(i + SY), nNY = y > 0 && Opaque(i - SY);
                    bool nPZ = gz + 1 < D0 && Opaque(i + W0), nNZ = gz > 0 && Opaque(i - W0);
                    if (status != null)
                    {
                        byte st = St(i);
                        if (st is 2 or 3)
                        {   // fehlt (hellblau) oder falscher Block (rot): durchscheinender Würfel mit den Plan-Texturen
                            var gf = atlas.Faces(v); float X0 = gx + ox, Z0 = gz + oz, X1 = X0 + 1, Z1 = Z0 + 1, Y0 = y, Y1 = y + 1f;
                            curT = true; curFl = st == 3 ? 1 : 2;
                            bool G(long j, bool inb) => inb && Ghost(j);
                            if (!nPX && !G(i + 1, gx + 1 < W0)) Quad(new[] { X1, Y0, Z0, X1, Y0, Z1, X1, Y1, Z1, X1, Y1, Z0 }, Unit, gf[0] * 8 + 0, true);
                            if (!nNX && !G(i - 1, gx > 0)) Quad(new[] { X0, Y0, Z0, X0, Y0, Z1, X0, Y1, Z1, X0, Y1, Z0 }, Unit, gf[1] * 8 + 1, false);
                            if (!nPY && !G(i + SY, y + 1 < H)) Quad(new[] { X0, Y1, Z0, X1, Y1, Z0, X1, Y1, Z1, X0, Y1, Z1 }, Unit, gf[2] * 8 + 2, true);
                            if (!nNY && !G(i - SY, y > 0)) Quad(new[] { X0, Y0, Z0, X1, Y0, Z0, X1, Y0, Z1, X0, Y0, Z1 }, Unit, gf[3] * 8 + 3, false);
                            if (!nPZ && !G(i + W0, gz + 1 < D0)) Quad(new[] { X0, Y0, Z1, X1, Y0, Z1, X1, Y1, Z1, X0, Y1, Z1 }, Unit, gf[4] * 8 + 4, false);
                            if (!nNZ && !G(i - W0, gz > 0)) Quad(new[] { X0, Y0, Z0, X1, Y0, Z0, X1, Y1, Z0, X0, Y1, Z0 }, Unit, gf[5] * 8 + 5, true);
                            curFl = cellFl; any = true; continue;
                        }
                    }
                    if (k == 5)
                    {   // echtes Blockmodell
                        int conn = 0, fv = fam[v];
                        if (fv != 0)
                        {
                            // Nachbarn Nord, Ost, Süd, West, oben, unten
                            int[] nb = { gz > 0 ? cells[i - W0] : 0, gx + 1 < W0 ? cells[i + 1] : 0, gz + 1 < D0 ? cells[i + W0] : 0, gx > 0 ? cells[i - 1] : 0, y + 1 < H ? cells[i + SY] : 0, y > 0 ? cells[i - SY] : 0 };
                            bool[] no = new bool[6]; for (int q2 = 0; q2 < 6; q2++) no[q2] = nb[q2] != 0 && opq[nb[q2]];
                            conn = fv == ModelBank.FamVine && ModelBank.SidesFromProps(v) is >= 0 and var sides ? sides : ModelBank.ConnMask(fv, nb, no, fam);
                        }
                        var qs = bank!.Get(v, conn);
                        if (p.Banners.Count > 0 && Entities.IsBanner(v) && p.Banners.TryGetValue(i, out var pat)) qs = bank.GetBanner(v, pat);
                        if (qs.Length == 0) qs = bank.Get(v, 0);
                        bool[] nOp = { nPX, nNX, nPY, nNY, nPZ, nNZ };
                        float wx = gx + ox, wz = gz + oz;
                        foreach (var q in qs)
                        {
                            if (q.Cull >= 0 && nOp[q.Cull]) continue;
                            curT = q.Semi;
                            var P = q.P;
                            Quad(new[] { wx + P[0], y + P[1], wz + P[2], wx + P[3], y + P[4], wz + P[5], wx + P[6], y + P[7], wz + P[8], wx + P[9], y + P[10], wz + P[11] },
                                 q.UV, q.Tile * 8 + q.Dir, false);
                        }
                        any = true; continue;
                    }
                    if (k == 1)
                    {
                        int bits = (nPX ? 0 : 1) | (nNX ? 0 : 2) | (nPY ? 0 : 4) | (nNY ? 0 : 8) | (nPZ ? 0 : 16) | (nNZ ? 0 : 32);
                        if (bits != 0)
                        {
                            cv[li] = (ushort)v; ex[li] = (byte)bits; any = true;
                            if (lx != null)
                            {   // Licht der Fläche = Licht im Feld davor
                                lx[li * 6 + 0] = (byte)Lt(i + 1, gx + 1 < W0); lx[li * 6 + 1] = (byte)Lt(i - 1, gx > 0);
                                lx[li * 6 + 2] = (byte)Lt(i + SY, y + 1 < H); lx[li * 6 + 3] = (byte)Lt(i - SY, y > 0);
                                lx[li * 6 + 4] = (byte)Lt(i + W0, gz + 1 < D0); lx[li * 6 + 5] = (byte)Lt(i - W0, gz > 0);
                            }
                        }
                        continue;
                    }
                    float bx = gx + ox, bz = gz + oz;
                    curT = false;
                    if (k == 3)
                    {
                        float s = .5f * .7071f * 1.13f, cx = bx + .5f, cz = bz + .5f; int td = cr[v] * 8 + 6;
                        Quad(new[] { cx - s, y, cz - s, cx + s, y, cz + s, cx + s, y + 1f, cz + s, cx - s, y + 1f, cz - s }, new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, td, false);
                        Quad(new[] { cx - s, y, cz + s, cx + s, y, cz - s, cx + s, y + 1f, cz - s, cx - s, y + 1f, cz + s }, new float[] { 0, 0, 1, 0, 1, 1, 0, 1 }, td, false);
                        any = true; continue;
                    }
                    var f = ft[v]; int bv = bas[v]; curT = trans[v];
                    if (cellFl == baseFl && baseFl == 0 && Blocks.IsWater(v)) curFl = 6;   // Wasser: für den Shader (Wellen, Spiegelung)
                    bool Same(long j, bool inb) => inb && VisI(j) && bas[cells[j]] == bv && kind[cells[j]] == 2 && boxes[cells[j]].Length == 1 && boxes[cells[j]][0] == Full;
                    bool isFull = boxes[v].Length == 1 && boxes[v][0] == Full;
                    var bxs = boxes[v];
                    // Wasseroberfläche liegt wie im Spiel etwas tiefer, wenn darüber kein Wasser ist
                    if (isFull && (Blocks.IsWater(v) || Blocks.IsLava(v)) && !Same(i + SY, y + 1 < H)) bxs = WaterTop;
                    foreach (var q in bxs)
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
            curT = false;
            if (!any) { outp[ri * 2] = new MeshChunk { Region = ri }; return; }
            // Zusammenfassen gleicher Flächen je Richtung und Schicht
            int[] dims = { W, H, D };
            for (int d = 0; d < 6; d++)
            {
                int na = NA[d], ua = AX[d][0], va = AX[d][1], bit = 1 << d, NU = dims[ua], NV = dims[va];
                var mask = new int[NU * NV]; var c = new int[3];
                for (int sl = 0; sl < dims[na]; sl++)
                {
                    bool has = false; c[na] = sl;
                    for (int vv = 0; vv < NV; vv++)
                    {
                        c[va] = vv;
                        for (int uu = 0; uu < NU; uu++)
                        {
                            c[ua] = uu; int li = (c[1] * D + c[2]) * W + c[0];
                            int m = 0;
                            if ((ex[li] & bit) != 0)
                            {   // Schlüssel: Kachel, hervorgehobene Ebene, Licht – nur Gleiches wird zusammengefasst
                                m = ft[cv[li]][d] + 1;
                                if (c[1] == hiY) m |= 1 << 16; else if (emis[cv[li]] && baseFl == 0) m |= 1 << 25;
                                if (lx != null) m |= lx[li * 6 + d] << 17;
                            }
                            mask[vv * NU + uu] = m; if (m != 0) has = true;
                        }
                    }
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
                            curFl = (k & (1 << 16)) != 0 ? 4 : (k & (1 << 25)) != 0 ? 7 : baseFl; curL = lx != null ? (k >> 17) & 0xFF : DayLight;
                            Quad(cs, uv, ((k & 0xFFFF) - 1) * 8 + d, d == 0 || d == 2 || d == 5);
                            uu += w;
                        }
                }
            }
            outp[ri * 2] = new MeshChunk { V = Vo.ToArray(), I = Io.ToArray(), Region = ri };
            if (It.Count > 0) outp[ri * 2 + 1] = new MeshChunk { V = Vt.ToArray(), I = It.ToArray(), Translucent = true, Region = ri };
        });
        return outp.Where(m => m != null).Select(m => m!).ToList();
    }
}
