using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Blix.Geometry;

namespace Blix.Demos.VulkanSponza;

// The cook's cache: what CookTransport makes from the scene alone -- patches (position, normal, albedo, openness),
// their couplings to patches and to sunlets, the sunlets, sky bins, sun-visibility maps -- and the texels, written once
// under a key of everything they depend on, and read back by any run whose key matches. Light (the sun, the sky's
// radiance) is not in it: the solve runs on load. Sponza's cook took minutes a run, which made every comparison a
// wait (the user: "as long as we keep cooking each run I don't think we'll be able to make any reasonable judgements").
// A scratch cache, not the cook format stage 5 will want: its layout is this demo's arrays, versioned by CacheVersion.
internal sealed partial class SponzaLoop
{
    // Bump when the cook's output changes meaning (a new sampling rule, a new field).
    // 3: a hit's patch the nearest it can see (NearestVisible), not the nearest: the couplings leaked through walls.
    // 5 (4 was a reverted experiment's): NearestVisible tries the transportCandidates nearest patches only; a hashed texel enclosed in a solid (12 of 16 rays meeting a single-sided back face) dropped (CookTexels).
    // 6: the patches' and the hashed texels' inside-a-solid tests read a coplanar contact as the back face it also is
    // (ClosestThroughContacts): patches had survived inside the crevices' corner block.
    // 7: contacts read from a table of coplanar opposite faces, not by jittered retries.
    // 8: the cache by stage (patches, sun visibility, couplings, texels), each under its own key.
    // 9: patches carry their placement (PatchStage.Placement), for the gather's same-surface fallback.
    private const int TransportCacheVersion = 9;
    // Each stage's own version, on top of that shared one (which every stage file's header checks and every key holds):
    // bump the stage whose output or format changed, and only its key -- and what is made from it -- moves. The shared
    // version moved for one stage's format re-cooked every stage, the texels and the sun records with them (2026-10-14).
    // 0 adds nothing to a key, so the keys from before these existed still answer.
    // Patches 1, couplings 2: the visibility lift scaled to the line (VisLift), own placement first and no hit on its ray's
    // own patch or twin (CouplingsOnMetal), 2026-10-22. Patches 3: patch sides and fill spots behind a sheet that lets light through kept.
    private const int PatchesStageVersion = 4, SunVisStageVersion = 0, CouplingsStageVersion = 3, TexelsStageVersion = 0;   // patches 4: each its triangle row (PatchStage.Row); couplings 3: the visibility lift bounded by each end's room (VisLift)
    private static string StageVersion(int v) => v == 0 ? "" : string.Create(CultureInfo.InvariantCulture, $";stage v{v}");
    private string? sceneAssetsRoot;

    // The cook's stages, each cached on its own under a key of what it depends on (TransportStageKeys): a change to how
    // hits find patches re-cooks the couplings alone, not the patches, the sun visibility or the texels.
    //   patches      positions, normals, albedos, openness   the scene, the spacing, the fill
    //   sun vis      the sun-visibility bins                 the patches, the bins' resolution
    //   couplings    couplings to patches and sunlets, the sunlets, the sky bins
    //                                                        the patches, the rays and every rule a hit finds its patch by
    //   texels       the hashed texels                       the scene, the texel settings
    // Combined: all four, the key what is made from the cook downstream is filed under (light bakes, buried flags, the
    // split light's parts).
    internal sealed record TransportStageKeys(string Patches, string SunVis, string Couplings, string Texels, string Combined);

    internal sealed class PatchStage
    {
        public Vector3[] Pos = Array.Empty<Vector3>(), Nrm = Array.Empty<Vector3>(), Albedo = Array.Empty<Vector3>();
        public float[] Openness = Array.Empty<float>();
        public uint[] Placement = Array.Empty<uint>();   // the placement each patch lies on (the ray scene's numbering)
        public uint[] Row = Array.Empty<uint>();         // the triangle row each lies on (uint.MaxValue: none, the fill's)
    }

    internal sealed class CouplingStage
    {
        public (int To, float W)[][] Couplings = Array.Empty<(int, float)[]>();
        public (int To, float W)[][] SunletCouplings = Array.Empty<(int, float)[]>();
        public Vector3[] SunletPos = Array.Empty<Vector3>(), SunletNrm = Array.Empty<Vector3>(), SunletAlbedo = Array.Empty<Vector3>();
        public float[] Sky = Array.Empty<float>();
    }

    // Everything the cook reads: what is TRACED -- every instance's transform, triangle and vertex counts and sampled
    // positions, and the surface table (albedo, alpha coverage) in full -- the scene's files (path, size, time), the
    // settings it is cooked under, the version. Hashing the traced scene rather than listing the flags that shape it
    // (--ray-lod-error, --no-foliage, any to come) keeps an A/B from reading another configuration's cook.
    private TransportStageKeys TransportCacheKeys(RayQueryScene traced, uint[] surfaces)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"instances {traced.Instances.Count};");
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            // One buffer, reused: a stackalloc inside these loops is not released until the method returns, and over
            // Sponza's instances and sampled positions it overflowed the background cook's stack.
            var buffer = new float[18];
            foreach (var inst in traced.Instances)
            {
                var m = inst.World;
                buffer[0] = m.M11; buffer[1] = m.M12; buffer[2] = m.M13; buffer[3] = m.M14;
                buffer[4] = m.M21; buffer[5] = m.M22; buffer[6] = m.M23; buffer[7] = m.M24;
                buffer[8] = m.M31; buffer[9] = m.M32; buffer[10] = m.M33; buffer[11] = m.M34;
                buffer[12] = m.M41; buffer[13] = m.M42; buffer[14] = m.M43; buffer[15] = m.M44;
                buffer[16] = BitConverter.Int32BitsToSingle(inst.Mesh.Indices.Length);
                buffer[17] = BitConverter.Int32BitsToSingle(inst.Mesh.Positions.Length);
                sha.AppendData(MemoryMarshal.AsBytes(buffer.AsSpan()));
                var positions = inst.Mesh.Positions;
                for (var i = 0; i < positions.Length; i += Math.Max(1, positions.Length / 16))
                {
                    var p = positions[i];
                    buffer[0] = p.X; buffer[1] = p.Y; buffer[2] = p.Z;
                    sha.AppendData(MemoryMarshal.AsBytes(buffer.AsSpan(0, 3)));
                }
            }
            sha.AppendData(MemoryMarshal.AsBytes(surfaces.AsSpan()));
            sb.Append(Convert.ToHexString(sha.GetHashAndReset())).Append(';');
        }
        sb.Append(CultureInfo.InvariantCulture, $"v{TransportCacheVersion};{scene.Name};");
        // How a cut-out is met: by the point-hit alpha (RayAlphaAtlas) or the coin -- the patches' visibility and the couplings differ.
        sb.Append(rayAlphaPlan is not null ? $"alpha-atlas {rayAlphaSide}{(rayBlendCutoff ? "" : " blend-stochastic")};" : "alpha-coin;");
        sb.Append(CultureInfo.InvariantCulture, $"bounds {sceneBoundsMin};{sceneBoundsSpan};");
        if (sceneAssetsRoot is { } root && Directory.Exists(root))
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                         .Where(f => f.EndsWith(".blixmesh", StringComparison.Ordinal) || f.EndsWith(".blixtex", StringComparison.Ordinal)
                                     // With --lightmap the hashed texels leave out what the atlas covers: its files decide them.
                                     || (lightmapEnabled && f.EndsWith(".blixlightmap", StringComparison.Ordinal)))
                         .OrderBy(f => f, StringComparer.Ordinal))
            {
                var info = new FileInfo(f);
                sb.Append(CultureInfo.InvariantCulture, $"{Path.GetRelativePath(root, f)}:{info.Length}:{info.LastWriteTimeUtc.Ticks};");
            }
        var scenePart = sb.ToString();
        static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();
        // How a hit finds its patch: the couplings depend on it, and the fill (which asks the same question) too.
        var finding = string.Create(CultureInfo.InvariantCulture,
            $"candidates {transportCandidates} lift {transportVisLift} far {transportFarCutoff} facing {transportFacingCos} {transportFacingWeight}");
        var patches = Hash(scenePart + string.Create(CultureInfo.InvariantCulture,
            $"patches;spacing {transportSpacing};reach {TexelOpenReach};{(transportFill ? $"fill {transportFillRays} x{transportFillPasses} {finding}" : "no fill")}{StageVersion(PatchesStageVersion)}"));
        var sunVis = Hash(patches + string.Create(CultureInfo.InvariantCulture, $";sun vis {transportVisRes}{StageVersion(SunVisStageVersion)}"));
        var couplings = Hash(patches + string.Create(CultureInfo.InvariantCulture,
            $";couplings;rays {transportRays};sunlet {transportSunlet};{finding}{(transportOwnFallback ? " own fallback" : "")}{(transportOwnFirst ? " own first" : "")}{(transportCpuCouplings ? " cpu" : "")}{(transportSeed != 0 ? $" seed {transportSeed}" : "")}{StageVersion(CouplingsStageVersion)}"));
        var texels = Hash(scenePart + string.Create(CultureInfo.InvariantCulture,
            $"texels {transportTexels};texel {texelSpacing};levels {texelLevels};reach {TexelOpenReach};lightmap {lightmapEnabled} (the atlas is level 0);{(texelFoliage ? "foliage;" : "")}{StageVersion(TexelsStageVersion)}"));
        return new TransportStageKeys(patches, sunVis, couplings, texels, Hash($"{patches};{sunVis};{couplings};{texels}"));
    }

    // Where the stages live: the scene's GI bundle -- a "gi" directory beside its packs, which --cook-gi makes and fills
    // with everything that depends on the geometry alone, so a boot only loads it -- or, without one, the user's cache.
    private static string transportCacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "blix", "transport");
    private static bool giBundle;

    private void ChooseGiBundle(string assetsRoot)
    {
        var bundle = Path.Combine(assetsRoot, "gi");
        if (cookGi)
        {
            Directory.CreateDirectory(bundle);
            // Nothing light-dependent: no split light's parts, no light bake (the runtime's, once it lights).
            splitLight = false;
            lightBakeOff = true;
        }
        giBundle = Directory.Exists(bundle);
        if (giBundle) transportCacheDir = bundle;
        Console.WriteLine(giBundle
            ? $"[VulkanSponza] GI bundle: {bundle}{(cookGi ? " (cooking it: the geometry's stages, then exit)" : "")}."
            : $"[VulkanSponza] GI bundle: none beside the packs ({bundle}); the stages come from the user's cache, or are cooked here. Run once with --cook-gi to make it.");
    }

    private static string TransportCachePath(string key) => Path.Combine(transportCacheDir, key + ".bin");

    // What depends on the light -- the split light's parts, the one light's bake, the sun records -- stays in the user's
    // cache, never the bundle: the bundle is the geometry's alone (and these go once the light is the runtime's).
    private static readonly string LightCacheDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "blix", "transport");
    private static string LightCachePath(string key) => Path.Combine(LightCacheDir, key + ".bin");

    private static string TransportStagePath(string key, string stage) => Path.Combine(transportCacheDir, $"{key}.{stage}");

    // A stage's file: its name, the version and its key, then what `read` reads; null when absent, another version's
    // or another key's, or unreadable.
    private static T? LoadStage<T>(string key, string stage, Func<BinaryReader, T> read) where T : class
    {
        var path = TransportStagePath(key, stage);
        if (!File.Exists(path)) return null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var stream = new BufferedStream(File.OpenRead(path), 1 << 22);
            using var r = new BinaryReader(stream);
            if (r.ReadString() != "blix-transport-" + stage || r.ReadInt32() != TransportCacheVersion || r.ReadString() != key) return null;
            var value = read(r);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport: {stage} read from the cache ({new FileInfo(path).Length / 1048576.0:0} MB, {clock.Elapsed.TotalSeconds:0.0} s)."));
            return value;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException)
        {
            Console.WriteLine($"[VulkanSponza] transport: the {stage} cache at {path} could not be read ({e.Message}); cooking it.");
            return null;
        }
    }

    private static void SaveStage(string key, string stage, Action<BinaryWriter> write)
    {
        var path = TransportStagePath(key, stage);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial";
        using (var stream = new BufferedStream(File.Create(temp), 1 << 22))
        using (var w = new BinaryWriter(stream))
        {
            w.Write("blix-transport-" + stage); w.Write(TransportCacheVersion); w.Write(key);
            write(w);
        }
        File.Move(temp, path, overwrite: true);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] transport: {stage} cached ({new FileInfo(path).Length / 1048576.0:0} MB)."));
    }

    private static PatchStage? LoadPatches(string key) => LoadStage(key, "patches", r => new PatchStage
        { Pos = ReadArray<Vector3>(r), Nrm = ReadArray<Vector3>(r), Albedo = ReadArray<Vector3>(r), Openness = ReadArray<float>(r), Placement = ReadArray<uint>(r), Row = ReadArray<uint>(r) });

    private static void SavePatches(string key, PatchStage p) => SaveStage(key, "patches", w =>
        { WriteArray(w, p.Pos); WriteArray(w, p.Nrm); WriteArray(w, p.Albedo); WriteArray(w, p.Openness); WriteArray(w, p.Placement); WriteArray(w, p.Row); });

    // skipSunlets (--live-texels, which never reads them: the live patch light has its own sun): the sunlets and their
    // couplings are seeked past, ~1.07 GB of Sponza's 1.65 GB file (2026-10-20), and every patch gets an empty list.
    private static CouplingStage? LoadCouplings(string key, bool skipSunlets = false) => LoadStage(key, "couplings", r =>
    {
        var couplings = ReadJagged(r);
        if (!skipSunlets)
            return new CouplingStage
            {
                Couplings = couplings, SunletCouplings = ReadJagged(r),
                SunletPos = ReadArray<Vector3>(r), SunletNrm = ReadArray<Vector3>(r), SunletAlbedo = ReadArray<Vector3>(r), Sky = ReadArray<float>(r),
            };
        SkipArray<int>(r); SkipArray<int>(r); SkipArray<float>(r);   // the sunlet couplings: counts, ids, weights
        SkipArray<Vector3>(r); SkipArray<Vector3>(r); SkipArray<Vector3>(r);
        var none = new (int, float)[couplings.Length][];
        Array.Fill(none, Array.Empty<(int, float)>());
        return new CouplingStage { Couplings = couplings, SunletCouplings = none, Sky = ReadArray<float>(r) };
    });

    private static void SkipArray<T>(BinaryReader r) where T : unmanaged
    {
        var n = r.ReadInt32();
        r.BaseStream.Seek((long)n * System.Runtime.CompilerServices.Unsafe.SizeOf<T>(), SeekOrigin.Current);
    }

    private static void SaveCouplings(string key, CouplingStage c) => SaveStage(key, "couplings", w =>
    {
        WriteJagged(w, c.Couplings); WriteJagged(w, c.SunletCouplings);
        WriteArray(w, c.SunletPos); WriteArray(w, c.SunletNrm); WriteArray(w, c.SunletAlbedo); WriteArray(w, c.Sky);
    });

    private static ulong[]? LoadSunVis(string key) => LoadStage(key, "sunvis", r => ReadArray<ulong>(r));

    private static void SaveSunVis(string key, ulong[] bits) => SaveStage(key, "sunvis", w => WriteArray(w, bits));

    private static TexelBake? LoadTexels(string key) => LoadStage(key, "texels", r =>
    {
        var table = ReadArray<uint>(r);
        var texels = ReadArray<Vector4>(r);
        var count = r.ReadInt32(); var capacity = r.ReadInt32();
        var grid = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return new TexelBake(table, texels, count, capacity, grid);
    });

    private static void SaveTexels(string key, TexelBake t) => SaveStage(key, "texels", w =>
    {
        WriteArray(w, t.Table); WriteArray(w, t.Texels); w.Write(t.Count); w.Write(t.Capacity);
        w.Write(t.Grid.X); w.Write(t.Grid.Y); w.Write(t.Grid.Z); w.Write(t.Grid.W);
    });

    private static void WriteArray<T>(BinaryWriter w, T[] a) where T : unmanaged
    {
        w.Write(a.Length);
        w.Write(MemoryMarshal.AsBytes(a.AsSpan()));
    }

    private static T[] ReadArray<T>(BinaryReader r) where T : unmanaged
    {
        var n = r.ReadInt32();
        var a = new T[n];
        var bytes = MemoryMarshal.AsBytes(a.AsSpan());
        var read = 0;
        while (read < bytes.Length)
        {
            var got = r.Read(bytes[read..]);
            if (got == 0) throw new EndOfStreamException();
            read += got;
        }
        return a;
    }

    // Per patch its count, then every (to, w) pair flattened.
    private static void WriteJagged(BinaryWriter w, (int To, float W)[][] j)
    {
        var counts = j.Select(e => e?.Length ?? 0).ToArray();
        WriteArray(w, counts);
        var to = new int[counts.Sum(x => (long)x)];
        var weight = new float[to.Length];
        var k = 0;
        foreach (var e in j)
            if (e is not null)
                foreach (var (t, x) in e) { to[k] = t; weight[k] = x; k++; }
        WriteArray(w, to);
        WriteArray(w, weight);
    }

    private static (int To, float W)[][] ReadJagged(BinaryReader r)
    {
        var counts = ReadArray<int>(r);
        var to = ReadArray<int>(r);
        var weight = ReadArray<float>(r);
        var j = new (int, float)[counts.Length][];
        var k = 0;
        for (var i = 0; i < counts.Length; i++)
        {
            var e = new (int, float)[counts[i]];
            for (var q = 0; q < e.Length; q++, k++) e[q] = (to[k], weight[k]);
            j[i] = e;
        }
        return j;
    }
}
