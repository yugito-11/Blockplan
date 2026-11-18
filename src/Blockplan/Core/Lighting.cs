using System;
using System.Collections.Generic;
using System.Linq;

namespace Blockplan.Core;

/// <summary>
/// Licht wie in Minecraft, vereinfacht: Blocklicht (Fackeln, Laternen …) und Himmelslicht breiten sich Feld für Feld aus
/// und werden je Schritt um 1 schwächer; volle undurchsichtige Blöcke halten es auf. Ergebnis je Feld: Himmel*16 + Block.
/// </summary>
public static class Lighting
{
    static readonly Dictionary<string, int> emit = new()
    {
        ["torch"] = 14, ["wall_torch"] = 14, ["soul_torch"] = 10, ["soul_wall_torch"] = 10, ["redstone_torch"] = 7, ["redstone_wall_torch"] = 7,
        ["copper_torch"] = 14, ["copper_wall_torch"] = 14,
        ["lantern"] = 15, ["soul_lantern"] = 10, ["glowstone"] = 15, ["sea_lantern"] = 15, ["jack_o_lantern"] = 15, ["shroomlight"] = 15,
        ["beacon"] = 15, ["conduit"] = 15, ["end_rod"] = 14, ["lava"] = 15, ["fire"] = 15, ["soul_fire"] = 10, ["campfire"] = 15, ["soul_campfire"] = 10,
        ["ochre_froglight"] = 15, ["verdant_froglight"] = 15, ["pearlescent_froglight"] = 15, ["magma_block"] = 3, ["crying_obsidian"] = 10,
        ["glow_lichen"] = 7, ["amethyst_cluster"] = 5, ["large_amethyst_bud"] = 4, ["medium_amethyst_bud"] = 2, ["small_amethyst_bud"] = 1,
        ["redstone_lamp"] = 15, ["furnace"] = 13, ["blast_furnace"] = 13, ["smoker"] = 13, ["end_portal"] = 15, ["nether_portal"] = 11,
        ["respawn_anchor"] = 15, ["enchanting_table"] = 7, ["ender_chest"] = 7, ["brewing_stand"] = 1, ["dragon_egg"] = 1,
        ["cave_vines"] = 14, ["cave_vines_plant"] = 14, ["glow_berries"] = 14, ["light"] = 15, ["sculk_catalyst"] = 6, ["sculk_sensor"] = 1,
        ["copper_bulb"] = 15, ["exposed_copper_bulb"] = 12, ["weathered_copper_bulb"] = 8, ["oxidized_copper_bulb"] = 4,
        ["waxed_copper_bulb"] = 15, ["waxed_exposed_copper_bulb"] = 12, ["waxed_weathered_copper_bulb"] = 8, ["waxed_oxidized_copper_bulb"] = 4,
        ["trial_spawner"] = 4, ["vault"] = 6, ["creaking_heart"] = 0, ["firefly_bush"] = 2,
    };

    /// <summary>Licht, das ein Blockzustand aussendet (0–15).</summary>
    public static int Emission(int v)
    {
        if (v <= 0 || v >= Blocks.Count) return 0;
        var b = Blocks.Get(v); var key = Blocks.Get(b.Base).Key; var pr = b.Props;
        string P(string k) => pr != null && pr.TryGetValue(k, out var x) ? x : "";
        if (key.EndsWith("candle")) return P("lit") == "true" && int.TryParse(P("candles"), out var n) ? 3 * n : 0;
        if (key == "sea_pickle") return P("waterlogged") == "true" && int.TryParse(P("pickles"), out var k) ? 3 + 3 * k : 0;
        if (key == "respawn_anchor") return int.TryParse(P("charges"), out var c) ? Math.Max(0, c * 4 - 1) : 0;
        if (key.Contains("cave_vines")) return P("berries") == "true" ? 14 : 0;
        if (!emit.TryGetValue(key, out var e)) return 0;
        // Dinge mit „lit“ leuchten nur, wenn sie an sind (Öfen, Lampen, Lagerfeuer, Kupferbirnen, Redstone-Fackeln)
        if (pr != null && pr.ContainsKey("lit")) return P("lit") == "true" ? e : 0;
        if (key == "redstone_lamp" || key.EndsWith("copper_bulb") || key.EndsWith("furnace") || key == "smoker") return 0;   // ohne Angabe: aus
        return e;
    }

    /// <summary>Hält der Block Licht auf? (volle, undurchsichtige Würfel)</summary>
    public static bool Stops(int v)
    {
        if (v <= 0) return false;
        var b = Blocks.Get(v);
        if (b.Shape != 'f' || b.SeeThrough) return b.Shape == 's' && b.Props != null && b.Props.TryGetValue("type", out var t) && t == "double";
        if (Blocks.IsCutout(v) || Blocks.IsGlassLike(v) || Blocks.IsWater(v) || Blocks.IsLava(v)) return false;
        return !Entities.IsEntity(v) && !Entities.IsInvisible(v);
    }

    /// <summary>Licht für alle Felder (bis zur Höhe top). Oberhalb des Projekts ist freier Himmel.</summary>
    public static byte[] Compute(Project p, int top = int.MaxValue)
    {
        int W = p.W, D = p.D, H = Math.Min(top, p.H - 1) + 1; long WD = (long)W * D, N = WD * H;
        var cells = p.Cells; var light = new byte[p.Cells.LongLength];
        var opaque = new bool[Blocks.Count]; var em = new byte[Blocks.Count];
        foreach (var v in p.CountBlocks(top).Keys) { opaque[v] = Stops(v); em[v] = (byte)Emission(v); }
        // Himmel: von oben herab, bis zum ersten undurchsichtigen Block volle Stärke
        var sky = new byte[N]; var blk = new byte[N];
        var q = new Queue<long>();
        for (long c = 0; c < WD; c++)
            for (int y = H - 1; y >= 0; y--)
            {
                long i = y * WD + c; if (opaque[cells[i]]) break;
                sky[i] = 15; q.Enqueue(i);
            }
        Spread(sky, q, cells, opaque, W, D, H);
        for (long i = 0; i < N; i++) { int e = em[cells[i]]; if (e > 0) { blk[i] = (byte)e; q.Enqueue(i); } }
        Spread(blk, q, cells, opaque, W, D, H);
        for (long i = 0; i < N; i++) light[i] = (byte)(sky[i] << 4 | blk[i]);
        for (long i = N; i < light.LongLength; i++) light[i] = 0xF0;
        return light;
    }

    static void Spread(byte[] L, Queue<long> q, ushort[] cells, bool[] opaque, int W, int D, int H)
    {
        long WD = (long)W * D;
        while (q.Count > 0)
        {
            long i = q.Dequeue(); int l = L[i]; if (l <= 1) continue;
            int x = (int)(i % W), z = (int)(i / W % D), y = (int)(i / WD);
            void To(long j) { if (L[j] < l - 1 && !opaque[cells[j]]) { L[j] = (byte)(l - 1); q.Enqueue(j); } }
            if (x > 0) To(i - 1); if (x + 1 < W) To(i + 1);
            if (z > 0) To(i - W); if (z + 1 < D) To(i + W);
            if (y > 0) To(i - WD); if (y + 1 < H) To(i + WD);
        }
    }

    /// <summary>
    /// Stellen, an denen nachts Monster erscheinen können: Boden ist ein voller Block, darüber zwei freie Felder, kein Blocklicht.
    /// Nur innerhalb des Projekts (bis zur Höhe top).
    /// </summary>
    public static List<(int x, int y, int z)> SpawnSpots(Project p, byte[] light, int top = int.MaxValue, int max = 200_000)
    {
        int W = p.W, D = p.D, H = Math.Min(top, p.H - 1) + 1; long WD = (long)W * D;
        var floor = new bool[Blocks.Count];
        foreach (var v in p.CountBlocks(top).Keys)
        {
            var b = Blocks.Get(v); var key = Blocks.Get(b.Base).Key;
            bool topSlab = b.Shape == 's' && b.Props != null && b.Props.TryGetValue("type", out var t) && t is "top" or "double";
            floor[v] = (Stops(v) || topSlab) && !key.Contains("glass") && !key.Contains("leaves") && key != "bedrock" && key != "barrier" && !key.Contains("ice") && key != "magma_block";
        }
        var r = new List<(int, int, int)>();
        for (int y = 1; y < H && r.Count < max; y++)
            for (int z = 0; z < D; z++) for (int x = 0; x < W; x++)
            {
                long i = y * WD + (long)z * W + x;
                if (p.Cells[i] != 0 || !floor[p.Cells[i - WD]] || (light[i] & 15) > 0) continue;
                if (y + 1 < H && p.Cells[i + WD] != 0) continue;
                r.Add((x, y, z)); if (r.Count >= max) break;
            }
        return r;
    }
}
