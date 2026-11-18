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
            {   // automatische Hilfslinien
                var tp = new Project { Name = "t", W = 10, D = 8, H = 3, OriginX = -3, OriginZ = 5 }; tp.Cells = new ushort[tp.W * tp.D * tp.H];
                for (int z = 2; z < 6; z++) for (int x = 2; x < 7; x++) tp.Cells[1 * 80 + z * 10 + x] = 1;   // 5x4 auf Ebene 1
                var tpd = new PlanData(tp); var ol = tpd.AddGuide("outline", 0, 0, 0); ol.Y = 0; ol.H = 3; int n1 = ol.ComputeOutline(tp);
                ol.Y = 0; ol.H = 1; int n0 = ol.ComputeOutline(tp); ol.AllLayers = true; int na = ol.ComputeOutline(tp);
                var gr = tpd.AddGuide("grid", 0, 0, 4); var gx = gr.GridLines(tp.W, tp.OriginX).ToList(); var gz = gr.GridLines(tp.D, tp.OriginZ).ToList();
                Ok(n1 == 14 && n0 == 0 && na == 14 && gx.SequenceEqual(new[] { 3, 7 }) && gz.SequenceEqual(new[] { 3, 7 }) && gr.Cells(tp).Count == 2 * 8 + 2 * 10 - 4,
                   $"Automatische Hilfslinien: Umriss {n1} Randfelder, Raster bei X {string.Join(",", gx)} / Z {string.Join(",", gz)}");
            }

            {   // Einkaufsliste: Blockzustände → Items
                var st = new System.Collections.Generic.Dictionary<int, long>
                {
                    [Blocks.Ensure("minecraft:wall_torch[facing=north]")] = 3, [Blocks.Ensure("minecraft:torch")] = 2,
                    [Blocks.Ensure("minecraft:oak_slab[type=double]")] = 5, [Blocks.Ensure("minecraft:oak_door[half=upper,facing=north]")] = 4,
                    [Blocks.Ensure("minecraft:oak_door[half=lower,facing=north]")] = 4, [Blocks.Ensure("minecraft:water")] = 100, [Blocks.Ensure("minecraft:stone")] = 130,
                    [Blocks.Ensure("minecraft:red_bed[part=head]")] = 1, [Blocks.Ensure("minecraft:red_bed[part=foot]")] = 1,
                };
                var it = Items.FromStates(st); var spc = Items.Space(it);
                Ok(it["torch"] == 5 && it["oak_slab"] == 10 && it["oak_door"] == 4 && !it.ContainsKey("water") && it["red_bed"] == 1 && it["stone"] == 130
                   && spc.slots == 1 + 1 + 1 + 1 + 3 && Items.StackText("stone", 130) == "2 Stacks + 2",
                   $"Einkaufsliste: Fackeln {it["torch"]}, Stufen {it["oak_slab"]}, Türen {it["oak_door"]}, {spc.slots} Plätze");
            }
            {   // Ersetzen: Eiche → Fichte, Treppen behalten ihre Richtung
                var tp = new Project { Name = "r", W = 3, D = 1, H = 1 };
                int planks = Blocks.Ensure("minecraft:oak_planks"), stairs = Blocks.Ensure("minecraft:oak_stairs[facing=east,half=top]"), stone = Blocks.Ensure("minecraft:stone");
                tp.Cells = new[] { (ushort)planks, (ushort)stairs, (ushort)stone };
                var map = BlockReplace.Map(planks, Blocks.Ensure("minecraft:spruce_planks"), true);
                for (int k = 0; k < 3; k++) tp.Cells[k] = (ushort)map(tp.Cells[k]);
                var s1 = Blocks.Get(tp.Cells[1]);
                Ok(Blocks.Get(tp.Cells[0]).Key == "spruce_planks" && s1.Key.StartsWith("spruce_stairs[") && s1.Props!["facing"] == "east" && s1.Props["half"] == "top" && tp.Cells[2] == stone,
                   $"Ersetzen: {Blocks.Get(tp.Cells[0]).Key}, {s1.Key}, Stein bleibt");
            }
            {   // Export als .schem und wieder einlesen
                var bytes = SchemExport.Build(ent, 0, 0, 0, ent.W - 1, ent.H - 1, ent.D - 1, null, 4440, out var nb);
                var tmp = Path.Combine(Path.GetTempPath(), "blockplan-export-test.schem"); File.WriteAllBytes(tmp, bytes);
                var back = SchematicReader.Load(tmp); File.Delete(tmp);
                bool eq = back.W == ent.W && back.H == ent.H && back.D == ent.D;
                if (eq) for (long k = 0; k < ent.Cells.LongLength; k++) if (back.Cells[k] != ent.Cells[k]) { eq = false; break; }
                Ok(eq && back.Count == nb && back.Banners.Count == ent.Banners.Count, $"Export .schem: {nb:N0} Blöcke, {bytes.Length / 1024:N0} KB, wieder eingelesen gleich, Banner {back.Banners.Count}");
                var bpx = SchematicReader.ToProject(bnr, 0, 0, 0, bnr.W - 1, bnr.H - 1, bnr.D - 1, false, false, 0);
                var bb = SchemExport.Build(bpx, 0, 0, 0, bpx.W - 1, bpx.H - 1, bpx.D - 1, null, 4440, out _);
                var tmp2 = Path.Combine(Path.GetTempPath(), "blockplan-export-banner.schem"); File.WriteAllBytes(tmp2, bb);
                var bback = SchematicReader.Load(tmp2); File.Delete(tmp2);
                Ok(bback.Banners.Count == bpx.Banners.Count && bpx.Banners.Count > 0 && bback.Banners.All(kv => bpx.Banners.TryGetValue(kv.Key, out var o) && o == kv.Value), $"Export mit Bannermustern: {bback.Banners.Count} von {bpx.Banners.Count}");
            }

            {   // Laufen: Boden, Stufe hochsteigen, Wand, Leiter, Licht
                var tp = new Project { Name = "c", W = 6, D = 3, H = 4 }; tp.Cells = new ushort[tp.W * tp.D * tp.H];
                ushort stone = (ushort)Blocks.Ensure("minecraft:stone"), slab = (ushort)Blocks.Ensure("minecraft:stone_slab[type=bottom]");
                for (int ix = 0; ix < 6; ix++) for (int iz = 0; iz < 3; iz++) tp.Cells[tp.Index(ix, 0, iz)] = stone;
                for (int iz = 0; iz < 3; iz++) { tp.Cells[tp.Index(2, 1, iz)] = slab; tp.Cells[tp.Index(4, 1, iz)] = stone; tp.Cells[tp.Index(4, 2, iz)] = stone; }
                // fallen und landen
                var (x1, y1, z1, g1, _, _) = Collision.Move(tp, 1.5f, 3f, 1.5f, 0, -2.5f, 0, false);
                // auf die Stufe (0,5 hoch) steigen
                float px = 1.5f, py = 1f; bool stepped = false;
                for (int k = 0; k < 20; k++) { var r = Collision.Move(tp, px, py, 1.5f, .1f, 0, 0, true); px = r.x; py = r.y; if (py > 1.4f) stepped = true; }
                // gegen die Wand (2 hoch) laufen
                float wxp = px; for (int k = 0; k < 30; k++) { var r = Collision.Move(tp, wxp, py, 1.5f, .1f, 0, 0, true); wxp = r.x; py = r.y; }
                var lamp = new Project { Name = "l", W = 5, D = 1, H = 3 }; lamp.Cells = new ushort[15];
                lamp.Cells[lamp.Index(0, 1, 0)] = (ushort)Blocks.Ensure("minecraft:torch"); for (int xx = 0; xx < 5; xx++) lamp.Cells[lamp.Index(xx, 2, 0)] = stone;
                var Lm = Lighting.Compute(lamp);
                Ok(g1 && Math.Abs(y1 - 1) < 1e-3 && stepped && wxp < 3.75f && wxp > 3.5f && (Lm[lamp.Index(0, 1, 0)] & 15) == 14 && (Lm[lamp.Index(3, 1, 0)] & 15) == 11 && (Lm[lamp.Index(3, 1, 0)] >> 4) == 0,
                   $"Laufen: landet auf Y {y1:0.##}, Stufe hochgestiegen, stoppt vor der Wand bei X {wxp:0.##}; Licht Fackel 14, 3 weiter {Lm[lamp.Index(3, 1, 0)] & 15}");
            }

            {   // Zeitraffer: Ereignisse kompakt speichern und wieder lesen
                var ev = new[] { new BuildEvent(1000, 5, true), new BuildEvent(1000, 3, true), new BuildEvent(2500, 900_000, true), new BuildEvent(9000, 5, false) };
                var back = Timeline.Decode(Timeline.Encode(ev));
                var tl = new Timeline { Events = back.ToArray(), Cells = 1_000_000 };
                var s1 = tl.StateAt(2000); var s2 = tl.StateAt(long.MaxValue);
                Ok(back.SequenceEqual(ev) && s1[5] && s1[3] && !s1[900_000] && !s2[5] && s2[900_000], $"Zeitraffer: {back.Count} Ereignisse gespeichert und gelesen, Stand je Zeitpunkt stimmt");
            }

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
