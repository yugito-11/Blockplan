using System;
using System.Collections.Generic;
using System.Linq;

namespace Blockplan.Core;

/// <summary>
/// Blockzustände → Items, wie man sie im Inventar braucht: Wandfackel = Fackel, doppelte Stufe = 2 Stufen,
/// obere Türhälfte und Bettkopf zählen nicht, Wasser/Feuer/Portale gibt es nicht als Item.
/// </summary>
public static class Items
{
    static readonly HashSet<string> skip = new()
    {
        "air", "cave_air", "void_air", "structure_void", "structure_block", "jigsaw", "water", "lava", "fire", "soul_fire",
        "bubble_column", "piston_head", "moving_piston", "nether_portal", "end_portal", "end_gateway", "frosted_ice", "light", "barrier",
    };
    static readonly Dictionary<string, string> rename = new()
    {
        ["wall_torch"] = "torch", ["soul_wall_torch"] = "soul_torch", ["redstone_wall_torch"] = "redstone_torch", ["copper_wall_torch"] = "copper_torch",
        ["redstone_wire"] = "redstone", ["tripwire"] = "string", ["wheat"] = "wheat_seeds", ["carrots"] = "carrot", ["potatoes"] = "potato",
        ["beetroots"] = "beetroot_seeds", ["cocoa"] = "cocoa_beans", ["sweet_berry_bush"] = "sweet_berries", ["melon_stem"] = "melon_seeds",
        ["attached_melon_stem"] = "melon_seeds", ["pumpkin_stem"] = "pumpkin_seeds", ["attached_pumpkin_stem"] = "pumpkin_seeds",
        ["bamboo_sapling"] = "bamboo", ["kelp_plant"] = "kelp", ["cave_vines"] = "glow_berries", ["cave_vines_plant"] = "glow_berries",
        ["twisting_vines_plant"] = "twisting_vines", ["weeping_vines_plant"] = "weeping_vines", ["tall_seagrass"] = "seagrass",
        ["big_dripleaf_stem"] = "big_dripleaf", ["powder_snow"] = "powder_snow_bucket", ["torchflower_crop"] = "torchflower_seeds",
        ["pitcher_crop"] = "pitcher_pod", ["water_cauldron"] = "cauldron", ["lava_cauldron"] = "cauldron", ["powder_snow_cauldron"] = "cauldron",
        ["dirt_path"] = "dirt", ["farmland"] = "dirt",
    };

    /// <summary>Items eines Blockzustands mit Anzahl (leer = gibt es nicht als Item).</summary>
    public static List<(string item, int n)> Of(int v)
    {
        var r = new List<(string, int)>();
        if (v <= 0 || v >= Blocks.Count) return r;
        var b = Blocks.Get(v); string id = Blocks.Get(Blocks.BaseOf(v)).Key;
        if (id.Contains(':')) id = id[(id.IndexOf(':') + 1)..];
        var pr = b.Props ?? new Dictionary<string, string>();
        string P(string k) => pr.TryGetValue(k, out var x) ? x : "";
        if (skip.Contains(id) || P("half") == "upper" || P("part") == "head") return r;
        int n = 1;
        if (P("type") == "double" && id.EndsWith("_slab")) n = 2;
        if (int.TryParse(P("candles"), out var c)) n = c;
        if (int.TryParse(P("pickles"), out var pk)) n = pk;
        if (int.TryParse(P("eggs"), out var eg)) n = eg;
        if (id == "snow" && int.TryParse(P("layers"), out var ly)) n = ly;
        if (int.TryParse(P("flower_amount"), out var fa)) n = fa;   // Blütenteppich: mehrere Blüten pro Feld
        if (int.TryParse(P("segment_amount"), out var sa)) n = sa;
        if (id.StartsWith("potted_"))
        {
            var plant = id[7..]; if (plant.EndsWith("_bush")) plant = plant[..^5];
            r.Add(("flower_pot", 1)); r.Add((plant is "azalea" or "flowering_azalea" ? plant : id[7..], 1)); return r;
        }
        if (rename.TryGetValue(id, out var rn)) id = rn;
        else foreach (var suf in new[] { "sign", "hanging_sign", "banner", "head", "skull", "fan" })
            if (id.EndsWith("_wall_" + suf)) { id = id[..^("_wall_" + suf).Length] + "_" + suf; break; }
        r.Add((id, n));
        return r;
    }

    /// <summary>Zählung nach Blockzustand → Zählung nach Item.</summary>
    public static Dictionary<string, long> FromStates(Dictionary<int, long> states)
    {
        var r = new Dictionary<string, long>();
        foreach (var (v, c) in states) foreach (var (it, n) in Of(v)) r[it] = r.GetValueOrDefault(it) + c * n;
        return r;
    }

    /// <summary>Wie viele davon in einen Inventarplatz passen.</summary>
    public static int StackSize(string item)
    {
        if (item.EndsWith("_bed") || item.EndsWith("_bucket") || item == "cake" || item.EndsWith("_boat") || item.EndsWith("minecart")
            || item.EndsWith("shulker_box") || item.EndsWith("_banner_pattern") || item == "saddle") return 1;
        if (item.EndsWith("_sign") || item.EndsWith("hanging_sign") || item.EndsWith("_banner") || item is "snowball" or "egg" or "ender_pearl" or "honey_bottle" or "armor_stand") return 16;
        return 64;
    }

    /// <summary>Plätze im Inventar bzw. in Kisten.</summary>
    public static long Slots(string item, long count) { int s = StackSize(item); return (count + s - 1) / s; }

    public const int ChestSlots = 27, DoubleChestSlots = 54, ShulkerSlots = 27;

    /// <summary>Lesbare Menge: „3 Stacks + 12“, „1,5 Shulker“ …</summary>
    public static string StackText(string item, long n)
    {
        int s = StackSize(item);
        if (s == 1) return n == 1 ? "" : $"{n} Plätze";
        if (n < s) return "";
        long st = n / s, rest = n % s;
        string t = $"{st:N0} Stack{(st == 1 ? "" : "s")}{(rest > 0 ? " + " + rest : "")}";
        long slots = Slots(item, n);
        if (slots >= ShulkerSlots) t += $" · {slots / (double)ShulkerSlots:0.#} Shulker";
        return t;
    }

    /// <summary>Gesamtbedarf an Platz: Stacks, Kisten, Doppelkisten, Shulker.</summary>
    public static (long slots, double shulker, double chests, double doubleChests) Space(Dictionary<string, long> items)
    {
        long sl = items.Sum(kv => Slots(kv.Key, kv.Value));
        return (sl, sl / (double)ShulkerSlots, sl / (double)ChestSlots, sl / (double)DoubleChestSlots);
    }

    /// <summary>Block zum Anzeigen eines Items (Symbol und Name), falls es einen gibt.</summary>
    public static int BlockFor(string item, int fallback)
    {
        int b = Blocks.Find(item); if (b != 0) return b;
        return fallback;
    }
}
