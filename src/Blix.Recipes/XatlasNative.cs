using System.Runtime.InteropServices;

namespace Blix.Recipes;

// P/Invoke into vendored xatlas (third_party/xatlas, built by the BuildXatlas MSBuild target into blix_xatlas with
// its C API; NativeLibraries names the file). Cook-time only: lightmap unwrapping. The layouts below mirror
// xatlas_c.h field for field; C's bool is one byte.
public static unsafe class XatlasNative
{
    private const string Lib = "blix_xatlas";

    static XatlasNative() => NativeLibraries.Ensure();

    /// <summary>Is the native unwrapper present?</summary>
    public static bool Available => NativeLibraries.Present(Lib);

    /// <summary>The filename this platform's loader will look for.</summary>
    public static string FileName => NativeLibraries.FileName(Lib);

    public enum ChartType { Planar, Ortho, Lscm, Piecewise, Invalid }

    public enum IndexFormat { UInt16, UInt32 }

    [StructLayout(LayoutKind.Sequential)]
    public struct Chart
    {
        public uint* FaceArray;
        public uint AtlasIndex;
        public uint FaceCount;
        public ChartType Type;
        public uint Material;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Vertex
    {
        public int AtlasIndex;
        public int ChartIndex;
        public float U, V;
        public uint Xref;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Mesh
    {
        public Chart* ChartArray;
        public uint* IndexArray;
        public Vertex* VertexArray;
        public uint ChartCount;
        public uint IndexCount;
        public uint VertexCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Atlas
    {
        public uint* Image;
        public Mesh* Meshes;
        public float* Utilization;
        public uint Width;
        public uint Height;
        public uint AtlasCount;
        public uint ChartCount;
        public uint MeshCount;
        public float TexelsPerUnit;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MeshDecl
    {
        public void* VertexPositionData;
        public void* VertexNormalData;
        public void* VertexUvData;
        public void* IndexData;
        public byte* FaceIgnoreData;
        public uint* FaceMaterialData;
        public byte* FaceVertexCount;
        public uint VertexCount;
        public uint VertexPositionStride;
        public uint VertexNormalStride;
        public uint VertexUvStride;
        public uint IndexCount;
        public int IndexOffset;
        public uint FaceCount;
        public IndexFormat IndexFormat;
        public float Epsilon;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ChartOptions
    {
        public void* ParamFunc;
        public float MaxChartArea;
        public float MaxBoundaryLength;
        public float NormalDeviationWeight;
        public float RoundnessWeight;
        public float StraightnessWeight;
        public float NormalSeamWeight;
        public float TextureSeamWeight;
        public float MaxCost;
        public uint MaxIterations;
        public byte UseInputMeshUvs;
        public byte FixWinding;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PackOptions
    {
        public uint MaxChartSize;
        public uint Padding;
        public float TexelsPerUnit;
        public uint Resolution;
        public byte Bilinear;
        public byte BlockAlign;
        public byte BruteForce;
        public byte CreateImage;
        public byte RotateChartsToAxis;
        public byte RotateCharts;
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern Atlas* xatlasCreate();
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void xatlasDestroy(Atlas* atlas);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern int xatlasAddMesh(Atlas* atlas, MeshDecl* decl, uint meshCountHint);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void xatlasAddMeshJoin(Atlas* atlas);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void xatlasGenerate(Atlas* atlas, ChartOptions* chartOptions, PackOptions* packOptions);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void xatlasMeshDeclInit(MeshDecl* decl);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void xatlasChartOptionsInit(ChartOptions* options);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern void xatlasPackOptionsInit(PackOptions* options);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] public static extern IntPtr xatlasAddMeshErrorString(int error);
}
