using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;

namespace Blockplan.Core;

/// <summary>Eine Fläche eines Blockmodells, Ecken im Block (0..1), Reihenfolge oben-links, oben-rechts, unten-rechts, unten-links.</summary>
public sealed class MQuad
{
    public readonly float[] P = new float[12];
    public readonly float[] UV = new float[8];   // in Kachel-Einheiten (0..1), v nach oben
    public int Tile, Dir, Cull = -1;               // Dir/Cull: 0 +x Ost, 1 -x West, 2 +y oben, 3 -y unten, 4 +z Süd, 5 -z Nord
    public bool Semi;
}

/// <summary>
/// Echte Minecraft-Blockmodelle aus der Spieldatei: Blockzustand → Variante(n) → Modell mit Elementen.
/// Drehungen wie im Spiel: erst Element-Drehung um seinen Ursprung, dann Variante um die Blockmitte
/// (zuerst x, dann y, jeweils in 90°-Schritten). Zäune, Scheiben und Mauern verbinden sich mit Nachbarn.
/// </summary>
public sealed class ModelBank
{
    readonly McAssets mc; readonly Atlas atlas;
    readonly ConcurrentDictionary<long, MQuad[]> cache = new();
    readonly ConcurrentDictionary<int, int[]?> simple = new();
    readonly ConcurrentDictionary<string, (Dictionary<string, string> tex, JsonArray? el)> resolved = new();

    public ModelBank(Atlas a) { atlas = a; mc = a.Mc!; }

    /* ---------- Familien, die sich nach ihren Nachbarn richten ---------- */
    public const int FamFence = 1, FamPane = 2, FamWall = 3, FamVine = 4, FamRedstone = 5, FamMushroom = 6, FamChorus = 7, FamTripwire = 8;
    public static int Family(int v)
    {
        var k = Blocks.Get(Blocks.BaseOf(v)).Key;
        if (k.EndsWith("_fence")) return FamFence;
        if (k.EndsWith("_pane") || k == "iron_bars") return FamPane;
        if (k.EndsWith("_wall")) return FamWall;
        if (k is "vine" or "glow_lichen" or "sculk_vein" or "resin_clump") return FamVine;
        if (k == "redstone_wire") return FamRedstone;
        if (k is "brown_mushroom_block" or "red_mushroom_block" or "mushroom_stem") return FamMushroom;
        if (k == "chorus_plant") return FamChorus;
        if (k == "tripwire") return FamTripwire;
        return 0;
    }
    public static bool IsGate(int v) => Blocks.Get(Blocks.BaseOf(v)).Key.EndsWith("_fence_gate");
    static string K(int v) => v == 0 ? "" : Blocks.Get(Blocks.BaseOf(v)).Key;
    static readonly HashSet<string> RedstoneParts = new() { "redstone_wire", "repeater", "comparator", "redstone_torch", "redstone_wall_torch", "redstone_block", "lever", "observer", "target", "daylight_detector", "lectern", "trapped_chest" };

    /// <summary>
    /// Verbindungs-Bits aus den Nachbarn: 1 Nord, 2 Ost, 4 Süd, 8 West, 16 oben, 32 unten.
    /// nb/nOpq: Nachbarn in der Reihenfolge Nord, Ost, Süd, West, oben, unten (0 = Luft oder außerhalb).
    /// </summary>
    public static int ConnMask(int fam, int[] nb, bool[] nOpq, byte[] famOf)
    {
        int m = 0;
        switch (fam)
        {
            case FamFence: case FamPane: case FamWall:
                for (int i = 0; i < 4; i++)
                {
                    int nv = nb[i], nf = nv == 0 ? 0 : famOf[nv]; if (nv == 0) continue;
                    bool c = nOpq[i] || fam switch
                    {
                        FamFence => nf == FamFence || IsGate(nv),
                        FamPane => nf == FamPane || nf == FamWall,
                        _ => nf == FamWall || nf == FamPane || IsGate(nv),
                    };
                    if (c) m |= 1 << i;
                }
                if (fam == FamWall && m != 5 && m != 10) m |= 16;   // Pfosten, außer bei gerader Mauer
                return m;
            case FamVine:
                for (int i = 0; i < 6; i++) if (nb[i] != 0 && famOf[nb[i]] != FamVine && (nOpq[i] || Blocks.Get(Blocks.BaseOf(nb[i])).Shape == 'f')) m |= 1 << i;
                if (m == 0) m = 16;   // ohne Halt: oben, damit sie sichtbar bleibt
                return m;
            case FamRedstone:
                for (int i = 0; i < 4; i++) if (RedstoneParts.Contains(K(nb[i]))) m |= 1 << i;
                if (m is 1 or 4) m = 5; else if (m is 2 or 8) m = 10;   // einzelne Verbindung läuft wie im Spiel gerade durch
                return m;
            case FamMushroom: return 63;
            case FamChorus:
                for (int i = 0; i < 6; i++) { var k = K(nb[i]); if (k is "chorus_plant" or "chorus_flower" || (i == 5 && k == "end_stone")) m |= 1 << i; }
                return m;
            case FamTripwire:
                for (int i = 0; i < 4; i++) if (K(nb[i]) is "tripwire" or "tripwire_hook") m |= 1 << i;
                return m;
        }
        return 0;
    }

    /// <summary>Blockzustand plus berechnete Verbindungen (nur wo die Schematic sie nicht schon enthält).</summary>
    Dictionary<string, string> Props(int v, int conn)
    {
        var pr = Blocks.Get(v).Props is { } p ? new Dictionary<string, string>(p) : new Dictionary<string, string>();
        int fam = Family(v);
        if (fam == 0) return pr;
        string[] dn = { "north", "east", "south", "west", "up", "down" };
        void Set(int i, string on, string off, bool force) { if (force || !pr.ContainsKey(dn[i])) pr[dn[i]] = (conn & (1 << i)) != 0 ? on : off; }
        switch (fam)
        {
            case FamFence: case FamPane: case FamTripwire: for (int i = 0; i < 4; i++) Set(i, "true", "false", true); break;
            case FamWall: for (int i = 0; i < 4; i++) Set(i, "low", "none", true); Set(4, "true", "false", true); break;
            case FamVine: for (int i = 0; i < 6; i++) Set(i, "true", "false", false); break;
            case FamRedstone: for (int i = 0; i < 4; i++) Set(i, "side", "none", false); break;
            case FamMushroom: conn = 63; for (int i = 0; i < 6; i++) Set(i, "true", "false", false); break;   // ohne Angabe: Außenseite überall
            case FamChorus: for (int i = 0; i < 6; i++) Set(i, "true", "false", false); break;
        }
        return pr;
    }

    /* ---------- Einfärbung wie im Spiel (Flächen mit „tintindex“) ---------- */
    static uint? BlockTint(int v)
    {
        var k = K(v); var pr = Blocks.Get(v).Props;
        string P(string n, string d) => pr != null && pr.TryGetValue(n, out var x) ? x : d;
        switch (k)
        {
            case "grass_block" or "short_grass" or "tall_grass" or "fern" or "large_fern" or "potted_fern" or "sugar_cane" or "bush" or "pink_petals" or "wildflowers": return 0x91BD59;
            case "oak_leaves" or "jungle_leaves" or "acacia_leaves" or "dark_oak_leaves" or "vine": return 0x77AB2F;
            case "mangrove_leaves": return 0x92C162;
            case "birch_leaves": return 0x80A755;
            case "spruce_leaves": return 0x619961;
            case "leaf_litter": return 0xA88A50;
            case "lily_pad": return 0x208030;
            case "water_cauldron": return 0x3F76E4;
            case "attached_melon_stem" or "attached_pumpkin_stem": return 0xE0C71C;
            case "melon_stem" or "pumpkin_stem":
            {
                int a = int.TryParse(P("age", "0"), out var ag) ? ag : 0;
                return (uint)(a * 32) << 16 | (uint)(255 - a * 8) << 8 | (uint)(a * 4);
            }
            case "redstone_wire":
            {
                float f = (int.TryParse(P("power", "0"), out var pw) ? pw : 0) / 15f;
                float r = f > 0 ? f * .6f + .4f : .3f, g = Math.Clamp(f * f * .7f - .5f, 0, 1), b = Math.Clamp(f * f * .6f - .7f, 0, 1);
                return (uint)(r * 255) << 16 | (uint)(g * 255) << 8 | (uint)(b * 255);
            }
        }
        return null;
    }

    static string Str(JsonNode? n) => n switch
    {
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonValue v when v.TryGetValue<double>(out var d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture),
        JsonObject o when o["sprite"] != null => Str(o["sprite"]),
        _ => "",
    };
    static string Nm(string s) => s.Replace("minecraft:", "").Replace("block/", "");

    static bool When(JsonNode? w, Dictionary<string, string> pr)
    {
        if (w is not JsonObject o) return true;
        if (o["OR"] is JsonArray or) return or.Any(x => When(x, pr));
        if (o["AND"] is JsonArray and) return and.All(x => When(x, pr));
        foreach (var kv in o)
        {
            if (!pr.TryGetValue(kv.Key, out var have)) return false;
            if (!Str(kv.Value).Split('|').Contains(have)) return false;
        }
        return true;
    }

    /// <summary>Passende Modelle mit Drehung für einen Blockzustand.</summary>
    List<(string model, int x, int y)> Pick(int v, int conn)
    {
        var res = new List<(string, int, int)>();
        var key = Blocks.Get(Blocks.BaseOf(v)).Key; if (key.Contains(':')) return res;
        if (!mc.BlockStates.TryGetValue(key, out var bs)) return res;
        var pr = Props(v, conn);
        static (string, int, int) Entry(JsonNode? e)
        {
            if (e is JsonArray a) e = a[0];
            return (Str(e?["model"]), (int?)e?["x"]?.GetValue<double>() ?? 0, (int?)e?["y"]?.GetValue<double>() ?? 0);
        }
        if (bs["variants"] is JsonObject vars)
        {
            JsonNode? hit = null;
            foreach (var kv in vars)
            {
                bool ok = kv.Key.Length == 0 || kv.Key.Split(',').All(c => { var a = c.Split('='); return a.Length == 2 && pr.TryGetValue(a[0], out var h) && h == a[1]; });
                if (ok) { hit = kv.Value; break; }
            }
            hit ??= vars.First().Value;
            res.Add(Entry(hit));
        }
        else if (bs["multipart"] is JsonArray mp)
            foreach (var part in mp) if (When(part!["when"], pr)) res.Add(Entry(part["apply"]));
        return res;
    }

    (Dictionary<string, string> tex, JsonArray? el) Resolve(string name) => resolved.GetOrAdd(Nm(name), n =>
    {
        var tex = new Dictionary<string, string>(); JsonArray? el = null; var chain = new List<JsonNode>();
        string? cur = n; int g = 0;
        while (cur != null && g++ < 24 && mc.Models.TryGetValue(Nm(cur), out var j)) { chain.Add(j); var par = Str(j["parent"]); cur = par.Length > 0 ? par : null; }
        for (int i = chain.Count - 1; i >= 0; i--)   // vom obersten Elternmodell zum Kind: Kind überschreibt
            if (chain[i]["textures"] is JsonObject t) foreach (var kv in t) tex[kv.Key] = Str(kv.Value);
        foreach (var j in chain) if (j["elements"] is JsonArray a) { el = a; break; }
        return (tex, el);
    });

    static string? Tex(Dictionary<string, string> tex, string r)
    {
        int g = 0; while (r.StartsWith('#') && g++ < 10) { if (!tex.TryGetValue(r[1..], out var n)) return null; r = n; }
        return r.Length == 0 || r.StartsWith('#') ? null : Nm(r);
    }

    static readonly string[] FaceNames = { "east", "west", "up", "down", "south", "north" };
    static readonly Vector3[] DirVec = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };

    /// <summary>Drehung der Variante: erst um x, dann um y, je in 90°-Schritten, um die Blockmitte.</summary>
    internal static Vector3 RotV(Vector3 p, int rx, int ry)
    {
        var c = new Vector3(.5f); var d = p - c;
        for (int i = 0; i < ((rx / 90) % 4 + 4) % 4; i++) d = new Vector3(d.X, d.Z, -d.Y);   // Nord → unten
        for (int i = 0; i < ((ry / 90) % 4 + 4) % 4; i++) d = new Vector3(-d.Z, d.Y, d.X);   // Nord → Ost (im Uhrzeigersinn von oben)
        return d + c;
    }
    static Vector3 RotDir(Vector3 d, int rx, int ry) => RotV(d + new Vector3(.5f), rx, ry) - new Vector3(.5f);
    static int DirOf(Vector3 n)
    {
        float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
        if (ay >= ax && ay >= az) return n.Y >= 0 ? 2 : 3;
        if (ax >= az) return n.X >= 0 ? 0 : 1;
        return n.Z >= 0 ? 4 : 5;
    }

    readonly ConcurrentDictionary<string, MQuad[]> bannerCache = new();
    /// <summary>Banner mit Mustern (je Muster-Kombination einmal gebaut).</summary>
    public MQuad[] GetBanner(int v, string patterns) => bannerCache.GetOrAdd(v + "#" + patterns, _ => Entities.Build(v, mc, atlas, patterns).ToArray());

    /// <summary>Flächen für Blockzustand v mit Verbindungen conn (leer, wenn es kein Modell gibt).</summary>
    public MQuad[] Get(int v, int conn) => cache.GetOrAdd(((long)v << 8) | (uint)conn, _ => Build(v, conn));

    MQuad[] Build(int v, int conn)
    {
        var outq = new List<MQuad>();
        foreach (var (model, rx, ry) in Pick(v, conn))
        {
            var (tex, el) = Resolve(model);
            if (el == null) continue;
            foreach (var e in el)
            {
                if (e is not JsonObject eo) continue;
                var f = eo["from"]!.AsArray().Select(x => (float)x!.GetValue<double>()).ToArray();
                var t = eo["to"]!.AsArray().Select(x => (float)x!.GetValue<double>()).ToArray();
                float x1 = f[0], y1 = f[1], z1 = f[2], x2 = t[0], y2 = t[1], z2 = t[2];
                bool shade = eo["shade"] is not JsonValue sv || !sv.TryGetValue<bool>(out var sb) || sb;
                // Element-Drehung
                Matrix4x4? erot = null; Vector3 eorg = default; Vector3 escale = Vector3.One;
                if (eo["rotation"] is JsonObject ro)
                {
                    float ang = (float)(ro["angle"]?.GetValue<double>() ?? 0) * MathF.PI / 180;
                    var o = ro["origin"]!.AsArray().Select(x => (float)x!.GetValue<double>() / 16f).ToArray(); eorg = new Vector3(o[0], o[1], o[2]);
                    string axis = Str(ro["axis"]);
                    erot = axis == "x" ? Matrix4x4.CreateRotationX(ang) : axis == "z" ? Matrix4x4.CreateRotationZ(ang) : Matrix4x4.CreateRotationY(ang);
                    if (ro["rescale"] is JsonValue rv && rv.TryGetValue<bool>(out var rs) && rs && MathF.Abs(ang) > 0.001f)
                    {
                        float k = 1 / MathF.Cos(MathF.Abs(ang));
                        escale = axis == "x" ? new Vector3(1, k, k) : axis == "z" ? new Vector3(k, k, 1) : new Vector3(k, 1, k);
                    }
                }
                if (eo["faces"] is not JsonObject faces) continue;
                foreach (var kv in faces)
                {
                    int fi = Array.IndexOf(FaceNames, kv.Key); if (fi < 0 || kv.Value is not JsonObject fo) continue;
                    var tn = Tex(tex, Str(fo["texture"])); int tile = tn == null ? -1 : atlas.TileByName(tn);
                    if (tile >= 0 && fo["tintindex"] != null && !McAssets.IsPretinted(tn!) && BlockTint(v) is uint tc && mc.Tinted(tn!, tc) is { } tpx) tile = atlas.TileOf(tpx);
                    if (tile < 0) continue;
                    // Ecken oben-links, oben-rechts, unten-rechts, unten-links (von außen gesehen) und Standard-UV
                    Vector3[] c; float[] uv;
                    switch (kv.Key)
                    {
                        case "north": c = new Vector3[] { new(x2, y2, z1), new(x1, y2, z1), new(x1, y1, z1), new(x2, y1, z1) }; uv = new[] { 16 - x2, 16 - y2, 16 - x1, 16 - y1 }; break;
                        case "south": c = new Vector3[] { new(x1, y2, z2), new(x2, y2, z2), new(x2, y1, z2), new(x1, y1, z2) }; uv = new[] { x1, 16 - y2, x2, 16 - y1 }; break;
                        case "west": c = new Vector3[] { new(x1, y2, z1), new(x1, y2, z2), new(x1, y1, z2), new(x1, y1, z1) }; uv = new[] { z1, 16 - y2, z2, 16 - y1 }; break;
                        case "east": c = new Vector3[] { new(x2, y2, z2), new(x2, y2, z1), new(x2, y1, z1), new(x2, y1, z2) }; uv = new[] { 16 - z2, 16 - y2, 16 - z1, 16 - y1 }; break;
                        case "up": c = new Vector3[] { new(x1, y2, z1), new(x2, y2, z1), new(x2, y2, z2), new(x1, y2, z2) }; uv = new[] { x1, z1, x2, z2 }; break;
                        default: c = new Vector3[] { new(x1, y1, z2), new(x2, y1, z2), new(x2, y1, z1), new(x1, y1, z1) }; uv = new[] { x1, 16 - z2, x2, 16 - z1 }; break;
                    }
                    if (fo["uv"] is JsonArray ua && ua.Count == 4) uv = ua.Select(x => (float)x!.GetValue<double>()).ToArray();
                    float[][] uc = { new[] { uv[0], uv[1] }, new[] { uv[2], uv[1] }, new[] { uv[2], uv[3] }, new[] { uv[0], uv[3] } };
                    int urot = (((int)(fo["rotation"]?.GetValue<double>() ?? 0)) / 90 % 4 + 4) % 4;
                    var q = new MQuad { Tile = tile, Semi = IsSemi(atlas.Tiles[tile]) };
                    for (int k = 0; k < 4; k++)
                    {
                        var p = c[k] / 16f;
                        if (erot is { } m) p = Vector3.Transform((p - eorg) * escale, m) + eorg;
                        p = RotV(p, rx, ry);
                        q.P[k * 3] = p.X; q.P[k * 3 + 1] = p.Y; q.P[k * 3 + 2] = p.Z;
                        var u = uc[((k - urot) % 4 + 4) % 4];
                        q.UV[k * 2] = u[0] / 16f; q.UV[k * 2 + 1] = 1 - u[1] / 16f;
                    }
                    var nrm = DirVec[fi]; if (erot is { } m2) nrm = Vector3.TransformNormal(nrm, m2);
                    q.Dir = shade ? DirOf(RotDir(nrm, rx, ry)) : 2;
                    var cf = Str(fo["cullface"]); int ci = Array.IndexOf(FaceNames, cf == "bottom" ? "down" : cf);
                    if (ci >= 0) q.Cull = DirOf(RotDir(DirVec[ci], rx, ry));
                    outq.Add(q);
                }
            }
        }
        // Truhen und Banner haben kein Blockmodell (das Spiel zeichnet sie als Objekte)
        if (outq.Count == 0 && Entities.IsEntity(v)) outq.AddRange(Entities.Build(v, mc, atlas));
        if (K(v) == "bell") outq.AddRange(Entities.BellBody(mc, atlas));   // Gestell aus dem Modell, Glocke als Objekt
        return outq.ToArray();
    }

    /// <summary>
    /// Für das schnelle Zusammenfassen: Ist das Modell ein einziger voller Würfel, die Kachel je Seite
    /// (+x,-x,+y,-y,+z,-z) nach Drehung – sonst null.
    /// </summary>
    public int[]? SimpleCube(int v) => simple.GetOrAdd(v, _ =>
    {
        if (Pick(v, 0).Any(m => m.x % 360 != 0)) return null;   // gekippte Blöcke (z. B. liegende Stämme) mit echter Texturdrehung
        var q = Get(v, 0);
        if (q.Length != 6) return null;
        var t = new int[6]; var seen = new bool[6];
        foreach (var f in q)
        {
            for (int k = 0; k < 12; k++) if (f.P[k] is > 0.001f and < 0.999f) return null;
            // volle Kachel ohne Teilausschnitt
            float umin = Math.Min(Math.Min(f.UV[0], f.UV[2]), Math.Min(f.UV[4], f.UV[6])), umax = Math.Max(Math.Max(f.UV[0], f.UV[2]), Math.Max(f.UV[4], f.UV[6]));
            if (umax - umin < 0.999f) return null;
            if (seen[f.Dir]) return null; seen[f.Dir] = true; t[f.Dir] = f.Tile;
        }
        return seen.All(x => x) ? t : null;
    });

    static bool IsSemi(byte[] px)
    {
        int semi = 0, n = px.Length / 4;
        for (int i = 0; i < n; i++) { byte a = px[i * 4 + 3]; if (a > 20 && a < 235) semi++; }
        return semi > n / 2;
    }
}
