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
