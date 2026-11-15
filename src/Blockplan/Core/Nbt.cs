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

    public static int Int(object? o) => o switch { sbyte b => b, short s => s, int i => i, long l => (int)l, _ => 0 };
    public static Dictionary<string, object>? C(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
    public static object? G(this Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v : null;
}
