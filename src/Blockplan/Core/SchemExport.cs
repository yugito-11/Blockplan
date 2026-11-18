using System;
using System.Collections.Generic;
using System.IO;

namespace Blockplan.Core;

/// <summary>Speichert ein Projekt oder einen Teil davon als Sponge-Schematic (.schem, Version 2) für WorldEdit und Litematica.</summary>
public static class SchemExport
{
    /// <summary>Bereich x0..x1, y0..y1, z0..z1 (Projekt-Koordinaten); keep = nur diese Felder (sonst Luft).</summary>
    public static byte[] Build(Project p, int x0, int y0, int z0, int x1, int y1, int z1, Func<int, int, int, bool>? keep, int dataVersion, out long blocks)
    {
        int W = x1 - x0 + 1, H = y1 - y0 + 1, L = z1 - z0 + 1;
        if (W <= 0 || H <= 0 || L <= 0) throw new ArgumentException("Leerer Bereich.");
        if (W > 65535 || H > 65535 || L > 65535) throw new ArgumentException("Für .schem zu groß (höchstens 65.535 je Richtung).");
        var palette = new Dictionary<string, object> { ["minecraft:air"] = 0 };
        var pid = new Dictionary<int, int> { [0] = 0 };
        var data = new MemoryStream((int)Math.Min((long)W * H * L, int.MaxValue / 2));
        var bes = new List<Dictionary<string, object>>();
        blocks = 0;
        for (int y = 0; y < H; y++) for (int z = 0; z < L; z++) for (int x = 0; x < W; x++)
        {
            int px = x0 + x, py = y0 + y, pz = z0 + z;
            int v = p.At(px, py, pz);
            if (v != 0 && keep != null && !keep(px, py, pz)) v = 0;
            if (!pid.TryGetValue(v, out var id))
            {
                id = pid.Count; pid[v] = id;
                var k = Blocks.Get(v).Key; int br = k.IndexOf('[');
                palette[(br < 0 ? k : k[..br]).Contains(':') ? k : "minecraft:" + k] = id;
            }
            if (v != 0) blocks++;
            // Zahl als VarInt
            uint u = (uint)id; while (u >= 0x80) { data.WriteByte((byte)(u | 0x80)); u >>= 7; } data.WriteByte((byte)u);
            if (v != 0 && p.Banners.Count > 0 && p.Banners.TryGetValue(p.Index(px, py, pz), out var pat))
            {
                var list = new List<Dictionary<string, object>>();
                foreach (var part in pat.Split('|', StringSplitOptions.RemoveEmptyEntries))
                {
                    var kv = part.Split(':'); if (kv.Length != 2) continue;
                    list.Add(new Dictionary<string, object> { ["pattern"] = "minecraft:" + kv[0], ["color"] = kv[1] });
                }
                if (list.Count > 0) bes.Add(new Dictionary<string, object> { ["Pos"] = new[] { x, y, z }, ["Id"] = "minecraft:banner", ["patterns"] = list });
            }
        }
        var root = new Dictionary<string, object>
        {
            ["Version"] = 2, ["DataVersion"] = dataVersion,
            ["Width"] = (short)W, ["Height"] = (short)H, ["Length"] = (short)L,
            ["Offset"] = new[] { 0, 0, 0 },
            ["Metadata"] = new Dictionary<string, object> { ["Name"] = p.Name, ["Author"] = "Blockplan", ["WEOffsetX"] = 0, ["WEOffsetY"] = 0, ["WEOffsetZ"] = 0 },
            ["PaletteMax"] = palette.Count, ["Palette"] = palette,
            ["BlockData"] = data.ToArray(),
            ["BlockEntities"] = bes,
        };
        return Nbt.Write("Schematic", root);
    }
}
