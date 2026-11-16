using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Blockplan.Core;

/*
 * Baupläne, Etappen, To-dos, Hilfslinien und Punkte – im selben Format wie im Blockplan-Artefakt.
 * Die Einträge sind dünne Hüllen um die JSON-Objekte in Project.Extra, damit nichts verloren geht,
 * was das Artefakt sonst noch speichert, und Exporte kompatibel bleiben.
 */

public abstract class JItem
{
    public readonly JsonObject J;
    protected JItem(JsonObject j) { J = j; }
    protected string S(string k, string d = "") => J[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : d;
    protected double D(string k, double d = 0) => Num(J[k], d);
    /// <summary>Zahl lesen, egal ob aus einer Datei geladen (JsonElement) oder neu angelegt (int, long, double).</summary>
    public static double Num(JsonNode? n, double d = 0)
    {
        if (n is not JsonValue v) return d;
        if (v.TryGetValue<double>(out var x)) return x;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<float>(out var f)) return f;
        return d;
    }
    protected bool B(string k, bool d = false) => J[k] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d;
    public string Id { get => S("id"); set => J["id"] = value; }
    public static string NewId(string pre) => pre + Project.B36(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) + Project.B36(Random.Shared.Next(1296));
}

public sealed class Cat : JItem
{
    public Cat(JsonObject j) : base(j) { }
    public string Name { get => S("name", "Kategorie"); set => J["name"] = value; }
    public bool Base => B("base");
}

public sealed class Part : JItem
{
    public Part(JsonObject j) : base(j) { }
    public string Cat { get => S("cat"); set => J["cat"] = value; }
    public string Name { get => S("name"); set => J["name"] = value; }
    public int No { get => (int)D("no", 1); set => J["no"] = value; }
    public string Sub => S("sub");
    public bool Base => B("base");
    public long N { get => (long)D("n"); set => J["n"] = value; }
    public int[] Box
    {
        get => J["b"] is JsonArray a && a.Count == 6 ? a.Select(x => (int)Num(x)).ToArray() : new int[6];
        set => J["b"] = new JsonArray(value.Select(x => (JsonNode)x).ToArray());
    }
    byte[]? mask;
    /// <summary>1 = Feld gehört zum Bauplan; Reihenfolge ((y-y0)*Tiefe + (z-z0))*Breite + (x-x0).</summary>
    public byte[] Mask
    {
        get
        {
            if (mask != null) return mask;
            var b = Box; long len = (long)(b[3] - b[0] + 1) * (b[4] - b[1] + 1) * (b[5] - b[2] + 1);
            var u = Project.UnRleBytes(S("m"), len); return mask = u;
        }
        set { mask = value; J["m"] = Project.RleBytes(value); }
    }
}

public sealed class Stage : JItem
{
    public Stage(JsonObject j) : base(j) { }
    public string Name { get => S("name", "Etappe"); set => J["name"] = value; }
    public bool Done { get => B("done"); set => J["done"] = value; }
    public List<string> Parts
    {
        get => J["parts"] is JsonArray a ? a.Select(x => (string?)x ?? "").Where(x => x.Length > 0).ToList() : new List<string>();
        set => J["parts"] = new JsonArray(value.Select(x => (JsonNode)x).ToArray());
    }
}

public sealed class Todo : JItem
{
    public Todo(JsonObject j) : base(j) { }
    public string Text { get => S("text"); set => J["text"] = value; }
    public string Who { get => S("who"); set => J["who"] = value; }
    public string Ref { get => S("ref"); set => J["ref"] = value; }
    public string By { get => S("by"); set => J["by"] = value; }
    public long At { get => (long)D("at"); set => J["at"] = value; }
    public bool Done { get => B("done"); set => J["done"] = value; }
    public string DoneBy { get => S("doneBy"); set => J["doneBy"] = value; }
}

public sealed class Guide : JItem
{
    public Guide(JsonObject j) : base(j) { }
    public string Name { get => S("name"); set => J["name"] = value; }
    /// <summary>ellipse, circle, rect, line</summary>
    public string Type { get => S("type", "ellipse"); set => J["type"] = value; }
    public double Cx { get => D("cx"); set => J["cx"] = value; }
    public double Cz { get => D("cz"); set => J["cz"] = value; }
    public double A { get => D("a", 20); set => J["a"] = value; }
    public double Bv { get => D("b", 20); set => J["b"] = value; }
    public double X2 { get => D("x2"); set => J["x2"] = value; }
    public double Z2 { get => D("z2"); set => J["z2"] = value; }
    public string Color { get => S("color", "#7ee08f"); set => J["color"] = value; }
    public bool On { get => B("on", true); set => J["on"] = value; }

    /// <summary>Felder (x, z), die die Linie im Raster berührt – so wie man sie in Minecraft setzen würde.</summary>
    public List<(int x, int z)> Cells()
    {
        var set = new HashSet<(int, int)>(); var r = new List<(int, int)>();
        void Add(double x, double z) { var c = ((int)Math.Floor(x), (int)Math.Floor(z)); if (set.Add(c)) r.Add(c); }
        if (Type == "line")
        {
            double len = Math.Max(Math.Abs(X2 - Cx), Math.Abs(Z2 - Cz)); int n = (int)Math.Ceiling(len * 2) + 1;
            for (int i = 0; i <= n; i++) { double t = n == 0 ? 0 : i / (double)n; Add(Cx + .5 + (X2 - Cx) * t, Cz + .5 + (Z2 - Cz) * t); }
        }
        else if (Type == "rect")
        {
            double x0 = Cx - A / 2, x1 = Cx + A / 2 - 1, z0 = Cz - Bv / 2, z1 = Cz + Bv / 2 - 1;
            for (double x = x0; x <= x1 + .001; x++) { Add(x + .5, z0 + .5); Add(x + .5, z1 + .5); }
            for (double z = z0; z <= z1 + .001; z++) { Add(x0 + .5, z + .5); Add(x1 + .5, z + .5); }
        }
        else
        {
            double ra = (A - 1) / 2, rb = ((Type == "circle" ? A : Bv) - 1) / 2; int n = (int)Math.Ceiling(Math.PI * (ra + rb) * 4) + 8;
            for (int i = 0; i < n; i++) { double t = i * 2 * Math.PI / n; Add(Cx + ra * Math.Cos(t), Cz + rb * Math.Sin(t)); }
        }
        return r;
    }
}

public sealed class PlanPoint : JItem
{
    public PlanPoint(JsonObject j) : base(j) { }
    public string Name { get => S("name", "Punkt"); set => J["name"] = value; }
    public int X { get => (int)D("x"); set => J["x"] = value; }
    public int Z { get => (int)D("z"); set => J["z"] = value; }
}

/// <summary>Zugriff auf alle Planungsdaten eines Projekts.</summary>
public sealed class PlanData
{
    readonly Project p;
    public PlanData(Project project) { p = project; }

    JsonArray Arr(string k) { if (p.Extra[k] is not JsonArray a) { a = new JsonArray(); p.Extra[k] = a; } return a; }
    IEnumerable<JsonObject> Objs(string k) => Arr(k).OfType<JsonObject>();

    public List<Cat> Cats => Objs("cats").Select(o => new Cat(o)).ToList();
    public List<Part> Parts => Objs("parts").Select(o => new Part(o)).ToList();
    public List<Stage> Stages => Objs("stages").Select(o => new Stage(o)).ToList();
    public List<Todo> Todos => Objs("todos").Select(o => new Todo(o)).ToList();
    public List<Guide> Guides => Objs("guides").Select(o => new Guide(o)).ToList();
    public List<PlanPoint> Points => Objs("points").Select(o => new PlanPoint(o)).ToList();

    public Cat AddCat(string name) { var o = new JsonObject { ["id"] = JItem.NewId("c"), ["name"] = name, ["hint"] = "Von Hand ausgewählt", ["manual"] = true }; Arr("cats").Add(o); return new Cat(o); }
    public Stage AddStage(string name) { var o = new JsonObject { ["id"] = JItem.NewId("s"), ["name"] = name, ["parts"] = new JsonArray(), ["done"] = false }; Arr("stages").Add(o); return new Stage(o); }
    public Todo AddTodo(string text, string who, string rf, string by)
    {
        var o = new JsonObject { ["id"] = JItem.NewId("t"), ["text"] = text, ["who"] = who, ["ref"] = rf, ["by"] = by, ["at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), ["done"] = false };
        Arr("todos").Insert(0, o); return new Todo(o);
    }
    public Guide AddGuide(string type, double cx, double cz, double size)
    {
        var cols = new[] { "#7ee08f", "#ffd23a", "#5ab4ff", "#ff6b9a", "#c08bff", "#ff9f43" };
        var o = new JsonObject
        {
            ["id"] = JItem.NewId("g"), ["type"] = type, ["name"] = type switch { "circle" => "Kreis", "rect" => "Rechteck", "line" => "Linie", _ => "Ellipse" },
            ["cx"] = Math.Round(cx), ["cz"] = Math.Round(cz), ["a"] = size, ["b"] = type == "ellipse" ? Math.Round(size * .6) : size,
            ["x2"] = Math.Round(cx + size), ["z2"] = Math.Round(cz), ["h"] = 12, ["color"] = cols[Arr("guides").Count % cols.Length], ["on"] = true,
        };
        Arr("guides").Add(o); return new Guide(o);
    }
    public PlanPoint AddPoint(string name, int x, int z) { var o = new JsonObject { ["id"] = JItem.NewId("pt"), ["name"] = name, ["x"] = x, ["z"] = z }; Arr("points").Add(o); return new PlanPoint(o); }

    public void Remove(string key, JItem it) { var a = Arr(key); a.Remove(it.J); }
    public void Move(string key, JItem it, int delta)
    {
        var a = Arr(key); int i = a.IndexOf(it.J), j = Math.Clamp(i + delta, 0, a.Count - 1); if (i < 0 || i == j) return;
        a.RemoveAt(i); a.Insert(j, it.J);
    }

    /// <summary>Neuer Bauplan aus einem Bereich (Projektkoordinaten), nur Felder mit Blöcken.</summary>
    public Part? AddPart(string catId, string name, int x0, int y0, int z0, int x1, int y1, int z1)
    {
        x0 = Math.Clamp(x0, 0, p.W - 1); x1 = Math.Clamp(x1, 0, p.W - 1); z0 = Math.Clamp(z0, 0, p.D - 1); z1 = Math.Clamp(z1, 0, p.D - 1); y0 = Math.Clamp(y0, 0, p.H - 1); y1 = Math.Clamp(y1, 0, p.H - 1);
        int w = x1 - x0 + 1, d = z1 - z0 + 1, h = y1 - y0 + 1; var m = new byte[(long)w * d * h]; long n = 0;
        int bx0 = int.MaxValue, by0 = int.MaxValue, bz0 = int.MaxValue, bx1 = -1, by1 = -1, bz1 = -1;
        for (int y = 0; y < h; y++) for (int z = 0; z < d; z++) for (int x = 0; x < w; x++)
            if (p.At(x0 + x, y0 + y, z0 + z) != 0)
            {
                m[((long)y * d + z) * w + x] = 1; n++;
                bx0 = Math.Min(bx0, x); bx1 = Math.Max(bx1, x); by0 = Math.Min(by0, y); by1 = Math.Max(by1, y); bz0 = Math.Min(bz0, z); bz1 = Math.Max(bz1, z);
            }
        if (n == 0) return null;
        // auf den belegten Bereich verkleinern
        int nw = bx1 - bx0 + 1, nd = bz1 - bz0 + 1, nh = by1 - by0 + 1; var m2 = new byte[(long)nw * nd * nh];
        for (int y = 0; y < nh; y++) for (int z = 0; z < nd; z++) for (int x = 0; x < nw; x++) m2[((long)y * nd + z) * nw + x] = m[((long)(y + by0) * d + z + bz0) * w + x + bx0];
        int no = Parts.Where(t => t.Cat == catId).Select(t => t.No).DefaultIfEmpty(0).Max() + 1;
        var o = new JsonObject { ["id"] = JItem.NewId("m"), ["cat"] = catId, ["name"] = name, ["no"] = no, ["sub"] = "", ["simOf"] = "", ["simHow"] = "", ["manual"] = true, ["base"] = false, ["n"] = n };
        var part = new Part(o) { Box = new[] { x0 + bx0, y0 + by0, z0 + bz0, x0 + bx1, y0 + by1, z0 + bz1 } };
        part.Mask = m2;
        Arr("parts").Add(o); p.Extra["partsDone"] = true;
        return part;
    }

    public string Label(Part t)
    {
        if (t.Name.Length > 0) return t.Name;
        var c = Cats.FirstOrDefault(c => c.Id == t.Cat);
        return (c?.Name ?? "Bauteil") + " " + t.No + (t.Sub.Length > 0 ? " · " + t.Sub : "");
    }

    /// <summary>Blöcke eines oder mehrerer Baupläne, nach Grundblock gezählt.</summary>
    public Dictionary<int, long> Material(IEnumerable<Part> parts)
    {
        var r = new Dictionary<int, long>();
        foreach (var t in parts)
        {
            var b = t.Box; var m = t.Mask; int w = b[3] - b[0] + 1, d = b[5] - b[2] + 1, h = b[4] - b[1] + 1;
            for (int y = 0; y < h; y++) for (int z = 0; z < d; z++) for (int x = 0; x < w; x++)
            {
                long mi = ((long)y * d + z) * w + x; if (mi >= m.LongLength || m[mi] == 0) continue;
                int v = p.At(b[0] + x, b[1] + y, b[2] + z); if (v == 0) continue;
                int bb = Blocks.BaseOf(v); r[bb] = r.GetValueOrDefault(bb) + 1;
            }
        }
        return r;
    }
}
