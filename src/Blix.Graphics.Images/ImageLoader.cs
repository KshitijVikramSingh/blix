using StbImageSharp;

namespace Blix.Graphics.Images;

public static class ImageLoader
{
    public static ImageData LoadRgba32(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Image file not found: {path}", path);
        }

        // stb defaults to bottom-up; OpenGL UV origin is bottom-left, so we want
        // image rows top-to-bottom to match the engine's UV convention (top-left = (0, 0)).
        StbImage.stbi_set_flip_vertically_on_load(1);

        using var stream = File.OpenRead(path);
        var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

        if (result.Width <= 0 || result.Height <= 0 || result.Data is null)
        {
            throw new InvalidDataException($"Image file is not a valid decodable image: {path}");
        }

        return new ImageData(
            result.Width,
            result.Height,
            Blix.Graphics.TextureFormat.Rgba8,
            result.Data);
    }

    // In-memory decode for image bytes that aren't on disk — the canonical case is a
    // PNG/JPEG embedded in a .glb's binary chunk. Same Y-flip as the disk path so UV
    // conventions match across both loading sources.
    public static ImageData LoadRgba32(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        StbImage.stbi_set_flip_vertically_on_load(1);
        var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        if (result.Width <= 0 || result.Height <= 0 || result.Data is null)
        {
            throw new InvalidDataException("Stream is not a valid decodable image.");
        }
        return new ImageData(
            result.Width,
            result.Height,
            Blix.Graphics.TextureFormat.Rgba8,
            result.Data);
    }

    // glTF metallicRoughness loader. Same as LoadRgba32 except for one fix:
    // when the source PNG is 1-channel grayscale (common for "Roughness only"
    // textures -- Khronos's Modern Sponza ships these), stb_image expands the
    // single value Y into (Y, Y, Y, 255) on RGBA-forced load. The shader
    // would then read .b as metallic (per glTF spec B = metalness), turning
    // every matte-stone surface into a fully metallic "mirror" with no
    // diffuse contribution. Symptom: shadowed walls render pure black, since
    // kD_ibl = (1 - metallic) -> ~0 collapses the indirect diffuse term.
    //
    // Fix: detect 1-channel sources via result.SourceComp and rewrite the
    // buffer to the canonical ORM layout where:
    //   R = 255 (no AO, full visibility)
    //   G = the grayscale value (roughness)
    //   B = 0   (no metallic)
    //   A = 255
    // 2-channel grey+alpha sources (rare for MR; would represent the gray
    // duplicated into RGB by stb again) get the same treatment.
    // 3+ channel sources are assumed to be properly authored with metallic
    // in B per glTF spec, and pass through unchanged.
    public static ImageData LoadMetallicRoughness(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        StbImage.stbi_set_flip_vertically_on_load(1);
        var result = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        if (result.Width <= 0 || result.Height <= 0 || result.Data is null)
        {
            throw new InvalidDataException("Stream is not a valid decodable image.");
        }

        if (result.SourceComp == ColorComponents.Grey
            || result.SourceComp == ColorComponents.GreyAlpha)
        {
            var data = result.Data;
            for (int i = 0; i + 3 < data.Length; i += 4)
            {
                data[i + 0] = 255; // R: no AO
                // G stays as the gray value (roughness)
                data[i + 2] = 0;   // B: no metallic
                data[i + 3] = 255; // A
            }
        }

        return new ImageData(
            result.Width,
            result.Height,
            Blix.Graphics.TextureFormat.Rgba8,
            result.Data);
    }

    public static ImageData LoadMetallicRoughness(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Image file not found: {path}", path);
        }
        using var stream = File.OpenRead(path);
        return LoadMetallicRoughness(stream);
    }

    // HDR (Radiance .hdr / RGBE) loader. Returns linear float RGBA per pixel; the
    // alpha channel is always 1.0 since .hdr is RGB-only. Pixels are in the
    // image's natural top-to-bottom order (v=0 = sky for equirect HDRIs).
    // Unlike LoadRgba32 we do NOT y-flip on load -- equirect math
    // (phi = acos(y), v = phi/pi) assumes the canonical layout where the
    // first scanline is the upward pole. Flipping here would put the ground
    // above the camera and the sky below.
    public static HdrImageData LoadRgba32F(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"HDR image file not found: {path}", path);
        }

        StbImage.stbi_set_flip_vertically_on_load(0);
        using var stream = File.OpenRead(path);
        var result = ImageResultFloat.FromStream(stream, ColorComponents.RedGreenBlueAlpha);
        // Restore the flip-on-load flag for callers that follow (LoadRgba32
        // relies on it being set; the global flag is sticky across calls).
        StbImage.stbi_set_flip_vertically_on_load(1);
        if (result.Width <= 0 || result.Height <= 0 || result.Data is null)
        {
            throw new InvalidDataException($"HDR file is not a valid decodable image: {path}");
        }
        return new HdrImageData(result.Width, result.Height, result.Data);
    }
}
