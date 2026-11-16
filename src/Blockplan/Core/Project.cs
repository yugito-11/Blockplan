using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blockplan.Core;

/// <summary>Ein Bauprojekt. Zellen in der Reihenfolge (y*D+z)*W+x, Wert = Index in Blocks.All.</summary>
public sealed class Project
{
    public string Id = NewId();
    public string Name = "Projekt";
    public int W, D, H;
    public ushort[] Cells = Array.Empty<ushort>();
    public int OriginX, OriginY = 64, OriginZ;
    public long Created = Now(), Updated = Now();
    /// <summary>Weitere Daten aus dem Artefakt (Baupläne, Etappen, To-dos …), unverändert aufbewahrt.</summary>
    public JsonObject Extra = new();
    /// <summary>Banner-Muster je Feld (Index wie Cells), z. B. "stripe_top:red|creeper:black".</summary>
    public Dictionary<long, string> Banners = new();

    public long Index(int x, int y, int z) => ((long)y * D + z) * W + x;
    public ushort At(int x, int y, int z) => (x < 0 || y < 0 || z < 0 || x >= W || y >= H || z >= D) ? (ushort)0 : Cells[Index(x, y, z)];

    static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public static string NewId() => "p" + Convert.ToString(Now(), 16) + Guid.NewGuid().ToString("N")[..6];

    /// <summary>Höchste belegte Schicht (oder -1).</summary>
    public int MaxY()
    {
        long WD = (long)W * D;
        for (int y = H - 1; y >= 0; y--) { long b = y * WD; for (long i = 0; i < WD; i++) if (Cells[b + i] != 0) return y; }
        return -1;
    }

    /// <summary>Jeden Blockzustand umwandeln (0 = entfernen). Gibt die Zahl der geänderten Felder zurück.</summary>
    public long Replace(Func<int, int> f)
    {
        var map = new int[Blocks.Count + 1]; for (int i = 0; i < map.Length; i++) map[i] = -1;
        long changed = 0;
        for (long i = 0; i < Cells.LongLength; i++)
        {
            int v = Cells[i]; if (v == 0) continue;
            if (v >= map.Length) { int old = map.Length; Array.Resize(ref map, Blocks.Count + 1); for (int k = old; k < map.Length; k++) map[k] = -1; }
            if (map[v] < 0) map[v] = f(v);
            if (map[v] != v) { Cells[i] = (ushort)map[v]; changed++; if (map[v] == 0) Banners.Remove(i); }
        }
        return changed;
    }

    /// <summary>Anzahl je Blockzustand.</summary>
    public Dictionary<int, long> CountBlocks(int maxY = int.MaxValue)
    {
        var c = new long[Blocks.Count + 1]; long end = Math.Min((long)H, (long)maxY + 1) * W * D;
        for (long i = 0; i < end; i++) { var v = Cells[i]; if (v != 0) c[v]++; }
        var r = new Dictionary<int, long>();
        for (int v = 1; v < c.Length; v++) if (c[v] > 0) r[v] = c[v];
        return r;
    }

    /* ---------- Austauschformat wie im Artefakt (JSON mit RLE in Basis 36) ---------- */
    static readonly string[] known = { "v", "app", "id", "name", "w", "d", "h", "pal", "cells", "origin", "created", "updated", "banners" };

    public string ToJson()
    {
        var used = new SortedSet<int>(); foreach (var v in Cells) if (v != 0) used.Add(v);
        var list = used.ToList(); var local = new Dictionary<int, int>();
        for (int i = 0; i < list.Count; i++) local[list[i]] = i + 1;
        var o = new JsonObject
        {
            ["v"] = 1, ["app"] = "blockplan", ["id"] = Id, ["name"] = Name, ["w"] = W, ["d"] = D, ["h"] = H,
            ["pal"] = new JsonArray(list.Select(v => (JsonNode)JsonValue.Create(Blocks.Get(v).Key)!).ToArray()),
            ["cells"] = Rle(Cells, v => v == 0 ? 0 : local[v]),
            ["origin"] = new JsonObject { ["x"] = OriginX, ["y"] = OriginY, ["z"] = OriginZ },
            ["created"] = Created, ["updated"] = Updated,
        };
        if (Banners.Count > 0) { var b = new JsonObject(); foreach (var kv in Banners) b[kv.Key.ToString()] = kv.Value; o["banners"] = b; }
        foreach (var kv in Extra) if (!known.Contains(kv.Key)) o[kv.Key] = kv.Value?.DeepClone();
        return o.ToJsonString();
    }

    public static Project FromJson(string json)
    {
        var o = JsonNode.Parse(json)!.AsObject();
        var p = new Project
        {
            Id = (string?)o["id"] ?? NewId(), Name = (string?)o["name"] ?? "Projekt",
            W = Math.Max(1, (int?)o["w"] ?? 1), D = Math.Max(1, (int?)o["d"] ?? 1), H = Math.Max(1, (int?)o["h"] ?? 1),
        };
        var pal = o["pal"]?.AsArray().Select(n => Blocks.Ensure((string?)n ?? "stone")).ToArray() ?? Array.Empty<int>();
        p.Cells = UnRle((string?)o["cells"] ?? "", (long)p.W * p.D * p.H, v => v == 0 || v > pal.Length ? (ushort)0 : (ushort)pal[v - 1]);
        if (o["origin"] is JsonObject og) { p.OriginX = (int?)og["x"] ?? 0; p.OriginY = (int?)og["y"] ?? 64; p.OriginZ = (int?)og["z"] ?? 0; }
        if (o["banners"] is JsonObject bo) foreach (var kv in bo) if (long.TryParse(kv.Key, out var bi) && (string?)kv.Value is { } bv) p.Banners[bi] = bv;
        p.Created = (long?)o["created"] ?? Now(); p.Updated = (long?)o["updated"] ?? Now();
        foreach (var kv in o) if (!known.Contains(kv.Key)) p.Extra[kv.Key] = kv.Value?.DeepClone();
        return p;
    }

    const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
    static string B36(long v) { if (v == 0) return "0"; var sb = new StringBuilder(); while (v > 0) { sb.Insert(0, digits[(int)(v % 36)]); v /= 36; } return sb.ToString(); }
    static long P36(ReadOnlySpan<char> s) { long v = 0; foreach (var c in s) v = v * 36 + (c <= '9' ? c - '0' : char.ToLowerInvariant(c) - 'a' + 10); return v; }

    static string Rle(ushort[] a, Func<int, int> map)
    {
        var sb = new StringBuilder(); long i = 0;
        while (i < a.LongLength)
        {
            var v = a[i]; long j = i + 1; while (j < a.LongLength && a[j] == v) j++;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(B36(map(v))); if (j - i > 1) { sb.Append('*'); sb.Append(B36(j - i)); }
            i = j;
        }
        return sb.ToString();
    }

    static ushort[] UnRle(string s, long n, Func<int, ushort> map)
    {
        var a = new ushort[n]; long p = 0; var span = s.AsSpan();
        while (span.Length > 0 && p < n)
        {
            int c = span.IndexOf(','); var tok = c < 0 ? span : span[..c]; span = c < 0 ? ReadOnlySpan<char>.Empty : span[(c + 1)..];
            int st = tok.IndexOf('*');
            long v = P36(st < 0 ? tok : tok[..st]), cnt = st < 0 ? 1 : P36(tok[(st + 1)..]);
            var mv = map((int)v); long e = Math.Min(n, p + cnt);
            if (mv != 0) for (long k = p; k < e; k++) a[k] = mv;
            p += cnt;
        }
        return a;
    }
}
