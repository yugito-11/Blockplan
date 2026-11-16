package de.blockplan.live;

import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientChunkEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientLifecycleEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.networking.v1.ClientPlayConnectionEvents;
import net.minecraft.client.Minecraft;
import net.minecraft.network.chat.Component;

import java.util.List;

/**
 * Blockplan im eigenen Spiel (z. B. Modrinth): Die Website läuft auf diesem PC unter localhost und zeigt,
 * was das Spiel gerade geladen hat. Auf dem Server muss nichts installiert sein.
 */
public class BlockplanLiveClient implements ClientModInitializer {
	private static boolean told = false;

	@Override
	public void onInitializeClient() {
		BlockplanLive.levels = () -> {
			Minecraft mc = Minecraft.getInstance();
			return mc.level == null ? List.of() : List.of(mc.level);
		};
		ClientLifecycleEvents.CLIENT_STARTED.register(mc -> BlockplanLive.startWeb("localhost"));
		ClientLifecycleEvents.CLIENT_STOPPING.register(mc -> BlockplanLive.stopWeb());
		ClientChunkEvents.CHUNK_LOAD.register((level, chunk) -> Changes.chunkLoaded(level, chunk.getPos().x(), chunk.getPos().z()));
		ClientTickEvents.END_CLIENT_TICK.register(mc -> {
			if (mc.level == null) return;
			Stand.radius = Math.max(4, Math.min(16, mc.options.getEffectiveRenderDistance())); // so weit, wie das Spiel geladen hat
			BlockplanLive.tickCommon();
		});
		ClientPlayConnectionEvents.JOIN.register((handler, sender, mc) -> {
			var sd = mc.getCurrentServer();
			BlockplanLive.switchWorld(sd != null ? sd.ip : "einzelspieler");
			if (!told) {
				told = true;
				mc.execute(() -> {
					if (mc.player != null) mc.player.sendSystemMessage(Component.literal("Blockplan läuft im Browser: " + BlockplanLive.link("localhost")));
				});
			}
		});
	}
}
