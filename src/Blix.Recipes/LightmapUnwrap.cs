using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Recipes;

/// <summary>Unwraps a triangle mesh for a lightmap with xatlas: charts, and their packing into an atlas at a world
/// density.</summary>
/// <remarks>
/// xatlas splits vertices along chart seams, so its output has its own vertices: each names the input vertex it copies
/// (<see cref="Result.Xref"/>) and the chart it belongs to. Positions are taken in world units, so
/// <c>texelsPerUnit</c> is texels per metre.
/// </remarks>
public static unsafe class LightmapUnwrap
{
    /// <param name="Uv">Each output vertex's atlas coordinate, 0-1 over <see cref="Width"/> x <see cref="Height"/>.</param>
    /// <param name="Xref">Each output vertex's input vertex.</param>
    /// <param name="VertexChart">Each output vertex's chart (atlas-wide index).</param>
    /// <param name="Indices">The output triangles, over the output vertices (one per input triangle, in order).</param>
    /// <param name="ChartTypes">Charts of each <see cref="XatlasNative.ChartType"/>.</param>
    public sealed record Result(
        Vector2[] Uv, int[] Xref, int[] VertexChart, uint[] Indices,
        int Charts, int[] ChartTypes, int Atlases, int Width, int Height, float Utilization);

    /// <summary>Unwraps one mesh. <paramref name="padding"/> texels between charts (bilinear lookups need at least one).</summary>
    public static Result Unwrap(Vector3[] positions, uint[] indices, float texelsPerUnit, int padding = 2, int maxResolution = 8192)
    {
        if (!XatlasNative.Available)
            throw new InvalidOperationException($"The xatlas native ({XatlasNative.FileName}) is missing: the BuildXatlas target in Blix.Recipes.csproj did not produce it.");
        var atlas = XatlasNative.xatlasCreate();
        try
        {
            fixed (Vector3* p = positions)
            fixed (uint* idx = indices)
            {
                XatlasNative.MeshDecl decl;
                XatlasNative.xatlasMeshDeclInit(&decl);
                decl.VertexPositionData = p;
                decl.VertexPositionStride = (uint)sizeof(Vector3);
                decl.VertexCount = (uint)positions.Length;
                decl.IndexData = idx;
                decl.IndexCount = (uint)indices.Length;
                decl.IndexFormat = XatlasNative.IndexFormat.UInt32;
                var error = XatlasNative.xatlasAddMesh(atlas, &decl, 1);
                if (error != 0)
                    throw new InvalidOperationException($"xatlas refused the mesh: {Marshal.PtrToStringAnsi(XatlasNative.xatlasAddMeshErrorString(error))}");
                XatlasNative.xatlasAddMeshJoin(atlas);
                XatlasNative.ChartOptions chart;
                XatlasNative.xatlasChartOptionsInit(&chart);
                XatlasNative.PackOptions pack;
                XatlasNative.xatlasPackOptionsInit(&pack);
                pack.TexelsPerUnit = texelsPerUnit;
                pack.Padding = (uint)padding;
                pack.MaxChartSize = (uint)maxResolution;
                pack.Bilinear = 1;
                XatlasNative.xatlasGenerate(atlas, &chart, &pack);
            }
            var a = *atlas;
            var mesh = a.Meshes[0];
            var uv = new Vector2[mesh.VertexCount];
            var xref = new int[mesh.VertexCount];
            var vertexChart = new int[mesh.VertexCount];
            for (var v = 0; v < mesh.VertexCount; v++)
            {
                var vert = mesh.VertexArray[v];
                uv[v] = new Vector2(vert.U / Math.Max(1u, a.Width), vert.V / Math.Max(1u, a.Height));
                xref[v] = (int)vert.Xref;
                vertexChart[v] = vert.ChartIndex;
            }
            var outIndices = new uint[mesh.IndexCount];
            for (var i = 0; i < mesh.IndexCount; i++) outIndices[i] = mesh.IndexArray[i];
            var types = new int[5];
            for (var c = 0; c < mesh.ChartCount; c++) types[(int)mesh.ChartArray[c].Type]++;
            var utilization = 0f;
            for (var i = 0; i < a.AtlasCount; i++) utilization += a.Utilization[i];
            return new Result(uv, xref, vertexChart, outIndices, (int)mesh.ChartCount, types, (int)a.AtlasCount,
                (int)a.Width, (int)a.Height, a.AtlasCount > 0 ? utilization / a.AtlasCount : 0f);
        }
        finally
        {
            XatlasNative.xatlasDestroy(atlas);
        }
    }
}
