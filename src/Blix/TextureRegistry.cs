using Blix.Graphics;

namespace Blix;

/// <summary>
/// Uploaded textures, keyed by what they ARE rather than by which object asked for them.
/// </summary>
/// <remarks>
/// <para>
/// The key is (identity, format). Identity is
/// <see cref="GltfTexture.ResourceId"/> — two loads of one file agree on it. Format is there because
/// one image can legitimately be resident twice: a base colour wants <c>Rgba8Srgb</c> and the same
/// picture used as a normal map wants <c>Rgba8</c>, and those are different GPU textures. Keying on
/// identity alone would hand a shader the wrong colour space; keying on the object gives up on both.
/// </para>
/// <para>
/// Unidentified textures are deduplicated within their object graph but not shared across imports. A texture built from bytes
/// with no origin has an empty identity, and an identity nobody can reproduce is not an identity —
/// so those fall back to the object key, which is exactly what every caller did before. Behaviour
/// for them is unchanged rather than degraded.
/// </para>
/// <para>
/// <see cref="ResidentBytes"/> reports identified texture bytes and <see cref="SharedUploads"/>
/// reports requests satisfied by an existing handle.
/// </para>
/// <para>
/// Eviction is deliberately absent; this registry names and shares resources but does not choose a
/// budget, age, priority, or lifetime policy.
/// </para>
/// </remarks>
public sealed class TextureRegistry
{
    private readonly Dictionary<(string Id, TextureFormat Format), TextureHandle> byIdentity = new();
    private readonly Dictionary<(GltfTexture Texture, TextureFormat Format), TextureHandle> byObject = new();
    private readonly Dictionary<(string Id, TextureFormat Format), long> bytes = new();

    /// <summary>Distinct textures resident on the GPU through this registry.</summary>
    public int ResidentCount => byIdentity.Count + byObject.Count;

    /// <summary>
    /// Total GPU bytes of the identified textures resident through this registry.
    /// </summary>
    /// <remarks>
    /// Identified ones only, and the omission is honest rather than convenient: an unidentified
    /// texture may be a second copy of one already counted, and a residency figure that might be
    /// double-counting is worse than one that states what it covers. <see cref="UnidentifiedCount"/>
    /// says how much is outside it.
    /// </remarks>
    public long ResidentBytes
    {
        get
        {
            long total = 0;
            foreach (var v in bytes.Values) total += v;
            return total;
        }
    }

    /// <summary>Resident textures this could not name, and therefore could not share or measure.</summary>
    public int UnidentifiedCount => byObject.Count;

    /// <summary>Uploads avoided because the pixels were already resident. The point of the thing.</summary>
    public int SharedUploads { get; private set; }

    /// <summary>
    /// The handle for these pixels at this format, uploading through <paramref name="upload"/> only
    /// if they are not resident already.
    /// </summary>
    public TextureHandle GetOrAdd(GltfTexture texture, TextureFormat format, Func<TextureHandle> upload)
    {
        ArgumentNullException.ThrowIfNull(texture);
        ArgumentNullException.ThrowIfNull(upload);

        if (texture.ResourceId.Length == 0)
        {
            var objectKey = (texture, format);
            if (byObject.TryGetValue(objectKey, out var known))
            {
                SharedUploads++;
                return known;
            }

            var fresh = upload();
            byObject[objectKey] = fresh;
            return fresh;
        }

        var key = (texture.ResourceId, format);
        if (byIdentity.TryGetValue(key, out var resident))
        {
            SharedUploads++;
            return resident;
        }

        var handle = upload();
        byIdentity[key] = handle;
        bytes[key] = format.TextureByteCount(texture.Width, texture.Height, Math.Max(1, texture.MipCount));
        return handle;
    }

    /// <summary>Forgets every entry. The handles are the caller's to destroy; this only stops naming them.</summary>
    /// <remarks>
    /// Not eviction — it frees nothing and decides nothing. It exists so an owner tearing down a
    /// device does not leave this pointing at handles that no longer exist.
    /// </remarks>
    public void Clear()
    {
        byIdentity.Clear();
        byObject.Clear();
        bytes.Clear();
        SharedUploads = 0;
    }
}
