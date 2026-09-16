namespace Blix.Assets;

public sealed class AssetImportContext
{
    public AssetImportContext(AssetId assetId, string sourcePath, bool flipTextureV = false, bool includeTangents = false, bool includeColour = false)
    {
        AssetId = assetId;
        SourcePath = sourcePath;
        FlipTextureV = flipTextureV;
        IncludeTangents = includeTangents;
        IncludeColour = includeColour;
    }

    public AssetId AssetId { get; }

    public string SourcePath { get; }

    // When true, static-mesh import produces the tangent-bearing vertex layout
    // (VertexPosition3NormalTangentTexture), forwarding the glTF TANGENT
    // attribute (world-transformed, handedness preserved) so renderers get a
    // real per-vertex TBN instead of a screen-space-derivative one. Falls back
    // to a normal-derived tangent for primitives without a TANGENT attribute.
    // Default off keeps the lean position/normal/uv layout for other consumers.
    public bool IncludeTangents { get; }

    // When true, static-mesh import reads the glTF COLOR_0 attribute into the
    // 36-byte VertexPosition3NormalTextureColor layout instead of discarding it.
    //
    // Off by default and it must stay that way: a vertex layout is a contract with
    // a pipeline that was already created, and Vulkan walks a vertex buffer at the
    // stride the PIPELINE declares. Six applications in this tree pin
    // VertexPosition3NormalTexture in a pipeline of their own, so a default-on flag
    // would hand them 36-byte vertices read at 32 — no crash, no compile error, just
    // a mesh that comes out wrong. The caller that opts in is the caller that has a
    // pipeline to match, which today is the studio and nothing else.
    //
    // Mutually exclusive with IncludeTangents, which throws rather than dropping one.
    public bool IncludeColour { get; }

    // When true, importers flip the texture V coordinate (V -> 1-V) as they
    // build vertex data, canonicalising a bottom-up (OpenGL-authored) source
    // to a top-down (Vulkan / D3D) sampling origin. Granular and per-import on
    // purpose: spec-compliant glTF stays untouched at the default (false), and
    // a scene can mix bottom-up and top-down assets by setting it per import
    // call — it is NOT a project-wide or backend-wide switch. The decision is
    // baked into the vertex buffer at build time, so it's a no-op for already-
    // cooked meshes (cook with the same flag to bake it).
    public bool FlipTextureV { get; }
}
