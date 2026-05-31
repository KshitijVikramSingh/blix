using Blix.Assets;
using Blix.Geometry;
using Blix.Graphics;

namespace Blix.Render;

// Geometry bundler: packs N source primitives into ONE shared vertex buffer +
// per-index-width (u16/u32) index buffers, returning a per-primitive sub-range
// descriptor for each. The engine's "bundling" half of the asset pipeline —
// pure geometry, no material/pipeline knowledge (that's the game's). It does
// not draw or own a frame; the caller bundles once at load, then composes its
// own draw groups / indirect fill / passes over the returned BundledMeshes.
//
// Indices stay primitive-local; a draw rebases them with vertexOffset =
// BaseVertex (vkCmdDrawIndexed / glDrawElementsBaseVertex), so u16 indices keep
// working even though the shared VB spans far more than 65 k vertices.
//
// Order-preserving: BundledMeshes come back in input order, so a caller that
// pre-sorts its inputs by (pipeline, material, …) gets contiguous runs it can
// group itself.

// One source primitive to bundle. VertexBytes is its raw vertex block (the
// caller's layout); Lods[0] is full detail (coarser after), each carrying its
// world-space geometric error for screen-space-error LOD selection.
public sealed record MeshGeometryInput(
    byte[] VertexBytes, int VertexCount, IReadOnlyList<MeshLod> Lods, Bounds3 Bounds);

// One bundled primitive: a sub-range of the shared buffers. No material or
// pipeline — the caller attaches those. BaseVertex + LodFirstIndex[lod] index
// the shared VB / width-matched IB.
public sealed record BundledMesh(
    bool IndicesAreU32,
    int BaseVertex,
    int[] LodFirstIndex,
    int[] LodIndexCounts,
    float[] LodErrors,
    Bounds3 Bounds);

// CPU-packed result, before any GPU upload — pure data, so it's unit-testable
// without a device.
public sealed record PackedGeometry(
    byte[] VertexBytes,
    ushort[] Indices16,
    uint[] Indices32,
    int VertexCount,
    IReadOnlyList<BundledMesh> Meshes);

// The uploaded bundle: shared GPU buffers + the per-primitive sub-ranges.
// Indices16/Indices32 are default (invalid) when no primitive of that width
// was bundled.
public sealed record MeshBundle(
    VertexBufferHandle Vertices,
    IndexBufferHandle Indices16,
    IndexBufferHandle Indices32,
    int VertexCount,
    IReadOnlyList<BundledMesh> Meshes);

public static class MeshBundler
{
    // Pure CPU pack: concatenate every primitive's vertex block into one buffer
    // (each at its BaseVertex) and its LOD index lists into the width-matched
    // index buffer. Indices are copied verbatim (primitive-local). No GPU work.
    public static PackedGeometry Pack(IReadOnlyList<MeshGeometryInput> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        long vertexByteTotal = 0;
        var vertexTotal = 0;
        var u16Total = 0;
        var u32Total = 0;
        foreach (var it in items)
        {
            vertexByteTotal += it.VertexBytes.Length;
            vertexTotal += it.VertexCount;
            var isU32 = it.Lods[0].Indices32 is not null;
            foreach (var lod in it.Lods)
            {
                if (isU32) u32Total += lod.Indices32!.Length;
                else u16Total += lod.Indices16!.Length;
            }
        }

        var vbytes = new byte[vertexByteTotal];
        var u16 = new ushort[u16Total];
        var u32 = new uint[u32Total];
        var meshes = new BundledMesh[items.Count];
        int vByteCursor = 0, vCursor = 0, i16 = 0, i32c = 0;

        for (var m = 0; m < items.Count; m++)
        {
            var it = items[m];
            var baseVertex = vCursor;
            Buffer.BlockCopy(it.VertexBytes, 0, vbytes, vByteCursor, it.VertexBytes.Length);
            vByteCursor += it.VertexBytes.Length;
            vCursor += it.VertexCount;

            var isU32 = it.Lods[0].Indices32 is not null;
            var firstIndex = new int[it.Lods.Count];
            var counts = new int[it.Lods.Count];
            var errors = new float[it.Lods.Count];
            for (var l = 0; l < it.Lods.Count; l++)
            {
                var lod = it.Lods[l];
                errors[l] = lod.Error;
                if (isU32)
                {
                    firstIndex[l] = i32c;
                    lod.Indices32!.CopyTo(u32, i32c);
                    i32c += lod.Indices32.Length;
                    counts[l] = lod.Indices32.Length;
                }
                else
                {
                    firstIndex[l] = i16;
                    lod.Indices16!.CopyTo(u16, i16);
                    i16 += lod.Indices16.Length;
                    counts[l] = lod.Indices16.Length;
                }
            }
            meshes[m] = new BundledMesh(isU32, baseVertex, firstIndex, counts, errors, it.Bounds);
        }

        return new PackedGeometry(vbytes, u16, u32, vertexTotal, meshes);
    }

    // Pack + create the shared GPU buffers. `name` prefixes the buffer debug names.
    public static MeshBundle Bundle(
        IReadOnlyList<MeshGeometryInput> items, VertexLayout layout, IGraphicsDevice device, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        var packed = Pack(items);

        var vb = device.CreateVertexBuffer(
            new VertexBufferData(
                new VertexBufferDescription(layout, packed.VertexCount, GraphicsBufferUsage.Static),
                packed.VertexBytes),
            $"{name}.vb");
        var ib16 = packed.Indices16.Length > 0
            ? device.CreateIndexBuffer(packed.Indices16, name: $"{name}.ib16")
            : default;
        var ib32 = packed.Indices32.Length > 0
            ? device.CreateIndexBuffer(packed.Indices32, name: $"{name}.ib32")
            : default;

        return new MeshBundle(vb, ib16, ib32, packed.VertexCount, packed.Meshes);
    }
}
