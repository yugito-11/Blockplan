using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Blockplan.Core;

/// <summary>Liest Minecraft-NBT (Big Endian). Compound = Dictionary, Liste = List, Arrays = byte[]/int[]/long[].</summary>
public static class Nbt
{
    public static byte[] GunzipMaybe(byte[] u)
    {
        if (u.Length < 2 || u[0] != 0x1f || u[1] != 0x8b) return u;
        using var gz = new GZipStream(new MemoryStream(u), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    public static Dictionary<string, object> Read(byte[] u)
    {
        int o = 0;
        if (u.Length == 0 || u[o++] != 10) throw new InvalidDataException("Das ist keine Schematic-Datei.");
        Str(u, ref o);
        return (Dictionary<string, object>)Tag(u, ref o, 10);
    }

    static string Str(byte[] u, ref int o)
    {
        int n = BinaryPrimitives.ReadUInt16BigEndian(u.AsSpan(o)); o += 2;
        var s = Encoding.UTF8.GetString(u, o, n); o += n; return s;
    }

    static object Tag(byte[] u, ref int o, int t)
    {
        switch (t)
        {
            case 1: return (sbyte)u[o++];
            case 2: { var v = BinaryPrimitives.ReadInt16BigEndian(u.AsSpan(o)); o += 2; return v; }
            case 3: { var v = BinaryPrimitives.ReadInt32BigEndian(u.AsSpan(o)); o += 4; return v; }
            case 4: { var v = BinaryPrimitives.ReadInt64BigEndian(u.AsSpan(o)); o += 8; return v; }
            case 5: { var v = BinaryPrimitives.ReadSingleBigEndian(u.AsSpan(o)); o += 4; return v; }
            case 6: { var v = BinaryPrimitives.ReadDoubleBigEndian(u.AsSpan(o)); o += 8; return v; }
            case 7: { int n = BinaryPrimitives.ReadInt32BigEndian(u.AsSpan(o)); o += 4; var a = u.AsSpan(o, n).ToArray(); o += n; return a; }
            case 8: return Str(u, ref o);
            case 9:
            {
                int et = u[o++]; int n = BinaryPrimitives.ReadInt32BigEndian(u.AsSpan(o)); o += 4;
                var l = new List<object>(Math.Max(0, n));
                for (int i = 0; i < n; i++) l.Add(Tag(u, ref o, et));
                return l;
            }
            case 10:
            {
                var d = new Dictionary<string, object>();
                for (;;) { int tt = u[o++]; if (tt == 0) break; var k = Str(u, ref o); d[k] = Tag(u, ref o, tt); }
                return d;
            }
            case 11:
            {
                int n = BinaryPrimitives.ReadInt32BigEndian(u.AsSpan(o)); o += 4; var a = new int[n];
                for (int i = 0; i < n; i++) { a[i] = BinaryPrimitives.ReadInt32BigEndian(u.AsSpan(o)); o += 4; }
                return a;
            }
            case 12:
            {
                int n = BinaryPrimitives.ReadInt32BigEndian(u.AsSpan(o)); o += 4; var a = new long[n];
                for (int i = 0; i < n; i++) { a[i] = BinaryPrimitives.ReadInt64BigEndian(u.AsSpan(o)); o += 8; }
                return a;
            }
            default: throw new InvalidDataException("Unbekanntes NBT-Feld " + t);
        }
    }

    /* ---------- Schreiben ---------- */
    /// <summary>NBT schreiben (gzip). Typen: sbyte, short, int, long, float, double, string, byte[], int[], long[],
    /// Dictionary (Compound), List&lt;Dictionary&gt; (Liste von Compounds).</summary>
    public static byte[] Write(string rootName, Dictionary<string, object> root)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
        using (var w = new BinaryWriter(gz))
        {
            w.Write((byte)10); WStr(w, rootName); WTag(w, root); 
        }
        return ms.ToArray();
    }

    static byte TypeOf(object o) => o switch
    {
        sbyte or byte or bool => 1, short => 2, int => 3, long => 4, float => 5, double => 6, byte[] => 7, string => 8,
        int[] => 11, long[] => 12, Dictionary<string, object> => 10, System.Collections.IList => 9,
        _ => throw new ArgumentException("NBT: unbekannter Typ " + o.GetType()),
    };

    static void WStr(BinaryWriter w, string s) { var b = Encoding.UTF8.GetBytes(s); WBE(w, (short)b.Length); w.Write(b); }
    static void WBE(BinaryWriter w, short v) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteInt16BigEndian(b, v); w.Write(b); }
    static void WBE(BinaryWriter w, int v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, v); w.Write(b); }
    static void WBE(BinaryWriter w, long v) { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(b, v); w.Write(b); }

    static void WTag(BinaryWriter w, object o)
    {
        switch (o)
        {
            case sbyte sb: w.Write((byte)sb); break;
            case byte by: w.Write(by); break;
            case bool bo: w.Write((byte)(bo ? 1 : 0)); break;
            case short sh: WBE(w, sh); break;
            case int i: WBE(w, i); break;
            case long l: WBE(w, l); break;
            case float f: { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteSingleBigEndian(b, f); w.Write(b); break; }
            case double d: { Span<byte> b = stackalloc byte[8]; BinaryPrimitives.WriteDoubleBigEndian(b, d); w.Write(b); break; }
            case byte[] ba: WBE(w, ba.Length); w.Write(ba); break;
            case string s: WStr(w, s); break;
            case int[] ia: WBE(w, ia.Length); foreach (var x in ia) WBE(w, x); break;
            case long[] la: WBE(w, la.Length); foreach (var x in la) WBE(w, x); break;
            case Dictionary<string, object> c:
                foreach (var (k, v) in c) { w.Write(TypeOf(v)); WStr(w, k); WTag(w, v); }
                w.Write((byte)0); break;
            case System.Collections.IList li:
            {
                byte et = li.Count > 0 ? TypeOf(li[0]!) : (byte)10;   // leere Liste als Compound-Liste
                w.Write(et); WBE(w, li.Count); foreach (var x in li) WTag(w, x!); break;
            }
            default: throw new ArgumentException("NBT: unbekannter Typ " + o.GetType());
        }
    }

    public static int Int(object? o) => o switch { sbyte b => b, short s => s, int i => i, long l => (int)l, _ => 0 };
    public static Dictionary<string, object>? C(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
    public static object? G(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v : null;
}
