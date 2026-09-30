using Blix.Core;
using System.Numerics;
using Blix.Assets;
using Blix.Cooked;

namespace Blix.Tools.Check;

// Headless asset judge. `blix inspect` reports what a source or cooked artifact
// contains; this command verifies supported model, rig, clip, and cooked-load
// contracts and returns non-zero when they fail. Shader and descriptor conformance
// and application-specific rig budgets belong to their consumers, not to an engine
// asset check.
public static class Program
{
    public static int Main(string[] args) => BlixApps.Main(args);

    [BlixApp("check", Summary = "judge model, rig, animation, and cooked-load contracts; exits non-zero", Default = true)]
    public static int Check(AppArgs args)
    {
        // Import refusals are expected asset verdicts. Other exception types remain tool faults and
        // are not converted into a clean asset report.
        try
        {
            if (args.String("rig") is not null)
            {
                Console.Error.WriteLine("check: --rig is gone: --model checks any model, and runs the rig checks on one with a skin.");
                return 2;
            }

            if (args.String("model") is { } model) return InspectModel(model, args.Flag("verbose"));
            if (args.String("cooked") is { } cooked) return JudgeCooked(cooked);
        }
        catch (AssetImportException refused)
        {
            Console.Error.WriteLine($"blix cannot read this: {refused.Message}");
            return 1;
        }

        if (args.Flag("help") | args.Flag("h"))
        {
            Console.WriteLine("Usage: blix check --model <gltf-or-glb> [--verbose] | --cooked <directory>");
            Console.WriteLine("  --model     import a model; report shape and reject unusable clip lengths, and on one");
            Console.WriteLine("              with a skin check the rig: hierarchy, rest palette, track coverage,");
            Console.WriteLine("              finiteness across every clip; report root-motion observations");
            Console.WriteLine("  --cooked    load supported meshes under a DIRECTORY and fail if geometry or images");
            Console.WriteLine("              resolve to source, fallback, or missing data");
            Console.WriteLine("  Exits non-zero when something is wrong. For a plain listing of an");
            Console.WriteLine("  asset's hierarchy and pivots, use: blix inspect <path>");
            return 0;
        }

        // Shader and binding conformance has no asset subject and belongs to Blix.Test.Studio.
        Console.Error.WriteLine(
            "check needs something to check: --model <path.glb> or --cooked <directory>.");
        Console.Error.WriteLine(
            "  (the studio's own binding model is checked by `blix test`, not here)");
        return 2;
    }

    /// <summary>The source kinds this judge knows how to load. Anything else is not its business.</summary>
    /// <remarks>
    /// Includes standalone cooked meshes so a source-free shipping tree is still exercised.
    /// </remarks>
    private static readonly string[] Judged = { ".gltf", ".glb", ".obj", ".blixmesh" };

    /// <summary>Loads each supported path the way the engine does: a model from its cooked file.</summary>
    /// <remarks>
    /// The engine reads cooked models only, so a glTF is judged by the <c>.blixmesh</c> beside it, and
    /// one with none is refused. OBJ uses <see cref="WavefrontParts"/>, which still reads a cooked
    /// sibling when it has one.
    /// </remarks>
    private static void LoadAsUsed(string source)
    {
        if (Path.GetExtension(source).Equals(".obj", StringComparison.OrdinalIgnoreCase))
        {
            // Reuse the cooked stamp's recenter setting. The sweep cannot infer which setting a
            // particular game wants; real consumers still enforce their own request at load time.
            var stamped = CookedFile.TryReadHeader(Path.ChangeExtension(source, ".blixmesh"));
            var recenter = stamped?.Stamp.Parameters.Contains("recenter=0", StringComparison.Ordinal) != true;
            WavefrontParts.Import(source, recenter);
            return;
        }

        var cooked = Path.GetExtension(source).Equals(".blixmesh", StringComparison.OrdinalIgnoreCase)
            ? source
            : Path.ChangeExtension(source, ".blixmesh");
        if (!File.Exists(cooked))
        {
            throw new AssetImportException(
                source, null, "has no cooked .blixmesh beside it, and the engine loads cooked models only — cook it");
        }

        ModelData.Load(cooked, new ModelNeeds(Skinned: true));
    }

    /// <summary>Loads every supported asset under a directory and fails on non-cooked loads.</summary>
    /// <remarks>
    /// This judges the path a runtime loader takes, including image loads triggered by a mesh. It
    /// does not judge source freshness; <c>blix cook status</c> compares stamps to sources.
    /// Unsupported extensions are outside this sweep and an empty supported set is reported with a
    /// successful exit.
    /// </remarks>
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
            // Texture reports are nested concurrent work, so their summed CPU time can exceed the
            // top-level asset load time. Report the two measures independently.
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

    private static int InspectModel(string path, bool verbose)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"No model at {path}.");
            return 2;
        }

        var problems = 0;

        // Cooked on open, so the probe checks what the engine would draw.
        var imported = ModelData.Load(Blix.Recipes.CookCache.Resolve(path), new ModelNeeds(Skinned: true));
        var rigged = imported.IsRigged;
        var primitives = rigged ? imported.SkinnedPrimitives().Count() : imported.Flattened().Count();

        Console.WriteLine($"{Path.GetFileName(path)}");
        Console.WriteLine(
            $"  {primitives} primitive(s), " +
            (rigged
                ? $"{imported.Skins[0].Skeleton.BoneCount} bone(s), {imported.Clips.Count} clip(s)"
                : "static — no skin, so no bones or clips to check"));
        if (rigged)
        {
            Console.WriteLine(
                imported.Placement(0).IsIdentity
                    ? "  mesh-node transform: identity"
                    : "  mesh-node transform: NOT identity — the asset orients itself at a parent node, " +
                      "so uModel must compose with it");
        }

        foreach (var clip in imported.Clips.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var translation = clip.Tracks.Count(t => t.Translation is not null);
            var rotation = clip.Tracks.Count(t => t.Rotation is not null);
            var scale = clip.Tracks.Count(t => t.Scale is not null);

            // A zero-length clip is an authored pose. Only a non-finite duration is unusable.
            var kind = clip.Duration > 0.0 ? $"{clip.Duration,6:0.00}s" : "  pose";
            Console.WriteLine(
                $"    {clip.Name,-28} {kind}  {clip.Tracks.Length,3} track(s)  " +
                $"T{translation} R{rotation} S{scale}");

            if (double.IsFinite(clip.Duration)) continue;
            Console.Error.WriteLine($"      clip duration is {clip.Duration} — not a usable length.");
            problems++;
        }

        if (problems > 0) Console.Error.WriteLine($"{problems} problem(s).");
        // A skin is a fact of the file, so its checks run whenever there is one.
        var rigVerdict = rigged ? InspectRig(path, verbose) : 0;
        return problems > 0 ? 1 : rigVerdict;
    }

    // Rig checks separate hierarchy, rest-pose, and sampled animation faults before a graphics
    // device is involved. Consumer budgets and naming conventions are deliberately outside it.
    private static int InspectRig(string path, bool verbose)
    {
        var problems = 0;
        var imported = ModelData.Load(Blix.Recipes.CookCache.Resolve(path), new ModelNeeds(Colour: true, Skinned: true));

        var skeleton = imported.Skins[0].Skeleton;

        // Attachments and static parts as the rig reads them: an unskinned mesh node under a joint,
        // and every other unskinned mesh node.
        var carried = imported.Attachments();
        var carriedNodes = carried.Select(a => a.NodeIndex).ToHashSet();
        var attachments = carried.Select(a => (
            Name: imported.Nodes[a.NodeIndex].Name,
            JointName: imported.Nodes[a.JointNode].Name,
            JointIndex: a.BoneIndex,
            Primitives: imported.Meshes[imported.Nodes[a.NodeIndex].MeshIndex].Primitives)).ToArray();
        var staticParts = Enumerable.Range(0, imported.Nodes.Count)
            .Where(n => imported.Nodes[n].MeshIndex >= 0 && imported.Nodes[n].SkinIndex < 0 && !carriedNodes.Contains(n))
            .Select(n => (Name: imported.Nodes[n].Name, Primitives: imported.Meshes[imported.Nodes[n].MeshIndex].Primitives))
            .ToArray();
        Console.WriteLine($"{Path.GetFileName(path)}");
        Console.WriteLine(
            $"  {skeleton.BoneCount} bone(s), {imported.Clips.Count} clip(s), " +
            $"{imported.SkinnedPrimitives().Count()} skinned primitive(s), {attachments.Length} attachment(s), " +
            $"{staticParts.Length} static part(s)");

        // ── Static parts and attachments ───────────────────────────────────
        // These are inventory, not failures: a rigged file may legitimately carry both.
        if (staticParts.Length > 0)
        {
            Console.WriteLine();
            foreach (var sp in staticParts.OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                var verts = sp.Primitives.Sum(p => p.Mesh.VertexCount);
                Console.WriteLine(
                    $"    {sp.Name,-22} static, on no joint      {sp.Primitives.Count} prim, {verts,6} verts");
            }
        }

        if (attachments.Length > 0)
        {
            Console.WriteLine();
            foreach (var a in attachments.OrderBy(a => a.JointName, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal))
            {
                var verts = a.Primitives.Sum(p => p.Mesh.VertexCount);
                Console.WriteLine(
                    $"    {a.Name,-22} on {a.JointName,-14} bone[{a.JointIndex,2}]  " +
                    $"{a.Primitives.Count} prim, {verts} verts");
            }

            // Several alternatives may share one joint; a caller normally selects one.
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

        // ── Hierarchy ───────────────────────────────────────────────────────
        // Skeleton construction already enforces parent-before-child ordering; report root count.
        var roots = 0;
        for (var i = 0; i < skeleton.BoneCount; i++)
        {
            var parent = skeleton.Bones[i].ParentIndex;
            if (parent < 0) roots++;
        }

        Console.WriteLine($"  hierarchy: {roots} root(s), order valid");

        // ── Rest pose ───────────────────────────────────────────────────────
        // BindWorld × InverseBindPose = I by construction, so every rest palette matrix is the
        // identity — and a rig whose inverse-bind matrices do not invert its bind pose fails here
        // rather than as a mesh that explodes the moment it is skinned. This is the single most
        // valuable check in the file: it is exact, it needs no clip, and it catches a bad export.
        var rest = skeleton.CreateRestPose();
        var palette = new BonePalette(skeleton.BoneCount);
        var worlds = new Matrix4x4[skeleton.BoneCount];
        skeleton.ComputeBonePalette(rest, palette, worlds);
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

        // ── World-space joint positions and deformation reach ──────────────
        // Weighted bones deform vertices; promoted ancestors are required to draw those chains
        // without gaps. Control bones may appear in neither set.
        var weighted = SkinningAnalysis.FindWeightedBones(skeleton, imported.SkinnedPrimitives().Select(p => p.Mesh));
        var deform = SkinningAnalysis.IncludeAncestors(skeleton, weighted);
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

        // ── Every clip, sampled ─────────────────────────────────────────────
        var poses = 0;
        var travelling = 0;
        var pose = skeleton.CreateRestPose();
        var rootBone = RootMotion.DefaultRootBone(skeleton);
        Console.WriteLine($"  root bone: {rootBone} '{skeleton.Bones[rootBone].Name}'");

        foreach (var clip in imported.Clips.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            // Zero-length clips are held poses; sample them once at t=0.
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

            // Interior samples catch non-finite interpolation that endpoint-only checks miss.
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

            // Track coverage identifies partial clips whose untouched bones remain at rest.
            var touched = new HashSet<int>();
            foreach (var track in clip.Tracks) touched.Add(track.BoneIndex);

            var cycle = RootMotion.PerCycle(clip, rootBone, rest.Locals[rootBone]);

            // ── Root-motion observations ────────────────────────────────────
            // The asset format carries no loop-intent bit. A large seam or multi-cycle mismatch is
            // useful evidence for an author or consumer, but not proof that an arbitrary clip is
            // invalid, so both remain notes rather than exit-code failures.
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
                Console.WriteLine(
                    $"    note: {clip.Name} moves {seam.Distance:0.0000} m across its end/start seam " +
                    $"against {interior.Distance:0.0000} m over an equal interior interval; only a " +
                    "consumer that loops this clip needs to treat that as a discontinuity");
            }

            // Integrate three cycles through ClipPlayer with a non-divisor step so wraps occur
            // mid-step and dropped or duplicated travel becomes measurable.
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
                    Console.WriteLine(
                        $"    note: {clip.Name} integrates to {summed.Length():0.0000} m over " +
                        $"{Cycles} wraps while one cycle reports {cycle.Distance:0.0000} m " +
                        $"(vector error {error:0.0000} m); inspect if this clip is intended to loop");
                }
            }

            if (!verbose && touched.Count == skeleton.BoneCount && !travels) continue;
            Console.WriteLine(
                $"    {clip.Name,-30} {clip.Duration,5:0.00}s  " +
                $"{touched.Count,3}/{skeleton.BoneCount} bones" +
                (travels ? $"  travels {cycle.Distance:0.000} m/cycle" : string.Empty));
        }

        Console.WriteLine(
            $"  {imported.Clips.Count - poses} timed clip(s), {poses} pose(s), " +
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
