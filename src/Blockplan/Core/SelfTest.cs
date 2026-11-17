using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Blockplan.Core;

/// <summary>Prüfung ohne Fenster: Blockplan --selftest &lt;ordner&gt;</summary>
public static class SelfTest
{
    public static int Run(string dir)
    {
        var sw = Stopwatch.StartNew(); int fail = 0;
        void Ok(bool c, string m) { Console.WriteLine((c ? "OK   " : "FEHL ") + m + $"  ({sw.ElapsedMilliseconds} ms)"); if (!c) fail++; }
        try
        {
            var schem = SchematicReader.Load(Path.Combine(dir, "titanic.schem"));
            Ok(schem.W == 59 && schem.D == 541 && schem.H == 137 && schem.Count == 251542, $"schem {schem.W}x{schem.D}x{schem.H}, {schem.Count} Blöcke, {schem.Types} Sorten, Wasser {schem.Water}");
            var p = SchematicReader.ToProject(schem, schem.X0, schem.Y0, schem.Z0, schem.X1, schem.Y1, schem.Z1, false, false, 0);
            var p2 = SchematicReader.ToProject(schem, 0, 0, 0, schem.W - 1, schem.H - 1, schem.D - 1, false, false, 1);
            Ok(p2.W == schem.D && p2.D == schem.W && p2.Cells.Count(c => c != 0) == schem.Count, "Drehung 90°");

            var js = File.ReadAllText(Path.Combine(dir, "titanic.json"));
            var a = Project.FromJson(js);
            Ok(a.W == 59 && a.Cells.Count(c => c != 0) == 251542 && a.Extra.ContainsKey("parts"), $"Artefakt-JSON: {a.Name}, {a.Extra.Count} Zusatzfelder");
            bool same = true; for (long i = 0; i < a.Cells.LongLength && same; i++) same = Blocks.Get(a.Cells[i] == 0 ? 1 : a.Cells[i]).Key == Blocks.Get(p2.Cells.Length > 0 ? (a.Cells[i] == 0 ? 1 : a.Cells[i]) : 1).Key;
            var db = Path.Combine(Path.GetTempPath(), "bp-test.db"); if (File.Exists(db)) File.Delete(db);
            using (var st = new Store(db))
            {
                st.Save(a); st.Save(p);
                var back = st.Load(a.Id)!;
                Ok(back.Cells.SequenceEqual(a.Cells) && back.Name == a.Name && back.Extra.ToJsonString() == a.Extra.ToJsonString(), $"SQLite speichern/laden, {st.List().Count} Projekte, Datei {new FileInfo(db).Length / 1024} KB");
                st.Set("last", a.Id); Ok(st.Get("last") == a.Id, "Einstellungen");
            }
            var back2 = Project.FromJson(a.ToJson());
            Ok(back2.Cells.SequenceEqual(a.Cells), "JSON hin und zurück");
            var ent = Project.FromJson(File.ReadAllText(Path.Combine(dir, "enterprise.json")));
            Ok(ent.W == 203 && ent.MaxY() > 0, $"Enterprise {ent.W}x{ent.D}x{ent.H}, {ent.CountBlocks().Count} Blockzustände");

            // Schnee entfernen: Decke weg, Gras auftauen, Schneeblock nur auf Wunsch
            var sp = new Project { W = 3, D = 1, H = 2, Cells = new ushort[6] };
            sp.Cells[0] = (ushort)Blocks.Ensure("grass_block[snowy=true]"); sp.Cells[3] = (ushort)Blocks.Ensure("snow[layers=1]");
            sp.Cells[1] = (ushort)Blocks.Ensure("snow_block"); sp.Cells[2] = (ushort)Blocks.Ensure("grass_block[snowy=true]"); sp.Cells[5] = (ushort)Blocks.Ensure("powder_snow");
            long ch = sp.Replace(v => SchematicReader.Desnow(v, false));
            Ok(ch == 4 && Blocks.Get(sp.Cells[0]).Key == "grass_block[snowy=false]" && sp.Cells[3] == 0 && sp.Cells[5] == 0 && Blocks.Get(sp.Cells[1]).Key == "snow_block" && sp.Cells[2] == sp.Cells[0],
               $"Schnee entfernen: {ch} Felder geändert, Gras {Blocks.Get(sp.Cells[0]).Key}, Schneeblock bleibt");
            sp.Replace(v => SchematicReader.Desnow(v, true)); Ok(sp.Cells[1] == 0, "Schneeblöcke auf Wunsch entfernt");
            var bnr = SchematicReader.Load(Path.Combine(dir, "banner_v3.schem"));
            Ok(bnr.Banners.Count == 4, $"Banner-Muster aus Schematic: {bnr.Banners.Count}");
            var bp = SchematicReader.ToProject(bnr, bnr.X0, bnr.Y0, bnr.Z0, bnr.X1, bnr.Y1, bnr.Z1, false, false, 1);
            var bp2 = Project.FromJson(bp.ToJson());
            Ok(bp.Banners.Count == 4 && bp2.Banners.Count == 4 && bp2.Banners.All(kv => Blocks.Get(Blocks.BaseOf(bp2.Cells[kv.Key])).Key.EndsWith("banner")), "Banner-Muster nach Drehung und Speichern am richtigen Block");

            // Planung: Bauplan aus Bereich, Speichern/Laden im Artefakt-Format, Material, Etappen, To-dos, Hilfslinien
            var ep = Project.FromJson(File.ReadAllText(Path.Combine(dir, "enterprise.json")));
            var pd = new PlanData(ep); int before = pd.Parts.Count;
            var cat = pd.AddCat("Test"); var part = pd.AddPart(cat.Id, "Brücke", 80, 0, 80, 120, ep.H - 1, 160);
            var ep2 = Project.FromJson(ep.ToJson()); var pd2 = new PlanData(ep2); var part2 = pd2.Parts.First(t => t.Id == part!.Id);
            long matN = pd2.Material(new[] { part2 }).Values.Sum();
            Ok(part != null && pd2.Parts.Count == before + 1 && part2.Mask.LongLength == part.Mask.LongLength && part2.Mask.SequenceEqual(part.Mask) && matN == part.N,
               $"Bauplan aus Bereich: {part!.N:N0} Blöcke, Maske gespeichert und geladen, Material {matN:N0}");
            var origMat = pd2.Material(pd2.Parts.Where(t => t.Id != part.Id)).Values.Sum();
            Ok(origMat == pd2.Parts.Where(t => t.Id != part.Id).Sum(t => t.N), $"Baupläne aus dem Artefakt: Masken passen zu den Blockzahlen ({origMat:N0})");
            var stg = pd2.AddStage("Test"); stg.Parts = new() { part.Id }; pd2.AddTodo("Testaufgabe", "Tobi", "s:" + stg.Id, "Claude");
            var gd = pd2.AddGuide("circle", 50, 50, 21); var cells = gd.Cells(); gd.Y = 30; gd.H = 5;
            var ep3 = Project.FromJson(ep2.ToJson()); var pd3 = new PlanData(ep3);
            Ok(pd3.Stages.Any(x => x.Parts.Contains(part.Id)) && pd3.Todos[0].Text == "Testaufgabe" && pd3.Guides.Count == 1 && pd3.Guides[0].Y == 30 && pd3.Guides[0].H == 5 && pd3.Guides[0].AtLayer(34) && !pd3.Guides[0].AtLayer(35), "Etappen, To-dos und Hilfslinien gespeichert");
            Ok(cells.All(c => Math.Abs(Math.Sqrt((c.x - 50) * (c.x - 50) + (c.z - 50) * (c.z - 50)) - 10) < 1.5) && cells.Count > 50, $"Kreis Ø 21 als Blockraster: {cells.Count} Felder im Abstand 10");

            var jar = McAssets.FindJar() ?? McAssets.DownloadAsync(new Progress<string>(s => { })).GetAwaiter().GetResult();
            var mc = McAssets.Load(jar); mc.LoadGermanAsync().GetAwaiter().GetResult();
            int withTex = ent.CountBlocks().Keys.Count(v => mc.FacesOf(v) != null || mc.CrossOf(v) != null);
            Ok(mc.Tex.Count > 1000 && mc.Faces.Count > 800, $"Texturen {mc.Version}: {mc.Tex.Count} Bilder, {mc.Faces.Count} Blöcke, deutsch {mc.NamesDe.Count}; Enterprise {withTex}/{ent.CountBlocks().Count} mit Textur");
            Ok(mc.NamesDe.TryGetValue("stone", out var stn) && stn == "Stein", "Deutscher Name für stone: " + stn);
        }
        catch (Exception e) { Console.WriteLine("FEHL " + e); fail++; }
        Console.WriteLine(fail == 0 ? "ALLES OK" : fail + " Fehler");
        return fail;
    }
}
