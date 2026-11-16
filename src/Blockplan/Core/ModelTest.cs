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
        Console.WriteLine(fail == 0 ? "ALLE MODELLE OK" : fail + " Fehler");
        return fail;
    }
}
