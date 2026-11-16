package de.blockplan.live;

import com.google.gson.JsonArray;
import com.google.gson.JsonElement;
import com.google.gson.JsonObject;
import com.google.gson.JsonParser;

import java.io.InputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.file.Files;
import java.nio.file.Path;
import java.nio.file.StandardCopyOption;
import java.time.Duration;
import java.util.zip.ZipEntry;
import java.util.zip.ZipInputStream;
import java.util.zip.ZipOutputStream;

/**
 * Originale Block-Texturen für die Weboberfläche. Der Server enthält keine Texturen, deshalb lädt die Mod
 * einmal den offiziellen Minecraft-Client derselben Version von Mojang (wie der Launcher), nimmt daraus nur
 * Block-Texturen, Blockmodelle und Blockzustände und legt sie als kleine ZIP-Datei in config/blockplan ab.
 */
final class Textures {
	private static final String MANIFEST = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
	private static final HttpClient http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(10)).followRedirects(HttpClient.Redirect.NORMAL).build();
	private static volatile boolean busy = false;
	static volatile String error = null;

	static Path file(Path dir, String version) { return dir.resolve("textures2-" + version.replaceAll("[^A-Za-z0-9._-]", "_") + ".zip"); }

	/** Liefert die ZIP-Datei oder null, solange sie noch erstellt wird (dann läuft der Download im Hintergrund). */
	static synchronized Path get(Path dir, String version) {
		Path f = file(dir, version);
		if (Files.exists(f)) return f;
		if (!busy) {
			busy = true; error = null;
			Thread t = new Thread(() -> {
				try { build(f, version); BlockplanLive.LOG.info("Blockplan Live: Minecraft-Texturen für {} bereit", version); }
				catch (Exception e) { error = e.getMessage(); BlockplanLive.LOG.warn("Blockplan Live: Texturen konnten nicht geladen werden", e); }
				finally { busy = false; }
			}, "blockplan-textures");
			t.setDaemon(true);
			t.start();
		}
		return null;
	}

	private static JsonObject json(String url) throws Exception {
		HttpResponse<String> r = http.send(HttpRequest.newBuilder(URI.create(url)).timeout(Duration.ofSeconds(30)).GET().build(), HttpResponse.BodyHandlers.ofString());
		if (r.statusCode() != 200) throw new IllegalStateException("HTTP " + r.statusCode() + " für " + url);
		return JsonParser.parseString(r.body()).getAsJsonObject();
	}

	private static void build(Path out, String version) throws Exception {
		String verUrl = null;
		JsonArray versions = json(MANIFEST).getAsJsonArray("versions");
		for (JsonElement v : versions) {
			JsonObject o = v.getAsJsonObject();
			if (o.get("id").getAsString().equals(version)) { verUrl = o.get("url").getAsString(); break; }
		}
		if (verUrl == null) throw new IllegalStateException("Version " + version + " nicht im Mojang-Verzeichnis gefunden");
		JsonObject ver = json(verUrl);
		String clientUrl = ver.getAsJsonObject("downloads").getAsJsonObject("client").get("url").getAsString();
		// Deutsche Blocknamen liegen nicht im Client, sondern (wie beim Launcher) im Asset-Verzeichnis
		byte[] deDe = null;
		try {
			JsonObject objs = json(ver.getAsJsonObject("assetIndex").get("url").getAsString()).getAsJsonObject("objects");
			JsonObject de = objs.getAsJsonObject("minecraft/lang/de_de.json");
			if (de != null) {
				String h = de.get("hash").getAsString();
				HttpResponse<byte[]> r2 = http.send(HttpRequest.newBuilder(URI.create("https://resources.download.minecraft.net/" + h.substring(0, 2) + "/" + h)).timeout(Duration.ofSeconds(30)).GET().build(), HttpResponse.BodyHandlers.ofByteArray());
				if (r2.statusCode() == 200) deDe = r2.body();
			}
		} catch (Exception e) {
			BlockplanLive.LOG.warn("Blockplan Live: deutsche Blocknamen nicht geladen ({})", e.getMessage());
		}
		Path tmp = out.resolveSibling(out.getFileName() + ".part");
		HttpResponse<InputStream> r = http.send(HttpRequest.newBuilder(URI.create(clientUrl)).timeout(Duration.ofMinutes(5)).GET().build(), HttpResponse.BodyHandlers.ofInputStream());
		if (r.statusCode() != 200) throw new IllegalStateException("HTTP " + r.statusCode() + " beim Client-Download");
		try (ZipInputStream in = new ZipInputStream(r.body()); ZipOutputStream zo = new ZipOutputStream(Files.newOutputStream(tmp))) {
			ZipEntry e;
			while ((e = in.getNextEntry()) != null) {
				String n = e.getName();
				if (!(n.matches("assets/minecraft/textures/block/[^/]+\\.png") || n.matches("assets/minecraft/models/block/[^/]+\\.json") || n.matches("assets/minecraft/blockstates/[^/]+\\.json") || n.matches("assets/minecraft/lang/(de_de|en_us)\\.json"))) continue;
				zo.putNextEntry(new ZipEntry(n));
				in.transferTo(zo);
				zo.closeEntry();
			}
			if (deDe != null) {
				zo.putNextEntry(new ZipEntry("assets/minecraft/lang/de_de.json"));
				zo.write(deDe);
				zo.closeEntry();
			}
		}
		Files.move(tmp, out, StandardCopyOption.REPLACE_EXISTING);
	}
}
