package de.blockplan.live;

import net.fabricmc.api.EnvType;
import net.fabricmc.api.ModInitializer;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerChunkEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.fabricmc.loader.api.FabricLoader;
import net.minecraft.commands.arguments.blocks.BlockStateParser;
import net.minecraft.core.BlockPos;
import net.minecraft.core.registries.BuiltInRegistries;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.Block;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.level.chunk.LevelChunk;
import net.minecraft.world.level.chunk.LevelChunkSection;
import org.slf4j.Logger;
import org.slf4j.LoggerFactory;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.SecureRandom;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.HashSet;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Properties;
import java.util.concurrent.CopyOnWriteArrayList;
import java.util.function.Supplier;

/**
 * Blockplan Live: beobachtet Baubereiche, die Blockplan anfordert, und schickt Blockänderungen
 * und Spielerpositionen per Server-Sent Events. Weltzugriffe passieren nur im Tick.
 *
 * Seit 1.1.0: Der Baubereich wird nur einmal gelesen (Chunk-Abschnitte direkt, leere übersprungen,
 * mit festem Zeitbudget je Tick). Danach kommen Änderungen als Ereignisse aus LevelChunk.setBlockState
 * statt durch ständiges Abtasten. Gesendet werden Blockzustände (z. B. Treppenrichtung), nicht nur Blöcke.
 */
public class BlockplanLive implements ModInitializer {
	public static final Logger LOG = LoggerFactory.getLogger("blockplan");
	/** Höchstens so viel Zeit je Tick fürs Lesen der Welt (Baubereiche und aktueller Stand zusammen). */
	static long budgetNanos = 3_000_000L;

	static MinecraftServer server;
	static WebServer web;
	static Path dir;
	static Properties cfg = new Properties();
	static final List<Watch> watches = new CopyOnWriteArrayList<>();
	static final List<WebServer.Client> clients = new CopyOnWriteArrayList<>();
	static volatile String playersJson = "[]";
	static volatile String worldsJson = "[]";
	private static int tick;
	/** Woher die Welt kommt: Server (alle Dimensionen) oder Client (die Welt, die das eigene Spiel geladen hat) */
	static Supplier<Iterable<? extends Level>> levels = List::of;
	static volatile boolean clientMode = false;
	static Path standFile;

	// Palette: Index 0 = Luft, 1 = nicht geladener Chunk
	static final List<String> palIds = new ArrayList<>(List.of("minecraft:air", "blockplan:unloaded"));
	static final List<Integer> palColors = new ArrayList<>(List.of(0, 0x808080));
	private static final Map<BlockState, Integer> palIndex = new IdentityHashMap<>();
	private static final Map<BlockState, String> stateNames = new IdentityHashMap<>();

	@Override
	public void onInitialize() {
		dir = FabricLoader.getInstance().getConfigDir().resolve("blockplan");
		clientMode = FabricLoader.getInstance().getEnvironmentType() == EnvType.CLIENT;
		loadConfig();
		standFile = dir.resolve("stand.jsonl.gz");
		if (clientMode) return; // im Client übernimmt BlockplanLiveClient
		ServerLifecycleEvents.SERVER_STARTED.register(s -> {
			server = s;
			levels = s::getAllLevels;
			StringBuilder w = new StringBuilder("[");
			for (ServerLevel l : s.getAllLevels()) {
				if (w.length() > 1) w.append(',');
				w.append('"').append(dimId(l)).append('"');
			}
			worldsJson = w.append(']').toString();
			Stand.load(standFile);
			startWeb("<server-adresse>");
		});
		ServerLifecycleEvents.SERVER_STOPPING.register(s -> {
			if (web != null) web.stop();
			Stand.save(standFile);
			watches.clear();
			server = null;
		});
		ServerTickEvents.END_SERVER_TICK.register(s -> tickCommon());
		ServerChunkEvents.CHUNK_LOAD.register((level, chunk, generated) -> Changes.chunkLoaded(level, chunk.getPos().x(), chunk.getPos().z()));
	}

	static void startWeb(String host) {
		try {
			web = new WebServer(cfg.getProperty("bind", clientMode ? "127.0.0.1" : "0.0.0.0"), Integer.parseInt(cfg.getProperty("port", "8100")), cfg.getProperty("token"), dir.resolve("projects"), cfg.getProperty("admin"));
			web.start();
			LOG.info("Blockplan Live läuft: {}", link(host));
			if (!clientMode) LOG.info("Blockplan Live: Admin-Passwort (Lage ändern, aktuellen Stand zurücksetzen): {}", cfg.getProperty("admin"));
		} catch (Exception e) {
			LOG.error("Blockplan Live konnte den Webserver nicht starten", e);
		}
	}

	static String link(String host) { return "http://" + host + ":" + cfg.getProperty("port", "8100") + "/?t=" + cfg.getProperty("token"); }

	static void stopWeb() { if (web != null) web.stop(); web = null; Stand.save(standFile); }

	/** Minecraft-Version für die Texturen (Server und Client gleich) */
	static String mcVersion() {
		return FabricLoader.getInstance().getModContainer("minecraft").map(c -> c.getMetadata().getVersion().getFriendlyString()).orElse("");
	}

	/** Client: anderer Server oder Einzelspieler → eigener „aktueller Stand“ je Server */
	static void switchWorld(String key) {
		Stand.save(standFile);
		watches.clear();
		Stand.reset();
		standFile = dir.resolve("stand-" + key.replaceAll("[^A-Za-z0-9._-]", "_") + ".jsonl.gz");
		Stand.load(standFile);
		for (WebServer.Client c : clients) for (String j : Stand.chunks.values()) c.send("chunk", j);
	}

	private void loadConfig() {
		Path f = dir.resolve("config.properties");
		try {
			Files.createDirectories(dir.resolve("projects"));
			if (Files.exists(f)) try (var in = Files.newBufferedReader(f)) { cfg.load(in); }
			boolean dirty = false;
			if (cfg.getProperty("token", "").isBlank()) {
				SecureRandom r = new SecureRandom();
				StringBuilder t = new StringBuilder();
				String abc = "abcdefghijkmnpqrstuvwxyz23456789";
				for (int i = 0; i < 20; i++) t.append(abc.charAt(r.nextInt(abc.length())));
				cfg.setProperty("token", t.toString());
				dirty = true;
			}
			if (cfg.getProperty("admin", "").isBlank()) {
				SecureRandom r = new SecureRandom();
				StringBuilder t = new StringBuilder();
				String abc = "abcdefghijkmnpqrstuvwxyz23456789";
				for (int i = 0; i < 10; i++) t.append(abc.charAt(r.nextInt(abc.length())));
				cfg.setProperty("admin", t.toString());
				dirty = true;
			}
			if (cfg.getProperty("port") == null) { cfg.setProperty("port", "8100"); dirty = true; }
			if (cfg.getProperty("bind") == null) { cfg.setProperty("bind", clientMode ? "127.0.0.1" : "0.0.0.0"); dirty = true; }
			if (dirty) try (var out = Files.newBufferedWriter(f)) {
				cfg.store(out, "Blockplan Live. Link: http://<server-adresse>:<port>/?t=<token>");
			}
		} catch (IOException e) {
			LOG.error("Blockplan Live: Konfiguration nicht lesbar", e);
		}
	}

	static String dimId(Level l) {
		return l.dimension().identifier().toString();
	}

	/** Name eines Blockzustands wie im Befehl /setblock, z. B. minecraft:oak_stairs[facing=east,half=bottom,…] */
	static synchronized String stateName(BlockState s) {
		return stateNames.computeIfAbsent(s, BlockStateParser::serialize);
	}

	static synchronized int palette(BlockState s, Level l, BlockPos p) {
		if (s.isAir()) return 0;
		Integer i = palIndex.get(s);
		if (i != null) return i;
		int idx = palIds.size();
		palIds.add(stateName(s));
		int col;
		try { col = s.getMapColor(l, p).col; } catch (Exception e) { col = 0x808080; }
		palColors.add(col);
		palIndex.put(s, idx);
		return idx;
	}

	static synchronized String paletteJson(int from) {
		StringBuilder sb = new StringBuilder("{\"from\":").append(from).append(",\"e\":[");
		for (int i = from; i < palIds.size(); i++) {
			if (i > from) sb.append(',');
			sb.append("[\"").append(palIds.get(i)).append("\",").append(palColors.get(i)).append(']');
		}
		return sb.append("]}").toString();
	}

	static synchronized int paletteSize() { return palIds.size(); }

	static void tickCommon() {
		tick++;
		Changes.active = !clients.isEmpty();
		if (tick % 20 == 0) updateWorlds();
		if (tick % 2 == 0) updatePlayers(); // 10x pro Sekunde, damit die Kamera flüssig folgt
		if (tick % 6000 == 0) Stand.save(standFile);
		if (clients.isEmpty()) { Changes.clear(); return; }
		long deadline = System.nanoTime() + budgetNanos;
		processChanges();
		for (Watch w : watches) {
			if (w.client.closed) { watches.remove(w); continue; }
			Level level = levelOf(w.dim);
			if (level != null) w.work(level, deadline);
			w.flush();
		}
		Stand.tick(tick, deadline);
	}

	static Level levelOf(String dim) {
		for (Level l : levels.get()) if (dimId(l).equals(dim)) return l;
		return null;
	}

	/** Gesammelte Blockänderungen an die betroffenen Baubereiche und den aktuellen Stand verteilen. */
	private static void processChanges() {
		BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();
		Map<Level, String> dims = new IdentityHashMap<>();
		Changes.Change c; int n = 0;
		while (n++ < 500_000 && (c = Changes.queue.poll()) != null) {
			String dim = dims.computeIfAbsent(c.level(), BlockplanLive::dimId);
			pos.set(BlockPos.getX(c.pos()), BlockPos.getY(c.pos()), BlockPos.getZ(c.pos()));
			Stand.dirty(dim, pos.getX() >> 4, pos.getZ() >> 4);
			for (Watch w : watches) {
				if (!w.dim.equals(dim)) continue;
				long i = w.index(pos.getX(), pos.getY(), pos.getZ());
				if (i >= 0) w.change(i, palette(c.state(), c.level(), pos));
			}
		}
		Changes.Load ld;
		while ((ld = Changes.loads.poll()) != null) {
			String dim = dimId(ld.level());
			for (Watch w : watches) if (w.dim.equals(dim)) w.chunkLoaded(ld.cx(), ld.cz());
			Stand.loaded(dim, ld.cx(), ld.cz());
		}
	}

	private static void updateWorlds() {
		StringBuilder w = new StringBuilder("[");
		for (Level l : levels.get()) { if (w.length() > 1) w.append(','); w.append('"').append(dimId(l)).append('"'); }
		String j = w.append(']').toString();
		if (!j.equals(worldsJson) && !j.equals("[]")) { worldsJson = j; for (WebServer.Client c : clients) c.send("hello", "{\"worlds\":" + j + ",\"local\":" + clientMode + "}"); }
	}

	private static void updatePlayers() {
		StringBuilder sb = new StringBuilder("[");
		for (Level l : levels.get()) {
			String d = dimId(l);
			for (Player p : l.players()) {
				Skins.remember(p);
				if (sb.length() > 1) sb.append(',');
				sb.append("{\"n\":\"").append(jsonEsc(p.getName().getString()))
					.append("\",\"u\":\"").append(p.getUUID())
					.append("\",\"d\":\"").append(d)
					.append("\",\"x\":").append(fmt(p.getX()))
					.append(",\"y\":").append(fmt(p.getY()))
					.append(",\"z\":").append(fmt(p.getZ()))
					.append(",\"yaw\":").append(fmt(p.getYRot())).append('}');
			}
		}
		String json = sb.append(']').toString();
		if (!json.equals(playersJson)) {
			playersJson = json;
			for (WebServer.Client c : clients) c.send("players", json);
		}
	}

	static String fmt(double v) { return String.format(Locale.ROOT, "%.2f", v); }

	static String jsonEsc(String s) {
		StringBuilder b = new StringBuilder();
		for (char c : s.toCharArray()) {
			if (c == '"' || c == '\\') b.append('\\').append(c);
			else if (c < 0x20) b.append(String.format("\\u%04x", (int) c));
			else b.append(c);
		}
		return b.toString();
	}

	/**
	 * Ein beobachteter Quader in einer Dimension. Wird einmal gelesen (Snapshot, über mehrere Ticks mit
	 * Zeitbudget), danach kommen nur noch Änderungen. Chunks, die beim Lesen nicht geladen waren, werden
	 * nachgeliefert, sobald sie laden.
	 */
	static final class Watch {
		final WebServer.Client client;
		final String dim;
		final int x0, y0, z0, w, h, d;
		final long total;
		final int cx0, cz0, ncx, ncz;
		long cursor = 0;
		int sentPal = 0;
		boolean full = false;
		private StringBuilder rle = new StringBuilder();
		private int runV = -1; private long runN = 0;
		private StringBuilder pend;
		private final HashSet<Long> missing = new HashSet<>();          // beim Lesen nicht geladene Chunks
		private final ArrayDeque<int[]> refill = new ArrayDeque<>();     // nachzuliefernde Chunk-Ausschnitte
		private final BlockPos.MutableBlockPos pos = new BlockPos.MutableBlockPos();

		Watch(WebServer.Client client, String dim, int x0, int y0, int z0, int w, int h, int d) {
			this.client = client; this.dim = dim;
			this.x0 = x0; this.y0 = y0; this.z0 = z0; this.w = w; this.h = h; this.d = d;
			this.total = (long) w * h * d;
			cx0 = x0 >> 4; cz0 = z0 >> 4; ncx = ((x0 + w - 1) >> 4) - cx0 + 1; ncz = ((z0 + d - 1) >> 4) - cz0 + 1;
		}

		long index(int wx, int wy, int wz) {
			int x = wx - x0, y = wy - y0, z = wz - z0;
			if (x < 0 || y < 0 || z < 0 || x >= w || y >= h || z >= d) return -1;
			return ((long) y * d + z) * w + x;
		}

		void change(long i, int v) {
			if (!full) return; // der Snapshot liest den neuen Stand ohnehin noch
			if (pend == null) pend = new StringBuilder("[");
			else pend.append(',');
			pend.append(i).append(',').append(v);
		}

		void chunkLoaded(int cx, int cz) {
			if (cx < cx0 || cz < cz0 || cx >= cx0 + ncx || cz >= cz0 + ncz) return;
			if (missing.remove(((long) cx << 32) ^ (cz & 0xffffffffL)) || full) refill.add(new int[]{cx, cz});
		}

		/** Blockwert an einer Stelle; Chunk-Spalten werden für die Dauer eines Ticks zwischengespeichert. */
		private int value(Level level, LevelChunk[] cols, boolean[] got, int wx, int wy, int wz) {
			int ci = ((wz >> 4) - cz0) * ncx + ((wx >> 4) - cx0);
			if (!got[ci]) { got[ci] = true; cols[ci] = level.getChunkSource().getChunkNow(wx >> 4, wz >> 4); }
			LevelChunk ch = cols[ci];
			if (ch == null) { if (!full) missing.add(((long) (wx >> 4) << 32) ^ ((wz >> 4) & 0xffffffffL)); return 1; }
			if (wy < level.getMinY() || wy >= level.getMinY() + level.getHeight()) return 0;
			LevelChunkSection sec = ch.getSection(level.getSectionIndex(wy));
			if (sec.hasOnlyAir()) return 0;
			BlockState st = sec.getBlockState(wx & 15, wy & 15, wz & 15);
			if (st.isAir()) return 0;
			pos.set(wx, wy, wz);
			return palette(st, level, pos);
		}

		void work(Level level, long deadline) {
			LevelChunk[] cols = new LevelChunk[ncx * ncz]; boolean[] got = new boolean[ncx * ncz];
			// 1. Snapshot in der Reihenfolge der Zellen, als RLE
			int k = 0;
			while (!full) {
				long i = cursor;
				int x = (int) (i % w), z = (int) ((i / w) % d), y = (int) (i / ((long) w * d));
				int v = value(level, cols, got, x0 + x, y0 + y, z0 + z);
				if (v == runV) runN++;
				else { appendRun(); runV = v; runN = 1; }
				if (++cursor >= total) {
					appendRun(); full = true; sendPalette();
					client.send("snapshot", "\"" + rle + "\"");
					rle = null;
					break;
				}
				if ((++k & 4095) == 0 && System.nanoTime() > deadline) return;
			}
			// 2. nachgeladene Chunks nachliefern
			while (!refill.isEmpty() && System.nanoTime() < deadline) {
				int[] c = refill.poll();
				int ax = Math.max(x0, c[0] << 4), bx = Math.min(x0 + w - 1, (c[0] << 4) + 15);
				int az = Math.max(z0, c[1] << 4), bz = Math.min(z0 + d - 1, (c[1] << 4) + 15);
				java.util.Arrays.fill(got, false);
				for (int y = 0; y < h; y++) for (int z = az; z <= bz; z++) for (int x = ax; x <= bx; x++) {
					int v = value(level, cols, got, x, y0 + y, z);
					if (pend == null) pend = new StringBuilder("["); else pend.append(',');
					pend.append(((long) y * d + (z - z0)) * w + (x - x0)).append(',').append(v);
				}
			}
		}

		private void appendRun() {
			if (runN <= 0) return;
			if (rle.length() > 0) rle.append(',');
			rle.append(Integer.toString(runV, 36));
			if (runN > 1) rle.append('*').append(Long.toString(runN, 36));
		}

		void flush() {
			if (pend == null) return;
			sendPalette();
			client.send("changes", pend.append(']').toString());
			pend = null;
		}

		private void sendPalette() {
			int size = paletteSize();
			if (size > sentPal) { client.send("palette", paletteJson(sentPal)); sentPal = size; }
		}
	}
}
