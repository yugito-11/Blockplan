using System;
using System.Linq;

namespace Blockplan.Core;

/// <summary>Prüft, ob Blockmodelle wie im Spiel liegen: Blockplan --modeltest</summary>
public static class ModelTest
{
    public static int Run()
    {
        var mc = McAssets.Load(McAssets.FindJar()!); var atlas = new Atlas(mc); var bank = new ModelBank(atlas);
        int fail = 0;
        void Ok(bool c, string m) { Console.WriteLine((c ? "OK   " : "FEHL ") + m); if (!c) fail++; }
        (float x0, float y0, float z0, float x1, float y1, float z1) Box(string key, Func<MQuad, bool> sel)
        {
            var qs = bank.Get(Blocks.Ensure(key), 0).Where(sel).ToArray();
            if (qs.Length == 0) return (9, 9, 9, -9, -9, -9);
            var xs = qs.SelectMany(q => new[] { q.P[0], q.P[3], q.P[6], q.P[9] }); var ys = qs.SelectMany(q => new[] { q.P[1], q.P[4], q.P[7], q.P[10] }); var zs = qs.SelectMany(q => new[] { q.P[2], q.P[5], q.P[8], q.P[11] });
            return (xs.Min(), ys.Min(), zs.Min(), xs.Max(), ys.Max(), zs.Max());
        }
        bool Upper(MQuad q) => q.P[1] > .51f || q.P[4] > .51f || q.P[7] > .51f || q.P[10] > .51f;
        bool Lower(MQuad q) => q.P[1] < .49f || q.P[4] < .49f || q.P[7] < .49f || q.P[10] < .49f;
        // Treppen: hohe Stufe liegt auf der Seite, in die „facing“ zeigt (Norden = -z)
        var n = Box("oak_stairs[facing=north,half=bottom,shape=straight]", q => Upper(q) && q.P[1] > .51f && q.P[4] > .51f && q.P[7] > .51f && q.P[10] > .51f);
        Ok(n.z1 <= .51f, $"Treppe Nord: Oberseite der Stufe z {n.z0:0.##}..{n.z1:0.##} (erwartet 0..0,5)");
        var e = Box("oak_stairs[facing=east,half=bottom,shape=straight]", q => q.P[1] > .99f && q.P[4] > .99f && q.P[7] > .99f && q.P[10] > .99f);
        Ok(e.x0 >= .49f, $"Treppe Ost: Oberseite x {e.x0:0.##}..{e.x1:0.##} (erwartet 0,5..1)");
        var so = Box("oak_stairs[facing=south,half=bottom,shape=straight]", q => q.P[1] > .99f && q.P[4] > .99f && q.P[7] > .99f && q.P[10] > .99f);
        Ok(so.z0 >= .49f, $"Treppe Süd: Oberseite z {so.z0:0.##}..{so.z1:0.##} (erwartet 0,5..1)");
        var w = Box("oak_stairs[facing=west,half=bottom,shape=straight]", q => q.P[1] > .99f && q.P[4] > .99f && q.P[7] > .99f && q.P[10] > .99f);
        Ok(w.x1 <= .51f, $"Treppe West: Oberseite x {w.x0:0.##}..{w.x1:0.##} (erwartet 0..0,5)");
        var tn = Box("oak_stairs[facing=north,half=top,shape=straight]", q => q.P[1] < .01f && q.P[4] < .01f && q.P[7] < .01f && q.P[10] < .01f);
        Ok(tn.z1 <= .51f, $"Treppe Nord oben (umgedreht): Unterseite der Stufe z {tn.z0:0.##}..{tn.z1:0.##} (erwartet 0..0,5)");
        // Stämme: Jahresringe (log_top) zeigen in Achsrichtung
        int ringT = atlas.TileByName("oak_log_top");
        string Dirs(string key) => string.Join(",", bank.Get(Blocks.Ensure(key), 0).Where(q => q.Tile == ringT).Select(q => q.Dir).OrderBy(d => d));
        Ok(Dirs("oak_log[axis=y]") == "2,3", "Stamm y: Ringe oben/unten (" + Dirs("oak_log[axis=y]") + ")");
        Ok(Dirs("oak_log[axis=x]") == "0,1", "Stamm x: Ringe Ost/West (" + Dirs("oak_log[axis=x]") + ")");
        Ok(Dirs("oak_log[axis=z]") == "4,5", "Stamm z: Ringe Süd/Nord (" + Dirs("oak_log[axis=z]") + ")");
        // Öfen: Front zeigt in facing
        int front = atlas.TileByName("furnace_front");
        foreach (var (f, d) in new[] { ("north", 5), ("east", 0), ("south", 4), ("west", 1) })
        {
            var fd = bank.Get(Blocks.Ensure($"furnace[facing={f},lit=false]"), 0).Where(q => q.Tile == front).Select(q => q.Dir).ToArray();
            Ok(fd.Length == 1 && fd[0] == d, $"Ofen {f}: Front zeigt nach Richtung {string.Join(",", fd)} (erwartet {d})");
            var sc = bank.SimpleCube(Blocks.Ensure($"furnace[facing={f},lit=false]"));
            Ok(sc != null && sc[d] == front, $"Ofen {f}: schnelles Zusammenfassen behält die Front");
        }
        // Wandfackel facing=north: hängt an der Südwand, Spitze ragt nach Norden
        var wt = bank.Get(Blocks.Ensure("wall_torch[facing=north]"), 0);
        float topZ = wt.SelectMany(q => Enumerable.Range(0, 4).Where(k => q.P[k * 3 + 1] > .7f).Select(k => q.P[k * 3 + 2])).DefaultIfEmpty(9).Average();
        float botZ = wt.SelectMany(q => Enumerable.Range(0, 4).Where(k => q.P[k * 3 + 1] < .35f).Select(k => q.P[k * 3 + 2])).DefaultIfEmpty(9).Average();
        Ok(botZ > topZ && botZ > .6f, $"Wandfackel Nord: unten z {botZ:0.##}, oben z {topZ:0.##} (unten an der Südwand, oben weiter nördlich)");
        // Tür facing=east geschlossen: liegt an der Westkante
        var door = Box("oak_door[facing=east,half=lower,hinge=left,open=false,powered=false]", q => true);
        Ok(door.x1 <= .2f, $"Tür Ost: x {door.x0:0.##}..{door.x1:0.##} (erwartet an der Westkante 0..0,19)");
        // Falltür unten geschlossen, oben offen nach Norden
        var td = Box("oak_trapdoor[facing=north,half=bottom,open=false,powered=false]", q => true);
        Ok(td.y1 <= .2f, $"Falltür unten: y {td.y0:0.##}..{td.y1:0.##}");
        var to = Box("oak_trapdoor[facing=north,half=top,open=true,powered=false]", q => true);
        Ok(to.z0 >= .8f, $"Falltür offen Nord: z {to.z0:0.##}..{to.z1:0.##} (erwartet an der Südkante)");
        // Laterne hängend: Kette reicht bis oben
        var la = Box("lantern[hanging=true]", q => true);
        Ok(la.y1 > .95f, $"Hängende Laterne: oben bis y {la.y1:0.##}");
        // Knopf an Wand facing=east: sitzt an der Westkante
        var bt = Box("stone_button[face=wall,facing=east,powered=false]", q => true);
        Ok(bt.x1 <= .2f, $"Knopf Ost: x {bt.x0:0.##}..{bt.x1:0.##} (erwartet an der Westkante)");
        // Leiter facing=west: an der Ostkante
        var ld = Box("ladder[facing=west]", q => true);
        Ok(ld.x0 >= .8f, $"Leiter West: x {ld.x0:0.##}..{ld.x1:0.##} (erwartet an der Ostkante)");
        // Zaun verbindet sich nach Osten
        var fv = Blocks.Ensure("oak_fence"); var f0 = bank.Get(fv, 0); var fe = bank.Get(fv, 2);
        float fx = fe.SelectMany(q => new[] { q.P[0], q.P[3], q.P[6], q.P[9] }).Max();
        Ok(fe.Length > f0.Length && fx > .99f, $"Zaun mit Verbindung Ost reicht bis x {fx:0.##}");
        var ps = bank.Get(Blocks.Ensure("glass_pane"), 1 | 4); float pz0 = ps.SelectMany(q => new[] { q.P[2], q.P[5], q.P[8], q.P[11] }).Min(), pz1 = ps.SelectMany(q => new[] { q.P[2], q.P[5], q.P[8], q.P[11] }).Max();
        Ok(pz0 < .01f && pz1 > .99f, $"Glasscheibe Nord–Süd: z {pz0:0.##}..{pz1:0.##}");
        // Betten und Schilder (in 26.3 echte Blockmodelle)
        var bh = Box("red_bed[facing=north,occupied=false,part=head]", q => true);
        Ok(bank.Get(Blocks.Ensure("red_bed[facing=north,occupied=false,part=head]"), 0).Length > 0 && bh.y1 < .6f, $"Bett Kopfteil: y bis {bh.y1:0.##}");
        var sg = Box("oak_sign[rotation=0,waterlogged=false]", q => true);
        Ok(sg.y1 > 1f && sg.y1 < 1.2f, $"Schild stehend: Höhe bis y {sg.y1:0.##}");
        var ws = Box("oak_wall_sign[facing=north,waterlogged=false]", q => true);
        Ok(ws.z0 > .8f, $"Wandschild Nord: z {ws.z0:0.##}..{ws.z1:0.##} (an der Südwand)");
        // Truhen: Schloss zeigt in facing
        foreach (var (f, dz, dx) in new[] { ("south", 1, 0), ("north", -1, 0), ("east", 0, 1), ("west", 0, -1) })
        {
            var qs = bank.Get(Blocks.Ensure($"chest[facing={f},type=single,waterlogged=false]"), 0);
            // Schloss = kleine Flächen auf Höhe 7..11 Pixel, die über den Korpus hinausragen
            var lockPts = qs.SelectMany(q => Enumerable.Range(0, 4).Select(k => (x: q.P[k * 3], y: q.P[k * 3 + 1], z: q.P[k * 3 + 2]))).Where(p => p.y > 6.9f / 16 && p.y < 11.1f / 16 && (p.x > 15.5f / 16 || p.x < .5f / 16 || p.z > 15.5f / 16 || p.z < .5f / 16)).ToList();
            float lx = lockPts.Count > 0 ? lockPts.Average(p => p.x) : -1, lz = lockPts.Count > 0 ? lockPts.Average(p => p.z) : -1;
            bool ok = lockPts.Count > 0 && (dz != 0 ? (dz > 0 ? lz > .9f : lz < .1f) : (dx > 0 ? lx > .9f : lx < .1f));
            Ok(ok, $"Truhe {f}: Schloss bei x {lx:0.##} z {lz:0.##}");
        }
        var dl = Box("chest[facing=north,type=left,waterlogged=false]", q => true); var dr = Box("chest[facing=north,type=right,waterlogged=false]", q => true);
        Ok(dl.x1 > .99f && dr.x0 < .01f, $"Doppeltruhe Nord: links x {dl.x0:0.##}..{dl.x1:0.##}, rechts x {dr.x0:0.##}..{dr.x1:0.##}");
        var bn = Box("red_banner[rotation=0]", q => true);
        Ok(bn.y1 > 1.7f && bn.y1 < 1.8f, $"Banner stehend: Höhe {bn.y1:0.##} Blöcke");
        var wb = Box("red_wall_banner[facing=south]", q => true);
        Ok(wb.z1 < .1f && wb.y1 <= 1.01f && wb.y0 < 0, $"Wandbanner Süd: z {wb.z0:0.##}..{wb.z1:0.##}, y {wb.y0:0.##}..{wb.y1:0.##} (an der Nordwand, hängt nach unten)");
        // Redstone mit Stärke 15 ist hellrot, mit 0 dunkelrot
        byte[] Px(MQuad q) => atlas.Tiles[q.Tile];
        (int r, int g) Avg(byte[] px) { long r = 0, g = 0, n = 0; for (int i = 0; i < px.Length; i += 4) if (px[i + 3] > 100) { r += px[i]; g += px[i + 1]; n++; } return n == 0 ? (0, 0) : ((int)(r / n), (int)(g / n)); }
        var rs15 = bank.Get(Blocks.Ensure("redstone_wire[east=none,north=none,power=15,south=none,west=none]"), 0); var rs0 = bank.Get(Blocks.Ensure("redstone_wire[east=none,north=none,power=0,south=none,west=none]"), 0);
        (int r, int g) c15 = rs15.Length > 0 ? Avg(Px(rs15[0])) : (0, 0), c0 = rs0.Length > 0 ? Avg(Px(rs0[0])) : (0, 0);
        Ok(c15.r > 150 && c15.r > c15.g * 2 && c0.r < c15.r, $"Redstone eingefärbt: Stärke 15 rot {c15.r}/grün {c15.g}, Stärke 0 rot {c0.r}");
        var lv = bank.Get(Blocks.Ensure("oak_leaves[distance=7,persistent=true,waterlogged=false]"), 0);
        (int r, int g) cl = lv.Length > 0 ? Avg(Px(lv[0])) : (0, 0); Ok(cl.g > cl.r, $"Eichenlaub grün: rot {cl.r}, grün {cl.g}");
        // Ranke nur mit Verbindung nach Osten: liegt an der Ostkante
        var vine = bank.Get(Blocks.Ensure("vine"), 2); float vx = vine.SelectMany(q => new[] { q.P[0], q.P[3], q.P[6], q.P[9] }).DefaultIfEmpty(-1).Min();
        Ok(vine.Length > 0 && vx > .9f, $"Ranke an der Ostwand: x ab {vx:0.##}");
        var mush = bank.Get(Blocks.Ensure("red_mushroom_block"), 0); int mt = atlas.TileByName("red_mushroom_block");
        Ok(mush.Length == 6 && mush.All(q => q.Tile == mt) && mush.Select(q => q.Dir).Distinct().Count() == 6, $"Pilzblock außen rot auf allen 6 Seiten ({mush.Length} Flächen)");
        // Shulkerkiste nach Norden: Deckel-Oberseite zeigt nach Norden
        int lidTop = atlas.TileOf(mc.Region("shulker/shulker_red", 16, 0, 16, 16)!);
        var sh = bank.Get(Blocks.Ensure("red_shulker_box[facing=north]"), 0).Where(q => q.Tile == lidTop).Select(q => q.Dir).ToArray();
        Ok(sh.Length == 1 && sh[0] == 5, $"Shulkerkiste Nord: Deckel zeigt nach {string.Join(",", sh)} (erwartet 5)");
        // Schädel: Gesicht zeigt bei Drehung 0 nach Süden, Wandschädel nach Norden an der Südwand
        int face = atlas.TileOf(mc.Region("skeleton/skeleton", 8, 8, 8, 8)!);
        var sk = bank.Get(Blocks.Ensure("skeleton_skull[powered=false,rotation=0]"), 0).Where(q => q.Tile == face).Select(q => q.Dir).ToArray();
        Ok(sk.Length == 1 && sk[0] == 4, $"Schädel Drehung 0: Gesicht nach {string.Join(",", sk)} (erwartet 4)");
        var wsk = bank.Get(Blocks.Ensure("skeleton_wall_skull[facing=north,powered=false]"), 0);
        var wf = wsk.Where(q => q.Tile == face).Select(q => q.Dir).ToArray(); float wz0 = wsk.SelectMany(q => new[] { q.P[2], q.P[5], q.P[8], q.P[11] }).Min();
        Ok(wf.Length == 1 && wf[0] == 5 && wz0 >= .49f, $"Wandschädel Nord: Gesicht nach {string.Join(",", wf)}, z ab {wz0:0.##}");
        Ok(bank.Get(Blocks.Ensure("bell[attachment=floor,facing=north,powered=false]"), 0).Length > 20, "Glocke mit Körper");
        foreach (var k in new[] { "decorated_pot[cracked=false,facing=north,waterlogged=false]", "conduit[waterlogged=true]", "copper_golem_statue[copper_golem_pose=standing,facing=north,waterlogged=false]", "end_portal", "player_head[powered=false,rotation=0]", "dragon_head[powered=false,rotation=0]", "piglin_head[powered=false,rotation=0]", "white_shulker_box[facing=up]" })
            Ok(bank.Get(Blocks.Ensure(k), 0).Length > 0, "gezeichnet: " + k);
        var inv = new Project { W = 2, D = 1, H = 1, Cells = new ushort[] { (ushort)Blocks.Ensure("barrier"), (ushort)Blocks.Ensure("light[level=15,waterlogged=false]") } };
        Ok(Mesher.Build(inv, 0, atlas).All(c => c.I.Length == 0), "Barriere und Licht unsichtbar");
        Console.WriteLine(fail == 0 ? "ALLE MODELLE OK" : fail + " Fehler");
        return fail;
    }
}
