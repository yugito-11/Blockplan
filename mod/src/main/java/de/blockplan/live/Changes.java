package de.blockplan.live;

import net.minecraft.core.BlockPos;
import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.state.BlockState;

import java.util.concurrent.ConcurrentLinkedQueue;

/** Warteschlange der Blockänderungen und neu geladenen Chunks; abgearbeitet im Tick. */
public final class Changes {
	/** Nur wenn jemand zuschaut, werden Änderungen gesammelt. */
	static volatile boolean active = false;

	record Change(Level level, long pos, BlockState state) {}
	record Load(Level level, int cx, int cz) {}

	static final ConcurrentLinkedQueue<Change> queue = new ConcurrentLinkedQueue<>();
	static final ConcurrentLinkedQueue<Load> loads = new ConcurrentLinkedQueue<>();

	public static void record(Level level, BlockPos pos, BlockState state) {
		if (!active || level == null) return;
		queue.add(new Change(level, pos.asLong(), state));
	}

	static void chunkLoaded(Level level, int cx, int cz) {
		if (!active || level == null) return;
		loads.add(new Load(level, cx, cz));
	}

	static void clear() { queue.clear(); loads.clear(); }
}
