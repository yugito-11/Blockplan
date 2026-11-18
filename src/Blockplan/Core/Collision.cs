using System;
using System.Collections.Generic;

namespace Blockplan.Core;

/// <summary>
/// Kollision zum Herumlaufen, vereinfacht wie im Spiel: Stufen halb hoch, Treppen zweistufig, Zäune und Mauern 1,5 hoch,
/// Teppiche flach, durch Pflanzen, Fackeln, Wasser, offene Türen und Ähnliches kann man hindurch.
/// </summary>
public static class Collision
{
    public readonly record struct Box(float X0, float Y0, float Z0, float X1, float Y1, float Z1);
    static readonly Box[] none = Array.Empty<Box>();
    static readonly Box full = new(0, 0, 0, 1, 1, 1);
    static readonly Dictionary<int, Box[]> cache = new();
    static readonly object gate = new();

    static readonly HashSet<string> passExact = new()
    {
        "water", "lava", "bubble_column", "fire", "soul_fire", "nether_portal", "end_portal", "end_gateway", "light", "structure_void", "cobweb",
        "powder_snow", "ladder", "vine", "glow_lichen", "sculk_vein", "resin_clump", "tripwire", "tripwire_hook", "redstone_wire", "rail", "powered_rail",
        "detector_rail", "activator_rail", "lever", "kelp", "kelp_plant", "seagrass", "tall_seagrass", "short_grass", "tall_grass", "fern", "large_fern",
        "sugar_cane", "frogspawn", "pink_petals", "leaf_litter", "wildflowers", "hanging_roots", "spore_blossom", "big_dripleaf_stem", "small_dripleaf",
        "cave_vines", "cave_vines_plant", "weeping_vines", "weeping_vines_plant", "twisting_vines", "twisting_vines_plant", "nether_wart", "dead_bush",
    };
    static readonly string[] passSuffix = { "_button", "_pressure_plate", "_sign", "_banner", "torch", "_sapling", "_coral", "_coral_fan", "_tulip" };

    /// <summary>Kann man hier hochklettern (Leiter, Ranken)?</summary>
    public static bool Climbable(int v)
    {
        if (v <= 0) return false; var k = Blocks.Get(Blocks.BaseOf(v)).Key;
        return k.Contains("ladder") || k.EndsWith("vine") || k.EndsWith("vines") || k.EndsWith("vines_plant") || k == "scaffolding";
    }

    public static bool Liquid(int v) => v > 0 && (Blocks.IsWater(v) || Blocks.IsLava(v));

    /// <summary>Kollisionsquader eines Blockzustands (0–1 im Feld).</summary>
    public static Box[] Boxes(int v)
    {
        if (v <= 0) return none;
        lock (gate)
        {
            if (cache.TryGetValue(v, out var c)) return c;
            return cache[v] = Make(v);
        }
    }

    static Box[] Make(int v)
    {
        var b = Blocks.Get(v); var key = Blocks.Get(b.Base).Key; var pr = b.Props;
        string P(string k, string d = "") => pr != null && pr.TryGetValue(k, out var x) ? x : d;
        if (Entities.IsInvisible(v) && key != "barrier") return none;
        if (key == "snow") { int l = int.TryParse(P("layers", "1"), out var n) ? n : 1; return l <= 1 ? none : new[] { new Box(0, 0, 0, 1, (l - 1) / 8f, 1) }; }
        if (key.EndsWith("carpet") || key == "moss_carpet") return new[] { new Box(0, 0, 0, 1, 1 / 16f, 1) };
        if (passExact.Contains(key)) return none;
        foreach (var sf in passSuffix) if (key.EndsWith(sf)) return none;
        if (key.EndsWith("_door")) return P("open") == "true" ? none : new[] { DoorBox(P("facing", "north")) };
        if (key.EndsWith("fence_gate")) return P("open") == "true" ? none : new[] { new Box(0, 0, .375f, 1, 1.5f, .625f) };
        if (key.EndsWith("trapdoor"))
        {
            if (P("open") == "true") return none;
            return new[] { P("half", "bottom") == "top" ? new Box(0, .8125f, 0, 1, 1, 1) : new Box(0, 0, 0, 1, .1875f, 1) };
        }
        if (key.EndsWith("_fence") || key.EndsWith("_wall") && !key.EndsWith("wall_sign")) return new[] { new Box(0, 0, 0, 1, 1.5f, 1) };
        if (key.EndsWith("_bed")) return new[] { new Box(0, 0, 0, 1, .5625f, 1) };
        if (key.Contains("chest")) return new[] { new Box(.0625f, 0, .0625f, .9375f, .875f, .9375f) };
        switch (b.Shape)
        {
            case 's':
            {
                var t = P("type", "bottom");
                return new[] { t == "double" ? full : t == "top" ? new Box(0, .5f, 0, 1, 1, 1) : new Box(0, 0, 0, 1, .5f, 1) };
            }
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
                return new[] { slab, q };
            }
            case 'h': return new[] { P("half", "bottom") == "top" ? new Box(0, .875f, 0, 1, 1, 1) : new Box(0, 0, 0, 1, .125f, 1) };
            case 'p': return new[] { full };   // Scheiben, Gitter: als ganzer Block
            case 'm': return key.Contains("lantern") || key.Contains("pot") || key.Contains("candle") || key.Contains("head") || key.Contains("skull")
                ? new[] { new Box(.3125f, 0, .3125f, .6875f, .5f, .6875f) } : none;
        }
        return new[] { full };
    }

    static Box DoorBox(string facing) => facing switch
    {
        "north" => new Box(0, 0, .8125f, 1, 1, 1), "south" => new Box(0, 0, 0, 1, 1, .1875f),
        "west" => new Box(.8125f, 0, 0, 1, 1, 1), _ => new Box(0, 0, 0, .1875f, 1, 1),
    };

    /// <summary>
    /// Spielerquader (Fußmitte x/z, Füße y, Breite 0,6, Höhe 1,8) in Projekt-Koordinaten bewegen; gibt die neue Lage zurück.
    /// Unter dem Projekt (y &lt; 0) ist fester Boden. Kleine Kanten bis 0,6 werden wie im Spiel hochgestiegen.
    /// </summary>
    public static (float x, float y, float z, bool ground, bool hitX, bool hitZ) Move(Project p, float x, float y, float z, float dx, float dy, float dz, bool allowStep)
    {
        const float r = .3f, hgt = 1.8f;
        bool ground = false, hx = false, hz = false;
        // senkrecht
        float ny = y + dy;
        if (dy != 0)
        {
            float lim = Limit(p, x - r, x + r, z - r, z + r, y, ny, hgt, 1);
            if (lim != ny) { if (dy < 0) ground = true; ny = lim; }
        }
        y = ny;
        // waagrecht, je Achse; bei Hindernis kleine Stufe hochsteigen
        foreach (int ax in new[] { 0, 2 })
        {
            float d = ax == 0 ? dx : dz; if (d == 0) continue;
            float nx = ax == 0 ? x + d : x, nz = ax == 2 ? z + d : z;
            if (Free(p, nx - r, nx + r, y, y + hgt, nz - r, nz + r)) { x = nx; z = nz; continue; }
            if (allowStep)
            {
                bool stepped = false;
                for (float up = .0625f; up <= .6f + 1e-4f; up += .0625f)
                    if (Free(p, nx - r, nx + r, y + up, y + up + hgt, nz - r, nz + r) && Free(p, x - r, x + r, y + up, y + up + hgt, z - r, z + r))
                    { x = nx; z = nz; y += up; stepped = true; break; }
                if (stepped) continue;
            }
            if (ax == 0) hx = true; else hz = true;
        }
        return (x, y, z, ground, hx, hz);
    }

    /// <summary>Ist der Quader frei?</summary>
    public static bool Free(Project p, float x0, float x1, float y0, float y1, float z0, float z1)
    {
        if (y0 < 0) return false;   // Boden unter dem Projekt
        for (int cy = (int)MathF.Floor(y0 - .5f); cy <= (int)MathF.Floor(y1); cy++)
            for (int cz = (int)MathF.Floor(z0); cz <= (int)MathF.Floor(z1 - 1e-4f); cz++)
                for (int cx = (int)MathF.Floor(x0); cx <= (int)MathF.Floor(x1 - 1e-4f); cx++)
                {
                    int v = p.At(cx, cy, cz); if (v == 0) continue;
                    foreach (var b in Boxes(v))
                        if (cx + b.X0 < x1 && cx + b.X1 > x0 && cy + b.Y0 < y1 - 1e-4f && cy + b.Y1 > y0 + 1e-4f && cz + b.Z0 < z1 && cz + b.Z1 > z0) return false;
                }
        return true;
    }

    /// <summary>Wie weit kommt der Quader senkrecht von y nach ny?</summary>
    static float Limit(Project p, float x0, float x1, float z0, float z1, float y, float ny, float hgt, int _)
    {
        if (ny < y)
        {   // fallen: höchste Oberkante zwischen ny und y
            float best = ny; if (ny < 0) best = 0;
            for (int cy = (int)MathF.Floor(ny) - 1; cy <= (int)MathF.Floor(y) + 1; cy++)
                for (int cz = (int)MathF.Floor(z0); cz <= (int)MathF.Floor(z1 - 1e-4f); cz++)
                    for (int cx = (int)MathF.Floor(x0); cx <= (int)MathF.Floor(x1 - 1e-4f); cx++)
                    {
                        int v = p.At(cx, cy, cz); if (v == 0) continue;
                        foreach (var b in Boxes(v))
                        {
                            if (!(cx + b.X0 < x1 && cx + b.X1 > x0 && cz + b.Z0 < z1 && cz + b.Z1 > z0)) continue;
                            float topY = cy + b.Y1; if (topY <= y + 1e-3f && topY > best) best = topY;
                        }
                    }
            return best;
        }
        else
        {   // steigen: tiefste Unterkante über dem Kopf
            float best = ny;
            for (int cy = (int)MathF.Floor(y + hgt) - 1; cy <= (int)MathF.Floor(ny + hgt) + 1; cy++)
                for (int cz = (int)MathF.Floor(z0); cz <= (int)MathF.Floor(z1 - 1e-4f); cz++)
                    for (int cx = (int)MathF.Floor(x0); cx <= (int)MathF.Floor(x1 - 1e-4f); cx++)
                    {
                        int v = p.At(cx, cy, cz); if (v == 0) continue;
                        foreach (var b in Boxes(v))
                        {
                            if (!(cx + b.X0 < x1 && cx + b.X1 > x0 && cz + b.Z0 < z1 && cz + b.Z1 > z0)) continue;
                            float bot = cy + b.Y0; if (bot >= y + hgt - 1e-3f && bot - hgt < best) best = bot - hgt;
                        }
                    }
            return best;
        }
    }

    /// <summary>Feld unter einem Punkt, an dem man stehen kann (für den Einstieg ins Laufen): Oberkante oder -1.</summary>
    public static float GroundBelow(Project p, float x, float y, float z)
    {
        for (int cy = Math.Min((int)MathF.Floor(y), p.H - 1); cy >= 0; cy--)
        {
            int v = p.At((int)MathF.Floor(x), cy, (int)MathF.Floor(z)); if (v == 0) continue;
            float top = -1; foreach (var b in Boxes(v)) top = Math.Max(top, cy + b.Y1);
            if (top >= 0) return top;
        }
        return 0;
    }
}
