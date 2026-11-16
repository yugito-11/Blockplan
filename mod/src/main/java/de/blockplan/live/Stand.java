package de.blockplan.live;

import net.minecraft.core.BlockPos;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.chunk.LevelChunk;
import net.minecraft.world.level.chunk.LevelChunkSection;
import net.minecraft.world.level.levelgen.Heightmap;

import java.io.BufferedReader;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.Writer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.IdentityHashMap;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.List;
import java.util.Map;
import java.util.concurrent.ConcurrentHashMap;
import java.util.zip.GZIPInputStream;
import java.util.zip.GZIPOutputStream;

/**
 * „Aktueller Stand“: Die Chunks rund um die Spieler (Oberfläche mit Gebäuden) für alle offenen Seiten.
 *
 * Seit 1.1.0 ereignisgesteuert: Ein Chunk wird gelesen, wenn er zum ersten Mal in die Nähe eines Spielers
 * kommt, und danach nur noch, wenn sich darin wirklich etwas geändert hat (gesammelt, höchstens alle
 * paar Sekunden). Gelesen wird über die Chunk-Abschnitte, leere Abschnitte werden übersprungen,
 * und das Ganze innerhalb des Zeitbudgets je Tick.
 */
final class Stand {
	static volatile int radius = 6;          // Chunks um jeden Spieler (im eigenen Spiel: Sichtweite)
	static volatile long settleMs = 2_000;   // nach einer Änderung so lange sammeln, dann einmal neu lesen
	static final int MAX_H = 320;            // höchstens so viele Blöcke hoch je Chunk
	static final int MAX_CHUNKS = 20_000;

	/** key = dim|cx|cz → fertiges JSON für das Ereignis „chunk“ */
	static final Map<String, String> chunks = new ConcurrentHashMap<>();
	private static final Map<String, Integer> hashes = new HashMap<>();
	/** einmal gelesen (oder gerade in der Warteschlange) */
	private static final java.util.Set<String> known = new java.util.HashSet<>();
	/** zu lesen: key → frühester Zeitpunkt */
	private static final LinkedHashMap<String, Long> todo = new LinkedHashMap<>();
	static volatile long lastUpdate = 0;

	static String key(String dim, int cx, int cz) { return dim + "|" + cx + "|" + cz; }

	/** Blockänderung in diesem Chunk: nach kurzer Pause neu lesen (nur Chunks, die wir schon zeigen). */
	static void dirty(String dim, int cx, int cz) {
		String k = key(dim, cx, cz);
		if (!known.contains(k)) return;
		todo.putIfAbsent(k, System.currentTimeMillis() + settleMs);
	}

	/** Ein Chunk wurde geladen: wenn wir ihn schon kennen, nachsehen, ob er anders ist. */
	static void loaded(String dim, int cx, int cz) {
		String k = key(dim, cx, cz);
		if (known.contains(k) && !chunks.containsKey(k)) todo.putIfAbsent(k, 0L);
	}

	static void tick(int tick, long deadline) {
		if (tick % 20 == 0) enqueueAroundPlayers();
		if (todo.isEmpty()) return;
		long now = System.currentTimeMillis();
		var it = todo.entrySet().iterator();
		List<String> retry = new ArrayList<>();
		while (it.hasNext() && System.nanoTime() < deadline) {
			var e = it.next();
			if (e.getValue() > now) continue;
			it.remove();
			String[] q = e.getKey().split("\\|");
			Level l = BlockplanLive.levelOf(q[0]);
			if (l == null || !scanChunk(l, Integer.parseInt(q[1]), Integer.parseInt(q[2]))) retry.add(e.getKey());
		}
		for (String k : retry) known.remove(k); // nicht geladen: beim nächsten Vorbeikommen wieder versuchen
	}

	private static void enqueueAroundPlayers() {
		for (Level l : BlockplanLive.levels.get()) {
			String d = BlockplanLive.dimId(l);
			for (Player p : l.players()) {
				int pcx = ((int) Math.floor(p.getX())) >> 4, pcz = ((int) Math.floor(p.getZ())) >> 4;
				for (int r = 0; r <= radius; r++)                // von innen nach außen: nahe Chunks zuerst
					for (int dx = -r; dx <= r; dx++)
						for (int dz = -r; dz <= r; dz++) {
							if (Math.max(Math.abs(dx), Math.abs(dz)) != r) continue;
							String key = key(d, pcx + dx, pcz + dz);
							if (known.add(key)) todo.putIfAbsent(key, 0L);
						}
			}
		}
		while (todo.size() > 4000) { String k = todo.keySet().iterator().next(); todo.remove(k); known.remove(k); }
	}

	/** Liest einen Chunk; false, wenn er gerade nicht geladen ist. */
	private static boolean scanChunk(Level l, int cx, int cz) {
		LevelChunk ch = l.getChunkSource().getChunkNow(cx, cz);
		if (ch == null) return false;
		int x0 = cx << 4, z0 = cz << 4, lo = Integer.MAX_VALUE, hi = Integer.MIN_VALUE;
		for (int x = 0; x < 16; x++)
			for (int z = 0; z < 16; z++) {
				int top = ch.getHeight(Heightmap.Types.WORLD_SURFACE, x, z);
				hi = Math.max(hi, top);
				int floor; // im Client gibt es die Meeresboden-Höhenkarte nicht
				try { floor = BlockplanLive.clientMode ? top - 12 : ch.getHeight(Heightmap.Types.OCEAN_FLOOR, x, z); } catch (Exception e) { floor = top - 12; }
				lo = Math.min(lo, floor);
			}
		// Im Baubereich bis zu dessen Boden lesen, auch unter Dächern (die Oberfläche wäre sonst das Dach)
		String dd = BlockplanLive.dimId(l);
		for (BlockplanLive.Watch w : BlockplanLive.watches)
			if (w.dim.equals(dd) && w.x0 < x0 + 16 && w.x0 + w.w > x0 && w.z0 < z0 + 16 && w.z0 + w.d > z0) { lo = Math.min(lo, w.y0); hi = Math.max(hi, Math.min(w.y0 + w.h - 1, hi + 64)); }
		int minY = l.getMinY(), maxY = l.getMinY() + l.getHeight() - 1;
		int y0 = Math.max(minY, lo - 4), h = Math.min(MAX_H, Math.max(1, Math.min(maxY, hi) - y0 + 1));
		BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
		Map<BlockState, Integer> pal = new IdentityHashMap<>();
		List<String> palJson = new ArrayList<>();
		palJson.add("[\"minecraft:air\",0]");
		StringBuilder rle = new StringBuilder();
		int last = -1, run = 0;
		for (int y = 0; y < h; y++) {
			int wy = y0 + y;
			LevelChunkSection sec = ch.getSection(l.getSectionIndex(wy));
			if (sec.hasOnlyAir()) {                            // ganze Lage leer: 256 Luft auf einmal
				if (last == 0) run += 256; else { if (run > 0) appendRun(rle, last, run); last = 0; run = 256; }
				continue;
			}
			for (int z = 0; z < 16; z++)
				for (int x = 0; x < 16; x++) {
					BlockState st = sec.getBlockState(x, wy & 15, z);
					int v = 0;
					if (!st.isAir()) {
						Integer i = pal.get(st);
						if (i == null) {
							i = palJson.size();
							pal.put(st, i);
							int col;
							pos.set(x0 + x, wy, z0 + z);
							try { col = st.getMapColor(l, pos).col; } catch (Exception e) { col = 0x808080; }
							palJson.add("[\"" + BlockplanLive.stateName(st) + "\"," + col + "]");
						}
						v = i;
					}
					if (v == last) run++;
					else {
						if (run > 0) appendRun(rle, last, run);
						last = v; run = 1;
					}
				}
		}
		appendRun(rle, last, run);
		String d = dd, key = key(d, cx, cz);
		int hash = rle.toString().hashCode() * 31 + y0 + 7 * palJson.hashCode();
		Integer old = hashes.get(key);
		if (old != null && old == hash) return true;
		if (old == null && chunks.size() >= MAX_CHUNKS) return true;
		hashes.put(key, hash);
		String json = "{\"d\":\"" + d + "\",\"cx\":" + cx + ",\"cz\":" + cz + ",\"y0\":" + y0 + ",\"h\":" + h
			+ ",\"p\":[" + String.join(",", palJson) + "],\"r\":\"" + rle + "\"}";
		chunks.put(key, json);
		lastUpdate = System.currentTimeMillis();
		for (WebServer.Client c : BlockplanLive.clients) c.send("chunk", json);
		return true;
	}

	private static void appendRun(StringBuilder sb, int v, int n) {
		if (sb.length() > 0) sb.append(',');
		sb.append(Integer.toString(v, 36));
		if (n > 1) sb.append('*').append(Integer.toString(n, 36));
	}

	static void reset() {
		chunks.clear(); hashes.clear(); known.clear(); todo.clear();
		lastUpdate = System.currentTimeMillis();
		for (WebServer.Client c : BlockplanLive.clients) c.send("standreset", "{}");
	}

	static void save(Path f) {
		try (Writer w = new OutputStreamWriter(new GZIPOutputStream(Files.newOutputStream(f)), StandardCharsets.UTF_8)) {
			for (String j : chunks.values()) { w.write(j); w.write('\n'); }
		} catch (Exception e) {
			BlockplanLive.LOG.warn("Blockplan Live: aktueller Stand konnte nicht gespeichert werden", e);
		}
	}

	static void load(Path f) {
		if (!Files.exists(f)) return;
		try (BufferedReader r = new BufferedReader(new InputStreamReader(new GZIPInputStream(Files.newInputStream(f)), StandardCharsets.UTF_8))) {
			String line;
			while ((line = r.readLine()) != null) {
				int a = line.indexOf("\"d\":\"") + 5, b = line.indexOf('"', a);
				int c1 = line.indexOf("\"cx\":") + 5, c2 = line.indexOf(',', c1);
				int z1 = line.indexOf("\"cz\":") + 5, z2 = line.indexOf(',', z1);
				String key = line.substring(a, b) + "|" + line.substring(c1, c2) + "|" + line.substring(z1, z2);
				chunks.put(key, line);
			}
			BlockplanLive.LOG.info("Blockplan Live: {} Chunks vom aktuellen Stand geladen", chunks.size());
		} catch (Exception e) {
			BlockplanLive.LOG.warn("Blockplan Live: aktueller Stand konnte nicht geladen werden", e);
		}
	}
}
