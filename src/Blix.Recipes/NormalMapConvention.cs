using Blix.Graphics.Images;
using SharpGLTF.Schema2;

namespace Blix.Recipes;

/// <summary>
/// Which way a normal map's green channel points, measured from its pixels.
/// </summary>
/// <remarks>
/// <para>
/// A tangent-space normal map is the gradient of a height field, so read in the convention it was
/// made in its curl is near zero, and read in the other convention it is not. glTF specifies green
/// up (OpenGL); a map made green down (DirectX) is lit upside down by a renderer that obeys the spec.
/// NormalTangentTest, the corpus asset that pins the convention, measures 0.010 read as OpenGL and
/// 0.088 read as DirectX.
/// </para>
/// <para>
/// <b>An instrument, not a decision.</b> It proposes and checks a project's <c>normal=</c> patch
/// rules; nothing flips a map on its authority. A flat or noisy map measures unclear, and a person
/// looks at it.
/// </para>
/// </remarks>
public static class NormalMapConvention
{
    /// <summary>The curl of the map read each way; the smaller is the convention it was made in.</summary>
    public readonly record struct Reading(double OpenGlCurl, double DirectXCurl)
    {
        /// <summary>How many times more curl the losing reading has. Always at least 1.</summary>
        public double Separation => Math.Max(OpenGlCurl, DirectXCurl) / Math.Max(Math.Min(OpenGlCurl, DirectXCurl), 1e-9);

        /// <summary>"opengl", "directx", or "unclear" when the two readings are within <paramref name="margin"/>.</summary>
        public string Verdict(double margin = 1.3) =>
            Separation < margin ? "unclear" : OpenGlCurl < DirectXCurl ? "opengl" : "directx";
    }

    /// <summary>One material's normal map, what it measures, and what the patch declares for it.</summary>
    /// <param name="Declared">"directx", or "opengl" whether stated or left at glTF's default.</param>
    public readonly record struct Row(string Material, string Image, Reading Reading, string Declared);

    /// <summary>Measures an RGBA8 normal map, sampled down to at most <paramref name="maxSide"/> on a side.</summary>
    public static Reading Measure(ReadOnlySpan<byte> rgba, int width, int height, int maxSide = 256)
    {
        var step = Math.Max(1, Math.Max(width, height) / maxSide);
        var w = width / step;
        var h = height / step;
        if (w < 3 || h < 3) return new Reading(0, 0);

        // Height slopes per sample: dh/dcol = -x/z, and dh/drow = +y/z read as OpenGL (rows run down
        // the image, green points up). The DirectX reading negates the row slope.
        //
        // Each sample AVERAGES its step-by-step block. Taking every step-th pixel instead aliases the
        // map's finest detail into curl that both readings share, and every map measures unclear.
        var slopeCol = new double[w * h];
        var slopeRow = new double[w * h];
        for (var r = 0; r < h; r++)
        for (var c = 0; c < w; c++)
        {
            double x = 0, y = 0, z = 0;
            for (var dr = 0; dr < step; dr++)
            for (var dc = 0; dc < step; dc++)
            {
                var at = ((((r * step) + dr) * width) + (c * step) + dc) * 4;
                x += (rgba[at] / 255.0 * 2) - 1;
                y += (rgba[at + 1] / 255.0 * 2) - 1;
                z += (rgba[at + 2] / 255.0 * 2) - 1;
            }

            var count = step * step;
            var zs = Math.Max(z / count, 0.2);
            slopeCol[(r * w) + c] = -(x / count) / zs;
            slopeRow[(r * w) + c] = (y / count) / zs;
        }

        double openGl = 0, directX = 0;
        var n = 0;
        for (var r = 1; r < h - 1; r++)
        for (var c = 1; c < w - 1; c++)
        {
            var dColByRow = (slopeCol[((r + 1) * w) + c] - slopeCol[((r - 1) * w) + c]) * 0.5;
            var dRowByCol = (slopeRow[(r * w) + c + 1] - slopeRow[(r * w) + c - 1]) * 0.5;
            openGl += (dColByRow - dRowByCol) * (dColByRow - dRowByCol);
            directX += (dColByRow + dRowByCol) * (dColByRow + dRowByCol);
            n++;
        }

        return new Reading(Math.Sqrt(openGl / n), Math.Sqrt(directX / n));
    }

    /// <summary>Every material's normal map in <paramref name="gltfPath"/>, measured beside what the patch declares.</summary>
    public static IReadOnlyList<Row> Survey(string gltfPath, MaterialPatch? patch = null)
    {
        ArgumentNullException.ThrowIfNull(gltfPath);
        var model = ModelRoot.Load(gltfPath);
        var names = model.LogicalMaterials.Select((m, i) => m.Name ?? $"material_{i}").ToArray();
        var directX = patch?.DirectXNormalMaterials(names) ?? new HashSet<int>();
        var measured = new Dictionary<int, Reading>();
        var rows = new List<Row>();

        for (var i = 0; i < names.Length; i++)
        {
            var image = model.LogicalMaterials[i].FindChannel("Normal")?.Texture?.PrimaryImage;
            if (image is null) continue;
            if (!measured.TryGetValue(image.LogicalIndex, out var reading))
            {
                using var stream = new MemoryStream(image.Content.Content.ToArray());
                var pixels = ImageLoader.LoadRgba32(stream);
                reading = Measure(pixels.Pixels, pixels.Width, pixels.Height);
                measured[image.LogicalIndex] = reading;
            }

            var imageName = image.Content.SourcePath is { } uri && !uri.StartsWith("data:", StringComparison.Ordinal)
                ? Path.GetFileName(uri)
                : image.Name ?? $"image_{image.LogicalIndex}";
            rows.Add(new Row(names[i], imageName, reading, directX.Contains(i) ? "directx" : "opengl"));
        }

        return rows;
    }
}
