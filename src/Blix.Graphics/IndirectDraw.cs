namespace Blix.Graphics;

/// <summary>The record an indirect draw reads, one per draw, written by the CPU or a compute pass.</summary>
/// <remarks>
/// Five 32-bit values in this order: index count, instance count, first index, vertex offset, first
/// instance. It is what <see cref="DrawIndexedIndirectCommand"/> consumes and what
/// <see cref="IGraphicsDevice.WriteIndirectCommands"/> writes, so the layout is the engine's rather
/// than a detail of whichever device reads it.
/// </remarks>
public static class IndirectDraw
{
    /// <summary>Bytes per draw record.</summary>
    public const int RecordStride = 20;
}
