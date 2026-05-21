namespace Blix.Audio;

// Stable opaque references to backend-owned audio resources. Mirrors the
// (VertexBufferHandle / TextureHandle / ...) convention in Blix.Graphics:
// game code holds the handle, the device owns the underlying object. Two
// handles compare equal when their backends and Ids match.

public readonly record struct AudioClipHandle(int Id)
{
    public static AudioClipHandle Invalid { get; } = new(0);
}

public readonly record struct AudioSourceHandle(int Id)
{
    public static AudioSourceHandle Invalid { get; } = new(0);
}
