package de.blockplan.live.build;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import net.minecraft.client.Minecraft;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.network.chat.Component;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.ItemStack;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.ChestBlock;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.properties.ChestType;
import net.minecraft.world.level.chunk.LevelChunk;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Map;
import java.util.Set;

/**
 * Bauen nach Plan im eigenen Spiel (Ersatz für Litematica): Plan aus Blockplan, Abgleich mit der Welt,
 * Lagerkisten und Inventar. Alles läuft auf dem Client-Thread.
 */
public final class Build {
	private Build() {}

	public static volatile Plan plan;
	public static boolean show = true, layerMode = false, hud = true;
	public static int layer; // lokale Höhe im Plan

	/** Was in der Nähe zu zeichnen ist (alle 10 Ticks neu): Lage und Zustand. */
	public record Spot(BlockPos pos, BlockState want, int kind) {} // kind 1 fehlt, 2 falsch, 3 falsche Ausrichtung
	static volatile List<Spot> spots = List.of();

	// Fortschritt des ganzen Plans: in Ruhe nach und nach gezählt (kleines Zeitbudget je Tick)
	static Map<Item, Integer> placedRun = new HashMap<>(), placed = new HashMap<>();
	static long placedBlocksRun, placedBlocks, unknownRun, unknown;
	static List<Map.Entry<Long, char[]>> scanList = List.of();
	static int scanSec, scanIdx;
	public static boolean scanDone;

	// Lagerkisten: Lage → Inhalt (zuletzt beim Öffnen gesehen)
	static final Set<Long> chests = new HashSet<>();
	static final Map<Long, Map<Item, Integer>> chestItems = new HashMap<>();
	static String worldKey = "welt";
	public static BlockPos lastUsed;

	static int tick;
	static String lastSent = "";

	/* ---------- Plan setzen (vom Webserver) ---------- */
	public static void setPlanJson(String json) {
		Plan p = Plan.parse(json);
		Minecraft.getInstance().execute(() -> {
			plan = p; layer = 0; placed = new HashMap<>(); placedBlocks = 0; unknown = p.blocks; scanDone = false; restartScan();
			msg("Blockplan: Plan „" + p.name + "“ geladen – " + p.w + "×" + p.d + "×" + p.h + ", " + String.format("%,d", p.blocks).replace(',', '.') + " Blöcke");
			pushState();
		});
	}

	/** Ansicht aus Blockplan: {show, mode:"layer"|"all", layer} */
	public static void setViewJson(String json) {
		JsonObject o = JsonParser.parseString(json).getAsJsonObject();
		Minecraft.getInstance().execute(() -> {
			if (o.has("show")) show = o.get("show").getAsBoolean();
			if (o.has("mode")) layerMode = "layer".equals(o.get("mode").getAsString());
			if (o.has("layer") && plan != null) layer = Math.max(0, Math.min(plan.h - 1, o.get("layer").getAsInt()));
			refreshSpots(); pushState();
		});
	}

	public static void clear() { plan = null; spots = List.of(); pushState(); }

	static void msg(String s) { var p = Minecraft.getInstance().player; if (p != null) p.sendOverlayMessage(Component.literal(s)); }

	/* ---------- Tasten ---------- */
	public static void toggleShow() { show = !show; msg(show ? "Blockplan: Plan sichtbar" : "Blockplan: Plan ausgeblendet"); refreshSpots(); pushState(); }

	public static void toggleMode()
	{
		var p = plan; if (p == null) { msg("Blockplan: noch kein Plan – in Blockplan „Plan ins Spiel“ klicken"); return; }
		layerMode = !layerMode;
		if (layerMode) { var pl = Minecraft.getInstance().player; if (pl != null) layer = Math.max(0, Math.min(p.h - 1, pl.blockPosition().getY() - p.y)); }
		msg(layerMode ? "Blockplan: Ebene Y " + (p.y + layer) : "Blockplan: ganzer Plan");
		refreshSpots(); pushState();
	}

	public static void layerBy(int d)
	{
		var p = plan; if (p == null) return;
		if (!layerMode) { layerMode = true; }
		layer = Math.max(0, Math.min(p.h - 1, layer + d));
		msg("Blockplan: Ebene Y " + (p.y + layer) + " (" + (layer + 1) + " von " + p.h + ")");
		refreshSpots(); pushState();
	}

	/** Kiste/Fass/Shulker, auf die man schaut, als Lagerkiste an- oder abmelden. */
	public static void toggleChest()
	{
		var mc = Minecraft.getInstance();
		if (!(mc.hitResult instanceof net.minecraft.world.phys.BlockHitResult hr) || mc.level == null) return;
		BlockPos pos = hr.getBlockPos();
		if (!isContainer(mc.level.getBlockState(pos))) { msg("Blockplan: Schau auf eine Kiste, ein Fass oder eine Shulkerkiste"); return; }
		long k = chestKey(mc.level, pos);
		if (chests.remove(k)) { chestItems.remove(k); msg("Blockplan: Lagerkiste entfernt (" + chests.size() + " Lagerkisten)"); }
		else { chests.add(k); msg("Blockplan: Lagerkiste gemerkt – einmal öffnen, damit der Inhalt zählt (" + chests.size() + " Lagerkisten)"); }
		saveStorage(); pushState();
	}

	/** Kiste an der Lage pos an- oder abmelden (Knopf im Kisten-Fenster); gibt zurück, ob sie jetzt Lagerkiste ist. */
	public static boolean toggleChestAt(BlockPos pos, Map<Item, Integer> contents)
	{
		var mc = Minecraft.getInstance(); if (mc.level == null) return false;
		long k = chestKey(mc.level, pos);
		boolean on;
		if (chests.remove(k)) { chestItems.remove(k); on = false; msg("Blockplan: Lagerkiste entfernt (" + chests.size() + " Lagerkisten)"); }
		else { chests.add(k); chestItems.put(k, contents); on = true; msg("Blockplan: Lagerkiste hinzugefügt (" + chests.size() + " Lagerkisten)"); }
		saveStorage(); pushState(); return on;
	}

	public static boolean isContainerAt(BlockPos pos) {
		var mc = Minecraft.getInstance(); return mc.level != null && pos != null && isContainer(mc.level.getBlockState(pos));
	}

	static boolean isContainer(BlockState s) {
		var id = BuiltInRegistries.BLOCK.getKey(s.getBlock()).getPath();
		return id.contains("chest") || id.equals("barrel") || id.endsWith("shulker_box");
	}

	/** Doppelkiste: beide Hälften zählen als eine (die kleinere Lage). */
	static long chestKey(Level level, BlockPos pos) {
		BlockState s = level.getBlockState(pos);
		if (s.getBlock() instanceof ChestBlock && s.hasProperty(ChestBlock.TYPE) && s.getValue(ChestBlock.TYPE) != ChestType.SINGLE) {
			BlockPos o = ChestBlock.getConnectedBlockPos(pos, s);
			return Math.min(pos.asLong(), o.asLong());
		}
		return pos.asLong();
	}

	/** Beim Schließen eines Kisten-Fensters: Inhalt merken, wenn es eine Lagerkiste ist. */
	public static void containerSeen(Map<Item, Integer> items)
	{
		var mc = Minecraft.getInstance(); if (lastUsed == null || mc.level == null) return;
		long k = chestKey(mc.level, lastUsed);
		if (!chests.contains(k)) return;
		chestItems.put(k, items); saveStorage(); pushState();
	}

	public static boolean isMarked(BlockPos p) {
		var mc = Minecraft.getInstance(); return mc.level != null && chests.contains(chestKey(mc.level, p));
	}

	public static Set<Long> markedChests() { return chests; }

	/* ---------- Welt wechseln: Lagerkisten je Welt/Server ---------- */
	public static void switchWorld(String key) {
		worldKey = key.replaceAll("[^A-Za-z0-9._-]", "_");
		chests.clear(); chestItems.clear();
		try {
			Path f = storageFile(); if (!Files.exists(f)) return;
			JsonObject o = JsonParser.parseString(Files.readString(f)).getAsJsonObject();
			for (var e : o.entrySet()) {
				long k = Long.parseLong(e.getKey()); chests.add(k);
				var m = new HashMap<Item, Integer>();
				for (var it : e.getValue().getAsJsonObject().entrySet()) {
					var item = BuiltInRegistries.ITEM.getValue(net.minecraft.resources.Identifier.parse(it.getKey()));
					if (item != Items.AIR) m.put(item, it.getValue().getAsInt());
				}
				chestItems.put(k, m);
			}
		} catch (Exception e) { de.blockplan.live.BlockplanLive.LOG.warn("Lagerkisten lesen: {}", e.toString()); }
	}

	static Path storageFile() { return net.fabricmc.loader.api.FabricLoader.getInstance().getConfigDir().resolve("blockplan").resolve("lager-" + worldKey + ".json"); }

	static void saveStorage() {
		try {
			JsonObject o = new JsonObject();
			for (long k : chests) {
				JsonObject m = new JsonObject();
				for (var e : chestItems.getOrDefault(k, Map.of()).entrySet()) m.addProperty(BuiltInRegistries.ITEM.getKey(e.getKey()).toString(), e.getValue());
				o.add(Long.toString(k), m);
			}
			Files.createDirectories(storageFile().getParent());
			Files.writeString(storageFile(), o.toString());
		} catch (Exception e) { de.blockplan.live.BlockplanLive.LOG.warn("Lagerkisten speichern: {}", e.toString()); }
	}

	/* ---------- Bestand: Inventar und Lager ---------- */
	public static Map<Item, Integer> inventory() {
		var m = new HashMap<Item, Integer>(); var p = Minecraft.getInstance().player; if (p == null) return m;
		for (ItemStack s : p.getInventory().getNonEquipmentItems()) if (!s.isEmpty()) m.merge(s.getItem(), s.getCount(), Integer::sum);
		return m;
	}

	public static Map<Item, Integer> storage() {
		var m = new HashMap<Item, Integer>();
		for (var c : chestItems.values()) for (var e : c.entrySet()) m.merge(e.getKey(), e.getValue(), Integer::sum);
		return m;
	}

	/* ---------- Abgleich mit der Welt ---------- */
	public static void tick(Minecraft mc)
	{
		if (mc.level == null || mc.player == null) return;
		tick++;
		var p = plan;
		if (p != null && sameDim(mc.level, p)) {
			if (tick % 10 == 0) refreshSpots();
			scanStep(mc.level, p, 1_500_000L);
		}
		if (tick % 40 == 0) pushState();
	}

	static boolean sameDim(Level l, Plan p) { return de.blockplan.live.BlockplanLive.dimId(l).equals(p.dim); }

	/** Weicher Untergrund zählt wie Luft (Gras, Schnee, Wasser …). */
	static boolean soft(BlockState s) { return s.isAir() || s.canBeReplaced(); }

	/** 0 richtig, 1 fehlt, 2 falscher Block, 3 richtiger Block, falsche Ausrichtung */
	static int status(BlockState want, BlockState have) {
		if (have == want) return 0;
		if (have.getBlock() == want.getBlock()) return 3;
		if (soft(have) && !soft(want)) return 1;
		if (soft(want) && soft(have)) return 0;
		return 2;
	}

	public static void refreshSpots()
	{
		var mc = Minecraft.getInstance(); var p = plan;
		if (!show || p == null || mc.level == null || mc.player == null || !sameDim(mc.level, p)) { spots = List.of(); return; }
		var out = new ArrayList<Spot>();
		var me = mc.player.blockPosition();
		int r = layerMode ? 64 : 40, ry = 28;
		int x0 = Math.max(0, me.getX() - p.x - r), x1 = Math.min(p.w - 1, me.getX() - p.x + r);
		int z0 = Math.max(0, me.getZ() - p.z - r), z1 = Math.min(p.d - 1, me.getZ() - p.z + r);
		int y0 = layerMode ? layer : Math.max(0, me.getY() - p.y - ry), y1 = layerMode ? layer : Math.min(p.h - 1, me.getY() - p.y + ry);
		var pos = new BlockPos.MutableBlockPos();
		for (int y = y0; y <= y1; y++)
			for (int z = z0; z <= z1; z++)
				for (int x = x0; x <= x1; x++) {
					int v = p.at(x, y, z); if (v == 0) continue;
					BlockState want = p.pal[v];
					pos.set(p.x + x, p.y + y, p.z + z);
					if (!mc.level.hasChunkAt(pos)) continue;
					int st = status(want, mc.level.getBlockState(pos));
					if (st != 0) out.add(new Spot(pos.immutable(), want, st));
					if (out.size() >= 20000) { spots = out; return; }
				}
		spots = out;
	}

	public static List<Spot> spots() { return spots; }

	static void restartScan() { var p = plan; scanList = p == null ? List.of() : new ArrayList<>(p.sections.entrySet()); scanSec = 0; scanIdx = 0; placedRun = new HashMap<>(); placedBlocksRun = 0; unknownRun = 0; }

	/** Ganzen Plan nach und nach mit der Welt vergleichen (nur so viel Zeit je Tick wie erlaubt). */
	static void scanStep(Level level, Plan p, long budget)
	{
		long end = System.nanoTime() + budget;
		var pos = new BlockPos.MutableBlockPos();
		while (System.nanoTime() < end) {
			if (scanSec >= scanList.size()) {
				placed = placedRun; placedBlocks = placedBlocksRun; unknown = unknownRun; scanDone = true; restartScan(); return;
			}
			var e = scanList.get(scanSec); long key = e.getKey(); char[] s = e.getValue();
			int sx = (int) (key & 0x1FFFFF) << 4, sz = (int) ((key >> 21) & 0x1FFFFF) << 4, sy = (int) (key >> 42) << 4;
			LevelChunk chunk = null; int lastCx = Integer.MIN_VALUE, lastCz = Integer.MIN_VALUE;
			for (int n = 0; n < 1024 && scanIdx < 4096; n++, scanIdx++) {
				int v = s[scanIdx]; if (v == 0) continue;
				int lx = sx + (scanIdx & 15), lz = sz + ((scanIdx >> 4) & 15), ly = sy + (scanIdx >> 8);
				pos.set(p.x + lx, p.y + ly, p.z + lz);
				int cx = pos.getX() >> 4, cz = pos.getZ() >> 4;
				if (cx != lastCx || cz != lastCz) { chunk = level.hasChunk(cx, cz) ? level.getChunk(cx, cz) : null; lastCx = cx; lastCz = cz; }
				BlockState want = p.pal[v];
				var it = Plan.itemOf(want);
				if (chunk == null) { unknownRun++; continue; }
				if (status(want, chunk.getBlockState(pos)) == 0) { placedBlocksRun++; if (it.item() != Items.AIR) placedRun.merge(it.item(), it.n(), Integer::sum); }
			}
			if (scanIdx >= 4096) { scanSec++; scanIdx = 0; }
		}
	}

	/** Was noch fehlt (Item → Anzahl), für den ganzen Plan oder die aktuelle Ebene. */
	public static Map<Item, Integer> remaining()
	{
		var p = plan; var out = new HashMap<Item, Integer>(); if (p == null) return out;
		if (layerMode) {
			var mc = Minecraft.getInstance(); if (mc.level == null) return out;
			var pos = new BlockPos.MutableBlockPos();
			for (int z = 0; z < p.d; z++) for (int x = 0; x < p.w; x++) {
				int v = p.at(x, layer, z); if (v == 0) continue;
				BlockState want = p.pal[v]; pos.set(p.x + x, p.y + layer, p.z + z);
				if (mc.level.hasChunkAt(pos) && status(want, mc.level.getBlockState(pos)) == 0) continue;
				var it = Plan.itemOf(want); if (it.item() != Items.AIR) out.merge(it.item(), it.n(), Integer::sum);
			}
			return out;
		}
		for (var e : p.need.entrySet()) { int left = e.getValue() - placed.getOrDefault(e.getKey(), 0); if (left > 0) out.put(e.getKey(), left); }
		return out;
	}

	public static long placedBlocks() { return placedBlocks; }

	/* ---------- Rückmeldung an Blockplan ---------- */
	static void pushState()
	{
		var p = plan;
		var o = new JsonObject();
		o.addProperty("plan", p == null ? "" : p.name);
		o.addProperty("show", show); o.addProperty("mode", layerMode ? "layer" : "all"); o.addProperty("layer", layer);
		o.addProperty("placed", placedBlocks); o.addProperty("total", p == null ? 0 : p.blocks); o.addProperty("scanned", scanDone);
		o.addProperty("chests", chests.size());
		o.add("inv", items(inventory())); o.add("lager", items(storage()));
		String j = o.toString();
		if (j.equals(lastSent)) return;
		lastSent = j;
		de.blockplan.live.BlockplanLive.broadcast("bau", j);
	}

	static JsonObject items(Map<Item, Integer> m) {
		var o = new JsonObject();
		for (var e : m.entrySet()) o.addProperty(BuiltInRegistries.ITEM.getKey(e.getKey()).getPath(), e.getValue());
		return o;
	}
}
