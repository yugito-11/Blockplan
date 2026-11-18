package de.blockplan.live;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import com.sun.net.httpserver.HttpExchange;
import com.sun.net.httpserver.HttpServer;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.URLDecoder;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.security.MessageDigest;
import java.util.HashMap;
import java.util.Map;
import java.util.concurrent.Executors;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.regex.Pattern;

/** Kleiner HTTP-Server: liefert die Oberfläche, Projekte und einen Live-Stream (Server-Sent Events). */
final class WebServer {
	private static final Pattern ID = Pattern.compile("[A-Za-z0-9_-]{1,64}");
	private static final int MAX_BODY = 8 * 1024 * 1024;
	private static final long MAX_VOLUME = 400_000_000L; // seit 1.1.0 nur einmal gelesen, kein Zwischenspeicher je Feld

	private final HttpServer http;
	private final String token;
	private final Path projects;
	private final String admin;
	static volatile String overlay = "{}";

	WebServer(String bind, int port, String token, Path projects, String admin) throws IOException {
		this.token = token;
		this.admin = admin;
		this.projects = projects;
		http = HttpServer.create(new InetSocketAddress(bind, port), 0);
		http.setExecutor(Executors.newCachedThreadPool(r -> {
			Thread t = new Thread(r, "blockplan-web");
			t.setDaemon(true);
			return t;
		}));
		http.createContext("/", this::handle);
	}

	void start() { http.start(); }

	void stop() {
		for (Client c : BlockplanLive.clients) c.closed = true;
		http.stop(0);
	}

	/** Eine offene Live-Verbindung. send() darf von jedem Thread aufgerufen werden. */
	static final class Client {
		final LinkedBlockingQueue<String> q = new LinkedBlockingQueue<>();
		volatile boolean closed;

		void send(String event, String data) {
			if (closed) return;
			if (q.size() > 30000) { closed = true; return; } // zu langsamer Empfänger
			q.offer("event: " + event + "\ndata: " + data + "\n\n");
		}
	}

	private void handle(HttpExchange ex) throws IOException {
		try {
			String path = ex.getRequestURI().getPath();
			Map<String, String> q = query(ex.getRequestURI().getRawQuery());
			if (path.equals("/") || path.equals("/index.html")) { page(ex); return; }
			if (path.equals("/overlay.html")) { resource(ex, "/blockplan/overlay.html", "text/html; charset=utf-8"); return; }
			if (!path.startsWith("/api/")) { send(ex, 404, "text/plain", "Nicht gefunden"); return; }
			if (!authorized(ex, q)) { send(ex, 401, "application/json", "{\"error\":\"token\"}"); return; }
			if (path.equals("/api/info")) {
				send(ex, 200, "application/json", "{\"worlds\":" + BlockplanLive.worldsJson + ",\"players\":" + BlockplanLive.playersJson + "}");
			} else if (path.equals("/api/stream")) {
				stream(ex, q);
			} else if (path.startsWith("/api/skin/")) {
				byte[] png = Skins.get(path.substring("/api/skin/".length()));
				if (png == null) { send(ex, 404, "text/plain", "kein Skin"); return; }
				ex.getResponseHeaders().set("Content-Type", "image/png");
				ex.getResponseHeaders().set("Cache-Control", "max-age=3600");
				ex.sendResponseHeaders(200, png.length);
				try (OutputStream o = ex.getResponseBody()) { o.write(png); }
			} else if (path.equals("/api/textures.zip")) {
				String ver = BlockplanLive.mcVersion();
				Path f = Textures.get(projects.getParent(), ver);
				if (f == null) {
					send(ex, 202, "application/json", "{\"status\":\"" + (Textures.error == null ? "laedt" : "fehler") + "\",\"version\":\"" + BlockplanLive.jsonEsc(ver) + "\""
						+ (Textures.error == null ? "" : ",\"error\":\"" + BlockplanLive.jsonEsc(Textures.error) + "\"") + "}");
					return;
				}
				byte[] b = Files.readAllBytes(f);
				ex.getResponseHeaders().set("Content-Type", "application/zip");
				ex.getResponseHeaders().set("X-Minecraft-Version", ver);
				ex.sendResponseHeaders(200, b.length);
				try (OutputStream o = ex.getResponseBody()) { o.write(b); }
			} else if (path.equals("/api/admin/check")) {
				send(ex, isAdmin(ex) ? 200 : 403, "application/json", isAdmin(ex) ? "{\"ok\":true}" : "{\"error\":\"admin\"}");
			} else if (path.equals("/api/overlay") && "POST".equals(ex.getRequestMethod())) {
				byte[] body = ex.getRequestBody().readNBytes(200_001);
				if (body.length > 200_000) { send(ex, 413, "application/json", "{}"); return; }
				overlay = new String(body, StandardCharsets.UTF_8).replace('\n', ' ');
				for (Client c : BlockplanLive.clients) c.send("overlay", overlay);
				send(ex, 200, "application/json", "{\"ok\":true}");
			} else if (path.equals("/api/plan") && "POST".equals(ex.getRequestMethod())) {
				if (BlockplanLive.planHandler == null) { send(ex, 404, "application/json", "{\"error\":\"nur im eigenen Spiel\"}"); return; }
				String body = new String(ex.getRequestBody().readAllBytes(), StandardCharsets.UTF_8);
				try { BlockplanLive.planHandler.accept(body); } catch (Exception e) { send(ex, 400, "application/json", "{\"error\":\"" + BlockplanLive.jsonEsc(e.toString()) + "\"}"); return; }
				send(ex, 200, "application/json", "{\"ok\":true}");
			} else if (path.equals("/api/plan") && "DELETE".equals(ex.getRequestMethod())) {
				if (BlockplanLive.planClear != null) BlockplanLive.planClear.run();
				send(ex, 200, "application/json", "{\"ok\":true}");
			} else if (path.equals("/api/view") && "POST".equals(ex.getRequestMethod())) {
				if (BlockplanLive.viewHandler == null) { send(ex, 404, "application/json", "{}"); return; }
				BlockplanLive.viewHandler.accept(new String(ex.getRequestBody().readNBytes(10_000), StandardCharsets.UTF_8));
				send(ex, 200, "application/json", "{\"ok\":true}");
			} else if (path.equals("/api/stand/reset") && "POST".equals(ex.getRequestMethod())) {
				if (!isAdmin(ex)) { send(ex, 403, "application/json", "{\"error\":\"admin\"}"); return; }
				Stand.reset();
				send(ex, 200, "application/json", "{\"ok\":true}");
			} else if (path.equals("/api/projects")) {
				listProjects(ex);
			} else if (path.startsWith("/api/projects/")) {
				project(ex, path.substring("/api/projects/".length()));
			} else send(ex, 404, "application/json", "{\"error\":\"not_found\"}");
		} catch (IOException e) {
			throw e;
		} catch (Exception e) {
			BlockplanLive.LOG.warn("Blockplan Live: Anfrage fehlgeschlagen", e);
			try { send(ex, 500, "application/json", "{\"error\":\"server\"}"); } catch (IOException ignored) {}
		} finally {
			ex.close();
		}
	}

	private boolean isAdmin(HttpExchange ex) {
		if (BlockplanLive.clientMode) return true; // im eigenen Spiel, nur auf diesem PC erreichbar
		String a = ex.getRequestHeaders().getFirst("X-Blockplan-Admin");
		return a != null && admin != null && MessageDigest.isEqual(a.getBytes(StandardCharsets.UTF_8), admin.getBytes(StandardCharsets.UTF_8));
	}

	private void resource(HttpExchange ex, String res, String type) throws IOException {
		try (InputStream in = BlockplanLive.class.getResourceAsStream(res)) {
			if (in == null) { send(ex, 404, "text/plain", "fehlt"); return; }
			byte[] b = in.readAllBytes();
			ex.getResponseHeaders().set("Content-Type", type);
			ex.getResponseHeaders().set("Cache-Control", "no-cache");
			ex.sendResponseHeaders(200, b.length);
			try (OutputStream o = ex.getResponseBody()) { o.write(b); }
		}
	}

	private boolean authorized(HttpExchange ex, Map<String, String> q) {
		String t = q.get("t");
		if (t == null) t = ex.getRequestHeaders().getFirst("X-Blockplan-Token");
		if (t == null || token == null) return false;
		return MessageDigest.isEqual(t.getBytes(StandardCharsets.UTF_8), token.getBytes(StandardCharsets.UTF_8));
	}

	private void page(HttpExchange ex) throws IOException {
		String html;
		try (InputStream in = BlockplanLive.class.getResourceAsStream("/blockplan/index.html")) {
			html = in == null ? "Oberfläche fehlt im Mod." : new String(in.readAllBytes(), StandardCharsets.UTF_8);
		}
		String doc = "<!doctype html><html><head><meta charset=utf-8><meta name=viewport content=\"width=device-width,initial-scale=1,viewport-fit=cover\">"
			+ "<style>:root{color-scheme:light}body{margin:0;font:14px system-ui,sans-serif}img{max-width:100%}[hidden]{display:none!important}</style>"
			+ "<script>window.BLOCKPLAN_LIVE=true</script></head><body>" + html + "</body></html>";
		ex.getResponseHeaders().set("Cache-Control", "no-cache");
		send(ex, 200, "text/html; charset=utf-8", doc);
	}

	private void stream(HttpExchange ex, Map<String, String> q) throws IOException {
		Client c = new Client();
		ex.getResponseHeaders().set("Content-Type", "text/event-stream; charset=utf-8");
		ex.getResponseHeaders().set("Cache-Control", "no-cache");
		ex.getResponseHeaders().set("X-Accel-Buffering", "no");
		ex.sendResponseHeaders(200, 0);
		c.send("hello", "{\"worlds\":" + BlockplanLive.worldsJson + ",\"local\":" + BlockplanLive.clientMode + "}");
		c.send("players", BlockplanLive.playersJson);
		c.send("overlay", overlay);
		if (BlockplanLive.bauJson != null) c.send("bau", BlockplanLive.bauJson);
		for (String j : Stand.chunks.values()) c.send("chunk", j);
		BlockplanLive.clients.add(c);
		int w = num(q, "w"), h = num(q, "h"), d = num(q, "d");
		if (w > 0 && h > 0 && d > 0) {
			if ((long) w * h * d > MAX_VOLUME) c.send("fail", "{\"error\":\"zu_gross\"}");
			else BlockplanLive.watches.add(new BlockplanLive.Watch(c, q.getOrDefault("dim", "minecraft:overworld"),
				num(q, "x"), num(q, "y"), num(q, "z"), w, h, d));
		}
		OutputStream out = ex.getResponseBody();
		try {
			out.write(": ok\n\n".getBytes(StandardCharsets.UTF_8));
			out.flush();
			while (!c.closed) {
				String m = c.q.poll(15, TimeUnit.SECONDS);
				out.write((m == null ? ": ping\n\n" : m).getBytes(StandardCharsets.UTF_8));
				out.flush();
			}
		} catch (IOException | InterruptedException e) {
			// Browser hat die Verbindung geschlossen
		} finally {
			c.closed = true;
			BlockplanLive.clients.remove(c);
			BlockplanLive.watches.removeIf(wt -> wt.client == c);
		}
	}

	private void listProjects(HttpExchange ex) throws IOException {
		StringBuilder sb = new StringBuilder("[");
		try (var files = Files.list(projects)) {
			for (Path f : (Iterable<Path>) files::iterator) {
				if (!f.toString().endsWith(".json")) continue;
				try {
					JsonObject o = JsonParser.parseString(Files.readString(f)).getAsJsonObject();
					if (sb.length() > 1) sb.append(',');
					sb.append("{\"id\":\"").append(BlockplanLive.jsonEsc(o.get("id").getAsString()))
						.append("\",\"name\":\"").append(BlockplanLive.jsonEsc(o.get("name").getAsString()))
						.append("\",\"updated\":").append(o.has("updated") ? o.get("updated").getAsLong() : 0).append('}');
				} catch (Exception ignored) {}
			}
		}
		send(ex, 200, "application/json", sb.append(']').toString());
	}

	private void project(HttpExchange ex, String id) throws IOException {
		if (!ID.matcher(id).matches()) { send(ex, 400, "application/json", "{\"error\":\"id\"}"); return; }
		Path f = projects.resolve(id + ".json");
		switch (ex.getRequestMethod()) {
			case "GET" -> {
				if (Files.exists(f)) send(ex, 200, "application/json", Files.readString(f));
				else send(ex, 404, "application/json", "{\"error\":\"not_found\"}");
			}
			case "PUT", "POST" -> {
				byte[] body = ex.getRequestBody().readNBytes(MAX_BODY + 1);
				if (body.length > MAX_BODY) { send(ex, 413, "application/json", "{\"error\":\"zu_gross\"}"); return; }
				String s = new String(body, StandardCharsets.UTF_8);
				JsonObject o = JsonParser.parseString(s).getAsJsonObject();
				if (!o.has("app") || !"blockplan".equals(o.get("app").getAsString())) { send(ex, 400, "application/json", "{\"error\":\"format\"}"); return; }
				Path tmp = projects.resolve(id + ".tmp");
				Files.writeString(tmp, s);
				Files.move(tmp, f, java.nio.file.StandardCopyOption.REPLACE_EXISTING, java.nio.file.StandardCopyOption.ATOMIC_MOVE);
				long upd = o.has("updated") ? o.get("updated").getAsLong() : 0;
				for (Client c : BlockplanLive.clients) c.send("projsaved", "{\"id\":\"" + id + "\",\"updated\":" + upd + "}");
				send(ex, 200, "application/json", "{\"ok\":true}");
			}
			case "DELETE" -> {
				Files.deleteIfExists(f);
				send(ex, 200, "application/json", "{\"ok\":true}");
			}
			default -> send(ex, 405, "application/json", "{\"error\":\"method\"}");
		}
	}

	private static int num(Map<String, String> q, String k) {
		try { return Integer.parseInt(q.getOrDefault(k, "0")); } catch (NumberFormatException e) { return 0; }
	}

	private static Map<String, String> query(String raw) {
		Map<String, String> m = new HashMap<>();
		if (raw == null) return m;
		for (String p : raw.split("&")) {
			int i = p.indexOf('=');
			if (i > 0) m.put(URLDecoder.decode(p.substring(0, i), StandardCharsets.UTF_8), URLDecoder.decode(p.substring(i + 1), StandardCharsets.UTF_8));
		}
		return m;
	}

	private static void send(HttpExchange ex, int code, String type, String body) throws IOException {
		byte[] b = body.getBytes(StandardCharsets.UTF_8);
		ex.getResponseHeaders().set("Content-Type", type);
		ex.sendResponseHeaders(code, b.length);
		try (OutputStream o = ex.getResponseBody()) { o.write(b); }
	}
}
