using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Blockplan.Core;

/// <summary>Ein Ereignis im Bauverlauf: zur Zeit T (ms) wurde Feld I gebaut (Placed) oder wieder entfernt.</summary>
public readonly record struct BuildEvent(long T, long I, bool Placed);

/// <summary>
/// Zeichnet beim Live-Verbinden auf, wann welcher Block des Plans gesetzt wurde (nur Änderungen),
/// und speichert das gebündelt in der eigenen Datenbank. Daraus entsteht später der Zeitraffer.
/// </summary>
public sealed class TimelapseRecorder
{
    readonly Store store; readonly Project p; bool[] state; readonly List<BuildEvent> pending = new(); DateTime lastFlush = DateTime.Now;
    public long Recorded { get; private set; }

    public TimelapseRecorder(Store s, Project project)
    {
        store = s; p = project;
        var tl = Timeline.Load(s, p);
        state = tl.StateAt(long.MaxValue); Recorded = tl.Events.Length;
    }

    /// <summary>Felder prüfen (null = alle) und Änderungen merken. Unbekannte Felder (nicht geladen) ändern nichts.</summary>
    public void Update(LiveClient lv, IEnumerable<(int x, int y, int z)>? cells)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); long WD = (long)p.W * p.D;
        void Check(int x, int y, int z)
        {
            if (x < 0 || y < 0 || z < 0 || x >= p.W || y >= p.H || z >= p.D) return;
            long i = y * WD + (long)z * p.W + x; int v = p.Cells[i]; if (v == 0) return;
            byte st = lv.Status(p, x, y, z, v); if (st == 4) return;
            bool placed = st == 1;
            if (placed != state[i]) { state[i] = placed; pending.Add(new BuildEvent(now, i, placed)); }
        }
        if (cells == null) { for (int y = 0; y < p.H; y++) for (int z = 0; z < p.D; z++) for (int x = 0; x < p.W; x++) Check(x, y, z); }
        else foreach (var (x, y, z) in cells) Check(x, y, z);
        if (pending.Count > 50_000 || (pending.Count > 0 && (DateTime.Now - lastFlush).TotalSeconds > 20)) Flush();
    }

    public void Flush()
    {
        lastFlush = DateTime.Now; if (pending.Count == 0) return;
        var ev = pending.ToArray(); pending.Clear(); Recorded += ev.Length;
        try { store.AddTimelapse(p.Id, ev[0].T, Timeline.Encode(ev)); } catch (Exception e) { McAssets.Log("Zeitraffer speichern: " + e.Message); }
    }
}

/// <summary>Gespeicherter Bauverlauf eines Projekts.</summary>
public sealed class Timeline
{
    public BuildEvent[] Events = Array.Empty<BuildEvent>();
    public long Cells;
    public long Start => Events.Length > 0 ? Events[0].T : 0;
    public long End => Events.Length > 0 ? Events[^1].T : 0;

    public static Timeline Load(Store s, Project p)
    {
        var all = new List<BuildEvent>();
        foreach (var blob in s.LoadTimelapse(p.Id)) all.AddRange(Decode(blob));
        all.Sort((a, b) => a.T.CompareTo(b.T));
        return new Timeline { Events = all.Where(e => e.I >= 0 && e.I < p.Cells.LongLength).ToArray(), Cells = p.Cells.LongLength };
    }

    /// <summary>Gebaut-Zustand je Feld zum Zeitpunkt t.</summary>
    public bool[] StateAt(long t)
    {
        var st = new bool[Cells];
        foreach (var e in Events) { if (e.T > t) break; st[e.I] = e.Placed; }
        return st;
    }

    /// <summary>Zustand von „from“ bis t weiterführen (für flüssiges Abspielen); gibt den neuen Index zurück.</summary>
    public int Advance(bool[] st, int from, long t)
    {
        int k = from;
        for (; k < Events.Length && Events[k].T <= t; k++) st[Events[k].I] = Events[k].Placed;
        return k;
    }

    public static long PlacedCount(bool[] st) { long n = 0; foreach (var b in st) if (b) n++; return n; }

    /* ---------- kompakt speichern: Zeit und Feld als Differenz, VarInt, gzip ---------- */
    public static byte[] Encode(BuildEvent[] ev)
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true))
        {
            void W(ulong u) { while (u >= 0x80) { gz.WriteByte((byte)(u | 0x80)); u >>= 7; } gz.WriteByte((byte)u); }
            W((ulong)ev.Length); long t = 0, i = 0;
            foreach (var e in ev)
            {
                W((ulong)Math.Max(0, e.T - t)); t = e.T;
                long d = e.I - i; i = e.I;
                W((ulong)((d << 1) ^ (d >> 63)) << 1 | (e.Placed ? 1UL : 0));
            }
        }
        return ms.ToArray();
    }

    public static List<BuildEvent> Decode(byte[] blob)
    {
        var r = new List<BuildEvent>();
        try
        {
            using var gz = new GZipStream(new MemoryStream(blob), CompressionMode.Decompress);
            using var ms = new MemoryStream(); gz.CopyTo(ms); var b = ms.ToArray(); int o = 0;
            ulong R() { ulong u = 0; int sh = 0; byte x; do { x = b[o++]; u |= (ulong)(x & 127) << sh; sh += 7; } while ((x & 128) != 0); return u; }
            long n = (long)R(), t = 0, i = 0;
            for (long k = 0; k < n; k++)
            {
                t += (long)R(); ulong v = R(); bool placed = (v & 1) != 0; ulong zz = v >> 1; long d = (long)(zz >> 1) ^ -(long)(zz & 1); i += d;
                r.Add(new BuildEvent(t, i, placed));
            }
        }
        catch (Exception e) { McAssets.Log("Zeitraffer lesen: " + e.Message); }
        return r;
    }
}
