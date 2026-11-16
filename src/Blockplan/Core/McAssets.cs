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
    public Dictionary<string, JsonNode> BlockStates { get; private set; } = new();   // blockstates/*.json
    public Dictionary<string, (int w, int h, byte[] px)> EntityTex { get; } = new();   // textures/entity/… (Objekte)

    /// <summary>Objekt-Texturen, die Blockplan für Blöcke ohne Blockmodell braucht.</summary>
    static bool EntityWanted(string n) =>
        n.StartsWith("chest/") || n.StartsWith("banner/") || n.StartsWith("shulker/") || n.StartsWith("bell/") || n.StartsWith("decorated_pot/")
        || n is "skeleton/skeleton" or "skeleton/wither_skeleton" or "zombie/zombie" or "creeper/creeper" or "piglin/piglin" or "player/wide/steve"
            or "enderdragon/dragon" or "conduit/base" or "end_portal/end_portal"
        || (n.StartsWith("copper_golem/copper_golem") && !n.Contains("eyes"));

    public static bool IsPretinted(string tex) => Tints.ContainsKey(tex);
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> tinted = new();
    /// <summary>Block-Textur in einer Farbe eingefärbt (Laub, Gras, Redstone …).</summary>
    public byte[]? Tinted(string name, uint c) => !Tex.TryGetValue(name, out var src) ? null : tinted.GetOrAdd(name + "|" + c, _ =>
    {
        var px = (byte[])src.Clone();
        for (int i = 0; i < px.Length; i += 4) { px[i] = (byte)(px[i] * ((c >> 16) & 255) / 255); px[i + 1] = (byte)(px[i + 1] * ((c >> 8) & 255) / 255); px[i + 2] = (byte)(px[i + 2] * (c & 255) / 255); }
        return px;
    });
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> regions = new();

    /// <summary>
    /// Banner-Bild wie im Spiel: Grundfläche (banner/base) in der Bannerfarbe, darüber jede Musterebene
    /// (banner/&lt;muster&gt;, weiß mit Deckkraft) in ihrer Farbe. Ergebnis als Objekt-Textur „banner/composed:…“.
    /// </summary>
    public string ComposeBanner(uint baseTint, string patterns, Func<string, uint> dye)
    {
        var name = $"banner/composed:{baseTint:x6}|{patterns}";
        lock (EntityTex)
        {
            if (EntityTex.ContainsKey(name) || !EntityTex.TryGetValue("banner/base", out var bt)) return EntityTex.ContainsKey(name) ? name : "banner/base";
            var px = new byte[bt.px.Length]; int n = bt.w * bt.h;
            for (int i = 0; i < n; i++)
            {
                px[i * 4] = (byte)(bt.px[i * 4] * ((baseTint >> 16) & 255) / 255); px[i * 4 + 1] = (byte)(bt.px[i * 4 + 1] * ((baseTint >> 8) & 255) / 255);
                px[i * 4 + 2] = (byte)(bt.px[i * 4 + 2] * (baseTint & 255) / 255); px[i * 4 + 3] = bt.px[i * 4 + 3];
            }
            foreach (var part in patterns.Split('|', StringSplitOptions.RemoveEmptyEntries))
            {
                var pc = part.Split(':'); if (pc.Length != 2 || !EntityTex.TryGetValue("banner/" + pc[0], out var pt) || pt.w != bt.w) continue;
                uint c = dye(pc[1]);
                for (int i = 0; i < n; i++)
                {
                    float a = pt.px[i * 4 + 3] / 255f; if (a <= 0) continue;
                    px[i * 4] = (byte)(px[i * 4] * (1 - a) + pt.px[i * 4] * ((c >> 16) & 255) / 255f * a);
                    px[i * 4 + 1] = (byte)(px[i * 4 + 1] * (1 - a) + pt.px[i * 4 + 1] * ((c >> 8) & 255) / 255f * a);
                    px[i * 4 + 2] = (byte)(px[i * 4 + 2] * (1 - a) + pt.px[i * 4 + 2] * (c & 255) / 255f * a);
                }
            }
            EntityTex[name] = (bt.w, bt.h, px);
            return name;
        }
    }

    /// <summary>Ausschnitt einer Objekt-Textur als 16×16-Kachel, optional eingefärbt (0xRRGGBB).</summary>
    public byte[]? Region(string tex, int x, int y, int w, int h, uint tint = 0xffffff, int baseW = 64)
    {
        (int w, int h, byte[] px) t;
        lock (EntityTex) { if (!EntityTex.TryGetValue(tex, out t) || w <= 0 || h <= 0) return null; }
        return regions.GetOrAdd($"{tex}|{x}|{y}|{w}|{h}|{tint}|{baseW}", _ =>
        {
            int sc = Math.Max(1, t.w / baseW); var px = new byte[T * T * 4];   // Texturen können größer als 64 px sein (Ressourcenpakete)
            for (int j = 0; j < T; j++) for (int i = 0; i < T; i++)
            {
                int sx = Math.Clamp((x + i * w / T) * sc, 0, t.w - 1), sy = Math.Clamp((y + j * h / T) * sc, 0, t.h - 1); int so = (sy * t.w + sx) * 4, d = (j * T + i) * 4;
                px[d] = (byte)(t.px[so] * ((tint >> 16) & 255) / 255); px[d + 1] = (byte)(t.px[so + 1] * ((tint >> 8) & 255) / 255);
                px[d + 2] = (byte)(t.px[so + 2] * (tint & 255) / 255); px[d + 3] = t.px[so + 3];
            }
            return px;
        });
    }
    public Dictionary<string, JsonNode> Models { get; private set; } = new();        // models/block/*.json
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
    public static string? FindJar() => FindJars().FirstOrDefault();

    /// <summary>Alle passenden Spieldateien, neueste zuerst.</summary>
    public static List<string> FindJars()
    {
        var cands = new List<(string ver, string path)>();
        string app = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        void VersionsDir(string vd)
        {   // Ordnerstruktur wie .minecraft\versions\<version>\<version>.jar
            if (!Directory.Exists(vd)) return;
            try { foreach (var d in Directory.GetDirectories(vd)) { var n = Path.GetFileName(d); var jar = Path.Combine(d, n + ".jar"); if (File.Exists(jar)) cands.Add((n, jar)); } } catch { }
        }
        VersionsDir(Path.Combine(MinecraftDir, "versions"));                                   // offizieller Launcher, TLauncher
        VersionsDir(Path.Combine(app, "ModrinthApp", "meta", "versions"));                     // Modrinth
        VersionsDir(Path.Combine(app, "com.modrinth.theseus", "meta", "versions"));
        VersionsDir(Path.Combine(home, "curseforge", "minecraft", "Install", "versions"));    // CurseForge
        VersionsDir(Path.Combine(app, ".minecraft", "versions"));
        foreach (var root in new[] { Path.Combine(app, "PrismLauncher"), Path.Combine(app, "MultiMC"), Path.Combine(app, "PolyMC") })
        {   // Prism/MultiMC: libraries\com\mojang\minecraft\<version>\minecraft-<version>-client.jar
            var lib = Path.Combine(root, "libraries", "com", "mojang", "minecraft");
            if (!Directory.Exists(lib)) continue;
            try { foreach (var d in Directory.GetDirectories(lib)) foreach (var f in Directory.GetFiles(d, "*client*.jar")) cands.Add((Path.GetFileName(d), f)); } catch { }
        }
        try { foreach (var f in Directory.GetFiles(Store.Dir, "minecraft-*.jar")) cands.Add((Path.GetFileNameWithoutExtension(f)[10..], f)); } catch { }
        var dl = Path.Combine(home, "Downloads");                                                // im Browser geladene client.jar
        try { if (Directory.Exists(dl)) foreach (var f in Directory.GetFiles(dl, "*.jar")) cands.Add(("0", f)); } catch { }
        // nur Dateien, in denen wirklich Block-Texturen stecken (Fabric/Forge-Hüllen fallen weg)
        var ok = cands.Where(c => HasTextures(c.path)).GroupBy(c => c.path).Select(g => g.First()).ToList();
        ok.Sort((a, b) => CmpVer(b.ver, a.ver));
        return ok.Select(c => c.path).ToList();
    }

    static bool HasTextures(string jar)
    {
        try { using var z = ZipFile.OpenRead(jar); return z.GetEntry("assets/minecraft/textures/block/stone.png") != null; }
        catch { return false; }
    }

    /// <summary>Adresse der aktuellen Spieldatei für den Download im Browser (falls das Programm selbst nicht durchkommt).</summary>
    public const string FallbackClientUrl = "https://piston-data.mojang.com/v1/objects/e877b6a07acd633fb3bb475002175cec036e7b87/client.jar";

    /// <summary>Kurzes Protokoll unter %LOCALAPPDATA%\Blockplan\blockplan.log, damit Fehler nachvollziehbar sind.</summary>
    public static void Log(string msg)
    {
        try { File.AppendAllText(Path.Combine(Store.Dir, "blockplan.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {msg}{Environment.NewLine}"); } catch { }
    }

    /// <summary>Lädt die aktuelle Spieldatei direkt von Mojang (einmalig, ca. 40 MB).</summary>
    public static async Task<string> DownloadAsync(IProgress<string>? prog)
    {
        try { return await DownloadOnce(prog, false); }
        catch (HttpRequestException e) when (e.InnerException != null)
        {
            Log("Download, erster Versuch: " + e.InnerException);
            prog?.Report("Zweiter Versuch …");
            return await DownloadOnce(prog, true);
        }
    }

    static async Task<string> DownloadOnce(IProgress<string>? prog, bool lenient)
    {
        var handler = new HttpClientHandler { CheckCertificateRevocationList = false };
        if (lenient) handler.SslProtocols = System.Security.Authentication.SslProtocols.Tls12;
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };
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
            else if (n.EndsWith(".png") && n.StartsWith("assets/minecraft/textures/entity/") && EntityWanted(n[33..^4]))
            {   // große Objekt-Texturen (Truhen, Banner) in voller Größe
                using var s = e.Open(); using var ms = new MemoryStream(); s.CopyTo(ms);
                using var bm = SKBitmap.Decode(ms.ToArray());
                if (bm != null)
                {
                    var px = new byte[bm.Width * bm.Height * 4];
                    for (int y = 0; y < bm.Height; y++) for (int x = 0; x < bm.Width; x++) { var c = bm.GetPixel(x, y); int o = (y * bm.Width + x) * 4; px[o] = c.Red; px[o + 1] = c.Green; px[o + 2] = c.Blue; px[o + 3] = c.Alpha; }
                    a.EntityTex[n[33..^4]] = (bm.Width, bm.Height, px);   // z. B. "chest/normal", "banner/base"
                }
            }
            else if (n.StartsWith("assets/minecraft/blockstates/") && n.EndsWith(".json")) { try { bs[Path.GetFileNameWithoutExtension(n)] = Parse(e); } catch { } }
            else if (n.StartsWith("assets/minecraft/models/block/") && n.EndsWith(".json")) { try { models[Path.GetFileNameWithoutExtension(n)] = Parse(e); } catch { } }
            else if (n == "assets/minecraft/lang/de_de.json") { try { de = Parse(e); } catch { } }
        }
        a.ResolveFaces(bs, models);
        a.BlockStates = bs; a.Models = models;
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
