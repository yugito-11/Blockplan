using System;
using System.Collections.Generic;
using System.Numerics;

namespace Blockplan.Core;

/// <summary>
/// Truhen und Banner: Minecraft zeichnet sie nicht über Blockmodelle, sondern als Objekte mit eigenen Texturen
/// (textures/entity/chest, textures/entity/banner). Hier mit den Maßen des Spiels nachgebaut.
/// Maße in Pixeln (16 = ein Block), y nach oben. Grundstellung: Vorderseite nach Süden.
/// </summary>
public static class Entities
{
    public static bool IsEntity(int v)
    {
        var k = Blocks.Get(Blocks.BaseOf(v)).Key;
        return k.EndsWith("chest") || k.EndsWith("_banner") || k.EndsWith("shulker_box") || k.EndsWith("_head") || k.EndsWith("_skull")
            || k == "decorated_pot" || k == "conduit" || k.Contains("copper_golem_statue") || k == "end_portal" || k == "end_gateway";
    }

    /// <summary>Im Spiel unsichtbar (Barriere, Licht, Strukturleere, bewegender Kolben, Luft).</summary>
    public static bool IsInvisible(int v)
    {
        var k = Blocks.Get(Blocks.BaseOf(v)).Key;
        return k is "barrier" or "light" or "structure_void" or "moving_piston" or "air" or "cave_air" or "void_air";
    }

    // Farben der Farbstoffe wie im Spiel (für Banner)
    static readonly Dictionary<string, uint> Dye = new()
    {
        ["white"] = 0xF9FFFE, ["orange"] = 0xF9801D, ["magenta"] = 0xC74EBD, ["light_blue"] = 0x3AB3DA, ["yellow"] = 0xFED83D, ["lime"] = 0x80C71F,
        ["pink"] = 0xF38BAA, ["gray"] = 0x474F52, ["light_gray"] = 0x9D9D97, ["cyan"] = 0x169C9C, ["purple"] = 0x8932B8, ["blue"] = 0x3C44AA,
        ["brown"] = 0x835432, ["green"] = 0x5E7C16, ["red"] = 0xB02E26, ["black"] = 0x1D1D21,
    };

    /// <summary>Kacheln je Seite für einen Quader mit Minecraft-Texturabwicklung (texOffs u,v; Größe w,h,d).</summary>
    static int[] Unwrap(McAssets mc, Atlas at, string tex, int u, int v, int w, int h, int d, uint tint = 0xffffff, int baseW = 64)
    {
        int T(int x, int y, int ww, int hh) { var px = mc.Region(tex, x, y, ww, hh, tint, baseW); return px == null ? -1 : at.TileOf(px); }
        // Reihenfolge Ost, West, oben, unten, Süd, Nord
        return new[]
        {
            T(u + d + w, v + d, d, h),       // Ost
            T(u, v + d, d, h),               // West
            T(u + d, v, w, d),               // oben
            T(u + d + w, v, w, d),           // unten
            T(u + d, v + d, w, h),           // Süd = Vorderseite
            T(u + 2 * d + w, v + d, w, h),   // Nord = Rückseite
        };
    }

    /// <summary>Quader (Pixel) mit Kacheln je Seite, um die Blockmitte gedreht (Grad, im Uhrzeigersinn von oben).</summary>
    static void Box(List<MQuad> o, float x1, float y1, float z1, float x2, float y2, float z2, int[] t, float deg)
    {
        x1 /= 16; y1 /= 16; z1 /= 16; x2 /= 16; y2 /= 16; z2 /= 16;
        float a = deg * MathF.PI / 180, ca = MathF.Cos(a), sa = MathF.Sin(a);
        Vector3 R(Vector3 p) { float dx = p.X - .5f, dz = p.Z - .5f; return new Vector3(.5f + dx * ca - dz * sa, p.Y, .5f + dx * sa + dz * ca); }
        var faces = new (Vector3[] c, Vector3 n)[]
        {
            (new Vector3[] { new(x2, y2, z2), new(x2, y2, z1), new(x2, y1, z1), new(x2, y1, z2) }, Vector3.UnitX),
            (new Vector3[] { new(x1, y2, z1), new(x1, y2, z2), new(x1, y1, z2), new(x1, y1, z1) }, -Vector3.UnitX),
            (new Vector3[] { new(x1, y2, z1), new(x2, y2, z1), new(x2, y2, z2), new(x1, y2, z2) }, Vector3.UnitY),
            (new Vector3[] { new(x1, y1, z2), new(x2, y1, z2), new(x2, y1, z1), new(x1, y1, z1) }, -Vector3.UnitY),
            (new Vector3[] { new(x1, y2, z2), new(x2, y2, z2), new(x2, y1, z2), new(x1, y1, z2) }, Vector3.UnitZ),
            (new Vector3[] { new(x2, y2, z1), new(x1, y2, z1), new(x1, y1, z1), new(x2, y1, z1) }, -Vector3.UnitZ),
        };
        float[] uv = { 0, 1, 1, 1, 1, 0, 0, 0 };
        for (int f = 0; f < 6; f++)
        {
            if (t[f] < 0) continue;
            var q = new MQuad { Tile = t[f] };
            for (int k = 0; k < 4; k++) { var p = R(faces[f].c[k]); q.P[k * 3] = p.X; q.P[k * 3 + 1] = p.Y; q.P[k * 3 + 2] = p.Z; q.UV[k * 2] = uv[k * 2]; q.UV[k * 2 + 1] = uv[k * 2 + 1]; }
            var n = faces[f].n; var rn = new Vector3(n.X * ca - n.Z * sa, n.Y, n.X * sa + n.Z * ca);
            float ax = MathF.Abs(rn.X), ay = MathF.Abs(rn.Y), az = MathF.Abs(rn.Z);
            q.Dir = ay >= ax && ay >= az ? (rn.Y >= 0 ? 2 : 3) : ax >= az ? (rn.X >= 0 ? 0 : 1) : (rn.Z >= 0 ? 4 : 5);
            o.Add(q);
        }
    }

    /// <summary>Flächen ab Index „from“ kippen/drehen wie eine Blockvariante (x, dann y, je 90°-Schritte).</summary>
    static void Orient(List<MQuad> o, int from, int rx, int ry)
    {
        for (int i = from; i < o.Count; i++)
        {
            var q = o[i];
            for (int k = 0; k < 4; k++)
            {
                var p = ModelBank.RotV(new Vector3(q.P[k * 3], q.P[k * 3 + 1], q.P[k * 3 + 2]), rx, ry);
                q.P[k * 3] = p.X; q.P[k * 3 + 1] = p.Y; q.P[k * 3 + 2] = p.Z;
            }
            var a = new Vector3(q.P[0], q.P[1], q.P[2]); var b = new Vector3(q.P[3], q.P[4], q.P[5]); var c = new Vector3(q.P[9], q.P[10], q.P[11]);
            var n = Vector3.Cross(c - a, b - a);   // zeigt nach außen (Ecken oben-links, oben-rechts, …, unten-links)
            float ax = MathF.Abs(n.X), ay = MathF.Abs(n.Y), az = MathF.Abs(n.Z);
            q.Dir = ay >= ax && ay >= az ? (n.Y >= 0 ? 2 : 3) : ax >= az ? (n.X >= 0 ? 0 : 1) : (n.Z >= 0 ? 4 : 5);
        }
    }

    static int[] Same(int t) => new[] { t, t, t, t, t, t };

    /// <summary>Glockenkörper (das Gestell kommt aus dem Blockmodell).</summary>
    public static List<MQuad> BellBody(McAssets mc, Atlas at)
    {
        var o = new List<MQuad>();
        Box(o, 5, 6, 5, 11, 13, 11, Unwrap(mc, at, "bell/bell_body", 0, 0, 6, 7, 6, baseW: 32), 0);
        Box(o, 4, 4, 4, 12, 6, 12, Unwrap(mc, at, "bell/bell_body", 0, 13, 8, 2, 8, baseW: 32), 0);
        return o;
    }

    /// <summary>Drehung aus „facing“: Süd 0°, West 90°, Nord 180°, Ost 270° (im Uhrzeigersinn von oben).</summary>
    static float FacingDeg(string? f) => f switch { "west" => 90, "north" => 180, "east" => 270, _ => 0 };

    public static bool IsBanner(int v) => Blocks.Get(Blocks.BaseOf(v)).Key.EndsWith("_banner");

    /// <summary>Fahne in 3 Abschnitten (oben, Mitte, unten), damit Muster scharf bleiben.</summary>
    static void Flag(List<MQuad> o, McAssets mc, Atlas at, string tex, float x1, float y1, float z1, float x2, float y2, float z2, float deg)
    {
        int[] rows = { 1, 14, 27, 41 };            // Fahne liegt in der Textur bei v 1..41 (40 Pixel hoch)
        int T(int x, int y, int w, int h) { var px = mc.Region(tex, x, y, w, h); return px == null ? -1 : at.TileOf(px); }
        for (int k = 0; k < 3; k++)
        {
            float top = y2 - (y2 - y1) * (rows[k] - 1) / 40f, bot = y2 - (y2 - y1) * (rows[k + 1] - 1) / 40f;
            int h = rows[k + 1] - rows[k];
            var t = new[] { T(21, rows[k], 1, h), T(0, rows[k], 1, h), k == 0 ? T(1, 0, 20, 1) : -1, k == 2 ? T(21, 0, 20, 1) : -1, T(1, rows[k], 20, h), T(22, rows[k], 20, h) };
            Box(o, x1, bot, z1, x2, top, z2, t, deg);
        }
    }

    public static List<MQuad> Build(int v, McAssets mc, Atlas at, string? patterns = null)
    {
        var o = new List<MQuad>();
        var b = Blocks.Get(v); var key = Blocks.Get(Blocks.BaseOf(v)).Key; var pr = b.Props ?? new Dictionary<string, string>();
        string P(string k, string d) => pr.TryGetValue(k, out var x) ? x : d;

        if (key.EndsWith("chest"))
        {
            // Textur: normal, trapped, ender, copper(_exposed|_weathered|_oxidized); gewachst sieht aus wie ungewachst
            string name = key == "chest" ? "normal" : key == "trapped_chest" ? "trapped" : key == "ender_chest" ? "ender" : key.Replace("waxed_", "").Replace("_chest", "");
            string tex = "chest/" + name; if (!mc.EntityTex.ContainsKey(tex)) tex = "chest/normal";
            string type = P("type", "single");
            // Doppeltruhe: Hälfte reicht bis zur Nachbarhälfte (LEFT → Partner im Uhrzeigersinn von facing, in Grundstellung Süd also West)
            float x1 = 1, x2 = 15;
            if (type == "left") x1 = 0; else if (type == "right") x2 = 16;
            float deg = FacingDeg(P("facing", "south"));
            Box(o, x1, 0, 1, x2, 10, 15, Unwrap(mc, at, tex, 0, 19, 14, 10, 14), deg);       // Korpus
            Box(o, x1, 9, 1, x2, 14, 15, Unwrap(mc, at, tex, 0, 0, 14, 5, 14), deg);         // Deckel
            if (type == "single") Box(o, 7, 7, 15, 9, 11, 16, Unwrap(mc, at, tex, 0, 0, 2, 4, 1), deg);      // Schloss vorne mittig
            else if (type == "left") Box(o, 0, 7, 15, 1, 11, 16, Unwrap(mc, at, tex, 0, 0, 2, 4, 1), deg);   // Schloss an der Naht
            else Box(o, 15, 7, 15, 16, 11, 16, Unwrap(mc, at, tex, 0, 0, 2, 4, 1), deg);
            return o;
        }
        if (key.EndsWith("_banner"))
        {
            bool wall = key.EndsWith("_wall_banner");
            string color = key.Replace("_wall_banner", "").Replace("_banner", "");
            uint tint = Dye.TryGetValue(color, out var c) ? c : 0xffffff;
            const float s = 2f / 3f;                                         // Banner werden im Spiel auf 2/3 verkleinert
            string ftex = mc.ComposeBanner(tint, patterns ?? "", n => Dye.TryGetValue(n, out var dc) ? dc : 0xffffff);
            var wood = "banner/banner_base";
            var pole = Unwrap(mc, at, wood, 44, 0, 2, 42, 2); var bar = Unwrap(mc, at, wood, 0, 42, 20, 2, 2);
            if (!wall)
            {
                float deg = int.TryParse(P("rotation", "0"), out var r) ? r * 22.5f : 0;
                Box(o, 8 - 1 * s, 0, 8 - 1 * s, 8 + 1 * s, 42 * s, 8 + 1 * s, pole, deg);                    // Stange
                Box(o, 8 - 10 * s, 40 * s, 8 - 1 * s, 8 + 10 * s, 42 * s, 8 + 1 * s, bar, deg);             // Querstange
                Flag(o, mc, at, ftex, 8 - 10 * s, 0, 8 + 1 * s, 8 + 10 * s, 40 * s, 8 + 2 * s, deg);         // Fahne vor der Stange
            }
            else
            {   // an der Wand: hängt von der Oberkante des Blocks nach unten über den Block hinaus
                float deg = FacingDeg(P("facing", "south"));
                float top = 16;
                Box(o, 8 - 10 * s, top - 2 * s, 0, 8 + 10 * s, top, 2 * s, bar, deg);
                Flag(o, mc, at, ftex, 8 - 10 * s, top - 42 * s, 2 * s - 1 * s, 8 + 10 * s, top - 2 * s, 2 * s, deg);
            }
            return o;
        }
        if (key.EndsWith("shulker_box"))
        {
            string color = key == "shulker_box" ? "" : "_" + key[..^12];
            string tex = "shulker/shulker" + color; if (!mc.EntityTex.ContainsKey(tex)) tex = "shulker/shulker";
            Box(o, 0, 0, 0, 16, 8, 16, Unwrap(mc, at, tex, 0, 28, 16, 8, 16), 0);       // Unterteil
            Box(o, 0, 4, 0, 16, 16, 16, Unwrap(mc, at, tex, 0, 0, 16, 12, 16), 0);      // Deckel
            var (rx, ry) = P("facing", "up") switch { "down" => (180, 0), "north" => (90, 0), "south" => (90, 180), "east" => (90, 90), "west" => (90, 270), _ => (0, 0) };
            Orient(o, 0, rx, ry);
            return o;
        }
        if (key.EndsWith("_head") || key.EndsWith("_skull"))
        {
            bool wall = key.Contains("_wall_");
            string kind = key.Replace("_wall", "").Replace("_head", "").Replace("_skull", "");
            float deg = wall ? FacingDeg(P("facing", "south")) : (int.TryParse(P("rotation", "0"), out var r) ? r * 22.5f : 0);
            float y0 = wall ? 4 : 0, z0 = wall ? 0 : 4;   // an der Wand: an der Rückwand (Grundstellung Nord), halbe Höhe
            switch (kind)
            {
                case "dragon":
                    Box(o, 0, y0, z0 - 4, 16, y0 + 16, z0 + 12, Unwrap(mc, at, "enderdragon/dragon", 176, 44, 16, 16, 16, baseW: 256), deg);
                    Box(o, 2, y0, z0 + 12, 14, y0 + 5, z0 + 28, Unwrap(mc, at, "enderdragon/dragon", 192, 64, 12, 5, 16, baseW: 256), deg);   // Schnauze
                    break;
                case "piglin":
                    Box(o, 3, y0, z0, 13, y0 + 8, z0 + 8, Unwrap(mc, at, "piglin/piglin", 0, 0, 10, 8, 8), deg);
                    Box(o, 6, y0, z0 + 8, 10, y0 + 4, z0 + 9, Unwrap(mc, at, "piglin/piglin", 31, 1, 4, 4, 1), deg);   // Rüssel
                    break;
                default:
                    string tex = kind switch
                    {
                        "skeleton" => "skeleton/skeleton", "wither_skeleton" => "skeleton/wither_skeleton", "zombie" => "zombie/zombie",
                        "creeper" => "creeper/creeper", _ => "player/wide/steve",
                    };
                    Box(o, 4, y0, z0, 12, y0 + 8, z0 + 8, Unwrap(mc, at, tex, 0, 0, 8, 8, 8), deg);
                    if (kind is "player" or "zombie")   // zweite Schicht (Hut) leicht größer
                        Box(o, 3.5f, y0 - .5f, z0 - .5f, 12.5f, y0 + 8.5f, z0 + 8.5f, Unwrap(mc, at, tex, 32, 0, 8, 8, 8), deg);
                    break;
            }
            return o;
        }
        if (key == "decorated_pot")
        {
            int side = mc.Region("decorated_pot/decorated_pot_side", 0, 0, 16, 16, baseW: 16) is { } sp ? at.TileOf(sp) : -1;
            var baseT = Unwrap(mc, at, "decorated_pot/decorated_pot_base", 0, 13, 14, 0, 14, baseW: 32);
            Box(o, 1, 0, 1, 15, 16, 15, new[] { side, side, baseT[2], baseT[3], side, side }, FacingDeg(P("facing", "south")));
            Box(o, 5, 16, 5, 11, 17, 11, Unwrap(mc, at, "decorated_pot/decorated_pot_base", 0, 5, 6, 1, 6, baseW: 32), 0);
            Box(o, 4, 17, 4, 12, 20, 12, Unwrap(mc, at, "decorated_pot/decorated_pot_base", 0, 0, 8, 3, 8, baseW: 32), 0);
            return o;
        }
        if (key == "conduit")
        {
            Box(o, 5, 5, 5, 11, 11, 11, Unwrap(mc, at, "conduit/base", 0, 0, 6, 6, 6, baseW: 32), 0);
            return o;
        }
        if (key.Contains("copper_golem_statue"))
        {
            string stage = key.Contains("oxidized") ? "_oxidized" : key.Contains("weathered") ? "_weathered" : key.Contains("exposed") ? "_exposed" : "";
            string tex = "copper_golem/copper_golem" + stage; if (!mc.EntityTex.ContainsKey(tex)) tex = "copper_golem/copper_golem";
            float deg = FacingDeg(P("facing", "south"));
            Box(o, 5, 0, 6, 7, 3, 9, Unwrap(mc, at, tex, 36, 22, 2, 3, 3), deg);         // Beine
            Box(o, 9, 0, 6, 11, 3, 9, Unwrap(mc, at, tex, 36, 22, 2, 3, 3), deg);
            Box(o, 4, 3, 5, 12, 9, 11, Unwrap(mc, at, tex, 0, 15, 8, 6, 6), deg);         // Körper
            Box(o, 3, 9, 4, 13, 14, 12, Unwrap(mc, at, tex, 0, 0, 10, 5, 8), deg);        // Kopf
            Box(o, 7, 14, 7, 9, 17, 9, Unwrap(mc, at, tex, 37, 8, 2, 3, 2), deg);         // Antenne
            return o;
        }
        if (key is "end_portal" or "end_gateway")
        {
            int t = mc.Region("end_portal/end_portal", 0, 0, 256, 256, baseW: 256) is { } ep ? at.TileOf(ep) : -1;
            if (key == "end_portal") Box(o, 0, 11.99f, 0, 16, 12, 16, new[] { -1, -1, t, -1, -1, -1 }, 0);
            else Box(o, 0, 0, 0, 16, 16, 16, Same(t), 0);
            return o;
        }
        return o;
    }
}
