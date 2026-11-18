package de.blockplan.live;

import net.fabricmc.api.ClientModInitializer;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientChunkEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientLifecycleEvents;
import net.fabricmc.fabric.api.client.event.lifecycle.v1.ClientTickEvents;
import net.fabricmc.fabric.api.client.networking.v1.ClientPlayConnectionEvents;
import net.minecraft.client.Minecraft;
import net.minecraft.network.chat.Component;

import java.util.List;
import de.blockplan.live.build.Build;
import de.blockplan.live.build.GhostRenderer;
import de.blockplan.live.build.Hud;
import net.fabricmc.fabric.api.client.keymapping.v1.KeyMappingHelper;
import net.fabricmc.fabric.api.client.rendering.v1.hud.HudElementRegistry;
import net.fabricmc.fabric.api.client.rendering.v1.level.LevelRenderEvents;
import net.fabricmc.fabric.api.client.screen.v1.ScreenEvents;
import net.fabricmc.fabric.api.client.screen.v1.Screens;
import net.fabricmc.fabric.api.event.player.UseBlockCallback;
import net.minecraft.client.KeyMapping;
import net.minecraft.client.gui.screens.inventory.AbstractContainerScreen;
import net.minecraft.resources.Identifier;
import net.minecraft.world.InteractionResult;
import com.mojang.blaze3d.platform.InputConstants;

/**
 * Blockplan im eigenen Spiel (z. B. Modrinth): Die Website läuft auf diesem PC unter localhost und zeigt,
 * was das Spiel gerade geladen hat. Auf dem Server muss nichts installiert sein.
 */
public class BlockplanLiveClient implements ClientModInitializer {
	private static boolean told = false;

	static KeyMapping keyShow, keyMode, keyUp, keyDown, keyChest, keyHud;

	/** Oben rechts neben dem Kisten-Fenster; passt es nicht daneben, dann darüber. */
	static int btnX(AbstractContainerScreen<?> cs, int w) {
		var a = (de.blockplan.live.mixin.ContainerScreenAccess) cs;
		int right = a.blockplan$left() + a.blockplan$width();
		return right + 4 + 120 <= w ? right + 4 : right - 120;
	}
	static int btnY(AbstractContainerScreen<?> cs) {
		var a = (de.blockplan.live.mixin.ContainerScreenAccess) cs;
		int right = a.blockplan$left() + a.blockplan$width();
		return right + 4 + 120 <= cs.width ? a.blockplan$top() : Math.max(2, a.blockplan$top() - 22);
	}

	static Component chestLabel(boolean on) {
		return Component.literal(on ? "Lagerkiste ✓ (entfernen)" : "+ Als Lagerkiste");
	}

	/** Inhalt der Kiste (ohne das eigene Inventar). */
	static java.util.Map<net.minecraft.world.item.Item, Integer> contents(AbstractContainerScreen<?> cs) {
		var m = new java.util.HashMap<net.minecraft.world.item.Item, Integer>();
		for (var slot : cs.getMenu().slots) {
			if (slot.container instanceof net.minecraft.world.entity.player.Inventory) continue;
			var st = slot.getItem(); if (!st.isEmpty()) m.merge(st.getItem(), st.getCount(), Integer::sum);
		}
		return m;
	}

	@Override
	public void onInitializeClient() {
		// Bauen nach Plan (Ersatz für Litematica)
		BlockplanLive.planHandler = Build::setPlanJson;
		BlockplanLive.viewHandler = Build::setViewJson;
		BlockplanLive.planClear = () -> Minecraft.getInstance().execute(Build::clear);
		var cat = KeyMapping.Category.register(Identifier.fromNamespaceAndPath("blockplan", "bauen"));
		keyShow = KeyMappingHelper.registerKeyMapping(new KeyMapping("key.blockplan.show", InputConstants.KEY_B, cat));
		keyMode = KeyMappingHelper.registerKeyMapping(new KeyMapping("key.blockplan.mode", InputConstants.KEY_N, cat));
		keyUp = KeyMappingHelper.registerKeyMapping(new KeyMapping("key.blockplan.up", InputConstants.KEY_PAGEUP, cat));
		keyDown = KeyMappingHelper.registerKeyMapping(new KeyMapping("key.blockplan.down", InputConstants.KEY_PAGEDOWN, cat));
		keyChest = KeyMappingHelper.registerKeyMapping(new KeyMapping("key.blockplan.chest", InputConstants.KEY_K, cat));
		keyHud = KeyMappingHelper.registerKeyMapping(new KeyMapping("key.blockplan.hud", InputConstants.KEY_J, cat));
		LevelRenderEvents.COLLECT_SUBMITS.register(GhostRenderer::render);
		HudElementRegistry.addLast(Identifier.fromNamespaceAndPath("blockplan", "bauen"), Hud::render);
		UseBlockCallback.EVENT.register((player, world, hand, hit) -> { if (world.isClientSide()) Build.lastUsed = hit.getBlockPos(); return InteractionResult.PASS; });
		ScreenEvents.AFTER_INIT.register((client, screen, w, h) -> {
			if (!(screen instanceof AbstractContainerScreen<?> cs) || !Build.isContainerAt(Build.lastUsed)) return;
			var pos = Build.lastUsed;
			// Knopf oben rechts: Kiste als Lagerkiste hinzufügen oder entfernen
			var btn = net.minecraft.client.gui.components.Button.builder(chestLabel(Build.isMarked(pos)), b -> {
				boolean on = Build.toggleChestAt(pos, contents(cs));
				b.setMessage(chestLabel(on));
			}).bounds(btnX(cs, w), btnY(cs), 120, 20)
			  .tooltip(net.minecraft.client.gui.components.Tooltip.create(Component.literal("Blockplan zählt den Inhalt von Lagerkisten zur Materialliste dazu.")))
			  .build();
			Screens.getWidgets(screen).add(btn);
			ScreenEvents.remove(screen).register(sc -> { if (Build.isMarked(pos)) Build.containerSeen(contents(cs)); });
		});
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
			while (keyShow.consumeClick()) Build.toggleShow();
			while (keyMode.consumeClick()) Build.toggleMode();
			while (keyUp.consumeClick()) Build.layerBy(1);
			while (keyDown.consumeClick()) Build.layerBy(-1);
			while (keyChest.consumeClick()) Build.toggleChest();
			while (keyHud.consumeClick()) Build.hud = !Build.hud;
			Build.tick(mc);
		});
		ClientPlayConnectionEvents.JOIN.register((handler, sender, mc) -> {
			var sd = mc.getCurrentServer();
			BlockplanLive.switchWorld(sd != null ? sd.ip : "einzelspieler");
			Build.switchWorld(sd != null ? sd.ip : mc.getSingleplayerServer() != null ? "sp-" + mc.getSingleplayerServer().getWorldData().getLevelName() : "einzelspieler");
			if (!told) {
				told = true;
				mc.execute(() -> {
					if (mc.player != null) mc.player.sendSystemMessage(Component.literal("Blockplan läuft im Browser: " + BlockplanLive.link("localhost")));
				});
			}
		});
	}
}
