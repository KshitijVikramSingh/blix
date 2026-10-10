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
    private const int TransportCacheVersion = 2;
    private string? sceneAssetsRoot;

    internal sealed class TransportCache
    {
        public Vector3[] Pos = Array.Empty<Vector3>(), Nrm = Array.Empty<Vector3>(), Albedo = Array.Empty<Vector3>();
        public float[] Openness = Array.Empty<float>();
        public (int To, float W)[][] Couplings = Array.Empty<(int, float)[]>();
        public (int To, float W)[][] SunletCouplings = Array.Empty<(int, float)[]>();
        public Vector3[] SunletPos = Array.Empty<Vector3>(), SunletNrm = Array.Empty<Vector3>(), SunletAlbedo = Array.Empty<Vector3>();
        public float[] Sky = Array.Empty<float>();
        public ulong[] SunVis = Array.Empty<ulong>();
        public TexelBake? Texels;
    }

    // Everything the cook reads: what is TRACED -- every instance's transform, triangle and vertex counts and sampled
    // positions, and the surface table (albedo, alpha coverage) in full -- the scene's files (path, size, time), the
    // settings it is cooked under, the version. Hashing the traced scene rather than listing the flags that shape it
    // (--ray-lod-error, --no-foliage, any to come) keeps an A/B from reading another configuration's cook.
    private string TransportCacheKey(RayQueryScene traced, uint[] surfaces)
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
        sb.Append(CultureInfo.InvariantCulture, $"spacing {transportSpacing};rays {transportRays};vis {transportVisRes};sunlet {transportSunlet};charts {transportCharts};");
        sb.Append(CultureInfo.InvariantCulture, $"texels {transportTexels};texel {texelSpacing};levels {texelLevels};reach {TexelOpenReach};lightmap {lightmapEnabled};");
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
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24].ToLowerInvariant();
    }

    private static string TransportCachePath(string key) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "blix", "transport", key + ".bin");

    private static TransportCache? LoadTransportCache(string key)
    {
        var path = TransportCachePath(key);
        if (!File.Exists(path)) return null;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var stream = new BufferedStream(File.OpenRead(path), 1 << 22);
            using var r = new BinaryReader(stream);
            if (r.ReadString() != "blix-transport-cache" || r.ReadInt32() != TransportCacheVersion || r.ReadString() != key) return null;
            var c = new TransportCache
            {
                Pos = ReadArray<Vector3>(r), Nrm = ReadArray<Vector3>(r), Albedo = ReadArray<Vector3>(r), Openness = ReadArray<float>(r),
                Couplings = ReadJagged(r), SunletCouplings = ReadJagged(r),
                SunletPos = ReadArray<Vector3>(r), SunletNrm = ReadArray<Vector3>(r), SunletAlbedo = ReadArray<Vector3>(r),
                Sky = ReadArray<float>(r), SunVis = ReadArray<ulong>(r),
            };
            if (r.ReadBoolean())
            {
                var table = ReadArray<uint>(r);
                var texels = ReadArray<Vector4>(r);
                var count = r.ReadInt32(); var capacity = r.ReadInt32();
                var grid = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                c.Texels = new TexelBake(table, texels, count, capacity, grid);
            }
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transport: cooked data read from the cache ({new FileInfo(path).Length / 1048576.0:0} MB, {clock.Elapsed.TotalSeconds:0.0} s): {path}"));
            return c;
        }
        catch (Exception e) when (e is IOException or EndOfStreamException or InvalidDataException)
        {
            Console.WriteLine($"[VulkanSponza] transport: the cache at {path} could not be read ({e.Message}); cooking.");
            return null;
        }
    }

    private static void SaveTransportCache(string key, TransportCache c)
    {
        var path = TransportCachePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".partial";
        using (var stream = new BufferedStream(File.Create(temp), 1 << 22))
        using (var w = new BinaryWriter(stream))
        {
            w.Write("blix-transport-cache"); w.Write(TransportCacheVersion); w.Write(key);
            WriteArray(w, c.Pos); WriteArray(w, c.Nrm); WriteArray(w, c.Albedo); WriteArray(w, c.Openness);
            WriteJagged(w, c.Couplings); WriteJagged(w, c.SunletCouplings);
            WriteArray(w, c.SunletPos); WriteArray(w, c.SunletNrm); WriteArray(w, c.SunletAlbedo);
            WriteArray(w, c.Sky); WriteArray(w, c.SunVis);
            w.Write(c.Texels is not null);
            if (c.Texels is { } t)
            {
                WriteArray(w, t.Table); WriteArray(w, t.Texels); w.Write(t.Count); w.Write(t.Capacity);
                w.Write(t.Grid.X); w.Write(t.Grid.Y); w.Write(t.Grid.Z); w.Write(t.Grid.W);
            }
        }
        File.Move(temp, path, overwrite: true);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] transport: cooked data cached ({new FileInfo(path).Length / 1048576.0:0} MB): {path}"));
    }

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
