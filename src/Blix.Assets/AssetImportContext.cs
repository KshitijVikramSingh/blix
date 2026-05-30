namespace Blix.Assets;

public sealed class AssetImportContext
{
    public AssetImportContext(AssetId assetId, string sourcePath, bool flipTextureV = false, bool includeTangents = false)
    {
        AssetId = assetId;
        SourcePath = sourcePath;
        FlipTextureV = flipTextureV;
        IncludeTangents = includeTangents;
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
