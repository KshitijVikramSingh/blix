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
    float MoveSpeed,
    Vector2? DefaultSunDegrees = null)
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
        // kloppenheim turned so its sun runs down the hall (yaw 90, elevation 74.5; tools/cook-sponza-modern.sh): under
        // Pizzo Pernice's (elevation 53) the sun barely clears the hall's walls and the default view was, in the
        // user's words, "a black mush" -- next to no sunlit floor to bounce from. Pizzo Pernice next: its detected
        // sun direction and irradiance independently round-trip to the source HDR within the recorded tolerance.
        ProbeCandidates: new[]
        {
            "kloppenheim_05_4k_sun90.blixprobe", "pizzo_pernice_puresky_4k.blixprobe", "kloppenheim_05_4k.blixprobe", "autumn_field_4k.blixprobe",
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
        MoveSpeed: 4.5f,
        // The sun across the hall, not down it (yaw 180, pitch -72, as --sun takes them): it lights the curtains from
        // behind and through, and the colonnade's arches side-on -- the user's pick (2026-10-13). The probe's sun stays
        // where the sky has it; --sun overrides this, --sun-authored keeps the authored direction.
        DefaultSunDegrees: new Vector2(180f, -72f));

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

    /// <summary>The Cornell box at room scale, sunlit through a skylight (<c>blix cornell</c>, tools/testbeds/setup.sh).</summary>
    /// <remarks>
    /// A correctness scene: one sunlit patch on the floor, and every other surface lit by its bounce, coloured by a
    /// red and a green wall; 0.2 m solid slabs, so light inside that did not come through the skylight or the open
    /// front is a leak. Procedural sky (no probe), whose default sun stands high enough to reach the floor. The
    /// camera stands outside the open front, looking in.
    /// </remarks>
    public static readonly SceneProfile Cornell = new(
        Name: "cornell",
        Title: "Vulkan Cornell box",
        AssetsVariable: "BLIX_TESTBED_ASSETS",
        SetupHint: "tools/testbeds/setup.sh generates and cooks it",
        Packs: new ScenePack[] { new("cornell", Required: true) },
        ProbeDirectory: "textures",
        ProbeCandidates: Array.Empty<string>(),
        FarPlane: 100f,
        CascadeSplits: new[] { 0.1f, 8f, 20f, 50f },
        SunDistance: 20f,
        FogFar: 30f,
        StartPosition: new Vector3(0f, 3f, 10f),
        StartYaw: 0f,
        StartPitch: 0f,
        MoveSpeed: 3f);

    /// <summary>The Cornell box with a panel hung from the skylight's edge (<c>blix cornell --panel</c>): with
    /// <c>--mover panel</c>, an occluder swinging across the light over surfaces that do not move.</summary>
    public static readonly SceneProfile CornellPanel = Cornell with
    {
        Name = "cornell-panel",
        Title = "Vulkan Cornell box, hanging panel",
        Packs = new ScenePack[] { new("cornell-panel", Required: true) },
    };

    /// <summary>The Cornell room with a veil and a pleated curtain in the sun's shaft (<c>blix drapes</c>): light through cloth.</summary>
    /// <remarks>
    /// Cornell's room and sun, its blocks swapped for two double-sided sheets that scatter light through themselves: the
    /// scene material response and transport through thin sheets are refined on, apart from Sponza's cost.
    /// </remarks>
    public static readonly SceneProfile Drapes = Cornell with
    {
        Name = "drapes",
        Title = "Vulkan drapes",
        Packs = new ScenePack[] { new("drapes", Required: true) },
    };

    /// <summary>A 60 m thin sheet 0.5 m over a floor (<c>blix sheetslab</c>): the closed form for the reference's sheets.</summary>
    /// <remarks>With <c>--ref-sky 0</c>, the floor under the sheet's middle is at T c E_s cos_s / (1 - rho_f rho_s (1 - T));
    /// the camera stands between the two, looking along them.</remarks>
    public static readonly SceneProfile SheetSlab = Cornell with
    {
        Name = "sheetslab",
        Title = "Vulkan sheet slab",
        Packs = new ScenePack[] { new("sheetslab", Required: true) },
        StartPosition = new Vector3(0f, 0.3f, 4f),
        StartPitch = -8f,
    };

    /// <summary>The sheet slab with a closed shell 1.5 cm thick for the sheet (<c>blix sheetslab --shell 0.015</c>): light
    /// through two walls, as the teacup's saucer has it, against its closed form (tools/gi-model/shell_slab.py).</summary>
    public static readonly SceneProfile SheetShell = SheetSlab with
    {
        Name = "sheetshell",
        Title = "Vulkan sheet shell",
        Packs = new ScenePack[] { new("sheetshell", Required: true) },
    };

    /// <summary>The sheet shell at 4 mm (<c>blix sheetslab --shell 0.004</c>): the teacup saucer's well, thinner than the gather's
    /// 1 cm lift; the same closed form (the gap's height does not enter it).</summary>
    public static readonly SceneProfile SheetShellThin = SheetSlab with
    {
        Name = "sheetshell-thin",
        Title = "Vulkan sheet shell, 4 mm",
        Packs = new ScenePack[] { new("sheetshell-thin", Required: true) },
    };

    /// <summary>Khronos' DiffuseTransmissionPlant x1.5 in the Cornell room's sunlit shaft (<c>blix khronos</c>): leaves that
    /// transmit red, on a model with reference renders elsewhere (Babylon.js; edited for path tracers' ground truth).</summary>
    public static readonly SceneProfile KhronosPlant = Cornell with
    {
        Name = "khronos-plant",
        Title = "Vulkan Khronos plant",
        Packs = new ScenePack[] { new("khronos-plant", Required: true) },
        StartPosition = new Vector3(-0.7f, 0.9f, 0.9f),
        StartPitch = -8f,
    };

    /// <summary>Khronos' DiffuseTransmissionTeacup x8 in the Cornell room's sunlit shaft: thin ceramic that lets light
    /// through (the glTF Sample Viewer's reference renders).</summary>
    public static readonly SceneProfile KhronosTeacup = Cornell with
    {
        Name = "khronos-teacup",
        Title = "Vulkan Khronos teacup",
        Packs = new ScenePack[] { new("khronos-teacup", Required: true) },
        StartPosition = new Vector3(-0.7f, 0.9f, 0.2f),
        StartPitch = -20f,
    };

    /// <summary>Khronos' DiffuseTransmissionTest x0.5 standing on open ground, a low sun behind it toward the camera (as
    /// its one directional light): each input of KHR_materials_diffuse_transmission on its own panel.</summary>
    public static readonly SceneProfile KhronosTest = Cornell with
    {
        Name = "khronos-test",
        Title = "Vulkan Khronos diffuse transmission test",
        Packs = new ScenePack[] { new("khronos-test", Required: true) },
        StartPosition = new Vector3(0.6f, 1.3f, 5f),
        StartPitch = -2f,
        DefaultSunDegrees = new Vector2(180f, -20f),
    };

    /// <summary>Two rooms sharing a 0.1 m wall, one open to the sky, one sealed (<c>blix thinwall</c>): a leak scene.</summary>
    /// <remarks>
    /// The truth inside the sealed room (x &gt; 0) is exactly no light, so every bit of indirect light the GI puts
    /// there crossed the wall. The camera starts at its centre, facing the shared wall; <c>--cam -3.05,3,0,90,0</c>
    /// is the same view from the open room, the light a leak is a fraction of.
    /// </remarks>
    public static readonly SceneProfile ThinWall = new(
        Name: "thinwall",
        Title: "Vulkan thin wall",
        AssetsVariable: "BLIX_TESTBED_ASSETS",
        SetupHint: "tools/testbeds/setup.sh generates and cooks it",
        Packs: new ScenePack[] { new("thinwall", Required: true) },
        ProbeDirectory: "textures",
        ProbeCandidates: Array.Empty<string>(),
        FarPlane: 100f,
        CascadeSplits: new[] { 0.1f, 8f, 20f, 50f },
        SunDistance: 20f,
        FogFar: 30f,
        StartPosition: new Vector3(3.05f, 3f, 0f),
        StartYaw: -90f,
        StartPitch: 0f,
        MoveSpeed: 3f);

    /// <summary>A courtyard with a slot, a shelf niche, an L corridor and a narrow gap (<c>blix crevices</c>): the shade scene.</summary>
    /// <remarks>
    /// Where the light reaches a surface through a few narrow paths. The camera stands in the courtyard's front half,
    /// looking at the back wall's slot and the shelf; <c>--cam 6,1.5,1.5,90,-5</c> looks down the corridor's first leg.
    /// </remarks>
    public static readonly SceneProfile Crevices = new(
        Name: "crevices",
        Title: "Vulkan crevices",
        AssetsVariable: "BLIX_TESTBED_ASSETS",
        SetupHint: "tools/testbeds/setup.sh generates and cooks it",
        Packs: new ScenePack[] { new("crevices", Required: true) },
        ProbeDirectory: "textures",
        ProbeCandidates: Array.Empty<string>(),
        FarPlane: 100f,
        CascadeSplits: new[] { 0.1f, 8f, 20f, 50f },
        SunDistance: 20f,
        FogFar: 30f,
        StartPosition: new Vector3(0f, 1.6f, 3.5f),
        StartYaw: -20f,
        StartPitch: -8f,
        MoveSpeed: 3f);

    /// <summary>The crevices at a grey of 0.25 (<c>blix crevices --albedo 0.25</c>): their shade lit through rare paths.</summary>
    public static readonly SceneProfile CrevicesDark = Crevices with
    {
        Name = "crevices-dark",
        Title = "Vulkan crevices (dark)",
        Packs = new ScenePack[] { new("crevices-dark", Required: true) },
    };

    /// <summary>The dark crevices cooked without a lightmap: every texel hashed (the cell grid's), as Sponza's trees,
    /// ivy and every surface the atlas leaves out are -- what --texel-filter does there.</summary>
    public static readonly SceneProfile CrevicesHashed = CrevicesDark with
    {
        Name = "crevices-hashed",
        Title = "Vulkan crevices (dark, hashed texels)",
        Packs = new ScenePack[] { new("crevices-hashed", Required: true) },
    };

    /// <summary>Crytek Sponza (Khronos glTF-Sample-Assets' Sponza: the CRYENGINE Limited License, downloaded, not kept in the
    /// repo; tools/testbeds/setup.sh with BLIX_TESTBED_KHRONOS=1): the atrium at ~262 k triangles, normal-mapped stone
    /// throughout -- Sponza's layout at a testbed's cost, where a light-change or a texel record's direction is quick to run.</summary>
    public static readonly SceneProfile CrytekSponza = Sponza with
    {
        Name = "crytek-sponza",
        Title = "Vulkan Crytek Sponza (testbed)",
        AssetsVariable = "BLIX_TESTBED_ASSETS",
        SetupHint = "tools/testbeds/setup.sh with BLIX_TESTBED_KHRONOS=1 downloads and cooks it",
        Packs = new ScenePack[] { new("crytek-sponza", Required: true) },
        ProbeCandidates = Array.Empty<string>(),
        // On the hall's floor at standing height, down the hall: Sponza's (-9, 3, 0) stands inside this model's west-end
        // gallery floor (between its vaults' tops and the floor slab), where 8% of the first view read only buried
        // records -- black where the camera saw the shell's inside (2026-10-23).
        StartPosition = new Vector3(-6f, 1.7f, 0f),
        StartPitch = 5f,
    };

    public static readonly IReadOnlyList<SceneProfile> All = new[] { Sponza, Bistro, City, Cornell, CornellPanel, Drapes, SheetSlab, SheetShell, SheetShellThin, ThinWall, Crevices, CrevicesDark, CrevicesHashed, KhronosPlant, KhronosTeacup, KhronosTest, CrytekSponza };

    /// <summary>The profile <c>--scene</c> names, Sponza when it names none.</summary>
    public static SceneProfile Named(string? name)
    {
        if (name is null) return Sponza;
        return All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw new Blix.Core.AppArgsException(
                $"--scene '{name}' is not one this renderer knows; it knows {string.Join(", ", All.Select(p => p.Name))}.");
    }
}
