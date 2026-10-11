using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --transfer-dump <file> (with --lightmap --transport-texels, a fixed --cam and --sun): the data for the surface transfer
// atlas's first experiment (plan.md, stage 5): can a texel's light be written as a non-negative blend of its nearby
// probes', with weights that hold when the sun moves? This bakes a deterministic sample of the atlas texels inside
// clipmap level 0 around the camera (BeginLightBake; nothing saved), lets the probes settle, and writes per texel its
// traced irradiance and, for each of the 8 level-0 probes around its lookup point, that probe's irradiance at the
// texel's normal and today's weight factors (trilinear, facing, Chebyshev: probe_clipmap.glsl, mirrored here). Run it
// once per sun; tools/transfer/fit.py fits weights on all suns but one and judges the one left out.
internal sealed partial class SponzaLoop
{
    private string? transferDumpPath;
    // --transfer-weights <file> (tools/transfer/fit.py --write): fitted weights over each atlas texel's 8 level-0 probes,
    // by atlas record (id - lightmapFirstRecord), 8 halves in a uvec4; zero where none was fitted. The incident pass
    // then answers a pixel whose texel has weights with sum_k w_k . probe_k (incident_clipmap.frag, transferAt) in
    // place of the probe blend, and reads no texels -- probes and transfer alone.
    private string? transferWeightsPath;
    private GpuBufferHandle transferWeightsBuffer;
    private bool transferWeightsOn;

    private void UploadTransferWeights(int first, int atlasCount)
    {
        if (transferWeightsPath is null) return;
        using var r = new BinaryReader(File.OpenRead(transferWeightsPath));
        var count = r.ReadInt32();
        var ids = MemoryMarshal.Cast<byte, uint>(r.ReadBytes(count * 4)).ToArray();
        var halves = MemoryMarshal.Cast<byte, ushort>(r.ReadBytes(count * 16)).ToArray();
        var words = new uint[(long)atlasCount * 4];
        var placed = 0;
        for (var i = 0; i < count; i++)
        {
            var rec = (long)ids[i] - first;
            if (rec < 0 || rec >= atlasCount) continue;
            for (var k = 0; k < 4; k++) words[rec * 4 + k] = halves[i * 8 + 2 * k] | ((uint)halves[i * 8 + 2 * k + 1] << 16);
            placed++;
        }
        transferWeightsBuffer = Own(device.CreateGpuBuffer(words.Length * 4, MemoryMarshal.AsBytes(words.AsSpan()), "sponza.transfer.weights"));
        transferWeightsOn = true;
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transfer weights: {placed:N0} atlas texels of {count:N0} in {Path.GetFileName(transferWeightsPath)}; the incident pass answers them from their probes."));
    }

    private ShaderBufferBinding TransferWeightsBinding()
    {
        if (transferWeightsBuffer.Equals(default(GpuBufferHandle)))
            transferWeightsBuffer = Own(device.CreateGpuBuffer(16, new byte[16], "sponza.transfer.weights.none"));
        return new ShaderBufferBinding("TransferWeights", transferWeightsBuffer);
    }

    // x 1 when the weights are up, y the first atlas record.
    private Vector4 TransferUniform => new(transferWeightsOn ? 1f : 0f, lightmapFirstRecord, 0f, 0f);
    private bool transferBakeDone;
    private uint transferBakeDoneFrame;
    // Frames after the region's bake before the dump: the probes keep solving meanwhile.
    private const uint TransferSettleFrames = 900;
    // One texel in this many (by a hash of its record id, so every run picks the same ones): --transfer-stride (4), 1 for
    // weights a run will read (--transfer-weights).
    private uint transferSampleStride = 4;
    // A dump row: id, position, normal, gathered light, 8 probes x (valid, trilinear, facing^2, Chebyshev, rgb), traced rgb.
    private const int TransferRowFloats = 1 + 3 + 3 + 3 + 8 * 7 + 3;
    // The texels' path-traced truth (SponzaLoop.GpuReference.cs, a ReferenceWave at the texels: the software traversal or
    // Metal under --ray-backend hw|both): --transfer-paths a texel (1,024), --ref-bounces deep. The dump writes it beside
    // the gathered light, and tools/transfer/fit.py fits to it.
    private List<Vector4>? transferCapturePoints;
    private ReferenceWave? transferWave;
    private uint transferWaveStart;
    private int transferPaths = 1024;
    // Lanes a texel (--transfer-lanes N): the capture is bound by frames (each lane runs its paths one bounce a frame), not
    // rays, so on Metal as many as TransferLaneBudget lanes in all allow (~200 B of state a lane), up to the paths; 4 on
    // the software traversal, whose rays are the cost.
    private int transferLanes;
    private const int TransferLaneBudget = 1 << 21;
    private int TransferLanesPerPoint(int points) => transferLanes > 0 ? Math.Min(transferLanes, transferPaths)
        : FrameRaysOnHardware ? Math.Clamp(TransferLaneBudget / Math.Max(points, 1), 4, transferPaths) : 4;

    // BeginLightBake's live list under --transfer-dump: the sample, inside level 0 with a probe on every side.
    private bool TransferSampleKeeps(Vector4[] texels, int i, int firstAtlas)
    {
        if (i < firstAtlas || MathF.Abs(texels[2 * i].W) < 2f || clipmap is null) return false;
        // Either side of a double-sided pair may be drawn: each is its own sample.
        var h = (uint)i * 2654435761u;
        if ((h >> 7) % transferSampleStride != 0) return false;
        var p = new Vector3(texels[2 * i].X, texels[2 * i].Y, texels[2 * i].Z);
        return clipmap.InsideDistance(0, p) >= 1.5f;
    }

    private void TransferDumpWhenDone()
    {
        if (transferDumpPath is null || !transferBakeDone || clipmap is null) return;
        if (transferWave is null && transferCapturePoints is { Count: > 0 } points)
        {
            var lanes = TransferLanesPerPoint(points.Count / 2);
            transferWave = CreateReferenceWave(points, lanes, (transferPaths + lanes - 1) / lanes, "transfer");
            transferWaveStart = texelFrame;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[VulkanSponza] transfer capture: {transferWave.PointCount:N0} texels, {transferPaths} paths x {refBounces} bounces each, {transferWave.Count:N0} lanes, ~{ReferenceWaveFrames(transferWave)} frames ({(FrameRaysOnHardware ? "Metal" : "software")})."));
        }
        var captured = transferWave is null || texelFrame - transferWaveStart >= (uint)ReferenceWaveFrames(transferWave);
        // The texels' truth against the scene their light was gathered in: the bake scene where there is one.
        if (!captured) RecordReferenceWave(transferWave!, BakeTracer);
        if (texelFrame - transferBakeDoneFrame < TransferSettleFrames || !captured) return;
        var path = transferDumpPath;
        transferDumpPath = null;
        var irradiance = device.ReadTexture(clipmapIrradiance, out var aw, out _, out _);
        var depth = device.ReadTexture(clipmapDepth, out _, out _, out _);
        var states = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(clipmapState, 0, clipmap.LevelCount * clipmap.ProbesPerLevel * 16).AsSpan()).ToArray();
        var texels = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelBuffer, 0, texelCount * 32).AsSpan()).ToArray();
        var light = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(texelLightBuffer, 0, texelCount * 16).AsSpan()).ToArray();
        var live = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(lightBakeLiveBuffer, 0, lightBakeLive * 4).AsSpan()).ToArray();
        // The path-traced truth, in the live list's order (as the capture's points were packed).
        var traced = transferWave is null ? null : ReferenceWaveSums(transferWave);

        Vector4 Atlas(byte[] img, int x, int y)
        {
            var o = (y * aw + x) * 8;
            return new Vector4((float)BitConverter.ToHalf(img, o), (float)BitConverter.ToHalf(img, o + 2), (float)BitConverter.ToHalf(img, o + 4), (float)BitConverter.ToHalf(img, o + 6));
        }
        // probe_clipmap.glsl's blix_clipmapTileSample: a direction's place in the 6 x 6 interior, bilinear by hand.
        Vector4 TileSample((int X, int Y) tile, Vector3 dir, byte[] img)
        {
            dir = Vector3.Normalize(dir);
            var d = dir / (MathF.Abs(dir.X) + MathF.Abs(dir.Y) + MathF.Abs(dir.Z));
            var uv = d.Z >= 0f ? new Vector2(d.X, d.Y)
                : new Vector2((1f - MathF.Abs(d.Y)) * (d.X >= 0f ? 1f : -1f), (1f - MathF.Abs(d.X)) * (d.Y >= 0f ? 1f : -1f));
            var t = (uv * 0.5f + new Vector2(0.5f)) * 6f + new Vector2(0.5f);
            int bx = (int)MathF.Floor(t.X), by = (int)MathF.Floor(t.Y);
            float fx = t.X - bx, fy = t.Y - by;
            Vector4 A(int ox, int oy) => Atlas(img, tile.X + bx + ox, tile.Y + by + oy);
            return Vector4.Lerp(Vector4.Lerp(A(0, 0), A(1, 0), fx), Vector4.Lerp(A(0, 1), A(1, 1), fx), fy);
        }

        var spacing = clipmap.Spacing(0);
        var rows = new List<float>(live.Length * TransferRowFloats);
        var written = 0;
        for (var li = 0; li < live.Length; li++)
        {
            var id = live[li];
            var pos = new Vector3(texels[2 * id].X, texels[2 * id].Y, texels[2 * id].Z);
            var n = Vector3.Normalize(new Vector3(texels[2 * id + 1].X, texels[2 * id + 1].Y, texels[2 * id + 1].Z));
            var l = light[id];
            if (l.W <= 0f) continue;
            rows.Add(id); rows.Add(pos.X); rows.Add(pos.Y); rows.Add(pos.Z); rows.Add(n.X); rows.Add(n.Y); rows.Add(n.Z);
            rows.Add(l.X); rows.Add(l.Y); rows.Add(l.Z);
            var p = pos + n * (0.25f * spacing);
            var g = p / spacing - new Vector3(0.5f);
            var b = new Vector3(MathF.Floor(g.X), MathF.Floor(g.Y), MathF.Floor(g.Z));
            var f = g - b;
            for (var k = 0; k < 8; k++)
            {
                int ox = k & 1, oy = (k >> 1) & 1, oz = (k >> 2) & 1;
                var cell = new Int3((int)b.X + ox, (int)b.Y + oy, (int)b.Z + oz);
                var slot = clipmap.Slot(cell);
                var s = clipmap.SlotIndex(slot) * 4;
                var valid = (int)states[s] == cell.X && (int)states[s + 1] == cell.Y && (int)states[s + 2] == cell.Z
                    && (states[s + 3] & 1u) != 0 && (states[s + 3] & 2u) == 0;
                var trilinear = (ox == 1 ? f.X : 1f - f.X) * (oy == 1 ? f.Y : 1f - f.Y) * (oz == 1 ? f.Z : 1f - f.Z);
                var toProbe = clipmap.ProbePosition(0, cell) - p;
                var dist = toProbe.Length();
                var dir = dist > 1e-5f ? toProbe / dist : n;
                var facing = Vector3.Dot(dir, n) * 0.5f + 0.5f;
                var tile = clipmap.TileOrigin(0, slot);
                var chebyshev = 1f;
                var rgb = Vector3.Zero;
                if (valid)
                {
                    var moments = TileSample(tile, -dir, depth);
                    if (dist > moments.X)
                    {
                        var variance = MathF.Max(moments.Y, 1e-5f);
                        var dd = dist - moments.X;
                        var c = variance / (variance + dd * dd);
                        chebyshev = c * c * c;
                    }
                    var e = TileSample(tile, n, irradiance);
                    rgb = new Vector3(e.X, e.Y, e.Z);
                }
                rows.Add(valid ? 1f : 0f); rows.Add(trilinear); rows.Add(facing * facing); rows.Add(chebyshev);
                rows.Add(rgb.X); rows.Add(rgb.Y); rows.Add(rgb.Z);
            }
            // The traced truth (pi x radiance over its paths), -1 where there was none.
            var t = traced is null ? new Vector4(-1f, -1f, -1f, 1f) : traced[li];
            var paths = MathF.Max(t.W, 1f);
            rows.Add(t.X / paths); rows.Add(t.Y / paths); rows.Add(t.Z / paths);
            written++;
        }
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(TransferRowFloats);
            w.Write(written);
            foreach (var v in rows) w.Write(v);
        }
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] transfer dump: {written:N0} texels (of {live.Length:N0} baked) with their 8 level-0 probes at {spacing} m, sun {sunDirection}: {path}"));
    }
}
