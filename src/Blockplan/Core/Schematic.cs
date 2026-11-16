using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Blockplan.Core;

/// <summary>Eingelesene Schematic: Blöcke schon als Blockplan-Indizes, Reihenfolge (y*D+z)*W+x.</summary>
public sealed class Schem
{
    public int W, H, D;
    public ushort[] Cells = Array.Empty<ushort>();
    public string Name = "";
    public long Count; public int Types;
    public int X0, Y0, Z0, X1, Y1, Z1;          // belegter Bereich
    public long Water, WaterPlants, Waterlogged;
    public long Snow, SnowBlocks, Snowy;       // Schneedecken/Pulverschnee, Schneeblöcke, verschneites Gras usw.
    /// <summary>Banner-Muster je Feld (Index wie Cells), z. B. "stripe_top:red|creeper:black".</summary>
    public Dictionary<long, string> Banners = new();
}

public static class SchematicReader
{
    static string StateOf(string name, Dictionary<string, object>? props)
    {
        if (props == null || props.Count == 0) return name;
        return name + "[" + string.Join(",", props.Select(kv => kv.Key + "=" + kv.Value)) + "]";
    }

    public static Schem Load(string path, Action<string>? progress = null)
    {
        progress?.Invoke("Lese Datei …");
        var raw = Nbt.GunzipMaybe(File.ReadAllBytes(path));
        progress?.Invoke("Entschlüssele Schematic …");
        var root = Nbt.Read(raw);
        var s = root.C("Schematic") ?? root;
        Schem r;
        if (s.C("Regions") != null) r = Litematic(s);
        else if (s.G("palette") != null || s.G("palettes") != null) r = Structure(s);
        else r = Sponge(s);
        r.Name = Path.GetFileNameWithoutExtension(path);
        progress?.Invoke("Zähle Blöcke …");
        Analyze(r);
        if (r.Count == 0) throw new InvalidDataException("In der Schematic sind keine Blöcke.");
        return r;
    }

    static ushort[] Alloc(long n)
    {
        if (n <= 0 || n > 1_500_000_000L) throw new InvalidDataException($"Die Schematic ist mit {n:N0} Feldern zu groß.");
        return new ushort[n];
    }

    static Schem Sponge(Dictionary<string, object> s)
    {
        Dictionary<string, object>? pal; byte[]? data;
        var blocks = s.C("Blocks");
        if (blocks != null && blocks.C("Palette") != null) { pal = blocks.C("Palette"); data = blocks.G("Data") as byte[]; }
        else if (s.C("Palette") != null) { pal = s.C("Palette"); data = s.G("BlockData") as byte[]; }
        else if (s.G("Blocks") is byte[] || s.G("Materials") != null)
            throw new InvalidDataException("Das ist das alte MCEdit-Format (vor 1.13). Speichere die Schematic in WorldEdit neu als .schem.");
        else throw new InvalidDataException("Unbekanntes Schematic-Format.");
        if (pal == null || data == null) throw new InvalidDataException("Die Schematic ist unvollständig.");
        int w = Nbt.Int(s.G("Width")) & 0xffff, h = Nbt.Int(s.G("Height")) & 0xffff, d = Nbt.Int(s.G("Length")) & 0xffff;
        var map = new ushort[pal.Count + 1];
        foreach (var kv in pal) { int i = Nbt.Int(kv.Value); if (i >= 0 && i < map.Length) map[i] = (ushort)Blocks.Ensure(kv.Key); }
        long n = (long)w * h * d; var cells = Alloc(n);
        long ci = 0; int p = 0;
        while (ci < n && p < data.Length)
        {
            int v = 0, sh = 0, b;
            do { b = data[p++]; v |= (b & 127) << sh; sh += 7; } while ((b & 128) != 0 && p < data.Length);
            cells[ci++] = v < map.Length ? map[v] : (ushort)0;
        }
        var sc = new Schem { W = w, H = h, D = d, Cells = cells };
        // Block-Entities: v2 „BlockEntities“ (Felder direkt), v3 „Blocks.BlockEntities“ (Felder unter „Data“), v1 „TileEntities“
        var bes = (blocks?.G("BlockEntities") ?? s.G("BlockEntities") ?? s.G("TileEntities")) as List<object>;
        if (bes != null)
            foreach (var o in bes.OfType<Dictionary<string, object>>())
                if (o.G("Pos") is int[] pos && pos.Length == 3) AddBanner(sc, pos[0], pos[1], pos[2], o.C("Data") ?? o);
        return sc;
    }

    static Schem Litematic(Dictionary<string, object> s)
    {
        var regs = s.C("Regions")!.Values.OfType<Dictionary<string, object>>().ToList();
        int[] mn = { int.MaxValue, int.MaxValue, int.MaxValue }, mx = { int.MinValue, int.MinValue, int.MinValue };
        var R = new List<(Dictionary<string, object> r, int[] lo, int[] A)>();
        foreach (var r in regs)
        {
            var P = r.C("Position")!; var S = r.C("Size")!;
            int[] pp = { Nbt.Int(P.G("x")), Nbt.Int(P.G("y")), Nbt.Int(P.G("z")) }, ss = { Nbt.Int(S.G("x")), Nbt.Int(S.G("y")), Nbt.Int(S.G("z")) };
            var lo = new int[3]; var A = new int[3];
            for (int k = 0; k < 3; k++) { lo[k] = pp[k] + (ss[k] < 0 ? ss[k] + 1 : 0); A[k] = Math.Abs(ss[k]); mn[k] = Math.Min(mn[k], lo[k]); mx[k] = Math.Max(mx[k], lo[k] + A[k] - 1); }
            R.Add((r, lo, A));
        }
        int w = mx[0] - mn[0] + 1, h = mx[1] - mn[1] + 1, d = mx[2] - mn[2] + 1;
        var cells = Alloc((long)w * h * d);
        foreach (var (r, lo, A) in R)
        {
            var palL = (List<object>)r.G("BlockStatePalette")!;
            var names = palL.Cast<Dictionary<string, object>>().Select(e => StateOf((string)e["Name"], e.C("Properties"))).ToArray();
            var map = names.Select(n => (ushort)Blocks.Ensure(n)).ToArray();
            int bits = Math.Max(2, 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)Math.Max(0, names.Length - 1)));
            ulong mask = (1UL << bits) - 1;
            var L = (long[])r.G("BlockStates")!;
            int sx = A[0], sy = A[1], sz = A[2];
            for (int y = 0; y < sy; y++) for (int z = 0; z < sz; z++) for (int x = 0; x < sx; x++)
            {
                long i = ((long)y * sz + z) * sx + x, sb = i * bits; int li = (int)(sb >> 6), off = (int)(sb & 63);
                ulong v = (ulong)L[li] >> off;
                if (off + bits > 64) v |= (ulong)L[li + 1] << (64 - off);
                v &= mask;
                int pv = (int)v; if (pv >= map.Length) continue;
                var gv = map[pv]; if (gv == 0) continue;
                cells[((long)(lo[1] - mn[1] + y) * d + (lo[2] - mn[2] + z)) * w + (lo[0] - mn[0] + x)] = gv;
            }
        }
        var sc = new Schem { W = w, H = h, D = d, Cells = cells };
        foreach (var (r, lo, _) in R)
            if (r.G("TileEntities") is List<object> tes)
                foreach (var o in tes.OfType<Dictionary<string, object>>())
                    AddBanner(sc, lo[0] - mn[0] + Nbt.Int(o.G("x")), lo[1] - mn[1] + Nbt.Int(o.G("y")), lo[2] - mn[2] + Nbt.Int(o.G("z")), o);
        return sc;
    }

    static Schem Structure(Dictionary<string, object> s)
    {
        var size = ((List<object>)s.G("size")!).Select(Nbt.Int).ToArray();
        int w = size[0], h = size[1], d = size[2];
        var palL = (s.G("palette") as List<object>) ?? (List<object>)((List<object>)s.G("palettes")!)[0];
        var map = palL.Cast<Dictionary<string, object>>().Select(e => (ushort)Blocks.Ensure(StateOf((string)e["Name"], e.C("Properties")))).ToArray();
        var cells = Alloc((long)w * h * d);
        foreach (Dictionary<string, object> b in (List<object>)s.G("blocks")!)
        {
            var p = ((List<object>)b["pos"]).Select(Nbt.Int).ToArray(); int st = Nbt.Int(b.G("state"));
            if (st < map.Length) cells[((long)p[1] * d + p[2]) * w + p[0]] = map[st];
        }
        var sc = new Schem { W = w, H = h, D = d, Cells = cells };
        foreach (Dictionary<string, object> b in (List<object>)s.G("blocks")!)
            if (b.C("nbt") is { } nbt) { var p = ((List<object>)b["pos"]).Select(Nbt.Int).ToArray(); AddBanner(sc, p[0], p[1], p[2], nbt); }
        return sc;
    }

    /* ---------- Banner-Muster ---------- */
    static readonly string[] DyeOrder = { "white", "orange", "magenta", "light_blue", "yellow", "lime", "pink", "gray", "light_gray", "cyan", "purple", "blue", "brown", "green", "red", "black" };
    static readonly Dictionary<string, string> LegacyPattern = new()
    {
        ["bs"] = "stripe_bottom", ["ts"] = "stripe_top", ["ls"] = "stripe_left", ["rs"] = "stripe_right", ["cs"] = "stripe_center", ["ms"] = "stripe_middle",
        ["drs"] = "stripe_downright", ["dls"] = "stripe_downleft", ["ss"] = "small_stripes", ["cr"] = "cross", ["sc"] = "straight_cross", ["ld"] = "diagonal_left",
        ["rud"] = "diagonal_right", ["lud"] = "diagonal_up_left", ["rd"] = "diagonal_up_right", ["vh"] = "half_vertical", ["vhr"] = "half_vertical_right",
        ["hh"] = "half_horizontal", ["hhb"] = "half_horizontal_bottom", ["bl"] = "square_bottom_left", ["br"] = "square_bottom_right", ["tl"] = "square_top_left",
        ["tr"] = "square_top_right", ["bt"] = "triangle_bottom", ["tt"] = "triangle_top", ["bts"] = "triangles_bottom", ["tts"] = "triangles_top", ["mc"] = "circle",
        ["mr"] = "rhombus", ["bo"] = "border", ["cbo"] = "curly_border", ["bri"] = "bricks", ["gra"] = "gradient", ["gru"] = "gradient_up", ["cre"] = "creeper",
        ["sku"] = "skull", ["flo"] = "flower", ["moj"] = "mojang", ["glb"] = "globe", ["pig"] = "piglin", ["flw"] = "flow", ["gus"] = "guster",
    };

    /// <summary>Muster aus den Zusatzdaten eines Banners (neues Format „patterns“, altes „Patterns“).</summary>
    static void AddBanner(Schem sc, int x, int y, int z, Dictionary<string, object> d)
    {
        if (x < 0 || y < 0 || z < 0 || x >= sc.W || y >= sc.H || z >= sc.D) return;
        var parts = new List<string>();
        if ((d.G("patterns") ?? d.C("components")?.G("minecraft:banner_patterns")) is List<object> np)
            foreach (var e in np.OfType<Dictionary<string, object>>())
            {
                var pat = e.G("pattern") switch { string ps => ps, Dictionary<string, object> pc => pc.G("asset_id") as string ?? "", _ => "" };
                var col = e.G("color") as string ?? "white";
                pat = pat.Replace("minecraft:", ""); if (pat.Length > 0) parts.Add(pat + ":" + col);
            }
        else if (d.G("Patterns") is List<object> op)
            foreach (var e in op.OfType<Dictionary<string, object>>())
            {
                var code = e.G("Pattern") as string ?? ""; int c = Nbt.Int(e.G("Color"));
                if (LegacyPattern.TryGetValue(code, out var pat)) parts.Add(pat + ":" + DyeOrder[Math.Clamp(c, 0, 15)]);
            }
        if (parts.Count > 0) sc.Banners[((long)y * sc.D + z) * sc.W + x] = string.Join("|", parts);
    }

    static void Analyze(Schem r)
    {
        long WD = (long)r.W * r.D; var seen = new HashSet<int>();
        int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = -1, y1 = -1, z1 = -1; long cnt = 0;
        var colUsed = new bool[WD];
        var wk = new Dictionary<ushort, int>(); var sk = new Dictionary<ushort, int>();
        for (long i = 0; i < r.Cells.LongLength; i++)
        {
            var v = r.Cells[i]; if (v == 0) continue;
            cnt++; long c = i % WD; int y = (int)(i / WD); colUsed[c] = true; if (y < y0) y0 = y; if (y > y1) y1 = y;
            if (!wk.TryGetValue(v, out var k)) { k = WaterKind(v); wk[v] = k; seen.Add(Blocks.BaseOf(v)); }
            if (k == 1) r.Water++; else if (k == 2) r.WaterPlants++; else if (k == 3) r.Waterlogged++;
            if (!sk.TryGetValue(v, out var sn)) { sn = SnowKind(v); sk[v] = sn; }
            if (sn == 1) r.Snow++; else if (sn == 2) r.SnowBlocks++; else if (sn == 3) r.Snowy++;
        }
        for (long c = 0; c < WD; c++) if (colUsed[c]) { int x = (int)(c % r.W), z = (int)(c / r.W); x0 = Math.Min(x0, x); x1 = Math.Max(x1, x); z0 = Math.Min(z0, z); z1 = Math.Max(z1, z); }
        r.Count = cnt; r.Types = seen.Count; r.X0 = x0; r.Y0 = y0; r.Z0 = z0; r.X1 = x1; r.Y1 = y1; r.Z1 = z1;
    }

    static readonly Regex wplant = new("^(seagrass|tall_seagrass|kelp|kelp_plant)$");
    /// <summary>1 Wasser, 2 Wasserpflanze, 3 geflutet, 0 sonst</summary>
    public static int WaterKind(int v)
    {
        if (v == 0) return 0; var b = Blocks.Get(Blocks.BaseOf(v));
        if (b.Key == "water" || b.Key == "bubble_column") return 1;
        if (wplant.IsMatch(b.Key)) return 2;
        var me = Blocks.Get(v); return me.Props != null && me.Props.TryGetValue("waterlogged", out var wl) && wl == "true" ? 3 : 0;
    }

    static readonly Dictionary<string, string> CW = new() { ["north"] = "east", ["east"] = "south", ["south"] = "west", ["west"] = "north" };
    static string RotateKey(string key, int q)
    {
        int br = key.IndexOf('['); if (q == 0 || br < 0) return key;
        var pr = key[(br + 1)..].TrimEnd(']').Split(',').Select(p => p.Split('=')).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1]);
        for (int k = 0; k < q; k++)
        {
            if (pr.TryGetValue("facing", out var f) && CW.ContainsKey(f)) pr["facing"] = CW[f];
            if (pr.TryGetValue("axis", out var ax)) pr["axis"] = ax == "x" ? "z" : ax == "z" ? "x" : ax;
            if (pr.TryGetValue("rotation", out var ro) && int.TryParse(ro, out var rv)) pr["rotation"] = ((rv + 4) % 16).ToString();
            pr.TryGetValue("north", out var n); pr.TryGetValue("east", out var e); pr.TryGetValue("south", out var s2); pr.TryGetValue("west", out var w);
            if (n != null || e != null || s2 != null || w != null)
            {
                foreach (var d in new[] { "north", "east", "south", "west" }) pr.Remove(d);
                if (n != null) pr["east"] = n; if (e != null) pr["south"] = e; if (s2 != null) pr["west"] = s2; if (w != null) pr["north"] = w;
            }
            if (pr.TryGetValue("shape", out var sh))
            {
                sh = sh == "north_south" ? "east_west" : sh == "east_west" ? "north_south" : sh;
                sh = Regex.Replace(sh, @"(ascending|south|north)_(east|west|north|south)$", m => Regex.Replace(m.Value, "north|east|south|west", d => CW[d.Value]));
                pr["shape"] = sh;
            }
        }
        return key[..br] + "[" + string.Join(",", pr.Select(kv => kv.Key + "=" + kv.Value)) + "]";
    }

    /// <summary>Ausschnitt als Projekt übernehmen, optional ohne Wasser und gedreht (q × 90° im Uhrzeigersinn).</summary>
    /// <summary>1 Schneedecke/Pulverschnee, 2 Schneeblock, 3 verschneiter Block (snowy=true), 0 sonst</summary>
    public static int SnowKind(int v)
    {
        if (v == 0) return 0; var b = Blocks.Get(Blocks.BaseOf(v));
        if (b.Key == "snow" || b.Key == "powder_snow") return 1;
        if (b.Key == "snow_block") return 2;
        var me = Blocks.Get(v); return me.Props != null && me.Props.TryGetValue("snowy", out var sv) && sv == "true" ? 3 : 0;
    }

    /// <summary>Schnee weg: Decken und Pulverschnee entfernen (Schneeblöcke auf Wunsch), verschneite Blöcke auftauen.</summary>
    public static int Desnow(int v, bool blocks)
    {
        int k = SnowKind(v);
        if (k == 1 || (k == 2 && blocks)) return 0;
        if (k == 3) return Blocks.Ensure(Blocks.Get(v).Key.Replace("snowy=true", "snowy=false"));
        return v;
    }

    public static Project ToProject(Schem s, int x0, int y0, int z0, int x1, int y1, int z1, bool dewater, bool dewaterPlants, int q, bool desnow = false, bool desnowBlocks = false)
    {
        int w = x1 - x0 + 1, h = y1 - y0 + 1, d = z1 - z0 + 1;
        int W2 = q % 2 == 1 ? d : w, D2 = q % 2 == 1 ? w : d;
        var cells = new ushort[(long)W2 * D2 * h];
        var conv = new Dictionary<ushort, ushort>(); var banners = new Dictionary<long, string>();
        ushort Conv(ushort v)
        {
            if (conv.TryGetValue(v, out var r)) return r;
            int o = v;
            if (dewater)
            {
                int k = WaterKind(o);
                if (k == 1 || (k == 2 && dewaterPlants)) o = 0;
                else if (k == 3) o = Blocks.Ensure(Regex.Replace(Regex.Replace(Blocks.Get(o).Key, "waterlogged=true,?", ""), @",?\]$", "]").Replace("[]", ""));
            }
            if (o != 0 && desnow) o = Desnow(o, desnowBlocks);
            if (o != 0 && q != 0) o = Blocks.Ensure(RotateKey(Blocks.Get(o).Key, q));
            return conv[v] = (ushort)o;
        }
        for (int y = 0; y < h; y++) for (int z = 0; z < d; z++)
        {
            long si = ((long)(y + y0) * s.D + (z + z0)) * s.W + x0;
            for (int x = 0; x < w; x++)
            {
                var v = s.Cells[si + x]; if (v == 0) continue;
                int nx = x, nz = z;
                switch (q) { case 1: nx = d - 1 - z; nz = x; break; case 2: nx = w - 1 - x; nz = d - 1 - z; break; case 3: nx = z; nz = w - 1 - x; break; }
                long ni = ((long)y * D2 + nz) * W2 + nx;
                cells[ni] = Conv(v);
                if (s.Banners.TryGetValue(si + x, out var bp)) banners[ni] = bp;
            }
        }
        return new Project { Name = s.Name, W = W2, D = D2, H = h, Cells = cells, Banners = banners };
    }
}
