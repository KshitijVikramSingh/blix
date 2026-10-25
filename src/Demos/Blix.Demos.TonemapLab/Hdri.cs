using Blix.Graphics;
using Blix.Graphics.Images;

namespace Blix.Demos.TonemapLab;

// Environment maps for the demo: Poly Haven's CC0 HDRIs (lab/tonemap/sources/hdris.json lists them; tools/fetch_hdris.py fetches
// them into lab/tonemap/data/hdri). Each is loaded on first use, scaled so its trimmed meter lands on the procedural noon's level
// (the files are not in absolute units: a night map's median is only a few stops under a day's), clamped to half-float range
// (the suns reach 245,000 and the engine's equirect-to-cube converter casts straight to half, which is infinity above 65,504),
// converted to a cube and uploaded as RGBA16F.
internal sealed class HdriLibrary : IDisposable
{
    /// <summary>The metered level the procedural noon starts at, in EV; every HDRI is scaled to meter here.</summary>
    public const double NoonMeterEv = 3.27;

    private const int FaceSize = 1024;
    private const float HalfSafeMaximum = 60000f;

    private readonly IGraphicsDevice device;
    private readonly Dictionary<string, TextureHandle> loaded = new();
    private readonly TextureHandle empty;

    public HdriLibrary(IGraphicsDevice device, string? directory)
    {
        this.device = device;
        // A one-texel cube for when no environment is selected: the scene shader always has something bound.
        empty = device.CreateTextureCubeHdr(1, new Half[6 * 4], SamplerDescription.LinearClamp, "hdri.empty");
        Directory = directory;
        Names = directory is not null && System.IO.Directory.Exists(directory)
            ? System.IO.Directory.GetFiles(directory, "*_2k.hdr").Select(f => Path.GetFileName(f)[..^"_2k.hdr".Length]).OrderBy(n => n, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
    }

    public string? Directory { get; }
    public IReadOnlyList<string> Names { get; }
    public TextureHandle Empty => empty;

    /// <summary>The cube for <paramref name="name"/>, loading it the first time. Also the meter it had before scaling.</summary>
    public (TextureHandle Cube, double MeterEv) Get(string name)
    {
        if (loaded.TryGetValue(name, out var cached)) return (cached, 0.0);
        var path = Path.Combine(Directory!, $"{name}_2k.hdr");
        var started = DateTime.UtcNow;
        var hdr = ImageLoader.LoadRgba32F(path);
        var meter = Meter.TrimmedEv(Meter.HistogramEquirect(hdr.Pixels, hdr.Width, hdr.Height), 0.09);
        var scale = (float)Math.Pow(2.0, NoonMeterEv - meter);
        var scaled = new float[hdr.Pixels.Length];
        for (var i = 0; i < scaled.Length; i++) scaled[i] = Math.Min(hdr.Pixels[i] * scale, HalfSafeMaximum);
        var faces = EquirectangularToCubemap.Convert(new HdrImageData(hdr.Width, hdr.Height, scaled), FaceSize);
        var cube = device.CreateTextureCubeHdr(FaceSize, faces, SamplerDescription.LinearClamp, $"hdri.{name}");
        loaded[name] = cube;
        Console.WriteLine($"[tonemap-lab] loaded {name}: {hdr.Width}x{hdr.Height}, trimmed meter {meter:F2} EV, scaled by {scale:F1}, in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
        return (cube, meter);
    }

    /// <summary>The default directory: lab/tonemap/data/hdri, found by walking up from the executable.</summary>
    public static string? FindDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "lab", "tonemap", "data", "hdri");
            if (System.IO.Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    public void Dispose()
    {
        foreach (var cube in loaded.Values) device.DestroyTexture(cube);
        device.DestroyTexture(empty);
    }
}
