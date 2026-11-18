package de.blockplan.live.mixin;

import net.minecraft.client.gui.screens.inventory.AbstractContainerScreen;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.gen.Accessor;

/** Lage des Kisten-Fensters, damit der Lagerkisten-Knopf oben rechts daneben sitzt. */
@Mixin(AbstractContainerScreen.class)
public interface ContainerScreenAccess {
	@Accessor("leftPos") int blockplan$left();
	@Accessor("topPos") int blockplan$top();
	@Accessor("imageWidth") int blockplan$width();
}
