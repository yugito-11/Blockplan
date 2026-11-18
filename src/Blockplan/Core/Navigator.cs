using System;
using System.Collections.Generic;
using System.Linq;

namespace Blockplan.Core;

/// <summary>Eine Stelle mit fehlenden oder falschen Blöcken (zusammenhängend).</summary>
public sealed record MissingSpot(int X, int Y, int Z, int MinY, int Missing, int Wrong, int MainBlock, int MainCount)
{
    public int Count => Missing + Wrong;
}

/// <summary>Sucht im Live-Stand die Stellen, an denen noch etwas fehlt oder falsch ist, und fasst Nachbarn zusammen.</summary>
public static class Navigator
{
    /// <summary>Gruppen sortiert: falsche Blöcke zuerst, dann von unten nach oben (Bauordnung).</summary>
    public static (List<MissingSpot> spots, long missing, long wrong, bool cut) Find(Project p, LiveClient lv, int top, int maxCells = 400_000)
    {
        int W = p.W, D = p.D, H = Math.Min(top, p.H - 1) + 1; long WD = (long)W * D;
        var bad = new Dictionary<long, byte>(); long miss = 0, wrong = 0; bool cut = false;
        for (int y = 0; y < H; y++)
            for (int z = 0; z < D; z++) for (int x = 0; x < W; x++)
            {
                long i = y * WD + (long)z * W + x; int v = p.Cells[i]; if (v == 0) continue;
                byte st = lv.Status(p, x, y, z, v); if (st != 2 && st != 3) continue;
                if (st == 2) wrong++; else miss++;
                if (bad.Count < maxCells) bad[i] = st; else cut = true;
            }
        var spots = new List<MissingSpot>();
        var seen = new HashSet<long>(); var q = new Queue<long>(); var members = new List<long>();
        foreach (var start in bad.Keys)
        {
            if (!seen.Add(start)) continue;
            members.Clear(); q.Enqueue(start);
            while (q.Count > 0)
            {
                long i = q.Dequeue(); members.Add(i);
                int x = (int)(i % W), z = (int)(i / W % D), y = (int)(i / WD);
                for (int dy = -1; dy <= 1; dy++) for (int dz = -1; dz <= 1; dz++) for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy, nz = z + dz; if (nx < 0 || nz < 0 || ny < 0 || nx >= W || nz >= D || ny >= H) continue;
                    long j = ny * WD + (long)nz * W + nx; if (bad.ContainsKey(j) && seen.Add(j)) q.Enqueue(j);
                }
            }
            double sx = 0, sy = 0, sz = 0; int mn = int.MaxValue, m = 0, w = 0; var byBlock = new Dictionary<int, int>();
            foreach (var i in members)
            {
                int x = (int)(i % W), z = (int)(i / W % D), y = (int)(i / WD); sx += x; sy += y; sz += z; mn = Math.Min(mn, y);
                if (bad[i] == 2) w++; else m++;
                int b = Blocks.BaseOf(p.Cells[i]); byBlock[b] = byBlock.GetValueOrDefault(b) + 1;
            }
            // Mittelpunkt: das Feld der Gruppe, das dem Schwerpunkt am nächsten liegt
            double cx = sx / members.Count, cy = sy / members.Count, cz = sz / members.Count; long best = members[0]; double bd = double.MaxValue;
            foreach (var i in members) { int x = (int)(i % W), z = (int)(i / W % D), y = (int)(i / WD); double d = (x - cx) * (x - cx) + (y - cy) * (y - cy) + (z - cz) * (z - cz); if (d < bd) { bd = d; best = i; } }
            var main = byBlock.MaxBy(kv => kv.Value);
            spots.Add(new MissingSpot((int)(best % W), (int)(best / WD), (int)(best / W % D), mn, m, w, main.Key, main.Value));
        }
        spots = spots.OrderByDescending(s => s.Wrong > 0).ThenBy(s => s.MinY).ThenByDescending(s => s.Count).ToList();
        return (spots, miss, wrong, cut);
    }
}
