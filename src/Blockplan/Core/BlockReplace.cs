using System;
using System.Collections.Generic;
using System.Linq;

namespace Blockplan.Core;

/// <summary>Block A durch Block B ersetzen, mit allen Varianten: Treppe, Stufe, Zaun … aus demselben Material wandern mit.</summary>
public static class BlockReplace
{
    static readonly string[] suffixes = { "_stairs", "_slab", "_wall", "_fence_gate", "_fence", "_button", "_pressure_plate", "_door", "_trapdoor", "_sign", "_wall_sign", "_hanging_sign", "_wall_hanging_sign" };

    /// <summary>Wortstamm eines Materials: oak_planks → oak, stone_bricks → stone_brick, bricks → brick, cobblestone → cobblestone.</summary>
    public static string Stem(string key)
    {
        if (key.EndsWith("_planks")) return key[..^7];
        if (key.EndsWith("_bricks")) return key[..^1];
        if (key.EndsWith("_tiles")) return key[..^1];
        if (key == "bricks") return "brick";
        if (key.EndsWith("_block") && !key.StartsWith("note") ) return key[..^6];
        return key;
    }

    /// <summary>Abbildung alter Blockzustand → neuer. Betroffen: der gewählte Block und seine Varianten aus demselben Material.</summary>
    public static Func<int, int> Map(int from, int to, bool keepForm)
    {
        string fk = Blocks.Get(Blocks.BaseOf(from)).Key, tk = Blocks.Get(Blocks.BaseOf(to)).Key;
        string fs = Stem(fk), ts = Stem(tk);
        var cache = new Dictionary<int, int>();
        return v =>
        {
            if (v == 0) return 0;
            if (cache.TryGetValue(v, out var r)) return r;
            var b = Blocks.Get(v); var bk = Blocks.Get(b.Base).Key; string? target = null;
            if (bk == fk) target = tk;
            else if (fs != ts)
                foreach (var suf in suffixes)
                    if (bk == fs + suf && Blocks.Find(ts + suf) > 0) { target = ts + suf; break; }   // Variante gibt es im neuen Material
            if (target == null) return cache[v] = v;
            var props = keepForm && b.Props != null && Blocks.Get(Blocks.Find(target)).Shape == Blocks.Get(b.Base).Shape ? b.Props : null;
            return cache[v] = Blocks.Ensure(props == null || props.Count == 0 ? target : target + "[" + string.Join(",", props.Select(kv => kv.Key + "=" + kv.Value)) + "]");
        };
    }
}
