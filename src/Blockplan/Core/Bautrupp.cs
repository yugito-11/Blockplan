using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Blockplan.Core;

/*
 * Brücke zu Bautrupp (Mod + Webseite für Gruppenprojekte, https://github.com/yugito-11/bautrupp).
 * Blockplan schreibt Etappen, Baupläne oder Ebenen als Bautrupp-Aufgaben in dieselbe Firebase-Datenbank,
 * im gleichen Format wie der Schematic-Import der Bautrupp-Webseite:
 *   /gruppen/<code>/projekte/<id>, /aufgaben/<id>, /material/<id>, /verlauf/<id>
 *   /gruppen/<code>__bauplan/<Aufgabe> = Positionen (Base36, Nummer = x + b * (z + l * y))
 * Die Bautrupp-Mod zählt dann Block für Block an den richtigen Stellen mit.
 */
public static class Bautrupp
{
    /* ---------- Verbindung ---------- */

    public static string NormUrl(string u)
    {
        u = (u ?? "").Trim();
        if (u.Length > 0 && !Regex.IsMatch(u, "^https?:")) u = "https://" + u;
        return u.TrimEnd('/');
    }
    public static string NormGroup(string g) => Regex.Replace((g ?? "").Trim(), "[^A-Za-z0-9_-]", "");

    /// <summary>Einladungs-Link der Webseite (…?db=…&amp;gruppe=…) in Adresse und Gruppencode zerlegen.</summary>
    public static bool ParseLink(string text, out string db, out string group)
    {
        db = ""; group = "";
        if (!Uri.TryCreate((text ?? "").Trim(), UriKind.Absolute, out var u) || u.Query.Length < 2) return false;
        foreach (var kv in u.Query[1..].Split('&'))
        {
            int i = kv.IndexOf('='); if (i <= 0) continue;
            var k = kv[..i]; var v = Uri.UnescapeDataString(kv[(i + 1)..].Replace('+', ' '));
            if (k == "db") db = NormUrl(v); else if (k == "gruppe") group = NormGroup(v);
        }
        return db.Length > 0 && group.Length > 0;
    }

    /// <summary>Firebase-Schlüssel dürfen kein . # $ [ ] / enthalten.</summary>
    public static string Key(string s) => Regex.Replace(s, @"[.#$\[\]/]", "_");

    static readonly Random rnd = new();
    /// <summary>Zeitlich sortierbare IDs wie in Mod und Webseite.</summary>
    public static string NewId() => B36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + B36(rnd.Next(1 << 20));
    public static string B36(long v)
    {
        if (v == 0) return "0";
        const string d = "0123456789abcdefghijklmnopqrstuvwxyz"; var sb = new StringBuilder();
        while (v > 0) { sb.Insert(0, d[(int)(v % 36)]); v /= 36; }
        return sb.ToString();
    }

    static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
    static string Url(string db, string group, string path) => db + "/gruppen/" + Uri.EscapeDataString(group) + path + ".json";

    /// <summary>Spieler der Gruppe (Namen); wirft bei falscher Adresse oder ohne Verbindung.</summary>
    public static async Task<List<string>> Players(string db, string group)
    {
        var res = await http.GetAsync(Url(db, group, "/spieler") + "?shallow=true");
        if (!res.IsSuccessStatusCode) throw new Exception($"Datenbank antwortet mit {(int)res.StatusCode}. Stimmt die Adresse?");
        var n = JsonNode.Parse(await res.Content.ReadAsStringAsync());
        return n is JsonObject o ? o.Select(kv => kv.Key).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
    }

    static async Task Patch(string url, JsonNode body)
    {
        var res = await http.PatchAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        if (!res.IsSuccessStatusCode) throw new Exception($"Speichern fehlgeschlagen ({(int)res.StatusCode}).");
    }

    /// <summary>Erst die Baupläne, dann Projekt und Aufgaben, zuletzt der Verlauf (so findet die Mod den Plan, sobald sie die Aufgabe sieht).</summary>
    public static async Task Send(string db, string group, Payload p)
    {
        if (p.Plans.Count > 0) await Patch(Url(db, group + "__bauplan", ""), p.Plans);
        await Patch(Url(db, group, ""), p.Update);
        var log = new JsonObject { ["wer"] = p.Me, ["text"] = p.LogText, ["zeit"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["aufgabe"] = "" };
        try { await Patch(Url(db, group, "/verlauf"), new JsonObject { [NewId()] = log }); } catch { /* Verlauf ist nicht wichtig */ }
    }

    /* ---------- Blockzustand -> Items (wie der Schematic-Import der Webseite) ---------- */

    static readonly HashSet<string> skip = new() { "air", "cave_air", "void_air", "structure_void", "structure_block", "jigsaw", "water", "lava", "fire", "soul_fire",
        "bubble_column", "piston_head", "moving_piston", "nether_portal", "end_portal", "end_gateway", "frosted_ice", "light", "barrier" };
    static readonly Dictionary<string, string> rename = new()
    {
        ["wall_torch"] = "torch", ["soul_wall_torch"] = "soul_torch", ["redstone_wall_torch"] = "redstone_torch", ["copper_wall_torch"] = "copper_torch",
        ["redstone_wire"] = "redstone", ["tripwire"] = "string", ["wheat"] = "wheat_seeds", ["carrots"] = "carrot", ["potatoes"] = "potato", ["beetroots"] = "beetroot_seeds",
        ["cocoa"] = "cocoa_beans", ["sweet_berry_bush"] = "sweet_berries", ["melon_stem"] = "melon_seeds", ["attached_melon_stem"] = "melon_seeds",
        ["pumpkin_stem"] = "pumpkin_seeds", ["attached_pumpkin_stem"] = "pumpkin_seeds", ["bamboo_sapling"] = "bamboo", ["kelp_plant"] = "kelp",
        ["cave_vines"] = "glow_berries", ["cave_vines_plant"] = "glow_berries", ["twisting_vines_plant"] = "twisting_vines", ["weeping_vines_plant"] = "weeping_vines",
        ["tall_seagrass"] = "seagrass", ["big_dripleaf_stem"] = "big_dripleaf", ["powder_snow"] = "powder_snow_bucket", ["torchflower_crop"] = "torchflower_seeds",
        ["pitcher_crop"] = "pitcher_pod", ["water_cauldron"] = "cauldron", ["lava_cauldron"] = "cauldron", ["powder_snow_cauldron"] = "cauldron", ["dirt_path"] = "dirt", ["farmland"] = "dirt",
    };
    static readonly Regex wall = new("_wall_(sign|hanging_sign|banner|head|skull|fan)$");
    static readonly Dictionary<int, (string id, int n)[]> itemCache = new();

    /// <summary>Items für einen Blockzustand (Index in Blocks.All), z. B. Doppelstufe = 2 Stufen, obere Türhälfte = nichts.</summary>
    public static (string id, int n)[] Items(int v)
    {
        lock (itemCache)
        {
            if (itemCache.TryGetValue(v, out var r)) return r;
            return itemCache[v] = Compute(v);
        }
    }

    static (string, int)[] Compute(int v)
    {
        if (v <= 0 || v >= Blocks.Count) return Array.Empty<(string, int)>();
        var b = Blocks.Get(v); var id = Blocks.Get(Blocks.BaseOf(v)).Key;
        if (id.Contains(':')) return Array.Empty<(string, int)>();   // Blöcke aus Mods kennt Bautrupp nicht
        var pr = b.Props ?? new Dictionary<string, string>();
        string P(string k) => pr.TryGetValue(k, out var s) ? s : "";
        if (skip.Contains(id) || P("half") == "upper" || P("part") == "head") return Array.Empty<(string, int)>();
        int n = 1;
        if (P("type") == "double" && id.EndsWith("_slab")) n = 2;
        foreach (var k in new[] { "candles", "pickles", "eggs" }) if (int.TryParse(P(k), out var c)) n = c;
        if (id == "snow" && int.TryParse(P("layers"), out var l)) n = l;
        if (id.StartsWith("potted_"))
        {
            var plant = Regex.Replace(id[7..], "_bush$", "");
            return new[] { ("flower_pot", 1), (plant is "azalea" or "flowering_azalea" ? plant : id[7..], 1) };
        }
        id = rename.TryGetValue(id, out var rn) ? rn : wall.Replace(id, "_$1");
        return new[] { (id, n) };
    }

    /// <summary>Englischer Name (so speichert Bautrupp itemName) und Anzeigename (deutsch, wenn bekannt).</summary>
    public static string NameEn(string id) { int v = Blocks.Find(id); return v > 0 ? Blocks.Get(v).Name : Regex.Replace(id.Replace('_', ' '), @"\b\w", m => m.Value.ToUpperInvariant()); }
    public static string Label(string id) { int v = Blocks.Find(id); return v > 0 ? Blocks.DisplayName(v, true) : NameEn(id); }

    /* ---------- Aufgaben zusammenstellen ---------- */

    public enum Mode { Stages, Parts, Layers }

    public sealed class Options
    {
        public Mode Mode = Mode.Stages;
        public int LayerHeight = 4;
        /// <summary>platzieren, kombi oder sammeln</summary>
        public string Art = "kombi";
        /// <summary>overworld, nether, end</summary>
        public string Dim = "overworld";
        public bool WithPlace = true, Order = true, SkipDone = true, RestAsMaterial = true;
        public string Who = "", Me = "", Name = "Projekt";
    }

    /// <summary>Eine künftige Aufgabe: Titel, Felder (Index im Projekt) und welche Gruppe vorher fertig sein muss (-1 = keine).</summary>
    public sealed record Group(string Title, List<long> Cells, int After);

    public static List<Group> Groups(Project p, PlanData pl, Options o)
    {
        var r = new List<Group>();
        var parts = pl.Parts.Where(t => !t.Base).ToList();
        string Y(List<long> cells)
        {
            long WD = (long)p.W * p.D; long a = long.MaxValue, b = long.MinValue;
            foreach (var c in cells) { long y = c / WD; a = Math.Min(a, y); b = Math.Max(b, y); }
            return a == long.MaxValue ? "" : o.WithPlace ? $"Y {p.OriginY + a}{(b > a ? "–" + (p.OriginY + b) : "")}" : $"Höhe {a + 1}{(b > a ? "–" + (b + 1) : "")}";
        }
        switch (o.Mode)
        {
            case Mode.Stages:
                foreach (var s in pl.Stages)
                {
                    if (o.SkipDone && s.Done) continue;
                    var cells = PartCells(p, parts.Where(t => s.Parts.Contains(t.Id)));
                    if (cells.Count == 0) continue;
                    r.Add(new Group(s.Name, cells, o.Order && r.Count > 0 ? r.Count - 1 : -1));
                }
                break;
            case Mode.Parts:
                foreach (var t in parts.OrderBy(t => t.Box[1]).ThenBy(t => pl.Label(t), StringComparer.CurrentCultureIgnoreCase))
                {
                    var cells = PartCells(p, new[] { t });
                    if (cells.Count > 0) r.Add(new Group(pl.Label(t), cells, -1));
                }
                break;
            default:
            {
                int h = Math.Max(1, o.LayerHeight); long WD = (long)p.W * p.D;
                for (int y0 = 0, k = 0; y0 < p.H; y0 += h)
                {
                    var cells = new List<long>();
                    for (long i = y0 * WD, end = Math.Min(p.H, y0 + h) * WD; i < end; i++) if (p.Cells[i] != 0 && Items(p.Cells[i]).Length > 0) cells.Add(i);
                    if (cells.Count == 0) continue;
                    k++;
                    r.Add(new Group($"Ebene {k} ({Y(cells)})", cells, o.Order && r.Count > 0 ? r.Count - 1 : -1));
                }
                break;
            }
        }
        return r;
    }

    /// <summary>Alle Felder mit Blöcken in diesen Bauplänen (ohne doppelte), sortiert.</summary>
    static List<long> PartCells(Project p, IEnumerable<Part> parts)
    {
        var set = new HashSet<long>();
        foreach (var t in parts)
        {
            var b = t.Box; var m = t.Mask; int w = b[3] - b[0] + 1, d = b[5] - b[2] + 1, h = b[4] - b[1] + 1;
            for (int y = 0; y < h; y++) for (int z = 0; z < d; z++) for (int x = 0; x < w; x++)
            {
                long mi = ((long)y * d + z) * w + x; if (mi >= m.LongLength || m[mi] == 0) continue;
                int px = b[0] + x, py = b[1] + y, pz = b[2] + z;
                if (px < 0 || py < 0 || pz < 0 || px >= p.W || py >= p.H || pz >= p.D) continue;
                long i = p.Index(px, py, pz); int v = p.Cells[i];
                if (v != 0 && Items(v).Length > 0) set.Add(i);
            }
        }
        var l = set.ToList(); l.Sort(); return l;
    }

    public sealed class Payload
    {
        public JsonObject Update = new(), Plans = new();
        public string ProjectId = "", Me = "", LogText = "";
        public int Tasks, Goals, Materials; public long Blocks;
    }

    /// <summary>Alles, was in die Datenbank geschrieben wird – ohne zu senden (für die Vorschau und zum Senden).</summary>
    public static Payload Build(Project p, PlanData pl, Options o, List<Group>? groups = null)
    {
        groups ??= Groups(p, pl, o);
        var res = new Payload { ProjectId = NewId(), Me = o.Me };
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), WD = (long)p.W * p.D;
        string pid = res.ProjectId; var upd = res.Update;
        // Positionen passen in eine Java-int-Zahl? Sonst nur Bereiche, kein Bauplan.
        bool plans = o.WithPlace && (long)p.W * p.D * p.H <= int.MaxValue;
        (int x, int y, int z) Pos(long i) => ((int)(i % p.W), (int)(i / WD), (int)(i / p.W % p.D));
        JsonObject Box(IEnumerable<long> cells)
        {
            int x1 = int.MaxValue, y1 = int.MaxValue, z1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue, z2 = int.MinValue;
            foreach (var c in cells) { var (x, y, z) = Pos(c); x1 = Math.Min(x1, x); x2 = Math.Max(x2, x); y1 = Math.Min(y1, y); y2 = Math.Max(y2, y); z1 = Math.Min(z1, z); z2 = Math.Max(z2, z); }
            return new JsonObject { ["x1"] = p.OriginX + x1, ["z1"] = p.OriginZ + z1, ["x2"] = p.OriginX + x2, ["z2"] = p.OriginZ + z2, ["y1"] = p.OriginY + y1, ["y2"] = p.OriginY + y2 };
        }

        var proj = new JsonObject
        {
            ["name"] = o.Name, ["ersteller"] = o.Me, ["erstellt"] = now,
            ["schematic"] = new JsonObject { ["datei"] = "Blockplan: " + p.Name, ["groesse"] = $"{p.W}x{p.H}x{p.D}" },
        };
        if (o.Who.Length > 0) proj["wer"] = o.Who;
        var used = new List<long>();
        foreach (var g in groups) used.AddRange(g.Cells);
        if (o.WithPlace && used.Count > 0) { proj["bereich"] = Box(used); proj["dim"] = o.Dim; }
        upd["projekte/" + pid] = proj;

        var ids = new string[groups.Count];
        for (int gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi]; string tid = NewId() + "s" + gi; ids[gi] = tid;
            // je Item: Felder und Anzahl (Doppelstufen zählen doppelt)
            var byItem = new Dictionary<string, (List<long> cells, int n)>();
            foreach (var c in g.Cells)
                foreach (var (id, n) in Items(p.Cells[c]))
                {
                    if (!byItem.TryGetValue(id, out var e)) e = (new List<long>(), 0);
                    e.cells.Add(c); byItem[id] = (e.cells, e.n + n);
                }
            int j = 0;
            foreach (var (id, (cells, n)) in byItem.OrderByDescending(kv => kv.Value.n))
            {
                string gid = j == 0 ? tid : $"{tid}c{j}", full = "minecraft:" + id;
                var goal = new JsonObject
                {
                    ["status"] = "offen", ["wer"] = o.Who, ["projekt"] = pid, ["dim"] = o.WithPlace ? o.Dim : "", ["art"] = o.Art,
                    ["fortschritt"] = 0, ["ersteller"] = o.Me, ["erstellt"] = now + gi,
                    ["item"] = full, ["itemName"] = NameEn(id), ["ziel"] = n,
                };
                if (o.WithPlace && o.Art != "sammeln")
                {
                    var box = Box(cells);
                    goal["bereich"] = box; goal["modus"] = "bestand";
                    goal["x"] = (int)Math.Round(((int)box["x1"]! + (int)box["x2"]!) / 2.0); goal["y"] = (int)box["y1"]!; goal["z"] = (int)Math.Round(((int)box["z1"]! + (int)box["z2"]!) / 2.0);
                    if (plans)
                    {
                        goal["plan"] = new JsonObject { ["x"] = p.OriginX, ["y"] = p.OriginY, ["z"] = p.OriginZ, ["b"] = p.W, ["l"] = p.D, ["rot"] = 0 };
                        // Feld-Index im Projekt ((y*D+z)*W+x) ist genau Bautrupps Nummer x + b * (z + l * y)
                        res.Plans[gid] = string.Join(",", cells.Select(B36));
                    }
                }
                if (j == 0)
                {
                    goal["titel"] = g.Title;
                    if (g.After >= 0) goal["nachAufgabe"] = ids[g.After];
                }
                else { goal["titel"] = $"{g.Title}: {Label(id)}"; goal["oberaufgabe"] = tid; }
                upd["aufgaben/" + gid] = goal;
                res.Goals++; res.Blocks += n; j++;
            }
            res.Tasks++;
        }

        // Was in keiner Aufgabe steckt, kommt als Zusatz in die Materialliste.
        if (o.RestAsMaterial)
        {
            var inTask = new HashSet<long>(used); var rest = new Dictionary<string, long>();
            for (long i = 0; i < p.Cells.LongLength; i++)
            {
                int v = p.Cells[i]; if (v == 0 || inTask.Contains(i)) continue;
                foreach (var (id, n) in Items(v)) rest[id] = rest.GetValueOrDefault(id) + n;
            }
            int k = 0;
            foreach (var (id, n) in rest.OrderByDescending(kv => kv.Value))
            {
                var full = "minecraft:" + id;
                upd[$"material/{NewId()}m{k++}"] = new JsonObject { ["item"] = NameEn(id), ["itemId"] = full, ["need"] = n, ["have"] = 0, ["zusatz"] = true, ["ersteller"] = o.Me, ["projekt"] = pid };
                res.Materials++;
            }
        }
        res.LogText = res.Tasks > 0
            ? $"hat das Projekt \"{o.Name}\" aus Blockplan angelegt ({res.Tasks} Aufgaben, {res.Goals} Ziele)"
            : $"hat das Projekt \"{o.Name}\" aus Blockplan angelegt (nur Materialliste)";
        return res;
    }
}
