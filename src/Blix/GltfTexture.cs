using Blix.Graphics;
using Blix.Graphics.Images;

namespace Blix;

// Decoded glTF texture image. Cached + shared by the importer when multiple
// materials reference the same image (deduping by glTF image index).
//
// Two storage paths use the same type:
//   - Source PNG/JPEG: Format = Rgba8 and MipBytes contains mip 0. The
//     graphics backend generates the rest of the chain when the format
//     supports linear blitting.
//   - Cooked .blixtex path: Format may be Rgba8 or Bc7Srgb/Bc7Unorm/Bc5/Bc6h.
//     MipBytes or LazyHandle exposes the pre-baked mip chain. Pre-baked
//     mips are required for compressed formats because the backend cannot
//     generate their chain with a linear blit.
//
// Eager CPU bytes can be released after downstream upload work has retained
// everything it needs. Logical identity, format, and dimensions remain
// available after ReleaseCpuMipBytes clears this instance's byte references.
// The type is mutable only for that ownership handoff; it has reference rather
// than value equality.
public sealed class GltfTexture
{
    public string Name { get; }

    /// <summary>
    /// Stable origin identity for sharing the same image across loads.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A resolved absolute path, or <c>&lt;container&gt;#&lt;imageIndex&gt;</c> for an image embedded
    /// in a .glb, which has no path of its own.
    /// </para>
    /// <para>
    /// This is origin identity rather than content identity: identical bytes under different paths
    /// remain distinct. Cooked mesh images separately carry <c>BlixMeshImage.ContentHash</c>.
    /// </para>
    /// <para>
    /// Empty means "unidentifiable" — a texture built from bytes with no origin. Such a texture is
    /// never shared, because an identity nobody can reproduce is not an identity.
    /// </para>
    /// </remarks>
    public string ResourceId { get; init; } = string.Empty;
    public TextureFormat Format { get; }
    public int Width { get; }
    public int Height { get; }
    public int MipCount { get; }

    // Eager path: byte arrays for each mip held in RAM. Set by source-image
    // decoding (single mip) or an eager .blixtex read. A caller may release
    // them once downstream work has retained every required byte array.
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

    // Factory for the source-decoded, single-mip Rgba8 shape. Cooked-load
    // callers use a constructor that exposes the artifact's full mip chain.
    public static GltfTexture Rgba8Single(string name, byte[] pixels, int width, int height, string resourceId = "")
        => new(name, TextureFormat.Rgba8, width, height, new[] { pixels }) { ResourceId = resourceId };

    // Drops this instance's eager CPU-byte references. The caller is responsible
    // for retaining any bytes still needed by deferred upload work.
    public void ReleaseCpuMipBytes()
    {
        MipBytes = null;
    }

    // Compatibility accessor for callers that require eager mip 0. It throws
    // after CPU data has been released; tolerant callers should inspect
    // MipBytes first.
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
