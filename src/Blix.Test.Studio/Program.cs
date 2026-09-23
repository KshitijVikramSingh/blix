using System.Numerics;
using Blix;
using Blix.Cooked;
using Blix.Graphics;
using Blix.Graphics.Vulkan;
using Blix.Tools.Studio;
using Blix.Verify;

// CLI test harness for the studio's binding model — the shaders it ships against the constants
// the renderer packs to.
//
// ── Why this is a suite and not a verb ──────────────────────────────────────
//   It used to be the first two hundred lines of `blix check`, which is a verb you
//   type at an ASSET. Running it with no asset still printed a full binding-model
//   report and exited 0, which is the tell: it never had an asset as its subject.
//   Its subject is the studio, and a thing that judges the studio belongs in the
//   gate that judges Blix.
//
//   Nothing here needs a device. The reflection sidecars are written at build by
//   spirv-cross and copied into this project's output by the same <None> glob every
//   studio consumer gets, so the shaders judged are the ones that ship.
//
// ── What it is FOR ──────────────────────────────────────────────────────────
//   Every number below exists in two places: once in a GLSL file and once in a C#
//   constant the renderer packs to. `mat4 m[128]` and StudioRig.MaxBones are the
//   same fact written twice, and two copies of a fact drift. The reflected block is
//   the authority; this is what notices when C# stops agreeing with it.
//
//   A push size that disagrees is not a subtle bug — the device refuses the draw —
//   but a PALETTE size that disagrees is: the lit pass looks perfect while the
//   caster reads past the end of its array, and the only symptom is a shadow that
//   belongs to a different body.
public static class Program
{
    public static int Main(string[] args)
    {
        var verbose = args.Contains("--verbose");
        var t = new TestRunner();

        var shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders");
        t.Expect("the studio's shaders are where a consumer would find them",
            Directory.Exists(shaderDirectory), shaderDirectory);

        if (!Directory.Exists(shaderDirectory))
        {
            t.PrintSummary();
            return 1;
        }

        // Each program, and the push size the renderer packs for it. A null means the renderer
        // pushes nothing and there is no constant to disagree with.
        var programs = new (string Name, string[] Stages, int? ExpectedPush)[]
        {
            ("studio.shadow", new[] { "studio_shadow.vert", "studio_shadow.frag" }, StudioRenderer.CasterPushBytes),
            ("studio.lit", new[] { "studio_lit.vert", "studio_lit.frag" }, StudioRenderer.LitPushBytes),
            ("studio.present", new[] { "studio_present.vert", "studio_present.frag" }, null),

            // The skinned LIT pass pushes the same 96-byte block as the unskinned one — the stride
            // rides in uMaterial's spare .z rather than widening it, because the block has to stay
            // byte-identical to what studio_lit.frag declares. The skinned CASTER pushes 16: its
            // fragment stage declares no block at all, so it was free to be exactly what the stage
            // uses, and an instance's placement is already in its palette.
            ("studio.skinned", new[] { "studio_skinned.vert", "studio_lit.frag" }, StudioRenderer.LitPushBytes),
            // <b>studio_skinned_shadow.frag, not studio_shadow.frag.</b> This pairing was left behind
            // when the skinned caster got a fragment stage of its own, and the stale half is what the
            // reflected total then described: a vertex and fragment stage that declare different push
            // blocks reflect the SUM, so pairing the skinned vert with the static frag reported 80
            // bytes against a 16-byte payload and read as a renderer bug. The renderer was right the
            // whole time — StudioRenderer builds this program from the matching frag.
            ("studio.skinned.shadow",
                new[] { "studio_skinned_shadow.vert", "studio_skinned_shadow.frag" },
                StudioRenderer.SkinnedCasterPushBytes),
        };

        foreach (var (name, stages, expectedPush) in programs)
        {
            ShaderInterface iface;
            try
            {
                iface = ShaderReflection.MergeStages(
                    stages.Select(s => ShaderReflection.Load(
                        Path.Combine(shaderDirectory, s + ".spv.refl.json"))).ToArray());
            }
            catch (Exception exception)
            {
                t.Fail($"{name} reflects", exception.Message);
                continue;
            }

            t.Pass($"{name} reflects  [{string.Join(", ", stages)}]");
            if (verbose) Describe(name, iface);

            if (expectedPush is not { } expected) continue;

            var pushTotal = 0;
            foreach (var range in iface.PushConstants) pushTotal = Math.Max(pushTotal, range.Offset + range.Size);

            t.Expect($"{name} push block matches the renderer's constant",
                pushTotal == expected,
                $"the shader declares {pushTotal}B, the renderer pushes {expected}B — one of them is " +
                "wrong and the device will refuse the draw");
        }

        CheckPalette(t, shaderDirectory);
        CheckRigCapacity(t);

        // Links the studio's own code, not only its build output: the stage's look is described
        // without a device anywhere in sight, which is the point of it being declared state rather
        // than a renderer's private field.
        var stage = new StudioRenderer();
        var look = stage.Look;
        t.Expect("the stage's declared look is readable without a device",
            look.SunElevation is > 0f and < 90f && look.ShadowSubjectRadius > 0f,
            $"sun {look.SunAzimuth:0.0} az / {look.SunElevation:0.0} el, " +
            $"ambient {look.AmbientStrength:0.00}, subject box {look.ShadowSubjectRadius:0.0}m " +
            $"at {StudioRenderer.ShadowMapSize}px");

        // <b>The house style is the type's defaults, so a fresh one must equal the stage's.</b> The
        // renderer owning its look is what lets `view` be opinionated without being asked; if these
        // two ever differ, the renderer has started adjusting the house style on the way past and
        // "what Blix thinks this should look like" has quietly become "what this tool does".
        var house = new StudioLook();
        t.Expect("a fresh StudioLook IS the house style the stage draws with",
            house.SunAzimuth == look.SunAzimuth && house.SunElevation == look.SunElevation
            && house.AmbientStrength == look.AmbientStrength && house.ShadowSubjectRadius == look.ShadowSubjectRadius
            && house.Exposure == look.Exposure && house.TonemapMode == look.TonemapMode
            && house.Ground == look.Ground,
            $"house sun {house.SunAzimuth:0.000}/{house.SunElevation:0.000} vs " +
            $"stage {look.SunAzimuth:0.000}/{look.SunElevation:0.000}");

        // The derived vectors agree with the angles from frame one — the fault the constructor's
        // Recompute exists for, now asserted where it cannot be reached by looking at a picture.
        var elevation = look.SunElevation * (MathF.PI / 180f);
        t.Expect("and its derived sun direction agrees with its declared angles",
            MathF.Abs(look.SunDirection.Y - MathF.Sin(elevation)) < 1e-5f
            && MathF.Abs(look.SunDirection.Length() - 1f) < 1e-5f,
            $"{look.SunDirection} vs elevation {look.SunElevation:0.000}°");

        // ── the tint table, which is how a kit asset gets a colour it does not ship ──────────
        //
        // <b>Keyed on the material NAME, which is the whole reason a game stays out of this.</b> The
        // 29 files in RTSGame's kit carry an empty pbrMetallicRoughness — no factor, no texture — and
        // twelve material names between them. SettlementArt turns those names into colours and lives
        // in a game the studio must never reference; keying on the name shows the exact surface that
        // table keys on while knowing nothing about it.
        var tints = new StudioTints();
        var asAuthored = new Vector3(1f, 1f, 1f);

        t.Expect("an empty table draws what the file said",
            tints.Resolve("Grass", asAuthored) == asAuthored && tints.Count == 0,
            $"{tints.Resolve("Grass", asAuthored)}");

        tints.Set("Grass", new Vector3(0.110f, 0.155f, 0.060f));
        t.Expect("an override replaces the asset's colour for that material only",
            tints.Resolve("Grass", asAuthored) == new Vector3(0.110f, 0.155f, 0.060f)
            && tints.Resolve("Bark_NormalTree", asAuthored) == asAuthored,
            $"grass {tints.Resolve("Grass", asAuthored)}, bark {tints.Resolve("Bark_NormalTree", asAuthored)}");

        // <b>An unnamed material must not become a key.</b> A single entry under "" would tint every
        // nameless material in the file together, which reads as a bug in the panel rather than as a
        // property of the asset.
        tints.Set(string.Empty, new Vector3(1f, 0f, 0f));
        t.Expect("a nameless material is not storable",
            tints.Count == 1 && tints.Resolve(string.Empty, asAuthored) == asAuthored,
            $"{tints.Count} entries");

        tints.Clear("Grass");
        t.Expect("clearing returns that material to the file's colour",
            tints.Resolve("Grass", asAuthored) == asAuthored && tints.Count == 0,
            $"{tints.Count} entries");

        tints.Set("A", Vector3.One);
        tints.Set("B", Vector3.Zero);
        tints.ClearAll();
        t.Expect("reset all empties the table",
            tints.Count == 0 && !tints.Has("A") && !tints.Has("B"),
            $"{tints.Count} entries");

        t.PrintSummary();
        return t.Failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// The palette's size, from the shader rather than from a C# constant.
    /// </summary>
    /// <remarks>
    /// <c>StudioRig.MaxBones</c> and the <c>mat4 m[128]</c> in studio_skinned.vert are the same
    /// number written in two files, which is exactly the drift this exists for. The reflected block
    /// is the authority.
    /// </remarks>
    private static void CheckPalette(TestRunner t, string shaderDirectory)
    {
        ShaderInterface skinned;
        ShaderInterface caster;
        try
        {
            skinned = ShaderReflection.MergeStages(
                ShaderReflection.Load(Path.Combine(shaderDirectory, "studio_skinned.vert.spv.refl.json")));
            caster = ShaderReflection.MergeStages(
                ShaderReflection.Load(Path.Combine(shaderDirectory, "studio_skinned_shadow.vert.spv.refl.json")));
        }
        catch (Exception exception)
        {
            t.Fail("the skinned stages reflect their palette", exception.Message);
            return;
        }

        var boneSlot = skinned.Slots.FirstOrDefault(s => s.Type == ShaderResourceType.StorageBuffer);
        if (boneSlot?.BlockLayout is not { } block)
        {
            t.Fail("studio.skinned declares a bone palette", "no storage buffer — where did the palette go?");
            return;
        }

        var declared = block.TotalSize / 64;
        var expected = StudioRig.MaxBones * StudioRig.MaxInstances;
        t.Expect("the palette holds as many matrices as C# thinks",
            declared == expected,
            $"the shader holds {declared}, StudioRig.MaxBones x MaxInstances says {expected} — the " +
            "shader indexes gl_InstanceIndex x stride into this array, and a short one reads past its end");

        // <b>Both skinned stages, not just the lit one.</b> They share ONE palette material, so a
        // caster whose array is a different size is a shadow reading a different body's pose — and
        // the lit pass would look perfect while it happened.
        var casterSlot = caster.Slots.FirstOrDefault(s => s.Type == ShaderResourceType.StorageBuffer);
        if (casterSlot?.BlockLayout is not { } casterBlock)
        {
            t.Fail("the skinned caster declares a palette", "it cannot cast a posed shadow without one");
            return;
        }

        t.Expect("the caster reads the same palette, same set, same size",
            casterBlock.TotalSize == block.TotalSize && casterSlot.Set == boneSlot.Set,
            $"caster is set {casterSlot.Set} x {casterBlock.TotalSize}B against the lit pass's " +
            $"set {boneSlot.Set} x {block.TotalSize}B — they share one material and must agree");
    }

    /// <summary>Studio, rather than the engine's asset checker, owns its fixed GPU palette budget.</summary>
    private static void CheckRigCapacity(TestRunner t)
    {
        t.Expect("Studio's palette capacity is its per-rig bone and instance budget",
            StudioRig.PaletteMatrixCapacity == StudioRig.MaxBones * StudioRig.MaxInstances,
            $"{StudioRig.PaletteMatrixCapacity} matrices vs " +
            $"{StudioRig.MaxBones} bones x {StudioRig.MaxInstances} instances");

        StudioRig.ValidatePaletteCapacity(
            "within.glb",
            new[] { SkinWith(StudioRig.MaxBones) });
        t.Pass("a rig exactly at Studio's bone limit is accepted");

        t.ExpectThrows<AssetImportException>(
            "Studio refuses an oversized rig before GPU allocation",
            () => StudioRig.ValidatePaletteCapacity(
                "oversized.glb",
                new[] { SkinWith(StudioRig.MaxBones + 1) }),
            $"at most {StudioRig.MaxBones} bones");

        t.ExpectThrows<AssetImportException>(
            "Studio checks every skin rather than only the primary one",
            () => StudioRig.ValidatePaletteCapacity(
                "multi-skin.glb",
                new[] { SkinWith(1), SkinWith(StudioRig.MaxBones + 1) }),
            "skin 1");
    }

    private static GltfSkinBinding SkinWith(int boneCount)
    {
        var bones = new Bone[boneCount];
        for (var i = 0; i < bones.Length; i++)
        {
            bones[i] = new Bone($"bone{i}", i - 1, Matrix4x4.Identity);
        }

        return new GltfSkinBinding(new Skeleton(bones), Matrix4x4.Identity);
    }

    /// <summary>
    /// The binding model in full, behind --verbose.
    /// </summary>
    /// <remarks>
    /// It was printed unconditionally when this was a verb, which is right for something you type
    /// at a thing to learn about it and wrong for a gate leg: two hundred lines of descriptor sets
    /// on every green run is noise that teaches a reader to skim. It stays because when a claim
    /// above fails, "set 0 binding 0 is a 64-byte uniform block" is the first thing worth seeing.
    /// </remarks>
    private static void Describe(string name, ShaderInterface iface)
    {
        foreach (var slot in iface.Slots.OrderBy(s => s.Set).ThenBy(s => s.Binding))
        {
            Console.WriteLine($"       set {slot.Set} binding {slot.Binding}  {slot.Type} {slot.Stages}");
            if (slot.BlockLayout is { } layout)
            {
                Console.WriteLine($"         block {layout.TotalSize} bytes");
                foreach (var member in layout.Members)
                {
                    Console.WriteLine($"           +{member.Offset,-6} {member.Size,3}B  {member.Name}");
                }
            }
        }

        foreach (var range in iface.PushConstants)
        {
            Console.WriteLine($"       push  +{range.Offset} {range.Size}B  {range.Stages}");
        }
    }
}
