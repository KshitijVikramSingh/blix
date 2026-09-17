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

        // <b>No flip. glTF puts UV (0,0) at the TOP-LEFT and so does Vulkan, and stb already
        // returns rows top-down — so the correct setting is the one that does nothing.</b>
        //
        // This read `1` and carried the comment "OpenGL UV origin is bottom-left", which was true
        // of a backend this engine no longer has: the GL sunset left the flip behind, and the flag
        // it cites makes rows BOTTOM-up, the opposite of what the comment claimed to want. Every
        // decoded image was therefore stored upside down, and `flipTextureV` — which negates V at
        // import — became the thing a consumer had to remember to pass to cancel it. Exactly one
        // did (VulkanSponza), so Sponza looked right and everything else rendered mirrored.
        //
        // Caught by Khronos's TextureCoordinateTest, which exists to answer this and rendered its
        // "top left" quad at the bottom with the lettering reversed. Nobody had looked, because
        // this tree's textured assets are mostly palette kits and roughly symmetric atlases; the
        // first freshly converted asset with an oriented texture made it obvious.
        StbImage.stbi_set_flip_vertically_on_load(0);

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
        StbImage.stbi_set_flip_vertically_on_load(0);
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
        StbImage.stbi_set_flip_vertically_on_load(0);
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
    // No y-flip, for the same reason nothing else here flips any more -- equirect math
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
        if (result.Width <= 0 || result.Height <= 0 || result.Data is null)
        {
            throw new InvalidDataException($"HDR file is not a valid decodable image: {path}");
        }
        return new HdrImageData(result.Width, result.Height, result.Data);
    }
}
