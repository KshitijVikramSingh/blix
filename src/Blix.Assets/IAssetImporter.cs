namespace Blix.Assets;

// Decodes a source file at `context.SourcePath` into a runtime intermediate
// type. Importers are stateless — register one instance per kind with the
// AssetDatabase and it gets reused for every asset of that kind.
//
// `Name` is the manifest dispatch key. Convention: `kind.format` (e.g.
// `texture.rgba8`, `static-mesh.obj`, `font.json`) so identical kinds with
// different formats can coexist without ambiguity.
public interface IAssetImporter<out TOutput>
{
    string Name { get; }

    TOutput Import(AssetImportContext context);
}
