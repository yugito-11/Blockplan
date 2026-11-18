package de.blockplan.live.build;

import com.mojang.blaze3d.vertex.PoseStack;
import com.mojang.blaze3d.vertex.QuadInstance;
import net.fabricmc.fabric.api.client.rendering.v1.level.LevelRenderContext;
import net.minecraft.client.Minecraft;
import net.minecraft.client.renderer.block.dispatch.BlockStateModelPart;
import net.minecraft.client.renderer.rendertype.RenderTypes;
import net.minecraft.client.renderer.texture.OverlayTexture;
import net.minecraft.client.resources.model.geometry.BakedQuad;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.util.LightCoordsUtil;
import net.minecraft.util.RandomSource;
import net.minecraft.world.level.block.state.BlockState;
import net.minecraft.world.phys.Vec3;
import net.minecraft.world.phys.shapes.Shapes;

import java.util.ArrayList;
import java.util.IdentityHashMap;
import java.util.List;
import java.util.Map;

/** Zeichnet den Plan in die Welt: fehlende Blöcke als echte Modelle, halb durchsichtig; falsche mit farbigem Rahmen. */
public final class GhostRenderer {
	private GhostRenderer() {}

	static final Map<BlockState, List<BakedQuad>> quads = new IdentityHashMap<>();
	static final int GHOST_ALPHA = 0x99;

	static List<BakedQuad> quadsOf(BlockState s) {
		return quads.computeIfAbsent(s, st -> {
			var model = Minecraft.getInstance().getModelManager().getBlockStateModelSet().get(st);
			var parts = new ArrayList<BlockStateModelPart>(); model.collectParts(RandomSource.create(42L), parts);
			var out = new ArrayList<BakedQuad>();
			for (var part : parts) { for (Direction d : Direction.values()) out.addAll(part.getQuads(d)); out.addAll(part.getQuads(null)); }
			return out;
		});
	}

	public static void render(LevelRenderContext ctx)
	{
		var spots = Build.spots(); var chests = Build.markedChests();
		if (spots.isEmpty() && chests.isEmpty()) return;
		var mc = Minecraft.getInstance();
		Vec3 cam = ctx.levelState().cameraRenderState.pos;
		PoseStack ps = ctx.poseStack(); var sub = ctx.submitNodeCollector();
		// fehlende Blöcke: echte Modelle, durchsichtig
		if (!spots.isEmpty())
			sub.submitCustomGeometry(ps, RenderTypes.translucentMovingBlock(), (pose, vc) -> {
				var local = new PoseStack(); var qi = new QuadInstance();
				qi.setLightCoords(LightCoordsUtil.FULL_BRIGHT); qi.setOverlayCoords(OverlayTexture.NO_OVERLAY);
				int n = 0;
				for (var sp : spots) {
					if (sp.kind() != 1) continue;
					if (++n > 12000) break;
					BlockPos p = sp.pos();
					local.last().set(pose);
					local.translate(p.getX() - cam.x, p.getY() - cam.y, p.getZ() - cam.z);
					for (var q : quadsOf(sp.want())) {
						int rgb = 0xFFFFFF;
						if (q.materialInfo().isTinted()) {
							try { rgb = mc.getBlockColors().getTintSource(sp.want(), q.materialInfo().tintIndex()).color(sp.want()) & 0xFFFFFF; } catch (Exception ignored) {}
						}
						qi.setColor(GHOST_ALPHA << 24 | rgb);
						vc.putBakedQuad(local.last(), q, qi);
					}
				}
			});
		// falscher Block: rot, falsche Ausrichtung: orange; Lagerkisten: grün
		int outlines = 0;
		for (var sp : spots) {
			if (sp.kind() == 1) continue;
			if (++outlines > 4000) break;
			Outline(ps, sub, sp.pos(), cam, sp.kind() == 2 ? 0xFFFF3B3B : 0xFFFFA529);
		}
		for (long k : chests) Outline(ps, sub, BlockPos.of(k), cam, 0xFF4CE07A);
	}

	static void Outline(PoseStack ps, net.minecraft.client.renderer.SubmitNodeCollector sub, BlockPos p, Vec3 cam, int color) {
		ps.pushPose();
		ps.translate(p.getX() - cam.x, p.getY() - cam.y, p.getZ() - cam.z);
		sub.submitShapeOutline(ps, Shapes.block(), RenderTypes.lines(), color, 3f, false);
		ps.popPose();
	}
}
