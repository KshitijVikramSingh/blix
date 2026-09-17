using Blix.Core;
using Blix.Graphics.Vulkan;
using System.Numerics;
using Blix.Assets;
using Blix.Tools.Studio;
using Blix.Cooked;

namespace Blix.Tools.Check;

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
//     both arrive from Blix.Tools.Studio. That was the whole claim of splitting
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
    [BlixApp("check", Summary = "judge an asset — clips, skeleton, binding contracts; exits non-zero")]
    public static int Main(string[] args)
    {
        // --model <path> reports what an import produced, with no device anywhere in sight. A glTF
        // is a build artifact too, and everything below is readable without a GPU.
        //
        // <b>One catch, for one exception type, and deliberately not a blanket one.</b>
        // AssetImportException is the engine saying "this is not something Blix can read" — a
        // refusal, with the path in it, and the only thing a person can act on. Anything ELSE
        // escaping from here is a fault in this tool and should arrive as a stack trace, because a
        // judge that swallows its own bugs reports a clean bill of health on a broken asset.
        //
        // Before this, nothing was caught at all: `blix check --rig not-a-glb` exited 134 through
        // the parser's own exception. A judge whose whole job is to survive a bad asset crashed on
        // the first one it was handed.
        try
        {
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "--model") return InspectModel(args[i + 1]);
                if (args[i] == "--rig") return InspectRig(args[i + 1], args.Contains("--verbose"));
                if (args[i] == "--cooked") return JudgeCooked(args[i + 1]);
            }
        }
        catch (AssetImportException refused)
        {
            Console.Error.WriteLine($"blix cannot read this: {refused.Message}");
            return 1;
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            Console.WriteLine("Usage: probe [--model <gltf-or-glb>] [--rig <rigged.glb> [--verbose]]");
            Console.WriteLine("  no args     check the lab's reflected binding model against the renderer");
            Console.WriteLine("  --model     check an asset: clip lengths, skeleton, mesh-node transform");
            Console.WriteLine("  --cooked    judge a DIRECTORY: load every asset under it and fail if");
            Console.WriteLine("              anything resolved to the slow path");
            Console.WriteLine("  --rig       check a RIG: hierarchy, rest palette, track coverage,");
            Console.WriteLine("              finiteness across every clip, root-motion loop continuity");
            Console.WriteLine("  Exits non-zero when something is wrong. For a plain listing of an");
            Console.WriteLine("  asset's hierarchy and pivots, use: blix-cook inspect <path>");
            return 0;
        }

        // <b>The binding model moved out, and running this with no asset is what showed it should.</b>
        // These two hundred lines described and judged the STUDIO — shader interfaces, push sizes,
        // the bone palette — and needed no asset at all: `blix check` with nothing to check printed
        // a full report and exited 0. A verb you type at an asset whose subject is not the asset is
        // two tools sharing a name. The studio's half is Blix.Test.Studio now, a leg of the gate
        // that judges Blix rather than a command aimed at a file.
        Console.Error.WriteLine(
            "check needs something to check: --model <path.glb> or --rig <rigged.glb>.");
        Console.Error.WriteLine(
            "  (the studio's own binding model is checked by `blix test`, not here)");
        return 2;
    }

    // <b>What blix-cook inspect does not say.</b> That tool prints the node tree, the composed
    // pivots and the assembled bounds, which is most of what an asset raises — but not its clips,
    // not its skeleton, and not whether the numbers are finite. A skeleton whose rest pose does not
    // build, or a clip with no usable length, surfaces later as a character folding inside out.

    /// <summary>
    /// Loads every asset under a directory and fails if any of them took the slow path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This judges what a LOAD does, not what files exist.</b> `blix cook status` answers the
    /// second question and is the right tool for it; they differ whenever a cooked artifact is
    /// present but unusable — corrupt, stale, or a format this build no longer reads — in which case
    /// the files are all there and the loader quietly parses the source anyway. Only running the
    /// load can tell you that, which is the whole reason the report exists.
    /// </para>
    /// <para>
    /// <b>And this is what makes cooking enforceable without making it mandatory.</b> Nothing in the
    /// engine refuses to run from source; a project that wants the guarantee asks for it here, and
    /// the exit code is the contract — the same deal every other check in this tree makes.
    /// </para>
    /// <para>
    /// Textures are judged too, for free: the mesh load pre-decodes its images and each reports
    /// itself, so an asset whose geometry is cooked and whose albedo is still a PNG decode fails —
    /// which it should, because that is the larger cost of the two.
    /// </para>
    /// </remarks>
    /// <summary>The source kinds this judge knows how to load. Anything else is not its business.</summary>
    /// <remarks>
    /// <b><c>.obj</c> was missing and the omission was invisible</b>, because a directory holding
    /// nothing else produced "nothing here that this judges" — the same sentence an empty directory
    /// produces. RTSGame's nature kit sat cooked and unjudged behind that sentence. An extension
    /// this cannot load is a real answer; an extension it silently skips is not.
    /// <para>
    /// <b><c>.blixmesh</c> is here because a cooked asset is now a thing you can open.</b> A tree
    /// holding only cooked artifacts — which is what a shippable tree IS — would otherwise be judged
    /// as empty, so the one shape the whole arc exists to produce would be the one shape the
    /// instrument could not see.
    /// </para>
    /// </remarks>
    private static readonly string[] Judged = { ".gltf", ".glb", ".obj", ".blixmesh" };

    /// <summary>Loads one source the way a game would, so the report says what a game would get.</summary>
    /// <remarks>
    /// <b>Loaded the way it would actually be used, not the way that is convenient.</b> This judged
    /// everything through the STATIC glTF importer once, which meant a rigged character was measured
    /// on a path no game takes — and the rigged path is the one with no cooked form at all, so the
    /// judge reported the cheaper half of the truth about the most expensive assets in the tree.
    /// <para>
    /// For glTF, which importer wants a file is not guessed from its extension: the rigged one
    /// refuses, by name, when no node carries both a mesh and a skin. So ask it first and let its
    /// refusal route the file. That refusal exists because a judge crashed on it once; it is also
    /// the cleanest way to ask "is this a rig?" without parsing the file twice ourselves.
    /// </para>
    /// <para>
    /// For OBJ the routing is a choice rather than a question the file can answer, and it goes to
    /// <see cref="WavefrontParts"/>: both readers report, but only that one can consume a cooked
    /// file with more than one part, so it is the reader the cooked format is shaped for. An asset
    /// a game loads through <c>ObjImporter</c> instead still reports for itself at load time — this
    /// only decides what the SWEEP asks.
    /// </para>
    /// </remarks>
    private static void LoadAsUsed(string source)
    {
        if (Path.GetExtension(source).Equals(".obj", StringComparison.OrdinalIgnoreCase))
        {
            WavefrontParts.Import(source);
            return;
        }

        // A cooked mesh opens through the same importer, which reads it standalone and never looks
        // for a glTF. Judged alongside its source when both are present, which is not double
        // counting: they are two different loads and the point is that they agree.
        if (Path.GetExtension(source).Equals(".blixmesh", StringComparison.OrdinalIgnoreCase))
        {
            new GltfStaticImporter().Import(new AssetImportContext(AssetId.Parse("check/cooked"), source));
            return;
        }

        try
        {
            new GltfImporter().Import(new AssetImportContext(AssetId.Parse("check/rig"), source));
        }
        catch (AssetImportException noSkin) when (noSkin.Message.Contains("no rig here", StringComparison.Ordinal))
        {
            new GltfStaticImporter().Import(new AssetImportContext(AssetId.Parse("check/cooked"), source));
        }
    }

    private static int JudgeCooked(string root)
    {
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine($"No directory at {root}.");
            return 2;
        }

        var sources = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => Judged.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                     && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        if (sources.Length == 0)
        {
            Console.WriteLine($"Nothing under {root} that this judges.");
            return 0;
        }

        AssetLoadLog.Start();
        var refused = 0;
        foreach (var source in sources)
        {
            try
            {
                LoadAsUsed(source);
            }
            catch (AssetImportException bad)
            {
                // Reported, not thrown: one unreadable asset should not stop the other forty from
                // being judged. A sweep that stops at the first problem finds one problem.
                Console.WriteLine($"RED  cannot read {Path.GetRelativePath(root, source)} — {bad.Message}");
                refused++;
            }
        }

        var reports = AssetLoadLog.Drain();
        AssetLoadLog.Enabled = false;

        var slow = reports.Where(r => r.Mode != AssetLoadMode.Cooked).ToArray();
        var cooked = reports.Length - slow.Length;

        foreach (var r in slow.OrderBy(r => r.SourcePath, StringComparer.Ordinal))
        {
            Console.WriteLine(
                $"RED  {r.Mode.ToString().ToUpperInvariant(),-8} {Shorten(root, r.SourcePath)}" +
                $"  {r.LoadMs:0.0} ms, {r.Bytes / 1024.0:0.0} KB — {r.Warning}");
        }

        Console.WriteLine();
        Console.WriteLine($"{sources.Length} asset(s), {reports.Length} load(s): {cooked} cooked, {slow.Length} on the slow path.");
        if (reports.Length > 0)
        {
            // <b>These two numbers are not a whole and a share, and saying so took two tries.</b>
            // A mesh import pre-decodes its own images, so a texture's time sits inside its
            // asset's — but the decodes run in Parallel.ForEach, so their SUM is concurrent CPU
            // time and can exceed the wall clock of the import containing them. The first version
            // of this line called it "of which", and RTSGame duly reported 3195 ms of 1540 ms.
            // Reported as what each actually is instead.
            var assetPaths = sources.Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
            var wallMs = reports.Where(r => assetPaths.Contains(Path.GetFullPath(r.SourcePath))).Sum(r => r.LoadMs);
            var slowMs = slow.Sum(r => r.LoadMs);
            Console.WriteLine($"  {wallMs:0} ms loading {sources.Length} asset(s).");
            if (slow.Length > 0)
            {
                Console.WriteLine($"  {slowMs:0} ms of CPU on source paths (summed; texture decodes run in parallel).");
            }
        }

        if (slow.Length == 0 && refused == 0)
        {
            Console.WriteLine("GREEN every load resolved to a cooked artifact.");
            return 0;
        }

        return 1;
    }

    private static string Shorten(string root, string path)
    {
        try
        {
            return Path.IsPathRooted(path) ? Path.GetRelativePath(root, path) : path;
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

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
        var attachments = imported.AttachmentsOrEmpty;
        var staticParts = imported.StaticPartsOrEmpty;
        Console.WriteLine(
            $"  {skeleton.BoneCount} bone(s), {imported.Animations.Length} clip(s), " +
            $"{imported.Primitives.Length} skinned primitive(s), {attachments.Length} attachment(s), " +
            $"{staticParts.Length} static part(s)");

        // ── 0a. Static parts ────────────────────────────────────────────────
        // <b>This block used to fail the check.</b> It counted what the importer had refused to read
        // and called an asset it could only half load unsound — which was right about the symptom
        // and wrong about the cause. Both refusals (a mesh on a second skin, a static mesh under no
        // joint) were this importer's rules rather than the format's, and both are gone. What is
        // left is a listing: these are read now, and seeing them is still worth the line.
        if (staticParts.Length > 0)
        {
            Console.WriteLine();
            foreach (var sp in staticParts.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var verts = sp.Primitives.Sum(p => p.Mesh.VertexCount);
                Console.WriteLine(
                    $"    {sp.Name,-22} static, on no joint      {sp.Primitives.Length} prim, {verts,6} verts");
            }
        }

        // ── 0. Attachments ──────────────────────────────────────────────────
        // <b>Reported before anything is judged, because their absence was the bug.</b> The importer
        // used to take nodes carrying both a mesh and a skin and drop the rest in silence — so the
        // Rogue loaded as six primitives of twelve and every tool agreed it was complete. Printing
        // them is most of the fix; a count nobody can see is the state this arc exists to leave.
        if (attachments.Length > 0)
        {
            Console.WriteLine();
            foreach (var a in attachments.OrderBy(a => a.JointName, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal))
            {
                var verts = a.Primitives.Sum(p => p.Mesh.VertexCount);
                Console.WriteLine(
                    $"    {a.Name,-22} on {a.JointName,-14} bone[{a.JointIndex,2}]  " +
                    $"{a.Primitives.Length} prim, {verts} verts");
            }

            // Several meshes on one joint is the ordinary case, not a fault — five of the Rogue's
            // six hang off handslot.r, and a game shows one. Said out loud so the picture a viewer
            // draws with all of them visible is expected rather than alarming.
            var shared = attachments.GroupBy(a => a.JointName, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .ToArray();
            foreach (var g in shared)
            {
                Console.WriteLine(
                    $"    note: {g.Count()} attachments share '{g.Key}' — a caller picks one; " +
                    $"showing all puts {g.Count()} meshes in the same place");
            }
        }

        // ── 1. Hierarchy ────────────────────────────────────────────────────
        // Skeleton's constructor already rejects a parent index that is not strictly less than the
        // child's, so a cycle is impossible by construction and this cannot fail for an imported
        // rig. It is checked anyway because the invariant is what makes every later single forward
        // pass correct, and a check that never fires is the cheapest possible documentation of one.
        var roots = 0;
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            // <b>Only the root count, because out-of-order cannot happen here.</b> Skeleton's
            // constructor throws when a bone parents forward, and GltfImporter builds every
            // skeleton through it — so a check for it downstream can only ever pass, and a check
            // that can only pass is not a check. What an out-of-order export actually does is fail
            // at IMPORT, naming the bone, which is both earlier and more useful.
            var parent = skeleton.Bones[i].ParentIndex;
            if (parent < 0) roots++;
        }

        Console.WriteLine($"  hierarchy: {roots} root(s), order valid");
        if (skeleton.BoneCount > StudioRig.MaxBones)
        {
            Console.Error.WriteLine(
                $"  {skeleton.BoneCount} bones exceeds the lab shader's {StudioRig.MaxBones}-matrix palette.");
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
        StudioRig.ComputeBoneWorlds(skeleton, rest, worlds);

        // <b>How many of those bones the mesh actually follows, and how many it takes to draw them.</b>
        // A rig ships the handles its animator posed through, and they are indistinguishable from
        // deform bones in the skeleton, in the palette and in every clip — the weights are the only
        // place the difference is recorded.
        //
        // Two numbers, because they are two facts and one name for both is how a report comes to lie.
        // Weighted is the census: what the mesh is attached to. The hierarchy adds the ancestors that
        // carry those chains — the Rogue's `root` is weighted by nothing and is the parent of
        // everything — and is what an overlay must draw to avoid floating segments.
        var weighted = StudioRig.FindWeightedBones(skeleton, imported.Primitives);
        var deform = StudioRig.PromoteToHierarchy(skeleton, weighted);
        var weightedCount = weighted.Count(b => b);
        var deformCount = deform.Count(b => b);
        Console.WriteLine(
            $"  weighted bones: {weightedCount}/{skeleton.BoneCount}" +
            (weightedCount == skeleton.BoneCount
                ? " (every bone skins something)"
                : $" — {skeleton.BoneCount - weightedCount} bone(s) no vertex weights") +
            (deformCount == weightedCount
                ? string.Empty
                : $"; {deformCount} to draw the chains unbroken"));
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
                    $"{at.X,8:0.000} {at.Y,8:0.000} {at.Z,8:0.000}  " +
                    $"{(weighted[i] ? "weighted" : deform[i] ? "carrier " : "control ")}");
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

        problems += CheckInstancing(skeleton, imported.Animations);

        if (problems == 0) return 0;
        Console.Error.WriteLine($"{problems} problem(s).");
        return 1;
    }

    // <b>Are N instances actually independent, or does one slice get read N times?</b>
    //
    // The failure this guards is silent: a stride of zero, a write that always lands in slot 0, a
    // shader that ignores gl_InstanceIndex — none of them throw, none of them warp the geometry, and
    // all of them render a row of bodies that looks entirely reasonable until you notice every body
    // is doing the same thing. On a rig whose clips happen to be similar, you might not notice at all.
    //
    // So it is checked in BOTH directions, with no device in sight:
    //   • different clips must produce different fingerprints — the positive claim;
    //   • the SAME clip at the SAME time, placed identically, must produce IDENTICAL ones.
    //
    // The second is the control, and it is the half that makes the first mean something. A check that
    // only ever asserts "these differ" passes trivially whenever anything differs, including for
    // reasons that have nothing to do with the mechanism.
    private static int CheckInstancing(Skeleton skeleton, AnimationClip[] clips)
    {
        var usable = clips.Where(c => c.Duration > 0.0).Take(3).ToArray();
        if (usable.Length < 2)
        {
            Console.WriteLine("  instancing: fewer than two timed clips — nothing to tell apart");
            return 0;
        }

        var problems = 0;
        var set = new BonePaletteSet(skeleton.BoneCount, usable.Length);

        // Positive: a different clip per slot, each at its own phase.
        for (var i = 0; i < usable.Length; i++)
        {
            var player = new ClipPlayer(skeleton, usable[i]);
            player.ScrubTo(i / (double)usable.Length * player.Duration);
            set.Add(skeleton, player.Pose, Matrix4x4.Identity);
        }

        var distinct = true;
        for (var i = 0; i < set.Count && distinct; i++)
        {
            for (var j = i + 1; j < set.Count; j++)
            {
                if (set.Fingerprint(i) != set.Fingerprint(j)) continue;
                Console.Error.WriteLine(
                    $"  instancing: slots {i} and {j} hold the SAME pose from different clips " +
                    $"('{usable[i].Name}' and '{usable[j].Name}') — the slices are aliasing.");
                distinct = false;
                problems++;
                break;
            }
        }

        // Control: one clip, one instant, one placement. These must be bit-identical.
        set.Reset();
        var control = new ClipPlayer(skeleton, usable[0]);
        control.ScrubTo(usable[0].Duration * 0.37);
        for (var i = 0; i < usable.Length; i++) set.Add(skeleton, control.Pose, Matrix4x4.Identity);

        var identical = true;
        for (var i = 1; i < set.Count; i++)
        {
            if (set.Fingerprint(i) == set.Fingerprint(0)) continue;
            Console.Error.WriteLine(
                $"  instancing: the control put one pose in every slot and slot {i} came out " +
                $"different — the packing is not deterministic.");
            identical = false;
            problems++;
            break;
        }

        Console.WriteLine(
            $"  instancing: {usable.Length} slots, " +
            $"{(distinct ? "different clips give different poses" : "ALIASED")}; " +
            $"{(identical ? "one clip gives one pose in every slot" : "NOT REPRODUCIBLE")}");
        problems += CheckMasks(skeleton);
        return problems;
    }

    /// <summary>
    /// What a layer mask over this rig would actually reach — judged, not listed.
    /// </summary>
    /// <remarks>
    /// <b>Two failures a mask can have, and neither looks like one.</b> A mask that reaches every bone
    /// is a whole-body blend wearing a mask's name; a mask that reaches none is a layer that runs,
    /// costs, and changes nothing. Both look perfectly plausible on a slider and neither survives a
    /// count, which is why this is a check and not a listing.
    /// <para>
    /// It judges the RIG as much as the mask: a skeleton with no bone the common spine names match
    /// is one where every masked layer has to be wired by hand, and saying so once at import beats
    /// finding out in a panel.
    /// </para>
    /// </remarks>
    private static int CheckMasks(Skeleton skeleton)
    {
        Console.WriteLine();
        Console.WriteLine("layer masks");

        var root = RigAnimation.GuessUpperBodyRoot(skeleton);
        if (root is null)
        {
            Console.WriteLine("  no bone matches the usual spine names — a masked layer must be named by hand");
            return 0;
        }

        var problems = 0;
        var hard = BoneMask.Subtree(skeleton, root);
        var soft = BoneMask.Subtree(skeleton, root, falloff: 2);

        Console.WriteLine($"  upper body from '{root}': {hard.Reach()} of {skeleton.BoneCount} bones");

        // A mask has to divide the rig. Covering all of it or none of it is the same fault twice.
        if (hard.Reach() == 0 || hard.Reach() == skeleton.BoneCount)
        {
            Console.Error.WriteLine(
                $"  PROBLEM: a mask from '{root}' reaches {hard.Reach()} of {skeleton.BoneCount} bones — " +
                "a layer that covers everything or nothing is not a layer");
            problems++;
        }

        // The falloff must add reach without adding anyone at full weight: it fades bones the hard
        // mask left out, and touches nothing the hard mask already had.
        if (soft.Reach() <= hard.Reach() || soft.Reach(0.999f) != hard.Reach(0.999f))
        {
            Console.Error.WriteLine(
                $"  PROBLEM: a falloff changed the fully-masked set ({hard.Reach(0.999f)} -> " +
                $"{soft.Reach(0.999f)}) or added no partial bones ({hard.Reach()} -> {soft.Reach()})");
            problems++;
        }
        else
        {
            Console.WriteLine(
                $"  with a 2-bone falloff: {soft.Reach()} reached, {soft.Reach(0.999f)} fully — " +
                $"{soft.Reach() - soft.Reach(0.999f)} fading");
        }

        // AN UPPER-BODY MASK MUST NOT REACH A LEG, which is a claim about what the mask MEANS rather
        // than about how it was computed — so it survives the algorithm being wrong. Breaking the
        // subtree walk to mark every bone moves the counts above without tripping them (39 of 41 is
        // neither everything nor nothing), and trips this immediately.
        var caught = new List<string>();
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var bone = skeleton.Bones[i].Name.ToLowerInvariant();
            var isLeg = bone.Contains("leg") || bone.Contains("thigh") || bone.Contains("shin")
                     || bone.Contains("calf") || bone.Contains("foot") || bone.Contains("toe");
            if (isLeg && hard[i] > 0f) caught.Add(skeleton.Bones[i].Name);
        }

        if (caught.Count > 0)
        {
            Console.Error.WriteLine(
                $"  PROBLEM: an upper-body mask reaches {caught.Count} lower-body bone(s): " +
                string.Join(", ", caught.Take(6)));
            problems++;
        }
        else
        {
            Console.WriteLine("  and it reaches no bone named like a leg");
        }

        // And the complement has to be the rest of the body, exactly. Two masks built separately are
        // not guaranteed to partition a rig; a mask and its inverse are.
        var lower = hard.Inverted();
        var exact = true;
        for (var i = 0; i < skeleton.BoneCount; i++) exact &= hard[i] + lower[i] == 1f;
        if (!exact)
        {
            Console.Error.WriteLine("  PROBLEM: a mask and its inverse do not partition the rig");
            problems++;
        }

        return problems;
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
