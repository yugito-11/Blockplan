using System;
using System.Collections.Generic;

namespace Blockplan.Core;

/// <summary>Kacheln (16×16 RGBA) für alle Blockseiten. Ohne Minecraft-Texturen: Ersatzkachel aus der Blockfarbe.</summary>
public sealed class Atlas
{
    public const int T = McAssets.T;
    public readonly List<byte[]> Tiles = new();
    readonly Dictionary<byte[], int> index = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<int, int[]> faces = new();
    readonly Dictionary<int, int> cross = new();
    readonly Dictionary<int, uint> avg = new();
    public McAssets? Mc { get; }

    public Atlas(McAssets? mc) { Mc = mc; }

    int Tile(byte[] px) { if (!index.TryGetValue(px, out var t)) { t = Tiles.Count; Tiles.Add(px); index[px] = t; } return t; }

    /// <summary>Kachel für eine Textur aus der Spieldatei (z. B. "oak_planks") oder -1.</summary>
    public int TileByName(string name)
    {
        if (Mc == null || !Mc.Tex.TryGetValue(name, out var px)) return -1;
        lock (faces) return Tile(px);
    }

    /// <summary>Kachel je Seite (+x,-x,+y,-y,+z,-z).</summary>
    public int[] Faces(int v)
    {
        lock (faces)
        {
            if (faces.TryGetValue(v, out var f)) return f;
            var mf = Mc?.FacesOf(v);
            if (mf == null) { int t = Tile(Fallback(v)); f = new[] { t, t, t, t, t, t }; }
            else { f = new int[6]; var fb = Fallback(v); for (int i = 0; i < 6; i++) f[i] = Tile(mf[i] ?? fb); }
            return faces[v] = f;
        }
    }

    public int Cross(int v)
    {
        lock (faces)
        {
            if (cross.TryGetValue(v, out var c)) return c;
            var px = Mc?.CrossOf(v) ?? Fallback(v);
            return cross[v] = Tile(px);
        }
    }

    readonly Dictionary<int, byte[]> fallbacks = new();
    byte[] Fallback(int v)
    {
        int b = Blocks.BaseOf(v);
        if (fallbacks.TryGetValue(b, out var px)) return px;
        uint c = Blocks.Get(b).Color; px = new byte[T * T * 4]; var rnd = new Random(b * 7919);
        for (int i = 0; i < T * T; i++)
        {
            double f = 0.86 + rnd.NextDouble() * 0.22;
            px[i * 4] = (byte)Math.Min(255, ((c >> 16) & 255) * f); px[i * 4 + 1] = (byte)Math.Min(255, ((c >> 8) & 255) * f);
            px[i * 4 + 2] = (byte)Math.Min(255, (c & 255) * f); px[i * 4 + 3] = 255;
        }
        return fallbacks[b] = px;
    }

    /// <summary>Durchschnittsfarbe der Oberseite (für die Karte aus der Ferne), 0xFFRRGGBB.</summary>
    public uint TopColor(int v)
    {
        lock (avg)
        {
            if (avg.TryGetValue(v, out var c)) return c;
            var f = Faces(v); var px = Tiles[f[2]]; long r = 0, g = 0, b = 0, n = 0;
            for (int i = 0; i < T * T; i++) if (px[i * 4 + 3] > 100) { r += px[i * 4]; g += px[i * 4 + 1]; b += px[i * 4 + 2]; n++; }
            if (n == 0) { uint bc = Blocks.Get(Blocks.BaseOf(v)).Color; return avg[v] = 0xff000000 | bc; }
            return avg[v] = 0xff000000 | (uint)(r / n) << 16 | (uint)(g / n) << 8 | (uint)(b / n);
        }
    }

    /// <summary>Alle Kacheln in ein quadratisches Bild (RGBA), Kantenlänge cols*16.</summary>
    public (byte[] px, int cols) Build()
    {
        lock (faces)
        {
            int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(Tiles.Count))), S = cols * T; var px = new byte[S * S * 4];
            for (int i = 0; i < Tiles.Count; i++)
            {
                int ox = (i % cols) * T, oy = (i / cols) * T; var t = Tiles[i];
                for (int y = 0; y < T; y++) Buffer.BlockCopy(t, y * T * 4, px, ((oy + y) * S + ox) * 4, T * 4);
            }
            return (px, cols);
        }
    }
}
