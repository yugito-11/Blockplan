package de.blockplan.live;

import com.google.gson.JsonObject;
import com.google.gson.JsonParser;
import net.minecraft.world.entity.player.Player;

import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.Base64;
import java.util.Map;
import java.util.UUID;
import java.util.concurrent.ConcurrentHashMap;

/** Holt die Skin-Datei eines Spielers über die Textur-Angabe in seinem Profil (Mojang-Skinserver). */
final class Skins {
	private static final Map<String, byte[]> cache = new ConcurrentHashMap<>();
	private static final Map<String, String> urls = new ConcurrentHashMap<>();
	private static final HttpClient http = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(5)).followRedirects(HttpClient.Redirect.NORMAL).build();

	/** Im Server-Tick aufrufen: merkt sich die Skin-Adressen der Spieler, die gerade online sind. */
	static void remember(Player p) {
		String u = p.getUUID().toString();
		if (urls.containsKey(u)) return;
		try {
			for (var prop : p.getGameProfile().properties().get("textures")) {
				String json = new String(Base64.getDecoder().decode(prop.value()), StandardCharsets.UTF_8);
				JsonObject o = JsonParser.parseString(json).getAsJsonObject().getAsJsonObject("textures");
				if (o != null && o.has("SKIN")) {
					String url = o.getAsJsonObject("SKIN").get("url").getAsString();
					if (url.startsWith("http://textures.minecraft.net/") || url.startsWith("https://textures.minecraft.net/")) urls.put(u, url.replace("http://", "https://"));
				}
			}
		} catch (Exception ignored) {}
		urls.putIfAbsent(u, "");
	}

	/** PNG des Skins oder null (Offline-Server, kein Skin gesetzt, Server nicht erreichbar). */
	static byte[] get(String uuid) {
		try { UUID.fromString(uuid); } catch (Exception e) { return null; }
		byte[] b = cache.get(uuid);
		if (b != null) return b.length == 0 ? null : b;
		String url = urls.get(uuid);
		if (url == null || url.isEmpty()) return null;
		try {
			HttpResponse<byte[]> r = http.send(HttpRequest.newBuilder(URI.create(url)).timeout(Duration.ofSeconds(8)).GET().build(), HttpResponse.BodyHandlers.ofByteArray());
			b = r.statusCode() == 200 ? r.body() : new byte[0];
		} catch (Exception e) {
			b = new byte[0];
		}
		cache.put(uuid, b);
		return b.length == 0 ? null : b;
	}

}
