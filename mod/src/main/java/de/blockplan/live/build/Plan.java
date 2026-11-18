package de.blockplan.live.build;

import com.google.gson.JsonArray;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import net.minecraft.commands.arguments.blocks.BlockStateParser;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.world.item.Item;
import net.minecraft.world.item.Items;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.block.state.properties.BlockStateProperties;
import net.minecraft.world.level.block.state.properties.BedPart;
import net.minecraft.world.level.block.state.properties.DoubleBlockHalf;
import net.minecraft.world.level.block.state.properties.SlabType;

import java.util.HashMap;
import java.util.LinkedHashMap;
import java.util.Map;

/**
 * Ein Bauplan aus Blockplan: Größe, Startecke (Nordwest unten) und Blöcke.
 * Gespeichert in Abschnitten zu 16×16×16, leere Abschnitte gar nicht – auch Minas Tirith passt so in den Speicher.
 */
public final class Plan {
	public final String name, dim;
	public final int x, y, z, w, h, d;
	public final BlockState[] pal;     // Index 0 = Luft
	final Map<Long, char[]> sections = new HashMap<>();
	public final long blocks;
	/** Benötigte Items für den ganzen Plan */
	public final Map<Item, Integer> need = new LinkedHashMap<>();

	private Plan(String name, String dim, int x, int y, int z, int w, int h, int d, BlockState[] pal, String cells) {
		this.name = name; this.dim = dim; this.x = x; this.y = y; this.z = z; this.w = w; this.h = h; this.d = d; this.pal = pal;
		long n = 0, i = 0, total = (long) w * h * d;
		int start = 0, len = cells.length();
		var count = new long[pal.length];
		while (start < len && i < total) {
			int end = cells.indexOf(',', start); if (end < 0) end = len;
			int star = cells.indexOf('*', start);
			int v; long c;
			if (star >= 0 && star < end) { v = Integer.parseInt(cells, start, star, 36); c = Long.parseLong(cells, star + 1, end, 36); }
			else { v = Integer.parseInt(cells, start, end, 36); c = 1; }
			if (v > 0 && v < pal.length && pal[v] != null && !pal[v].isAir()) {
				count[v] += c;
				for (long k = 0; k < c && i + k < total; k++) put(i + k, (char) v);
				n += c;
			}
			i += c; start = end + 1;
		}
		blocks = n;
		for (int v = 1; v < pal.length; v++) if (count[v] > 0 && pal[v] != null) {
			var it = itemOf(pal[v]);
			if (it.item != Items.AIR) need.merge(it.item, (int) Math.min(Integer.MAX_VALUE, count[v] * it.n), Integer::sum);
		}
	}

	private void put(long idx, char v) {
		int lx = (int) (idx % w), lz = (int) (idx / w % d), ly = (int) (idx / ((long) w * d));
		long key = key(lx >> 4, ly >> 4, lz >> 4);
		var s = sections.computeIfAbsent(key, k -> new char[4096]);
		s[((ly & 15) << 8) | ((lz & 15) << 4) | (lx & 15)] = v;
	}

	static long key(int sx, int sy, int sz) { return ((long) sy << 42) | ((long) sz << 21) | sx; }

	/** Palettenindex an einer Stelle im Plan (lokale Koordinaten), 0 = Luft oder außerhalb. */
	public int at(int lx, int ly, int lz) {
		if (lx < 0 || ly < 0 || lz < 0 || lx >= w || ly >= h || lz >= d) return 0;
		var s = sections.get(key(lx >> 4, ly >> 4, lz >> 4));
		return s == null ? 0 : s[((ly & 15) << 8) | ((lz & 15) << 4) | (lx & 15)];
	}

	public BlockState stateAt(int lx, int ly, int lz) { int v = at(lx, ly, lz); return v == 0 ? null : pal[v]; }

	public Iterable<Map.Entry<Long, char[]>> sections() { return sections.entrySet(); }

	/** Projekt-JSON aus Blockplan (w, d, h, pal, cells, origin, name) plus Dimension. */
	public static Plan parse(String json) {
		JsonObject o = JsonParser.parseString(json).getAsJsonObject();
		int w = o.get("w").getAsInt(), d = o.get("d").getAsInt(), h = o.get("h").getAsInt();
		JsonObject or = o.has("origin") ? o.getAsJsonObject("origin") : new JsonObject();
		int x = or.has("x") ? or.get("x").getAsInt() : 0, y = or.has("y") ? or.get("y").getAsInt() : 64, z = or.has("z") ? or.get("z").getAsInt() : 0;
		String name = o.has("name") ? o.get("name").getAsString() : "Plan";
		String dim = o.has("dim") && !o.get("dim").isJsonNull() ? o.get("dim").getAsString() : "minecraft:overworld";
		JsonArray pa = o.getAsJsonArray("pal");
		var pal = new BlockState[pa.size() + 1];
		pal[0] = Blocks.AIR.defaultBlockState();
		for (int k = 0; k < pa.size(); k++) pal[k + 1] = state(pa.get(k).getAsString());
		return new Plan(name, dim, x, y, z, w, h, d, pal, o.get("cells").getAsString());
	}

	/** „oak_stairs[facing=east]“ → Blockzustand; Unbekanntes wird zu Luft (im Spiel nicht vorhanden). */
	static BlockState state(String s) {
		String id = s.contains(":") && s.indexOf(':') < (s.indexOf('[') < 0 ? s.length() : s.indexOf('[')) ? s : "minecraft:" + s;
		try { return BlockStateParser.parseForBlock(BuiltInRegistries.BLOCK, id, false).blockState(); }
		catch (Exception e) {
			int br = id.indexOf('[');
			if (br > 0) try { return BlockStateParser.parseForBlock(BuiltInRegistries.BLOCK, id.substring(0, br), false).blockState(); } catch (Exception ignored) {}
			return Blocks.AIR.defaultBlockState();
		}
	}

	public record ItemCount(Item item, int n) {}

	/** Item und Anzahl für einen Blockzustand: doppelte Stufe = 2, obere Türhälfte und Bettkopf = 0, Wasser = keins. */
	public static ItemCount itemOf(BlockState s) {
		Block b = s.getBlock();
		if (s.isAir() || b == Blocks.WATER || b == Blocks.LAVA || b == Blocks.BUBBLE_COLUMN || b == Blocks.FIRE) return new ItemCount(Items.AIR, 0);
		if (s.hasProperty(BlockStateProperties.DOUBLE_BLOCK_HALF) && s.getValue(BlockStateProperties.DOUBLE_BLOCK_HALF) == DoubleBlockHalf.UPPER) return new ItemCount(Items.AIR, 0);
		if (s.hasProperty(BlockStateProperties.BED_PART) && s.getValue(BlockStateProperties.BED_PART) == BedPart.HEAD) return new ItemCount(Items.AIR, 0);
		int n = 1;
		if (s.hasProperty(BlockStateProperties.SLAB_TYPE) && s.getValue(BlockStateProperties.SLAB_TYPE) == SlabType.DOUBLE) n = 2;
		if (s.hasProperty(BlockStateProperties.CANDLES)) n = s.getValue(BlockStateProperties.CANDLES);
		if (s.hasProperty(BlockStateProperties.PICKLES)) n = s.getValue(BlockStateProperties.PICKLES);
		if (s.hasProperty(BlockStateProperties.LAYERS) && b == Blocks.SNOW) n = s.getValue(BlockStateProperties.LAYERS);
		Item it = b.asItem();
		return new ItemCount(it, it == Items.AIR ? 0 : n);
	}
}
