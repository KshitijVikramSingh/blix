using Blix.Graphics;
using Blix.Graphics.Images;

namespace Blix;

// Decoded glTF texture image. Cached + shared by the importer when multiple
// materials reference the same image (deduping by glTF image index).
//
// Two flavours via the same type:
//   - Source PNG/JPEG path: Format = Rgba8, MipBytes contains one entry
//     (mip 0). The runtime calls glGenerateMipmap on upload to fill out
//     the chain.
//   - Cooked .blixtex path: Format may be Rgba8 or Bc7Srgb/Bc7Unorm/Bc5/Bc6h.
//     MipBytes carries pre-baked mips (CPU box-filtered + per-mip BCn
//     encoded). For compressed formats this is mandatory -- GL can't
//     generate mips on compressed textures.
//
// **CPU-byte ownership**: the byte arrays are owned by this instance only
// until the GPU upload has been enqueued (ResourceUploader takes the
// reference in its work-item list). After that, the CPU data is dead
// weight -- a 4K texture with mips is ~85MB of bytes that won't be read
// again. ReleaseCpuMipBytes() drops the references so the GC can reclaim
// them. The texture's logical identity (name + format + dimensions) stays
// queryable; only the pixel data goes.
//
// Mutable class rather than the original record because the byte arrays
// need to be released after upload without throwing away the rest of the
// object. The cost is no value-equality, which nothing relied on anyway.
public sealed class GltfTexture
{
    public string Name { get; }
    public TextureFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int MipCount { get; }

    // Eager path: byte arrays for each mip held in RAM. Set by the source-
    // PNG decoder (single-mip) or the .blixtex eager Read. Released after
    // upload-enqueue via ReleaseCpuMipBytes.
    public IReadOnlyList<byte[]>? MipBytes { get; private set; }

    // Lazy path: mip data stays on disk; ReadMip(level) pulls one mip at
    // upload time. Used by the cooked-.blixtex pipeline for memory-light
    // load. Mutually exclusive with MipBytes -- exactly one is set.
    public BlixTexLazyHandle? LazyHandle { get; }

    public GltfTexture(string name, TextureFormat format, int width, int height, IReadOnlyList<byte[]> mipBytes)
    {
        Name = name;
        Format = format;
        Width = width;
        Height = height;
        MipBytes = mipBytes;
        MipCount = mipBytes.Count;
    }

    public GltfTexture(string name, BlixTexLazyHandle lazyHandle)
    {
        Name = name;
        Format = lazyHandle.Format;
        Width = lazyHandle.Width;
        Height = lazyHandle.Height;
        MipCount = lazyHandle.MipCount;
        LazyHandle = lazyHandle;
    }

    // Convenience for the legacy single-mip Rgba8 shape. Used by the
    // source-PNG decode path; cooked-load callers use the constructor
    // directly with the BlixTex's full mip chain.
    public static GltfTexture Rgba8Single(string name, byte[] pixels, int width, int height)
        => new(name, TextureFormat.Rgba8, width, height, new[] { pixels });

    // Drops the CPU pixel-data references so the garbage collector can
    // reclaim the ~85MB-per-4K-texture working set the rest of the app
    // doesn't need anymore. Called by GltfSceneInstance after the upload
    // has been enqueued through ResourceUploader; the uploader's work
    // items still hold their own slot of the same arrays (alive until
    // each mip is processed), so freeing here just removes OUR reference.
    public void ReleaseCpuMipBytes()
    {
        MipBytes = null;
    }

    // Legacy shortcut used by older single-mip Rgba8 call sites. Throws
    // if CPU data has been released; callers should branch on MipBytes
    // being null when they need to be tolerant.
    public byte[] RgbaPixels
    {
        get
        {
            if (MipBytes is null)
            {
                throw new InvalidOperationException(
                    $"GltfTexture '{Name}' has had its CPU mip bytes released; cannot read RgbaPixels.");
            }
            return MipBytes[0];
        }
    }
}
