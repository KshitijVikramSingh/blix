using Blix.Graphics.Vulkan;
using System.Numerics;
using Blix.Assets;
using Blix.Labs.Toolchain;

namespace Blix.Labs.Toolchain.Probe;

// The lab's binding model, printed — and checked.
//
// ── Which tool answers which question ───────────────────────────────────────
//   blix-cook inspect <asset>   LISTS what is in a file — node hierarchy, composed
//                               pivots, assembled bounds. Always exits 0. It reports.
//   this probe --model <asset>  CHECKS that an asset is sound — clip lengths, skeleton,
//                               and the binding contracts below — and exits non-zero
//                               when it is not. It judges.
//
// Deliberately not merged. They overlap in subject and not in purpose, and the honest
// fix for "two tools answer the same question" is to make the questions different rather
// than to fuse the tools: a check that returns an exit code belongs next to the thing it
// gates, and a listing belongs next to the cookers that produce the files.
//
// ── Proves ──────────────────────────────────────────────────────────────────
//   • A second executable over one lab. This project declares no shaders, owns no
//     render code and never opens a window; the .spv sidecars and the lab's types
//     both arrive from Blix.Labs.Toolchain. That was the whole claim of splitting
//     the lab out, and until now nothing tested it.
//   • Reflection is worth something OFF the GPU: the binding model is a build
//     artifact, so it can be read, diffed and asserted against without a device.
//
// ── Why it checks rather than only prints ───────────────────────────────────
//   The lab's first run died on "payload length 96 does not match the shader's
//   declared total push-constant size 64" — a C# constant disagreeing with the
//   SPIR-V it describes. That is the exact drift the reflected path exists to
//   prevent, and the device caught it at draw time, which is late. Here it is a
//   non-zero exit code before anything is submitted.
public static class Program
{
    public static int Main(string[] args)
    {
        // --model <path> reports what an import produced, with no device anywhere in sight. A glTF
        // is a build artifact too, and everything below is readable without a GPU.
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--model") return InspectModel(args[i + 1]);
            if (args[i] == "--rig") return InspectRig(args[i + 1], args.Contains("--verbose"));
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: probe [--model <gltf-or-glb>] [--rig <rigged.glb> [--verbose]]");
            Console.WriteLine("  no args     check the lab's reflected binding model against the renderer");
            Console.WriteLine("  --model     check an asset: clip lengths, skeleton, mesh-node transform");
            Console.WriteLine("  --rig       check a RIG: hierarchy, rest palette, track coverage,");
            Console.WriteLine("              finiteness across every clip, root-motion loop continuity");
            Console.WriteLine("  Exits non-zero when something is wrong. For a plain listing of an");
            Console.WriteLine("  asset's hierarchy and pivots, use: blix-cook inspect <path>");
            return 0;
        }

        var shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!Directory.Exists(shaderDirectory))
        {
            Console.Error.WriteLine($"No Shaders directory at {shaderDirectory}.");
            return 2;
        }

        var programs = new (string Name, string[] Stages, int? ExpectedPush)[]
        {
            ("lab.shadow", new[] { "lab_shadow.vert", "lab_shadow.frag" }, LabRenderer.CasterPushBytes),
            ("lab.lit", new[] { "lab_lit.vert", "lab_lit.frag" }, LabRenderer.LitPushBytes),
            ("lab.present", new[] { "lab_present.vert", "lab_present.frag" }, null),

            // The skinned pair pushes the SAME blocks as the unskinned one — skinning adds a
            // descriptor set, not push bytes — so both are checked against the same two constants.
            // That is the claim worth checking: if a skinned shader ever grew a push member, the
            // renderer would keep packing 96 bytes into a 112-byte block and the tail would be
            // whatever was left in the scratch buffer.
            ("lab.skinned", new[] { "lab_skinned.vert", "lab_lit.frag" }, LabRenderer.LitPushBytes),
            ("lab.skinned.shadow",
                new[] { "lab_skinned_shadow.vert", "lab_shadow.frag" }, LabRenderer.CasterPushBytes),
        };

        var failures = 0;
        Console.WriteLine($"lab binding model — reflected from {shaderDirectory}");
        Console.WriteLine();

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
                Console.Error.WriteLine($"{name}: could not reflect — {exception.Message}");
                failures++;
                continue;
            }

            Console.WriteLine($"{name}  [{string.Join(", ", stages)}]");

            // Ordered, so two runs of this are diffable and a shader change shows up as a
            // line rather than as a reshuffle.
            foreach (var slot in iface.Slots.OrderBy(s => s.Set).ThenBy(s => s.Binding))
            {
                Console.WriteLine(
                    $"  set {slot.Set} binding {slot.Binding}  {slot.Type,-13} {slot.Stages}");

                if (slot.BlockLayout is not { } block) continue;
                Console.WriteLine($"    block {block.TotalSize} bytes");
                foreach (var member in block.Members.OrderBy(m => m.Offset))
                {
                    Console.WriteLine($"      +{member.Offset,-4} {member.Size,4}B  {member.Name}");
                }
            }

            var pushTotal = 0;
            foreach (var range in iface.PushConstants.OrderBy(r => r.Offset))
            {
                Console.WriteLine($"  push  +{range.Offset} {range.Size}B  {range.Stages}");
                pushTotal = Math.Max(pushTotal, range.Offset + range.Size);
            }

            if (expectedPush is { } expected)
            {
                if (pushTotal == expected)
                {
                    Console.WriteLine($"  push total {pushTotal}B matches the renderer's constant");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"  MISMATCH: shader declares {pushTotal}B of push constants, " +
                        $"the renderer pushes {expected}B. One of them is wrong and the device " +
                        $"will refuse the draw.");
                    failures++;
                }
            }

            Console.WriteLine();
        }

        // <b>The palette's size, from the shader rather than from a C# constant.</b> LabRig.MaxBones
        // and the `mat4 m[128]` in lab_skinned.vert are the same number written in two files, which
        // is exactly the drift this probe exists for. The reflected block is the authority.
        try
        {
            var skinned = ShaderReflection.MergeStages(
                ShaderReflection.Load(Path.Combine(shaderDirectory, "lab_skinned.vert.spv.refl.json")));
            var boneSlot = skinned.Slots.FirstOrDefault(s => s.Type == ShaderResourceType.StorageBuffer);
            if (boneSlot?.BlockLayout is { } block)
            {
                var declared = block.TotalSize / 64;
                Console.WriteLine(
                    $"bone palette: set {boneSlot.Set} binding {boneSlot.Binding}, " +
                    $"{block.TotalSize}B = {declared} matrices");
                if (declared != LabRig.MaxBones)
                {
                    Console.Error.WriteLine(
                        $"  MISMATCH: the shader holds {declared} bones, LabRig.MaxBones says " +
                        $"{LabRig.MaxBones}. A rig between the two would index past the array.");
                    failures++;
                }
            }
            else
            {
                Console.Error.WriteLine("lab.skinned declares no storage buffer — where did the palette go?");
                failures++;
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"lab.skinned: could not reflect the palette — {exception.Message}");
            failures++;
        }

        Console.WriteLine();

        // Links the lab's own code, not just its build output — the scene is described
        // without a device anywhere in sight.
        var scene = LabScene.Default();
        Console.WriteLine(
            $"scene: {scene.Objects.Count} object(s), " +
            $"{scene.Objects.Count(o => o.IsGround)} ground, " +
            $"sun {scene.SunDirection.X:0.00}, {scene.SunDirection.Y:0.00}, {scene.SunDirection.Z:0.00}, " +
            $"shadow map {LabRenderer.ShadowMapSize}px");

        if (failures == 0) return 0;
        Console.Error.WriteLine($"{failures} problem(s).");
        return 1;
    }

    // <b>What blix-cook inspect does not say.</b> That tool prints the node tree, the composed
    // pivots and the assembled bounds, which is most of what an asset raises — but not its clips,
    // not its skeleton, and not whether the numbers are finite. A skeleton whose rest pose does not
    // build, or a clip with no usable length, surfaces later as a character folding inside out.
    private static int InspectModel(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No model at {path}.");
            return 2;
        }

        var problems = 0;
        var imported = new GltfImporter().Import(new AssetImportContext(AssetId.Parse("probe"), path));

        Console.WriteLine($"{Path.GetFileName(path)}");
        Console.WriteLine(
            $"  {imported.Primitives.Length} primitive(s), " +
            $"{imported.Skeleton.BoneCount} bone(s), {imported.Animations.Length} clip(s)");
        Console.WriteLine(
            imported.MeshNodeTransform.IsIdentity
                ? "  mesh-node transform: identity"
                : "  mesh-node transform: NOT identity — the asset orients itself at a parent node, " +
                  "so uModel must compose with it");

        foreach (var clip in imported.Animations.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var translation = clip.Tracks.Count(t => t.Translation is not null);
            var rotation = clip.Tracks.Count(t => t.Rotation is not null);
            var scale = clip.Tracks.Count(t => t.Scale is not null);

            // <b>A zero-length clip is a POSE, not a fault.</b> This used to count them as problems and
            // reported seven on the Rogue — T-Pose, Lie_Pose and four Sit/Unarmed poses, every one of
            // them a deliberate single-keyframe shape an animator authored to be held. A tool that
            // cries fault on a sound file is worse than one that says nothing, because it teaches a
            // reader to stop looking at the output. Only a NON-finite duration is a real fault: that
            // is a length nothing can sample against.
            var kind = clip.Duration > 0.0 ? $"{clip.Duration,6:0.00}s" : "  pose";
            Console.WriteLine(
                $"    {clip.Name,-28} {kind}  {clip.Tracks.Length,3} track(s)  " +
                $"T{translation} R{rotation} S{scale}");

            if (double.IsFinite(clip.Duration)) continue;
            Console.Error.WriteLine($"      clip duration is {clip.Duration} — not a usable length.");
            problems++;
        }

        if (problems == 0) return 0;
        Console.Error.WriteLine($"{problems} problem(s).");
        return 1;
    }

    // <b>What a rig can be wrong about, checked without a device.</b>
    //
    // Every failure below shows up on screen as the same thing — a character folded inside out —
    // and on screen they are indistinguishable. Here they are five separate lines with five
    // different causes, and the run exits non-zero before anything is submitted.
    //
    // Not a listing. `blix-cook inspect` lists and the --model path above reports; this judges, and
    // the difference is the exit code.
    private static int InspectRig(string path, bool verbose)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No rig at {path}.");
            return 2;
        }

        var problems = 0;
        var imported = new GltfImporter().Import(new AssetImportContext(AssetId.Parse("probe.rig"), path));
        var skeleton = imported.Skeleton;

        Console.WriteLine($"{Path.GetFileName(path)}");
        Console.WriteLine(
            $"  {skeleton.BoneCount} bone(s), {imported.Animations.Length} clip(s), " +
            $"{imported.Primitives.Length} primitive(s)");

        // ── 1. Hierarchy ────────────────────────────────────────────────────
        // Skeleton's constructor already rejects a parent index that is not strictly less than the
        // child's, so a cycle is impossible by construction and this cannot fail for an imported
        // rig. It is checked anyway because the invariant is what makes every later single forward
        // pass correct, and a check that never fires is the cheapest possible documentation of one.
        var roots = 0;
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var parent = skeleton.Bones[i].ParentIndex;
            if (parent < 0) roots++;
            else if (parent >= i)
            {
                Console.Error.WriteLine($"  bone {i} '{skeleton.Bones[i].Name}' parents to {parent} — out of order.");
                problems++;
            }
        }

        Console.WriteLine($"  hierarchy: {roots} root(s), order valid");
        if (skeleton.BoneCount > LabRig.MaxBones)
        {
            Console.Error.WriteLine(
                $"  {skeleton.BoneCount} bones exceeds the lab shader's {LabRig.MaxBones}-matrix palette.");
            problems++;
        }

        // ── 2. The rest pose must build the identity ────────────────────────
        // BindWorld × InverseBindPose = I by construction, so every rest palette matrix is the
        // identity — and a rig whose inverse-bind matrices do not invert its bind pose fails here
        // rather than as a mesh that explodes the moment it is skinned. This is the single most
        // valuable check in the file: it is exact, it needs no clip, and it catches a bad export.
        var rest = skeleton.CreateRestPose();
        var palette = new BonePalette(skeleton.BoneCount);
        skeleton.ComputeBonePalette(rest, palette);
        var worstRest = 0f;
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var m = palette.Matrices[i];
            if (!IsFinite(m))
            {
                Console.Error.WriteLine($"  rest palette for bone {i} '{skeleton.Bones[i].Name}' is not finite.");
                problems++;
                continue;
            }

            worstRest = MathF.Max(worstRest, DistanceFromIdentity(m));
        }

        // A millimetre of float drift over a long bone chain is normal; a centimetre is a rig whose
        // bind matrices were baked at a different scale from its joints.
        var restOk = worstRest < 0.01f;
        Console.WriteLine(
            $"  rest palette: worst deviation from identity {worstRest:0.00000}" + (restOk ? "" : "  ← TOO LARGE"));
        if (!restOk) problems++;

        // ── 3. Where the bones actually are ─────────────────────────────────
        // The number that was missing when the lab's first skeleton overlay drew a knot at the
        // origin: bone WORLD positions are metres apart, palette translations are near zero. Two
        // correct arithmetics, one of which answers a different question.
        var worlds = new System.Numerics.Matrix4x4[skeleton.BoneCount];
        LabRig.ComputeBoneWorlds(skeleton, rest, worlds);

        // <b>How many of those bones the mesh actually follows.</b> A rig ships the handles its
        // animator posed through, and they are indistinguishable from deform bones in the skeleton,
        // in the palette and in every clip — the weights are the only place the difference is
        // recorded. Worth a line because it is the number that explains why a 41-bone skeleton drawn
        // honestly looks like a starburst.
        var deform = LabRig.FindDeformBones(skeleton, imported.Primitives);
        var deformCount = deform.Count(b => b);
        Console.WriteLine(
            $"  deform bones: {deformCount}/{skeleton.BoneCount}" +
            (deformCount == skeleton.BoneCount
                ? " (every bone skins something)"
                : $" — {skeleton.BoneCount - deformCount} control/IK bone(s) the mesh never follows"));
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var at = new Vector3(worlds[i].M41, worlds[i].M42, worlds[i].M43);
            min = Vector3.Min(min, at);
            max = Vector3.Max(max, at);
        }

        Console.WriteLine(
            $"  rest joints: {min.X:0.000},{min.Y:0.000},{min.Z:0.000} .. {max.X:0.000},{max.Y:0.000},{max.Z:0.000}");
        if (verbose)
        {
            for (var i = 0; i < skeleton.BoneCount; i++)
            {
                var at = new Vector3(worlds[i].M41, worlds[i].M42, worlds[i].M43);
                Console.WriteLine(
                    $"    {i,3} {skeleton.Bones[i].Name,-24} parent {skeleton.Bones[i].ParentIndex,3}  " +
                    $"{at.X,8:0.000} {at.Y,8:0.000} {at.Z,8:0.000}  {(deform[i] ? "deform" : "control")}");
            }
        }

        // ── 4 & 5. Every clip, sampled ──────────────────────────────────────
        var poses = 0;
        var travelling = 0;
        var pose = skeleton.CreateRestPose();
        var rootBone = RootMotion.DefaultRootBone(skeleton);
        Console.WriteLine($"  root bone: {rootBone} '{skeleton.Bones[rootBone].Name}'");

        foreach (var clip in imported.Animations.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            // A zero-length clip is a POSE, not a fault. The Rogue ships seven — T-Pose, Lie_Pose,
            // four Sit/Unarmed poses — and calling them broken was this probe's own bug: it reported
            // seven problems on a file with none, which is worse than reporting nothing at all
            // because it trains a reader to ignore the output.
            if (clip.Duration <= 0.0)
            {
                poses++;
                if (!SampleIsFinite(clip, pose, rest, 0.0, out var poseBone))
                {
                    Console.Error.WriteLine($"    {clip.Name}: bone {poseBone} is not finite at t=0.");
                    problems++;
                }

                continue;
            }

            if (!double.IsFinite(clip.Duration))
            {
                Console.Error.WriteLine($"    {clip.Name}: duration is {clip.Duration}.");
                problems++;
                continue;
            }

            // <b>Sampled across the whole clip, not only at its ends.</b> A NaN at t=0.7 is a
            // character folding inside out three-quarters of the way through a swing, and both
            // endpoints are perfectly finite. 33 points is dense enough to land inside every
            // keyframe span of a clip this length and cheap enough to run over all 76.
            const int Samples = 33;
            var bad = -1;
            var badAt = 0.0;
            for (var s = 0; s < Samples && bad < 0; s++)
            {
                var t = clip.Duration * s / (Samples - 1.0);
                if (SampleIsFinite(clip, pose, rest, t, out var bone)) continue;
                bad = bone;
                badAt = t;
            }

            if (bad >= 0)
            {
                Console.Error.WriteLine(
                    $"    {clip.Name}: bone {bad} '{skeleton.Bones[bad].Name}' is not finite at t={badAt:0.000}.");
                problems++;
                continue;
            }

            // Track coverage: which bones this clip leaves at rest. A clip that touches a handful
            // is a partial one, and a partial one is exactly the case the rest reset exists for —
            // so this line is the evidence for why ClipPlayer resets rather than an assertion about
            // a rule someone remembered.
            var touched = new HashSet<int>();
            foreach (var track in clip.Tracks) touched.Add(track.BoneIndex);

            var cycle = RootMotion.PerCycle(clip, rootBone, rest.Locals[rootBone]);

            // ── Root-motion loop continuity ─────────────────────────────────
            // The check the whole of Stage C hangs on. Travel across the seam — the last sliver of
            // one cycle plus the first sliver of the next — must be about the same size as travel
            // across an equal span in the clip's interior. The naive implementation reports a whole
            // cycle backwards here, which is a number three orders of magnitude out, so the
            // tolerance does not need to be clever.
            var sliver = clip.Duration * 0.02;
            var seam = RootMotion.AcrossLoop(
                clip, rootBone, rest.Locals[rootBone], clip.Duration - sliver, sliver, clip.Duration);
            var interior = RootMotion.Between(
                clip, rootBone, rest.Locals[rootBone], clip.Duration * 0.4, (clip.Duration * 0.4) + (sliver * 2));

            var travels = cycle.Distance > 0.001f;
            if (travels) travelling++;

            var continuous = seam.Distance <= MathF.Max(0.01f, interior.Distance * 8f);
            if (!continuous)
            {
                Console.Error.WriteLine(
                    $"    {clip.Name}: travel across the loop seam is {seam.Distance:0.0000} m against " +
                    $"{interior.Distance:0.0000} m in the interior — the wrap is being subtracted, not walked.");
                problems++;
            }

            // ── The wrap, integrated rather than reasoned about ─────────────
            // The check above compares travel across the seam to travel in the interior, which
            // catches the subtraction bug at one boundary. This one runs the actual player at a
            // fixed step over three whole cycles and adds up what it reported: if ClipPlayer's
            // piecewise walk drops a sliver at a wrap, misses a cycle, or double-counts one, the
            // total comes out short or long and no amount of per-seam reasoning would have said so.
            //
            // A step deliberately NOT a divisor of the duration, so wraps land mid-step — which is
            // the only case the piecewise loop exists for. A step that divides evenly would land on
            // the seam exactly and pass whatever the code did.
            if (travels)
            {
                const int Cycles = 3;
                var player = new ClipPlayer(skeleton, clip) { RootBone = rootBone };
                var step = clip.Duration / 7.37;
                var summed = Vector3.Zero;
                var elapsed = 0.0;
                while (elapsed < clip.Duration * Cycles)
                {
                    var dt = Math.Min(step, (clip.Duration * Cycles) - elapsed);
                    player.Advance(dt);
                    summed += player.RootDelta.Translation;
                    elapsed += dt;
                }

                var expected = cycle.Translation * Cycles;
                var error = (summed - expected).Length();
                if (error > MathF.Max(0.002f, expected.Length() * 0.02f))
                {
                    Console.Error.WriteLine(
                        $"    {clip.Name}: {Cycles} cycles integrate to {summed.Length():0.0000} m " +
                        $"but one cycle travels {cycle.Distance:0.0000} m — off by {error:0.0000} m.");
                    problems++;
                }
            }

            if (!verbose && touched.Count == skeleton.BoneCount && !travels) continue;
            Console.WriteLine(
                $"    {clip.Name,-30} {clip.Duration,5:0.00}s  " +
                $"{touched.Count,3}/{skeleton.BoneCount} bones" +
                (travels ? $"  travels {cycle.Distance:0.000} m/cycle" : string.Empty));
        }

        Console.WriteLine(
            $"  {imported.Animations.Length - poses} timed clip(s), {poses} pose(s), " +
            $"{travelling} with root travel");

        if (problems == 0) return 0;
        Console.Error.WriteLine($"{problems} problem(s).");
        return 1;
    }

    private static bool SampleIsFinite(AnimationClip clip, Pose pose, Pose rest, double time, out int bone)
    {
        pose.CopyFrom(rest);
        clip.Sample(time, pose);
        for (var i = 0; i < pose.BoneCount; i++)
        {
            var local = pose.Locals[i];
            if (IsFinite(local.Translation) && IsFinite(local.Scale) &&
                float.IsFinite(local.Rotation.X) && float.IsFinite(local.Rotation.Y) &&
                float.IsFinite(local.Rotation.Z) && float.IsFinite(local.Rotation.W))
            {
                continue;
            }

            bone = i;
            return false;
        }

        bone = -1;
        return true;
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static bool IsFinite(System.Numerics.Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M22) && float.IsFinite(m.M33) && float.IsFinite(m.M44) &&
        float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43);

    // Largest absolute difference between a matrix and the identity, over all sixteen entries.
    private static float DistanceFromIdentity(System.Numerics.Matrix4x4 m)
    {
        var identity = System.Numerics.Matrix4x4.Identity;
        var worst = 0f;
        for (var row = 1; row <= 4; row++)
        {
            for (var col = 1; col <= 4; col++)
            {
                worst = MathF.Max(worst, MathF.Abs(At(m, row, col) - At(identity, row, col)));
            }
        }

        return worst;
    }

    private static float At(System.Numerics.Matrix4x4 m, int row, int col) => (row, col) switch
    {
        (1, 1) => m.M11, (1, 2) => m.M12, (1, 3) => m.M13, (1, 4) => m.M14,
        (2, 1) => m.M21, (2, 2) => m.M22, (2, 3) => m.M23, (2, 4) => m.M24,
        (3, 1) => m.M31, (3, 2) => m.M32, (3, 3) => m.M33, (3, 4) => m.M34,
        _ => col switch { 1 => m.M41, 2 => m.M42, 3 => m.M43, _ => m.M44 },
    };
}
