using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using SkiaSharp;

namespace Blockplan.Core;

/// <summary>Seitentexturen eines Blocks (Namen der Texturen).</summary>
public sealed record Faces(string? Top, string? Side, string? Bottom, string? Cross);

/// <summary>Original-Texturen aus der Minecraft-Spieldatei (.jar). Bleiben nur auf diesem PC.</summary>
public sealed class McAssets
{
    public const int T = 16; // Kantenlänge einer Kachel
    public Dictionary<string, byte[]> Tex { get; } = new();          // Name → RGBA 16×16
    public Dictionary<string, Faces> Faces { get; } = new();          // Block-Key → Texturen
    public Dictionary<string, string> NamesDe { get; } = new();       // Block-Key → deutscher Name
    public string Version { get; private set; } = "";

    static readonly Dictionary<string, (int r, int g, int b)> Tints = new()
    {
        ["grass_block_top"] = (145, 189, 89), ["short_grass"] = (145, 189, 89), ["tall_grass_top"] = (145, 189, 89), ["tall_grass_bottom"] = (145, 189, 89),
        ["fern"] = (145, 189, 89), ["large_fern_top"] = (145, 189, 89), ["large_fern_bottom"] = (145, 189, 89), ["sugar_cane"] = (145, 189, 89),
        ["vine"] = (119, 171, 47), ["oak_leaves"] = (119, 171, 47), ["jungle_leaves"] = (119, 171, 47), ["acacia_leaves"] = (119, 171, 47),
        ["dark_oak_leaves"] = (119, 171, 47), ["mangrove_leaves"] = (146, 193, 98), ["birch_leaves"] = (128, 167, 85), ["spruce_leaves"] = (97, 153, 97),
        ["lily_pad"] = (32, 128, 48), ["water_still"] = (63, 118, 228), ["water_flow"] = (63, 118, 228), ["grass_block_side_overlay"] = (145, 189, 89),
    };

    /* ---------- Spieldatei finden oder herunterladen ---------- */

    public static string MinecraftDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    static int[] VerKey(string s) => s.Split('.', '-').Select(p => int.TryParse(p, out var n) ? n : -1).ToArray();
    static int CmpVer(string a, string b)
    {
        var x = VerKey(a); var y = VerKey(b);
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++) { int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0; if (p != q) return p.CompareTo(q); }
        return 0;
    }

    /// <summary>Neueste Vollversion in .minecraft\versions oder eine schon heruntergeladene Datei.</summary>
    public static string? FindJar()
    {
        var cands = new List<(string ver, string path)>();
        var vd = Path.Combine(MinecraftDir, "versions");
        if (Directory.Exists(vd))
            foreach (var d in Directory.GetDirectories(vd))
            {
                var n = Path.GetFileName(d); var jar = Path.Combine(d, n + ".jar");
                if (File.Exists(jar) && System.Text.RegularExpressions.Regex.IsMatch(n, @"^\d+(\.\d+)+$")) cands.Add((n, jar));
            }
        foreach (var f in Directory.GetFiles(Store.Dir, "minecraft-*.jar"))
            cands.Add((Path.GetFileNameWithoutExtension(f)[10..], f));
        if (cands.Count == 0) return null;
        cands.Sort((a, b) => CmpVer(b.ver, a.ver));
        return cands[0].path;
    }

    /// <summary>Lädt die aktuelle Spieldatei direkt von Mojang (einmalig, ca. 40 MB).</summary>
    public static async Task<string> DownloadAsync(IProgress<string>? prog)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        prog?.Report("Frage Mojang nach der neuesten Version …");
        var man = JsonNode.Parse(await http.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"))!;
        var id = (string)man["latest"]!["release"]!;
        var url = man["versions"]!.AsArray().First(v => (string?)v!["id"] == id)!["url"]!.GetValue<string>();
        var vj = JsonNode.Parse(await http.GetStringAsync(url))!;
        var client = vj["downloads"]!["client"]!; var jurl = (string)client["url"]!; long size = (long?)client["size"] ?? 0;
        var dest = Path.Combine(Store.Dir, "minecraft-" + id + ".jar"); var tmp = dest + ".part";
        using (var resp = await http.GetAsync(jurl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var fs = File.Create(tmp);
            var buf = new byte[1 << 16]; long got = 0; int n; var last = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n)); got += n;
                if ((DateTime.Now - last).TotalMilliseconds > 200) { last = DateTime.Now; prog?.Report(size > 0 ? $"Lade Minecraft {id} von Mojang … {got * 100 / size} %" : $"Lade Minecraft {id} … {got / 1_000_000} MB"); }
            }
        }
        File.Move(tmp, dest, true);
        return dest;
    }

    /// <summary>Deutsche Blocknamen: aus .minecraft\assets oder einmalig von Mojang, dann zwischengespeichert.</summary>
    public async Task LoadGermanAsync()
    {
        var cache = Path.Combine(Store.Dir, "de_de-" + Version + ".json");
        try
        {
            string? json = File.Exists(cache) ? await File.ReadAllTextAsync(cache) : null;
            if (json == null)
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                JsonNode? vj = null;
                var local = Path.Combine(MinecraftDir, "versions", Version, Version + ".json");
                if (File.Exists(local)) vj = JsonNode.Parse(await File.ReadAllTextAsync(local));
                if (vj == null)
                {
                    var man = JsonNode.Parse(await http.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"))!;
                    var e = man["versions"]!.AsArray().FirstOrDefault(v => (string?)v!["id"] == Version) ?? man["versions"]!.AsArray().First(v => (string?)v!["id"] == (string?)man["latest"]!["release"]);
                    vj = JsonNode.Parse(await http.GetStringAsync((string)e!["url"]!));
                }
                var ai = vj!["assetIndex"]!; var aid = (string)ai["id"]!;
                var idxLocal = Path.Combine(MinecraftDir, "assets", "indexes", aid + ".json");
                var idx = JsonNode.Parse(File.Exists(idxLocal) ? await File.ReadAllTextAsync(idxLocal) : await http.GetStringAsync((string)ai["url"]!))!;
                var hash = (string)idx["objects"]!["minecraft/lang/de_de.json"]!["hash"]!;
                var objLocal = Path.Combine(MinecraftDir, "assets", "objects", hash[..2], hash);
                json = File.Exists(objLocal) ? await File.ReadAllTextAsync(objLocal) : await http.GetStringAsync($"https://resources.download.minecraft.net/{hash[..2]}/{hash}");
                await File.WriteAllTextAsync(cache, json);
            }
            if (JsonNode.Parse(json) is JsonObject dob)
                foreach (var kv in dob) if (kv.Key.StartsWith("block.minecraft.") && kv.Key.Count(c => c == '.') == 2) NamesDe[kv.Key[16..]] = (string?)kv.Value ?? "";
        }
        catch { /* ohne Netz: englische Namen */ }
    }

    /* ---------- Spieldatei auslesen ---------- */

    public static McAssets Load(string jarPath, IProgress<string>? prog = null)
    {
        var a = new McAssets { Version = Path.GetFileNameWithoutExtension(jarPath).Replace("minecraft-", "") };
        prog?.Report("Lese Texturen aus " + Path.GetFileName(jarPath) + " …");
        using var zip = ZipFile.OpenRead(jarPath);
        var bs = new Dictionary<string, JsonNode>(); var models = new Dictionary<string, JsonNode>();
        JsonNode? de = null;
        foreach (var e in zip.Entries)
        {
            var n = e.FullName;
            if (!n.StartsWith("assets/minecraft/")) continue;
            if (n.StartsWith("assets/minecraft/textures/block/") && n.EndsWith(".png") && n.IndexOf('/', 32) < 0)
            {
                var name = Path.GetFileNameWithoutExtension(n);
                using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                var px = Decode(ms.ToArray(), name); if (px != null) a.Tex[name] = px;
            }
            else if (n.StartsWith("assets/minecraft/blockstates/") && n.EndsWith(".json")) { try { bs[Path.GetFileNameWithoutExtension(n)] = Parse(e); } catch { } }
            else if (n.StartsWith("assets/minecraft/models/block/") && n.EndsWith(".json")) { try { models[Path.GetFileNameWithoutExtension(n)] = Parse(e); } catch { } }
            else if (n == "assets/minecraft/lang/de_de.json") { try { de = Parse(e); } catch { } }
        }
        a.ResolveFaces(bs, models);
        if (de is JsonObject dob)
            foreach (var kv in dob) if (kv.Key.StartsWith("block.minecraft.") && kv.Key.Count(c => c == '.') == 2) a.NamesDe[kv.Key[16..]] = (string?)kv.Value ?? "";
        return a;
    }

    static JsonNode Parse(ZipArchiveEntry e) { using var s = e.Open(); return JsonNode.Parse(s)!; }

    static byte[]? Decode(byte[] png, string name)
    {
        using var bm = SKBitmap.Decode(png); if (bm == null) return null;
        int s = bm.Width; var px = new byte[T * T * 4];
        Tints.TryGetValue(name, out var t); bool tint = t.r != 0;
        for (int y = 0; y < T; y++) for (int x = 0; x < T; x++)
        {
            var c = bm.GetPixel(x * s / T, y * s / T); int o = (y * T + x) * 4; // animiert: erstes Bild
            px[o] = tint ? (byte)(c.Red * t.r / 255) : c.Red; px[o + 1] = tint ? (byte)(c.Green * t.g / 255) : c.Green;
            px[o + 2] = tint ? (byte)(c.Blue * t.b / 255) : c.Blue; px[o + 3] = c.Alpha;
        }
        return px;
    }

    void ResolveFaces(Dictionary<string, JsonNode> bs, Dictionary<string, JsonNode> models)
    {
        static string Str(JsonNode? s) => s is JsonValue v && v.TryGetValue<string>(out var t) ? t : (s is JsonObject o && o["sprite"] is JsonValue sv && sv.TryGetValue<string>(out var sp) ? sp : "");
        static string Nm(string s) => s.Replace("minecraft:", "").Replace("block/", "");
        var cache = new Dictionary<string, (Dictionary<string, string> t, List<string> par)>();
        (Dictionary<string, string> t, List<string> par) Model(string name, int depth = 0)
        {
            name = Nm(name); if (cache.TryGetValue(name, out var c)) return c;
            var m = (t: new Dictionary<string, string>(), par: new List<string>());
            if (depth < 24 && models.TryGetValue(name, out var j))
            {
                var parent = Str(j["parent"]);
                if (parent.Length > 0) { var pm = Model(parent, depth + 1); foreach (var kv in pm.t) m.t[kv.Key] = kv.Value; m.par.Add(Nm(parent)); m.par.AddRange(pm.par); }
                if (j["textures"] is JsonObject tx) foreach (var kv in tx) m.t[kv.Key] = Str(kv.Value);
            }
            return cache[name] = m;
        }
        foreach (var (id, j) in bs)
        {
            string? reff = null;
            try
            {
                if (j["variants"] is JsonObject vars) { var v = vars.First().Value; reff = Str((v is JsonArray arr ? arr[0] : v)!["model"]); }
                else if (j["multipart"] is JsonArray mp) { var ap = mp[0]!["apply"]; reff = Str((ap is JsonArray arr ? arr[0] : ap)!["model"]); }
            }
            catch { }
            if (string.IsNullOrEmpty(reff)) continue;
            var m = Model(reff);
            string? R(string k) { m.t.TryGetValue(k, out var v); int g = 0; while (v != null && v.StartsWith('#') && g++ < 8) m.t.TryGetValue(v[1..], out v); return string.IsNullOrEmpty(v) ? null : Nm(v); }
            var top = R("top") ?? R("end") ?? R("all") ?? R("texture") ?? R("cross") ?? R("plant") ?? R("torch") ?? R("side") ?? R("particle");
            var side = R("side") ?? R("all") ?? R("texture") ?? R("front") ?? R("cross") ?? R("plant") ?? R("torch") ?? top;
            bool isCross = m.par.Any(p => p.Contains("cross") || p.Contains("torch"));
            Faces[id] = new Faces(top, side, R("bottom") ?? R("end") ?? top, isCross ? (R("cross") ?? R("plant") ?? R("torch") ?? top) : null);
        }
    }

    /// <summary>Deutsche Namen an die Blockliste hängen.</summary>
    public void ApplyNames()
    {
        for (int i = 1; i < Blocks.Count; i++) { var b = Blocks.All[i]; if (b != null && b.Props == null && NamesDe.TryGetValue(b.Key, out var n)) b.NameDe = n; }
    }

    /// <summary>Texturen für die 6 Seiten (+x,-x,+y,-y,+z,-z) eines Blockzustands oder null.</summary>
    public byte[]?[]? FacesOf(int v)
    {
        var b = Blocks.Get(v); var B = Blocks.Get(Blocks.BaseOf(v));
        if (!Faces.TryGetValue(B.Key, out var f)) return null;
        byte[]? G(string? n) => n != null && Tex.TryGetValue(n, out var t) ? t : null;
        var top = G(f.Top) ?? G(f.Side); var side = G(f.Side) ?? top; var bot = G(f.Bottom) ?? top ?? side;
        if (top == null && side == null) return null;
        string axis = b.Props != null && b.Props.TryGetValue("axis", out var ax) ? ax : "y";
        if (axis == "x") return new[] { top, top, side, side, side, side };
        if (axis == "z") return new[] { side, side, side, side, top, top };
        return new[] { side, side, top, bot, side, side };
    }

    public byte[]? CrossOf(int v)
    {
        var B = Blocks.Get(Blocks.BaseOf(v));
        if (!Faces.TryGetValue(B.Key, out var f)) return null;
        var n = f.Cross ?? f.Side ?? f.Top; return n != null && Tex.TryGetValue(n, out var t) ? t : null;
    }
}
