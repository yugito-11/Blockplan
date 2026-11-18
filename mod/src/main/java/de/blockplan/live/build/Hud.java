package de.blockplan.live.build;

import net.minecraft.client.DeltaTracker;
import net.minecraft.client.Minecraft;
import net.minecraft.client.gui.GuiGraphicsExtractor;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;

import java.util.ArrayList;
import java.util.List;
import java.util.Map;

/** Anzeige am Bildschirmrand: Fortschritt und was noch fehlt – abgeglichen mit Inventar und Lagerkisten. */
public final class Hud {
	private Hud() {}

	record Row(Item item, int left, int inv, int lager) {}
	static List<Row> rows = List.of();
	static int lastTick = -100;

	public static void render(GuiGraphicsExtractor g, DeltaTracker dt)
	{
		var mc = Minecraft.getInstance(); var p = Build.plan;
		if (!Build.hud || p == null || mc.player == null) return;
		if (Build.tick - lastTick >= 20) { lastTick = Build.tick; rebuild(); }
		var font = mc.font;
		int x = 6, y = 6, w = 210;
		var lines = new ArrayList<String>();
		String title = "Blockplan · " + p.name;
		String mode = Build.layerMode ? "Ebene Y " + (p.y + Build.layer) + "  (" + (Build.layer + 1) + "/" + p.h + ")" : "ganzer Plan";
		long done = Build.placedBlocks(); double pct = p.blocks == 0 ? 0 : 100.0 * done / p.blocks;
		String prog = Build.scanDone ? String.format("%,d von %,d gesetzt (%.0f %%)", done, p.blocks, pct).replace(',', '.') : "zähle …";
		int h = 12 * 3 + 6 + Math.min(rows.size(), 10) * 18 + (rows.isEmpty() ? 12 : 0) + 14;
		g.fill(x - 3, y - 3, x + w, y + h, 0xB0101814);
		g.text(font, title, x, y, 0xFFFFFFFF, true); y += 12;
		g.text(font, mode + (Build.show ? "" : "  · ausgeblendet"), x, y, 0xFFC8D2CB, false); y += 12;
		g.text(font, prog, x, y, 0xFF9BE3AE, false); y += 14;
		if (rows.isEmpty()) { g.text(font, Build.layerMode ? "Diese Ebene ist fertig ✓" : "Alles gebaut ✓", x, y, 0xFF9BE3AE, false); y += 12; }
		int shown = 0;
		for (var r : rows) {
			if (shown++ >= 10) break;
			g.item(new ItemStack(r.item()), x, y);
			int have = r.inv() + r.lager();
			String name = r.item().getName(new ItemStack(r.item())).getString();
			if (name.length() > 18) name = name.substring(0, 17) + "…";
			g.text(font, name, x + 19, y + 1, 0xFFFFFFFF, false);
			String nums = "fehlt " + r.left() + "  ·  da " + have + (r.lager() > 0 ? " (Lager " + r.lager() + ")" : "");
			g.text(font, nums, x + 19, y + 10, have >= r.left() ? 0xFF9BE3AE : 0xFFFFB4B4, false);
			y += 18;
		}
		g.text(font, "B Plan · N Ebene/ganz · Bild↑↓ Ebene · K Lagerkiste · J Liste", x, y + 2, 0xFF8A9690, false);
	}

	static void rebuild()
	{
		Map<Item, Integer> left = Build.remaining(), inv = Build.inventory(), lager = Build.storage();
		var out = new ArrayList<Row>();
		for (var e : left.entrySet()) out.add(new Row(e.getKey(), e.getValue(), inv.getOrDefault(e.getKey(), 0), lager.getOrDefault(e.getKey(), 0)));
		// zuerst, was noch beschafft werden muss
		out.sort((a, b) -> Integer.compare(b.left() - b.inv() - b.lager(), a.left() - a.inv() - a.lager()));
		rows = out;
	}
}
