using System.Runtime.InteropServices;
using Blix.Graphics;
using Blix.Render;

namespace Blix;

/// <summary>A skinned model's palette buffers, one per skin, and the copy from palette sets into them.</summary>
/// <remarks>
/// The 4x4s go up untransposed (conventions §2): GLSL reads std430 column-major, the transpose of the
/// row-vector form the CPU built, so <c>skin * v</c> in the shader computes what <c>v_row * skin</c>
/// computes here. Only the live prefix is written each frame, into the current frame slot's buffer.
/// </remarks>
public sealed class BoneBuffers : IDisposable
{
    private readonly IGraphicsDevice device;
    private readonly IMaterialBindings[] bindings;
    private readonly SkinBinding[] skins;
    private readonly byte[] payload;

    internal BoneBuffers(IGraphicsDevice device, IMaterialBindings[] bindings, SkinBinding[] skins, int maxInstances)
    {
        this.device = device;
        this.bindings = bindings;
        this.skins = skins;
        MaxInstances = maxInstances;
        payload = new byte[(skins.Length == 0 ? 0 : skins.Max(s => s.JointCount)) * maxInstances * 64];
    }

    /// <summary>How many bodies each skin's buffer holds.</summary>
    public int MaxInstances { get; }

    /// <summary>The per-draw set a skinned part of <paramref name="skin"/> binds.</summary>
    public IMaterialBindings For(int skin) => bindings[skin];

    /// <summary>Copies one skin's live palettes into this frame's buffer.</summary>
    public void Upload(int skin, BonePaletteSet palettes)
    {
        ArgumentNullException.ThrowIfNull(palettes);
        if ((uint)skin >= (uint)bindings.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(skin), skin, $"this rig has {bindings.Length} skin(s).");
        }

        // By identity, not by stride: a set packed for another skin of the same joint count has the right size and
        // the wrong joints, and the shader would read its slices as this skin's without failing.
        if (!ReferenceEquals(palettes.Skin, skins[skin]))
        {
            throw new ArgumentException(
                $"the palette set was packed for another skin ({palettes.JointCount} joints) than skin {skin} of this model; " +
                "a buffer's joint indices mean its own skin's joints only.",
                nameof(palettes));
        }

        if (palettes.Count > MaxInstances)
        {
            throw new InvalidOperationException(
                $"{palettes.Count} bodies for buffers made for {MaxInstances}; make the bone buffers larger.");
        }

        var live = palettes.LiveMatrixCount;
        for (var i = 0; i < live; i++) MemoryMarshal.Write(payload.AsSpan(i * 64, 64), in palettes.Matrices[i]);
        bindings[skin].WriteBuffer(device.CurrentFrameSlot, 0, payload.AsSpan(0, live * 64));
    }

    public void Dispose()
    {
        foreach (var binding in bindings) device.DestroyMaterial(binding.Handle);
    }
}
