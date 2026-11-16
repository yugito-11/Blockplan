using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Blockplan.Core;

public sealed record LivePlayer(string Name, string Uuid, string Dim, double X, double Y, double Z, double Yaw);

/// <summary>Ein Chunk vom „aktuellen Stand“: die Oberfläche der echten Welt rund um die Spieler.</summary>
public sealed class StandChunk
{
    public int Cx, Cz, Y0, H;
    public ushort[] Data = Array.Empty<ushort>();   // (y*16 + z)*16 + x
    public ushort At(int x, int y, int z) => y < 0 || y >= H ? (ushort)0 : Data[(y * 16 + z) * 16 + x];
}

/// <summary>
/// Verbindung zur Mod „blockplan-live“ (Server-Sent Events). Hält die echte Welt im Baubereich (mit Rand)
/// und den aktuellen Stand als Chunks. Alles Einlesen passiert im Hintergrund; die Oberfläche bekommt
/// nur gebündelte Ereignisse: welche Felder sich geändert haben, damit nur diese Bereiche neu gezeichnet werden.
/// </summary>
public sealed class LiveClient : IDisposable
{
    public const ushort Unloaded = 65535;
    public const int Margin = 8;

    public string BaseUrl { get; private set; } = "";
    public string Token { get; private set; } = "";
    public string State { get; private set; } = "aus";     // aus, verbinde, lese, live, fehler
    public string Message { get; private set; } = "";
    public List<LivePlayer> Players { get; private set; } = new();

    // Baubereich: Projekt + Rand, Welt-Koordinaten der Nordwest-unten-Ecke
    public int Rx, Ry, Rz, Rw, Rh, Rd; public string Dim = "minecraft:overworld";
    public ushort[]? World;              // null, bis der Snapshot da ist
    public bool Known => World != null && known;
    volatile bool known;
    readonly List<ushort> pal = new() { 0, Unloaded };
    public readonly ConcurrentDictionary<(int, int), StandChunk> Chunks = new();

    /// <summary>Geänderte Felder (Index im Baubereich) seit dem letzten Abholen; -1 = alles neu.</summary>
    readonly HashSet<long> dirty = new(); bool dirtyAll;
    readonly HashSet<(int, int)> dirtyChunks = new();
    public event Action? Changed;         // aus dem Hintergrund-Thread, gebündelt
    public event Action? PlayersChanged;
    public event Action? StatusChanged;

    CancellationTokenSource? cts;
    readonly HttpClient http = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Link aus dem Server-Log, z. B. http://1.2.3.4:8100/?t=abc…</summary>
    public static bool ParseLink(string link, out string baseUrl, out string token)
    {
        baseUrl = ""; token = "";
        if (!Uri.TryCreate(link.Trim(), UriKind.Absolute, out var u)) return false;
        var m = Regex.Match(u.Query, @"[?&]t=([^&]+)"); if (!m.Success) return false;
        token = Uri.UnescapeDataString(m.Groups[1].Value);
        baseUrl = $"{u.Scheme}://{u.Authority}";
        return true;
    }

    void SetState(string s, string msg = "") { State = s; Message = msg; StatusChanged?.Invoke(); }

    /// <summary>Verbinden und den Baubereich des Projekts beobachten (mit Rand für die Umgebung).</summary>
    public void Connect(string link, Project p, string dim = "minecraft:overworld")
    {
        Stop();
        if (!ParseLink(link, out var b, out var t)) { SetState("fehler", "Der Link ist ungültig. Er steht im Server-Log und sieht so aus: http://adresse:8100/?t=…"); return; }
        BaseUrl = b; Token = t; Dim = dim;
        Rx = p.OriginX - Margin; Ry = p.OriginY; Rz = p.OriginZ - Margin; Rw = p.W + 2 * Margin; Rh = p.H; Rd = p.D + 2 * Margin;
        World = null; known = false; lock (dirty) { dirty.Clear(); dirtyAll = true; }
        var my = new CancellationTokenSource(); cts = my;
        _ = Task.Run(() => Run(my.Token));
    }

    public void Stop()
    {
        cts?.Cancel(); cts = null; known = false;
        if (State != "aus") SetState("aus");
    }

    public void Dispose() { Stop(); http.Dispose(); }

    async Task Run(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                SetState("verbinde", "Verbinde mit " + BaseUrl + " …");
                var url = $"{BaseUrl}/api/stream?t={Uri.EscapeDataString(Token)}&dim={Uri.EscapeDataString(Dim)}&x={Rx}&y={Ry}&z={Rz}&w={Rw}&h={Rh}&d={Rd}";
                using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                if ((int)resp.StatusCode == 401) { SetState("fehler", "Der Server lehnt den Schlüssel ab. Nimm den Link aus dem aktuellen Server-Log."); return; }
                resp.EnsureSuccessStatusCode();
                await using var s = await resp.Content.ReadAsStreamAsync(ct);
                using var rd = new StreamReader(s, System.Text.Encoding.UTF8, false, 1 << 16);
                attempt = 0; SetState("lese", "Lese den Baubereich …");
                string ev = "message"; var data = new System.Text.StringBuilder();
                var flush = Task.Run(() => Flusher(ct), ct);
                while (!ct.IsCancellationRequested)
                {
                    var line = await rd.ReadLineAsync(ct);
                    if (line == null) break;
                    if (line.Length == 0) { if (data.Length > 0) Handle(ev, data.ToString()); ev = "message"; data.Clear(); continue; }
                    if (line[0] == ':') continue;
                    if (line.StartsWith("event:")) ev = line[6..].Trim();
                    else if (line.StartsWith("data:")) { if (data.Length > 0) data.Append('\n'); data.Append(line.AsSpan(line.Length > 5 && line[5] == ' ' ? 6 : 5)); }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) { McAssets.Log("Live: " + e.Message); SetState("fehler", "Keine Verbindung: " + e.Message); }
            if (ct.IsCancellationRequested) return;
            attempt++; await Task.Delay(Math.Min(30, 2 * attempt) * 1000, ct).ContinueWith(_ => { });
        }
    }

    /// <summary>Gesammelte Änderungen höchstens 4x pro Sekunde weitergeben.</summary>
    async Task Flusher(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct).ContinueWith(_ => { });
            bool any; lock (dirty) any = dirtyAll || dirty.Count > 0 || dirtyChunks.Count > 0;
            if (any) Changed?.Invoke();
        }
    }

    /// <summary>Geänderte Felder abholen (Projektkoordinaten x, y, z) und geänderte Umgebungs-Chunks.</summary>
    public (bool all, List<(int x, int y, int z)> cells, List<(int cx, int cz)> chunks) TakeChanges()
    {
        lock (dirty)
        {
            var cells = new List<(int, int, int)>(dirty.Count);
            foreach (var i in dirty) { int x = (int)(i % Rw), z = (int)(i / Rw % Rd), y = (int)(i / ((long)Rw * Rd)); cells.Add((x - Margin, y, z - Margin)); }
            var r = (dirtyAll, cells, dirtyChunks.ToList());
            dirty.Clear(); dirtyAll = false; dirtyChunks.Clear();
            return r;
        }
    }

    ushort Map(string id)
    {
        if (id == "blockplan:unloaded") return Unloaded;
        var bare = id.StartsWith("minecraft:") ? id[10..] : id; var br = bare.IndexOf('[');
        if (Blocks.Airs.Contains(br < 0 ? bare : bare[..br])) return 0;
        return (ushort)Blocks.Ensure(id);
    }

    void Handle(string ev, string data)
    {
        try
        {
            switch (ev)
            {
                case "palette":
                {
                    var o = JsonNode.Parse(data)!; int from = (int)o["from"]!;
                    var e = o["e"]!.AsArray();
                    lock (pal) { while (pal.Count < from + e.Count) pal.Add(0); for (int k = 0; k < e.Count; k++) pal[from + k] = Map((string)e[k]![0]!); }
                    break;
                }
                case "snapshot":
                {
                    var str = JsonSerializer.Deserialize<string>(data) ?? "";
                    var w = new ushort[(long)Rw * Rh * Rd]; long p = 0; ushort[] pa; lock (pal) pa = pal.ToArray();
                    foreach (var tok in str.Split(','))
                    {
                        if (tok.Length == 0) continue; int st = tok.IndexOf('*');
                        long v = Project.P36(st < 0 ? tok : tok[..st]), n = st < 0 ? 1 : Project.P36(tok[(st + 1)..]);
                        var val = v < pa.Length ? pa[v] : (ushort)0; long e = Math.Min(w.LongLength, p + n);
                        if (val != 0) Array.Fill(w, val, (int)p, (int)(e - p)); p += n;
                    }
                    World = w; known = true;
                    lock (dirty) dirtyAll = true;
                    SetState("live", "Live");
                    break;
                }
                case "changes":
                {
                    var w = World; if (w == null) break; ushort[] pa; lock (pal) pa = pal.ToArray();
                    using var doc = JsonDocument.Parse(data); var a = doc.RootElement;
                    lock (dirty)
                        for (int k = 0; k + 1 < a.GetArrayLength(); k += 2)
                        {
                            long i = a[k].GetInt64(); int v = a[k + 1].GetInt32();
                            if (i < 0 || i >= w.LongLength) continue;
                            w[i] = v < pa.Length ? pa[v] : (ushort)0; dirty.Add(i);
                        }
                    break;
                }
                case "players":
                {
                    using var doc = JsonDocument.Parse(data);
                    Players = doc.RootElement.EnumerateArray().Select(q => new LivePlayer(q.GetProperty("n").GetString() ?? "", q.GetProperty("u").GetString() ?? "", q.GetProperty("d").GetString() ?? "",
                        q.GetProperty("x").GetDouble(), q.GetProperty("y").GetDouble(), q.GetProperty("z").GetDouble(), q.TryGetProperty("yaw", out var yw) ? yw.GetDouble() : 0)).ToList();
                    PlayersChanged?.Invoke(); if (State == "live") StatusChanged?.Invoke();
                    break;
                }
                case "chunk":
                {
                    using var doc = JsonDocument.Parse(data); var o = doc.RootElement;
                    if (o.GetProperty("d").GetString() != Dim) break;
                    var map = o.GetProperty("p").EnumerateArray().Select(e => Map(e[0].GetString() ?? "air")).ToArray();
                    int h = o.GetProperty("h").GetInt32(); var c = new StandChunk { Cx = o.GetProperty("cx").GetInt32(), Cz = o.GetProperty("cz").GetInt32(), Y0 = o.GetProperty("y0").GetInt32(), H = h, Data = new ushort[256 * h] };
                    int p = 0;
                    foreach (var tok in (o.GetProperty("r").GetString() ?? "").Split(','))
                    {
                        if (tok.Length == 0) continue; int st = tok.IndexOf('*');
                        int v = (int)Project.P36(st < 0 ? tok : tok[..st]), n = (int)Project.P36(tok.Length > st + 1 && st >= 0 ? tok[(st + 1)..] : "1");
                        var val = v < map.Length ? map[v] : (ushort)0; int e = Math.Min(c.Data.Length, p + n);
                        if (val != 0 && e > p) Array.Fill(c.Data, val, p, e - p); p += n;
                    }
                    Chunks[(c.Cx, c.Cz)] = c;
                    lock (dirty) dirtyChunks.Add((c.Cx, c.Cz));
                    break;
                }
                case "standreset": Chunks.Clear(); lock (dirty) dirtyAll = true; break;
                case "fail": SetState("fehler", "Der Baubereich ist für diese Version der Mod zu groß. Installiere blockplan-live 1.1.0 auf dem Server."); break;
            }
        }
        catch (Exception e) { McAssets.Log($"Live {ev}: {e.Message}"); }
    }

    /// <summary>Welt an einer Projekt-Stelle: Baubereich, sonst aktueller Stand, sonst Unloaded.</summary>
    public ushort WorldAt(Project p, int x, int y, int z)
    {
        var w = World;
        if (w != null && known)
        {
            int ix = x + Margin, iz = z + Margin;
            if (ix >= 0 && iz >= 0 && y >= 0 && ix < Rw && iz < Rd && y < Rh) { var v = w[((long)y * Rd + iz) * Rw + ix]; if (v != Unloaded) return v; }
        }
        int wx = p.OriginX + x, wy = p.OriginY + y, wz = p.OriginZ + z;
        if (Chunks.TryGetValue((wx >> 4, wz >> 4), out var c)) { if (wy < c.Y0) return Unloaded; return c.At(wx & 15, wy - c.Y0, wz & 15); }
        return Unloaded;
    }

    static readonly Regex soft = new("^(water|bubble_column|seagrass|tall_seagrass|kelp|kelp_plant|short_grass|tall_grass|fern|large_fern|snow|dead_bush|bush|leaf_litter|cave_air|void_air)$");
    static readonly ConcurrentDictionary<int, bool> softCache = new();
    static bool Soft(int v) => softCache.GetOrAdd(v, x => soft.IsMatch(Blocks.Get(Blocks.BaseOf(x)).Key));

    /// <summary>Baufortschritt eines Plan-Blocks: 1 gesetzt, 2 falscher Block, 3 fehlt, 4 unbekannt (noch nicht gelesen).</summary>
    public byte Status(Project p, int x, int y, int z, int planV)
    {
        var w = WorldAt(p, x, y, z);
        if (w == Unloaded) return 4;
        if (w != 0 && Blocks.BaseOf(w) != Blocks.BaseOf(planV) && Soft(w)) w = 0;
        return w == 0 ? (byte)3 : Blocks.BaseOf(w) == Blocks.BaseOf(planV) ? (byte)1 : (byte)2;
    }
}
