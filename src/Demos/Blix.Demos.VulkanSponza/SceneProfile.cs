using System.Numerics;

namespace Blix.Demos.VulkanSponza;

/// <summary>One cooked pack of a scene: a directory under the scene's root holding one <c>.blixmesh</c>.</summary>
/// <param name="Directory">Relative to the scene root.</param>
/// <param name="Required">A missing required pack stops the run; an optional one is skipped.</param>
/// <param name="Foliage">Dropped by <c>--no-foliage</c>, the pack-level arm that prices cutout geometry.</param>
internal sealed record ScenePack(string Directory, bool Required = false, bool Foliage = false);

/// <summary>What this renderer needs to know about a scene that its files do not say.</summary>
/// <remarks>
/// <para>
/// The renderer is generic over whatever meshes load — LOD, culling, the indirect groups and the pre-pass
/// never asked which scene they were drawing. What was Sponza's was the layout of its cooked tree and a set
/// of constants sized to a 30 m building: the far plane, the cascade splits, the sun's distance, the fog's
/// reach and where the camera starts. Those are this record, one per scene, chosen with <c>--scene</c>.
/// </para>
/// <para>
/// They are the demo's decisions, so they live here and not in the engine or the cook. The probe's sun still
/// comes from the cooked tree, as it did; the scene's bounds come from its placements.
/// </para>
/// </remarks>
internal sealed record SceneProfile(
    string Name,
    string Title,
    string AssetsVariable,
    string SetupHint,
    ScenePack[] Packs,
    string ProbeDirectory,
    string[] ProbeCandidates,
    float FarPlane,
    float[] CascadeSplits,
    float SunDistance,
    float FogFar,
    Vector3 StartPosition,
    float StartYaw,
    float StartPitch,
    float MoveSpeed)
{
    /// <summary>Intel Sponza (Khronos New Sponza): the atrium, ~30 m, and the packs it was built around.</summary>
    public static readonly SceneProfile Sponza = new(
        Name: "sponza",
        Title: "Vulkan Sponza",
        AssetsVariable: "BLIX_SPONZA_ASSETS",
        SetupHint: "tools/setup-sponza-modern.sh prepares one from your local Khronos packs",
        Packs: new ScenePack[]
        {
            new("main_sponza", Required: true),
            new("curtains"),
            new("ivy", Foliage: true),
            new("trees", Foliage: true),
        },
        ProbeDirectory: "textures",
        // Pizzo Pernice is the standard first choice; its detected sun direction and irradiance
        // independently round-trip to the source HDR within the recorded tolerance.
        ProbeCandidates: new[]
        {
            "pizzo_pernice_puresky_4k.blixprobe", "kloppenheim_05_4k.blixprobe", "autumn_field_4k.blixprobe",
            "rogland_overcast_4k.blixprobe", "sky_hdr.blixprobe",
        },
        FarPlane: 200f,
        // The 14 m near split keeps its resolution transition out of common mid-range subjects.
        CascadeSplits: new[] { 0.1f, 14f, 30f, 60f },
        SunDistance: 40f,
        FogFar: 60f,
        // Aimed at the +X end-wall lavabo (wall fountain), so the sculpture is in the first frame: useful
        // for normal-map debugging.
        StartPosition: new Vector3(-9f, 3f, 0f),
        StartYaw: 90f,
        StartPitch: 0f,
        MoveSpeed: 4.5f);

    /// <summary>Amazon Lumberyard Bistro, exterior (ORCA, CC-BY 4.0): a street ~170 m by 180 m, 2.8M triangles.</summary>
    /// <remarks>
    /// The start pose is the file's own camera at the first key of its path (composed through its parent),
    /// so the first frame is the view Bistro was authored to be seen from. The far plane and cascades are
    /// sized to the street: the old 60 m shadow reach stopped mid-block.
    /// </remarks>
    public static readonly SceneProfile Bistro = new(
        Name: "bistro",
        Title: "Vulkan Bistro",
        AssetsVariable: "BLIX_BISTRO_ASSETS",
        SetupHint: "tools/bistro/setup.sh fetches, converts and cooks it",
        Packs: new ScenePack[] { new("exterior", Required: true) },
        ProbeDirectory: "Bistro_v5_2",
        // The sky its .pyscene names, shipped in the pack (Poly Haven's San Giuseppe bridge).
        ProbeCandidates: new[] { "san_giuseppe_bridge_4k.blixprobe" },
        FarPlane: 500f,
        CascadeSplits: new[] { 0.1f, 20f, 60f, 150f },
        SunDistance: 120f,
        FogFar: 150f,
        StartPosition: new Vector3(24.82f, 3.16f, -61.65f),
        StartYaw: -161.4f,
        StartPitch: -3.3f,
        MoveSpeed: 8f);

    /// <summary>The generated city (<c>blix city</c>, tools/city/setup.sh): the scale scene, any size from a seed.</summary>
    /// <remarks>
    /// No probe ships with it, so it lights with the procedural sky (the clipmap still traces it):
    /// it is for measuring how the frame grows with the world, not for how it looks. The start pose stands
    /// in the street at x = -7 (the street east of the fourth block column at the default sizes), looking
    /// down it.
    /// </remarks>
    public static readonly SceneProfile City = new(
        Name: "city",
        Title: "Vulkan City",
        AssetsVariable: "BLIX_CITY_ASSETS",
        SetupHint: "tools/city/setup.sh generates and cooks one",
        Packs: new ScenePack[] { new("city", Required: true) },
        ProbeDirectory: "textures",
        ProbeCandidates: Array.Empty<string>(),
        FarPlane: 2000f,
        CascadeSplits: new[] { 0.1f, 30f, 120f, 400f },
        SunDistance: 120f,
        FogFar: 150f,
        StartPosition: new Vector3(-7f, 1.8f, 30f),
        StartYaw: 0f,
        StartPitch: 0f,
        MoveSpeed: 15f);

    public static readonly IReadOnlyList<SceneProfile> All = new[] { Sponza, Bistro, City };

    /// <summary>The profile <c>--scene</c> names, Sponza when it names none.</summary>
    public static SceneProfile Named(string? name)
    {
        if (name is null) return Sponza;
        return All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new Blix.Core.AppArgsException(
                $"--scene '{name}' is not one this renderer knows; it knows {string.Join(", ", All.Select(p => p.Name))}.");
    }
}
