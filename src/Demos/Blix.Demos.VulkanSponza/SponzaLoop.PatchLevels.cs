using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// Coarser patch levels (--cone-lookups, the runtime gather's model): the surface cache's mip chain, for a cone whose
// footprint outgrows a patch and its neighbours. Level k (1..PatchLevelCount) joins the finer level's nodes that touch
// along the surface within a grid of the patch spacing x 2^k and the axis their normal leans to (so a wall's two faces,
// a floor at its foot, and the floors either side of a wall stay apart); a node is its patches' mean position and normal,
// its neighbours the 8 nearest of the nodes its members' neighbours lie in, facing within 60 degrees. Cooked once from the patches (cookedPacked); each light change, patch_levels.comp averages
// the patches' light into every node (both columns, the sun's direct in, as the cells' and the footprint's form).
internal sealed partial class SponzaLoop
{
    private const int PatchLevelCount = 5;   // to 0.25 x 32 = 8 m at Sponza's spacing
    private GpuBufferHandle levelNodesBuffer, levelParentsBuffer, levelNeighboursBuffer, levelMemberStart, levelMembers, levelLightBuffer;
    private int levelNodeCount, levelPatchCount;
    private (Vector3, int) levelLitFor = (new Vector3(float.NaN), -1);
    private ShaderInterface patchLevelsInterface = null!;
    private PipelineHandle patchLevelsPipeline;

    private void DeclarePatchLevels(Func<string[], ShaderInterface> reflect) => patchLevelsInterface = reflect(new[] { "patch_levels.comp" });

    private void CreatePatchLevelsPipeline(string shaderDir)
    {
        patchLevelsPipeline = Own(device.CreateComputePipeline(Own(device.CreateComputeShaderProgramFromSpv(
            File.ReadAllBytes(Path.Combine(shaderDir, "patch_levels.comp.spv")), patchLevelsInterface, "patch_levels")), "patch_levels"));
    }

    private bool PatchLevelsReady => !levelNodesBuffer.Equals(default(GpuBufferHandle));

    private static int NormalAxis(Vector3 n)
    {
        var a = Vector3.Abs(n);
        return a.X >= a.Y && a.X >= a.Z ? (n.X >= 0 ? 0 : 1) : a.Y >= a.Z ? (n.Y >= 0 ? 2 : 3) : (n.Z >= 0 ? 4 : 5);
    }

    // Once the patches are up: the levels' nodes, parents and neighbours. A node joins only what its finer level's nodes
    // reach along the surface (the patches' neighbours, never across a thin wall: SponzaLoop.FarField.cs): within its grid
    // cell and normal axis, the connected parts. Grouped by the cell alone, a 2 m node took the sunlit floor outside a
    // corridor's wall into the corridor's: its seams read 27% bright (2026-10-23).
    private void BuildPatchLevels()
    {
        if (PatchLevelsReady || cookedPacked is not { } packed || farNeighbours.Equals(default(GpuBufferHandle))) return;
        var clock = Stopwatch.StartNew();
        var patches = packed.Length / 3;
        levelPatchCount = patches;
        device.WaitIdle();
        var patchNb = MemoryMarshal.Cast<byte, int>(device.ReadGpuBuffer(farNeighbours, 0, patches * FarNeighbours * 4).AsSpan()).ToArray();
        // The finer level: positions, normals, patch counts, neighbour lists (level 0: the patches).
        var fPos = new Vector3[patches];
        var fNrm = new Vector3[patches];
        var fCount = new int[patches];
        var fNb = new List<int>[patches];
        for (var i = 0; i < patches; i++)
        {
            fPos[i] = new Vector3(packed[3 * i].X, packed[3 * i].Y, packed[3 * i].Z);
            var n = new Vector3(packed[3 * i + 1].X, packed[3 * i + 1].Y, packed[3 * i + 1].Z);
            fNrm[i] = n.LengthSquared() > 1e-12f ? Vector3.Normalize(n) : Vector3.UnitY;
            fCount[i] = 1;
            fNb[i] = new List<int>();
            for (var j = 0; j < FarNeighbours; j++) { var o = patchNb[i * FarNeighbours + j]; if (o >= 0 && o != i) fNb[i].Add(o); }
        }
        var patchOf = Enumerable.Range(0, patches).ToArray();   // each patch's node at the finer level
        var nodes = new List<Vector4>();
        var parents = new int[patches * PatchLevelCount];
        var neighbours = new List<int>();
        var memberStart = new List<uint> { 0 };
        var members = new List<uint>();
        var counts = new List<string>();
        for (var k = 1; k <= PatchLevelCount; k++)
        {
            var s = transportSpacing * (1 << k);
            var fn = fPos.Length;
            (int, int, int, int) Key(int i) => ((int)MathF.Floor(fPos[i].X / s), (int)MathF.Floor(fPos[i].Y / s), (int)MathF.Floor(fPos[i].Z / s), NormalAxis(fNrm[i]));
            var keys = new (int, int, int, int)[fn];
            for (var i = 0; i < fn; i++) keys[i] = Key(i);
            var root = Enumerable.Range(0, fn).ToArray();
            int Find(int x) { while (root[x] != x) { root[x] = root[root[x]]; x = root[x]; } return x; }
            for (var i = 0; i < fn; i++)
                foreach (var o in fNb[i])
                    if (keys[o] == keys[i]) { var a2 = Find(i); var b2 = Find(o); if (a2 != b2) root[a2] = b2; }
            var nodeOf = new int[fn];
            var byRoot = new Dictionary<int, int>();
            for (var i = 0; i < fn; i++) { var r = Find(i); if (!byRoot.TryGetValue(r, out var g)) { g = byRoot.Count; byRoot[r] = g; } nodeOf[i] = g; }
            var cn = byRoot.Count;
            var cPos = new Vector3[cn]; var cNrm = new Vector3[cn]; var cCount = new int[cn];
            for (var i = 0; i < fn; i++) { var g = nodeOf[i]; cPos[g] += fPos[i] * fCount[i]; cNrm[g] += fNrm[i] * fCount[i]; cCount[g] += fCount[i]; }
            var first = nodes.Count / 2;
            for (var g = 0; g < cn; g++)
            {
                cPos[g] /= cCount[g];
                cNrm[g] = cNrm[g].LengthSquared() > 1e-12f ? Vector3.Normalize(cNrm[g]) : Vector3.UnitY;
                nodes.Add(new Vector4(cPos[g], k)); nodes.Add(new Vector4(cNrm[g], cCount[g]));
            }
            // Members (patches) by node, and each patch's parent at this level.
            var byNode = new List<uint>[cn];
            for (var g = 0; g < cn; g++) byNode[g] = new List<uint>();
            for (var p = 0; p < patches; p++)
            {
                patchOf[p] = nodeOf[patchOf[p]];
                parents[p * PatchLevelCount + k - 1] = first + patchOf[p];
                byNode[patchOf[p]].Add((uint)p);
            }
            for (var g = 0; g < cn; g++) { members.AddRange(byNode[g]); memberStart.Add((uint)members.Count); }
            // Neighbours: the nodes the members' neighbours lie in, nearest first, facing within 60 degrees.
            var cNb = new List<int>[cn];
            for (var g = 0; g < cn; g++) cNb[g] = new List<int>();
            var sets = new HashSet<int>[cn];
            for (var g = 0; g < cn; g++) sets[g] = new HashSet<int>();
            for (var i = 0; i < fn; i++)
                foreach (var o in fNb[i]) { var a2 = nodeOf[i]; var b2 = nodeOf[o]; if (a2 != b2) { sets[a2].Add(b2); sets[b2].Add(a2); } }
            var lvlNeighbours = new int[cn * 8];
            for (var g = 0; g < cn; g++)
            {
                var near = sets[g].Where(o => Vector3.Dot(cNrm[o], cNrm[g]) > 0.5f).OrderBy(o => Vector3.DistanceSquared(cPos[o], cPos[g])).ToList();
                cNb[g] = sets[g].ToList();
                for (var j = 0; j < 8; j++) lvlNeighbours[g * 8 + j] = j < near.Count ? first + near[j] : -1;
            }
            neighbours.AddRange(lvlNeighbours);
            counts.Add(string.Create(CultureInfo.InvariantCulture, $"{s:0.##} m {cn:N0}"));
            fPos = cPos; fNrm = cNrm; fCount = cCount; fNb = cNb;
        }
        levelNodeCount = nodes.Count / 2;
        var nodeArr = nodes.ToArray();
        levelNodesBuffer = Own(device.CreateGpuBuffer(nodeArr.Length * 16, MemoryMarshal.AsBytes(nodeArr.AsSpan()), "sponza.patch-levels.nodes"));
        levelParentsBuffer = Own(device.CreateGpuBuffer(parents.Length * 4, MemoryMarshal.AsBytes(parents.AsSpan()), "sponza.patch-levels.parents"));
        var nb = neighbours.ToArray();
        levelNeighboursBuffer = Own(device.CreateGpuBuffer(nb.Length * 4, MemoryMarshal.AsBytes(nb.AsSpan()), "sponza.patch-levels.neighbours"));
        var ms = memberStart.ToArray();
        levelMemberStart = Own(device.CreateGpuBuffer(ms.Length * 4, MemoryMarshal.AsBytes(ms.AsSpan()), "sponza.patch-levels.member-start"));
        var mm = members.ToArray();
        levelMembers = Own(device.CreateGpuBuffer(mm.Length * 4, MemoryMarshal.AsBytes(mm.AsSpan()), "sponza.patch-levels.members"));
        levelLightBuffer = Own(device.CreateGpuBuffer(levelNodeCount * 32, name: "sponza.patch-levels.light"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] patch levels: {patches:N0} patches into {levelNodeCount:N0} nodes, joined along the surface ({string.Join(", ", counts)}) in {clock.Elapsed.TotalMilliseconds:0} ms."));
    }

    // The levels' light for the lights as they are (each node its patches' mean: incident per column + the sun's direct).
    private void LightPatchLevelsNow()
    {
        if (!PatchLevelsReady || sunNodePatchLight.Equals(default(GpuBufferHandle))) return;
        var key = (liveSunToward, liveSunColumn);
        if (levelLitFor == key) return;
        levelLitFor = key;
        var clock = Stopwatch.StartNew();
        var skyColumn = Math.Max(0, Array.IndexOf(sunNodeKinds, 2));
        var sunColumn = Math.Max(0, Array.IndexOf(sunNodeKinds, 1));
        var declared = patchLevelsInterface.Slots.Select(sl => sl.Name).Where(nm => nm is not null).ToHashSet();
        device.DispatchNow(new[] { new DispatchCommand(patchLevelsPipeline, (levelNodeCount + 63) / 64, 1, 1,
            new ShaderUniform[]
            {
                new("uLevels", new Vector4Uniform(new Vector4(levelNodeCount, sunNodeKinds.Length, skyColumn, sunColumn))),
                new("uIrr", new Vector4Uniform(new Vector4(EffectiveSunIrradiance, 0f))),
            },
            Array.Empty<ShaderTextureBinding>(),
            Buffers: new[]
            {
                new ShaderBufferBinding("LevelMemberStart", levelMemberStart),
                new ShaderBufferBinding("LevelMembers", levelMembers),
                new ShaderBufferBinding("SunNodePatchLight", sunNodePatchLight),
                new ShaderBufferBinding("PatchDirect", liveDirect.Equals(default(GpuBufferHandle)) ? levelMembers : liveDirect),
                new ShaderBufferBinding("LevelLight", levelLightBuffer),
            }.Where(b => declared.Contains(b.Name)).ToArray()) }, "patch-levels");
        device.WaitIdle();
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] patch levels: their light for this sun in {clock.Elapsed.TotalMilliseconds:0.0} ms."));
    }
}
