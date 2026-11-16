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
        return k.EndsWith("chest") || k.EndsWith("_banner");
    }

    // Farben der Farbstoffe wie im Spiel (für Banner)
    static readonly Dictionary<string, uint> Dye = new()
    {
        ["white"] = 0xF9FFFE, ["orange"] = 0xF9801D, ["magenta"] = 0xC74EBD, ["light_blue"] = 0x3AB3DA, ["yellow"] = 0xFED83D, ["lime"] = 0x80C71F,
        ["pink"] = 0xF38BAA, ["gray"] = 0x474F52, ["light_gray"] = 0x9D9D97, ["cyan"] = 0x169C9C, ["purple"] = 0x8932B8, ["blue"] = 0x3C44AA,
        ["brown"] = 0x835432, ["green"] = 0x5E7C16, ["red"] = 0xB02E26, ["black"] = 0x1D1D21,
    };

    /// <summary>Kacheln je Seite für einen Quader mit Minecraft-Texturabwicklung (texOffs u,v; Größe w,h,d).</summary>
    static int[] Unwrap(McAssets mc, Atlas at, string tex, int u, int v, int w, int h, int d, uint tint = 0xffffff)
    {
        int T(int x, int y, int ww, int hh) { var px = mc.Region(tex, x, y, ww, hh, tint); return px == null ? -1 : at.TileOf(px); }
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
        return o;
    }
}
