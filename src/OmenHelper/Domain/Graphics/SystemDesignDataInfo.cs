using System;

namespace OmenHelper.Domain.Graphics;

[Flags]
internal enum GraphicsModeSupportSlot : byte
{
    None = 0x00,
    Integrated = 0x01,
    Hybrid = 0x02,
    Dedicated = 0x04,
    Optimus = 0x08
}

internal sealed class SystemDesignDataInfo
{
    public static readonly SystemDesignDataInfo Empty = new SystemDesignDataInfo(0, GraphicsModeSupportSlot.None, GraphicsModeSupportSlot.None, false);

    public static SystemDesignDataInfo FromRaw(byte rawGpuModeSwitch, bool readSucceeded)
    {
        GraphicsModeSupportSlot rawGraphicsModeSlots = (GraphicsModeSupportSlot)(rawGpuModeSwitch & 0x0F);
        GraphicsModeSupportSlot normalizedGraphicsModeSlots = Normalize(rawGraphicsModeSlots);
        return new SystemDesignDataInfo(rawGpuModeSwitch, rawGraphicsModeSlots, normalizedGraphicsModeSlots, readSucceeded);
    }

    private SystemDesignDataInfo(byte rawGpuModeSwitch, GraphicsModeSupportSlot rawGraphicsModeSlots, GraphicsModeSupportSlot graphicsModeSlots, bool readSucceeded)
    {
        RawGpuModeSwitch = rawGpuModeSwitch;
        RawGraphicsModeSlots = rawGraphicsModeSlots;
        GraphicsModeSlots = graphicsModeSlots;
        ReadSucceeded = readSucceeded;
    }

    public byte RawGpuModeSwitch { get; }

    public GraphicsModeSupportSlot RawGraphicsModeSlots { get; }

    public GraphicsModeSupportSlot GraphicsModeSlots { get; }

    public bool HasIntegratedSlot => (GraphicsModeSlots & GraphicsModeSupportSlot.Integrated) != 0;

    public bool HasHybridSlot => (GraphicsModeSlots & GraphicsModeSupportSlot.Hybrid) != 0;

    public bool HasDedicatedSlot => (GraphicsModeSlots & GraphicsModeSupportSlot.Dedicated) != 0;

    public bool HasOptimusSlot => (GraphicsModeSlots & GraphicsModeSupportSlot.Optimus) != 0;

    public bool SupportsGraphicsSwitching => HasIntegratedSlot && HasAlternateSlot;

    public bool HasAlternateSlot => (GraphicsModeSlots & (GraphicsModeSupportSlot.Hybrid | GraphicsModeSupportSlot.Dedicated | GraphicsModeSupportSlot.Optimus)) != 0;

    public bool ReadSucceeded { get; }

    private static GraphicsModeSupportSlot Normalize(GraphicsModeSupportSlot rawGraphicsModeSlots)
    {
        GraphicsModeSupportSlot normalized = rawGraphicsModeSlots;
        if ((normalized & GraphicsModeSupportSlot.Integrated) == 0 &&
            (normalized & (GraphicsModeSupportSlot.Hybrid | GraphicsModeSupportSlot.Dedicated | GraphicsModeSupportSlot.Optimus)) != 0)
        {
            normalized |= GraphicsModeSupportSlot.Integrated;
        }

        return normalized;
    }
}
