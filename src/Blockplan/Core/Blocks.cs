using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace Blockplan.Core;

/// <summary>Ein Blocktyp. Varianten (z. B. Treppe nach Osten) verweisen über Base auf den Grundblock.</summary>
public sealed class Block
{
    public required string Key;          // z. B. "oak_stairs" oder "oak_stairs[facing=east,half=top]"
    public required string Name;         // englischer Name
    public string? NameDe;               // deutscher Name (aus den Minecraft-Sprachdateien)
    public uint Color;                   // 0xRRGGBB
    public char Shape = 'f';             // f voll, s Stufe, t Treppe, h flach, p Pfosten/Zaun/Scheibe, m klein
    public string Category = "Welt";
    public int Base;                     // Index des Grundblocks (sich selbst bei Grundblöcken)
    public Dictionary<string, string>? Props;
    public bool SeeThrough;
    public bool Dynamic;                 // nicht in der Liste, aus einer Schematic ergänzt
}

/// <summary>Alle bekannten Blöcke. Index 0 ist Luft.</summary>
public static class Blocks
{
    public static readonly List<Block?> All = new() { null };
    static readonly Dictionary<string, int> key = new();
    static readonly object gate = new();

    public static readonly HashSet<string> Airs = new() { "air", "cave_air", "void_air", "structure_void" };

    static Blocks()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Blockplan.Data.blocks.txt")!;
        using var r = new StreamReader(s);
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            var f = line.Split('|');
            if (f.Length < 7) continue;
            var b = new Block
            {
                Key = f[0], Name = f[1], Color = Convert.ToUInt32(f[2], 16), Shape = f[3].Length > 0 ? f[3][0] : 'f',
                Category = f[5],
            };
            b.SeeThrough = f[6] == "1" || b.Shape != 'f';
            b.Base = All.Count;
            All.Add(b);
            key[b.Key] = b.Base;
        }
    }

    public static int Count => All.Count;
    public static Block Get(int v) => All[v]!;
    public static int BaseOf(int v) => v > 0 && v < All.Count && All[v] != null ? All[v]!.Base : v;
    public static int Find(string k) => key.TryGetValue(k, out var i) ? i : 0;

    public static string DisplayName(int v, bool german)
    {
        if (v <= 0 || v >= All.Count) return german ? "Luft" : "Air";
        var b = Get(BaseOf(v));
        return german && b.NameDe != null ? b.NameDe : b.Name;
    }

    static readonly Regex autoProps = new("^(north|east|south|west|up|down)=");
    static readonly Regex dropConn = new("redstone_wire|tripwire|chorus|mushroom_block|_stem$|vine$|lichen|sculk_vein|resin_clump");

    static string NormProps(int baseIdx, string props)
    {
        var b = Get(baseIdx);
        bool drop = b.Shape == 'p' && !Regex.IsMatch(b.Key, "door|gate");
        var parts = props.Split(',').Select(p => p.Trim())
            .Where(p => p.Length > 0 && p != "waterlogged=false" && !(drop && autoProps.IsMatch(p)))
            .OrderBy(p => p, StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    /// <summary>Block für einen Zustand wie "minecraft:oak_stairs[facing=east]" finden oder anlegen. 0 für Luft.</summary>
    public static int Ensure(string id)
    {
        lock (gate)
        {
            if (key.TryGetValue(id, out var hit)) return hit;
            var full = id.StartsWith("minecraft:") ? id[10..] : id;
            if (key.TryGetValue(full, out hit)) return hit;
            int br = full.IndexOf('[');
            string bk = br < 0 ? full : full[..br];
            string? props = br < 0 ? null : full[(br + 1)..].TrimEnd(']');
            if (Airs.Contains(bk)) return 0;
            if (!key.TryGetValue(bk, out var bi))
            {
                var nm = bk.Contains(':') ? bk[(bk.IndexOf(':') + 1)..] : bk;
                nm = Regex.Replace(nm.Replace('_', ' '), @"\b\w", m => m.Value.ToUpperInvariant());
                var nb = new Block { Key = bk, Name = nm, Color = 0x8a8a8a, Shape = 'f', Category = "Welt", Dynamic = true };
                nb.Base = All.Count; All.Add(nb); key[bk] = nb.Base; bi = nb.Base;
            }
            if (string.IsNullOrEmpty(props)) return bi;
            var np = NormProps(bi, props);
            if (np.Length == 0) return bi;
            var k = bk + "[" + np + "]";
            if (key.TryGetValue(k, out hit)) return hit;
            var B = Get(bi);
            var pr = new Dictionary<string, string>();
            foreach (var p in np.Split(',')) { var kv = p.Split('='); if (kv.Length == 2) pr[kv[0]] = kv[1]; }
            var v = new Block { Key = k, Name = B.Name, Color = B.Color, Shape = B.Shape, Category = B.Category, Base = bi, Props = pr, SeeThrough = B.SeeThrough };
            int idx = All.Count; All.Add(v); key[k] = idx;
            return idx;
        }
    }

    static readonly Regex cutout = new("leaves|_pane$|bars$|_door$|trapdoor$|fence|ladder|rail$|vine|scaffolding|chain$|cobweb|spawner|_grate$");
    public static bool IsCutout(int v) => cutout.IsMatch(Get(BaseOf(v)).Key);
    public static bool IsWater(int v) { var k = Get(BaseOf(v)).Key; return k == "water" || k == "bubble_column"; }
    public static bool IsLava(int v) => Get(BaseOf(v)).Key == "lava";
    public static bool IsGlassLike(int v) { var k = Get(BaseOf(v)).Key; return k.Contains("glass") || k.EndsWith("ice"); }
}
