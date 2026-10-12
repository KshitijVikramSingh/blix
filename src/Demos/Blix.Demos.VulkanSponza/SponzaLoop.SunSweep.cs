using System.Globalization;
using System.Numerics;
using Blix.Core;

namespace Blix.Demos.VulkanSponza;

// --sun-sweep FILE: the texels' light baked at a list of suns in one run, for studying the light across the band
// (plan.md, potential suns: baked at band nodes, interpolated between). FILE holds one sun a line, "hour dec" in
// degrees at the band's latitude, north as the lightlets set it (BandNorth); '#' starts a comment. Once the boot's
// texels are at rest, each sun in turn: turned to (FollowSun re-solves the patches, the light re-bakes, RestTexels
// resolves and saves it), and when its resolved file is on disk a line goes to the manifest (--sun-sweep-out, default
// FILE.manifest): "hour dec elevation toSun.x toSun.y toSun.z path". The run closes after the last.
internal sealed partial class SponzaLoop
{
    private (float Hour, float Dec)[]? sunSweep;
    private string? sunSweepManifest;
    private int sunSweepIndex = -1;
    private string? sunSweepWaitingFor;
    private System.Diagnostics.Stopwatch? sunSweepClock;

    private void ReadSunSweepArgs(AppArgs args)
    {
        if (args.String("sun-sweep") is not { } file) return;
        var suns = new List<(float, float)>();
        foreach (var raw in File.ReadAllLines(file))
        {
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            suns.Add((float.Parse(parts[0], CultureInfo.InvariantCulture), float.Parse(parts[1], CultureInfo.InvariantCulture)));
        }
        sunSweep = suns.ToArray();
        sunSweepManifest = args.String("sun-sweep-out") ?? file + ".manifest";
        File.WriteAllText(sunSweepManifest, "# hour dec elevation toSun.x toSun.y toSun.z resolved\n");
        Console.WriteLine($"[VulkanSponza] sun sweep: {sunSweep.Length} suns from {file}; manifest {sunSweepManifest}.");
    }

    // FollowSun, each frame: the next sun once the last one's resolved light is saved.
    private void StepSunSweep()
    {
        if (sunSweep is null || lightBakeKey is null) return;
        // At rest: baked, resolved, the gather's buffers let go (the save is read back just before they go).
        var resting = !lightBakeActive && relightTask is null && texelResolveCurrent && texelGatherFreed;
        if (!resting) return;
        if (sunSweepWaitingFor is { } path)
        {
            if (!File.Exists(path)) return;
            var (hour, dec) = sunSweep[sunSweepIndex];
            var toSun = -Vector3.Normalize(sunDirection);
            File.AppendAllText(sunSweepManifest!, string.Create(CultureInfo.InvariantCulture,
                $"{hour:0.####} {dec:0.####} {MathF.Asin(toSun.Y) * 180f / MathF.PI:0.###} {toSun.X:0.######} {toSun.Y:0.######} {toSun.Z:0.######} {path}\n"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] sun sweep: {sunSweepIndex + 1}/{sunSweep.Length} saved in {sunSweepClock!.Elapsed.TotalSeconds:0} s (hour {hour:0.##}, dec {dec:0.##})."));
            sunSweepWaitingFor = null;
        }
        if (++sunSweepIndex >= sunSweep.Length)
        {
            Console.WriteLine("[VulkanSponza] sun sweep: done; closing.");
            sunSweep = null;
            host.RequestClose();
            return;
        }
        var next = sunSweep[sunSweepIndex];
        var north = BandNorth(out _);
        // Through yaw and pitch, as the Sun controls hold it (they rebuild the direction each frame the overlay shows).
        var travel = -ToScene(SunLocal(BandLatitude, next.Dec, next.Hour), north);
        sunPitch = MathF.Asin(Math.Clamp(travel.Y, -1f, 1f));
        sunYaw = MathF.Atan2(travel.X, -travel.Z);
        UpdateSunDirection();
        // What FollowSun will solve for, and so where the bake's resolved light lands.
        var target = ResolvedPathFor(LightBakePathFor(lightBakeKey, (-Vector3.Normalize(sunDirection), EffectiveSunIrradiance)));
        if (File.Exists(target)) File.Delete(target);
        sunSweepWaitingFor = target;
        sunSweepClock = System.Diagnostics.Stopwatch.StartNew();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] sun sweep: {sunSweepIndex + 1}/{sunSweep.Length}: hour {next.Hour:0.##}, dec {next.Dec:0.##} -> {sunDirection}."));
    }
}
