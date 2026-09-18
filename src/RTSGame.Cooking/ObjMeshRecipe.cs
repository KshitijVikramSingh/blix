using Blix.Assets;
using Blix.Cooked;
using Blix.Graphics;

namespace RTSGame.Cooking;

/// <summary>
/// An <c>.obj</c> to a <c>.blixmesh</c> — and the first recipe declared outside <c>Blix.*</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It exists to answer the half of K-G that the font atlas did not.</b> That stage's claim is
/// that <i>a PROJECT declares a recipe and it costs the recipe</i>. The font recipe proved a fourth
/// BLIX format was cheap; every one of the four still lived in <c>Blix.Recipes</c>, so the part
/// about a project was never tried. This is the try, and what it cost is one file and one
/// <c>ProjectReference</c> to <c>Blix.Cooked</c> — which is what
/// <see cref="RecipeAttribute"/> says it should cost.
/// </para>
/// <para>
/// <b>And it is a real gap rather than a demonstration.</b> Nothing claims <c>.obj</c>: no recipe
/// consumes it, and <c>blix check --cooked</c> answers "nothing here that this judges" on a
/// directory of them, so fourteen meshes are invisible to the cook instrument entirely. RTSGame is
/// the only consumer of that format in the tree, which is exactly why the recipe belongs here and
/// not in the engine — Blix owns the <c>ObjImporter</c> capability and the <c>.blixmesh</c> format,
/// and the decision to cook THESE files is the game's.
/// </para>
/// <para>
/// <b>The three-way split, kept.</b> The capability — parsing Wavefront OBJ — is
/// <see cref="ObjImporter"/> and is untouched. The format is <c>BlixMesh</c>, engine, because the
/// runtime reads it. What is here is only the decision: this source, this layout, that format. It
/// calls the engine and writes the engine's format and parses nothing of its own.
/// </para>
/// <para>
/// <b>SourceRequired, and honestly so.</b> The <c>.mtl</c> beside an <c>.obj</c> carries the
/// material colours, and those are cooked: each part becomes a primitive and a material carrying
/// its name and base colour. What stays in the source is what <c>WavefrontParts</c> does not
/// surface — texture images above all — so the flag is narrowed to
/// <c>SourceRequiredForImagesOnly</c> rather than cleared — so the source stays a runtime
/// dependency for the same reason the glTF recipe's does, and the flag says so where a tool can
/// see it rather than where a reader has to infer it.
/// </para>
/// </remarks>
public static class ObjMeshRecipe
{
    /// <summary>Bumped when the written bytes change for an unchanged source.</summary>
    // v3: the material table (.blixmesh v5) — each part's colour travels with the geometry.
    // v4: `recenter` is stamped, because the loader now refuses a file cooked with the other value.
    // v5: `recenter` is an OPTION, and the flags stop claiming a debt this kit does not owe.
    public const int Version = 5;

    /// <summary>What <c>blix cook</c> calls, and what the index finds without loading this assembly.</summary>
    [Recipe("omsh",
        Produces = ".blixmesh",
        Consumes = ".obj",
        Version = Version,
        Summary = "a Wavefront OBJ to .blixmesh geometry (RTSGame's nature kit)")]
    public static CookOutcome Cook(CookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // <b>One primitive per OBJ part, and that is the whole correctness of this recipe.</b>
        // The first version imported the file as ONE merged mesh, which produced thirteen cooked
        // files that no consumer could use: RTSGame reads this kit through WavefrontParts and
        // colours each part by its material name, so a merged mesh throws away the only structure
        // the reader is looking for. The cooked form has to be substitutable for the source, and
        // "smaller and faster but a different shape" is not substitutable.
        //
        // Asked for the SOURCE explicitly. An importer allowed to find a cooked sibling would, on
        // the second run, cook its own output — the one mistake a recipe can make that still looks
        // like it worked.
        //
        // recenter is left at its default because the CONSUMER leaves it at its default. A cook
        // that centres differently from the load it replaces moves every model a few centimetres
        // and does it only on machines that have cooked.
        // <b>ImportSource, not Import — a recipe must never read through the cooked path.</b>
        // WavefrontParts prefers a .blixmesh sibling now, which is right for a game and would have
        // this recipe consume its own previous output and re-cook that. Caught here only because a
        // format version changed in the same commit; at a matching version it would have looked
        // like it worked.
        // <b>recenter is the CALLER's, because two consumers in this tree disagree about it.</b>
        // WavefrontParts centres by default and SettlementArt loads Villager.obj through
        // ObjImporter with RecenterToOrigin = false, so a recipe that hardcodes one of them can
        // only ever cook for one of them — the other gets its cooked file refused by the loader's
        // settings guard, which is a worse outcome than not cooking it at all.
        //
        // It arrives through the BlixCook item's Options, which Directory.Build.targets has
        // threaded to recipes since K-D and which nothing had used. A mechanism with no consumer
        // is a mechanism nobody has checked.
        var recenter = request.Flag("recenter", fallback: true);
        var parts = WavefrontParts.ImportSource(request.SourcePath, recenter);
        if (parts.Count == 0) return CookOutcome.Skipped("no parts in this .obj");

        // BlixMeshFile carries ONE layout for every primitive, so a file whose parts disagree
        // cannot be written rather than written wrongly. Wavefront has no way to express this
        // today; it is checked because the format's invariant is real, not because the parser is
        // suspected.
        var layout = parts[0].Mesh.Layout;
        foreach (var part in parts)
        {
            if (part.Mesh.Layout.Stride != layout.Stride)
            {
                // Thrown rather than returned: CookOutcome says written-or-skipped, and this is
                // neither. A file that cannot be represented must fail the build, not be recorded
                // as a skip a reader would take for "already up to date".
                throw new InvalidDataException(
                    $"{request.SourcePath}: parts disagree on vertex layout " +
                    $"({layout.Stride}B vs {part.Mesh.Layout.Stride}B) and .blixmesh carries one.");
            }
        }

        // <b>One material per part, carrying the colour out of the .mtl.</b> This is the half of
        // the kit its only consumer actually reads: SettlementArt classifies each part by its
        // material colour and name. Cooking the geometry and leaving the colour in a sibling file
        // would have produced an artifact that is faster and still not substitutable for the source.
        //
        // Materials are indexed 1:1 with parts rather than deduplicated by name. Two parts sharing
        // a material is possible and the table would be one entry shorter; keeping the mapping
        // positional keeps MaterialIndex readable straight off the primitive without a second
        // lookup, and these files have one or two parts.
        var materials = parts.Select((part, i) => new BlixMeshMaterial(
            Name: part.Material,
            BaseColorFactor: part.Color,
            BaseColorTexCoord: 0,
            // Wavefront has no PBR metallic-roughness model, so these are the glTF defaults for a
            // material that declares nothing rather than a guess at what the author meant. The kit
            // is shaded as foliage by the consumer, which supplies its own surface class.
            MetallicFactor: 0f,
            RoughnessFactor: 1f,
            OcclusionStrength: 1f,
            EmissiveFactor: System.Numerics.Vector3.Zero,
            EmissiveStrength: 1f,
            AlphaMode: BlixMesh.AlphaOpaque,
            AlphaCutoff: 0.5f,
            DoubleSided: false,
            TransmissionFactor: 0f,
            // A .mtl has no KHR_materials_* anything, so every field takes its spec default.
            Extensions: BlixMaterialExtensions.None)).ToArray();

        var primitives = parts.Select((part, i) => new BlixMeshPrimitive(
            // <b>The part's MATERIAL name, not the mesh's.</b> It is what the reader matches on to
            // find the colour in the sibling .mtl, so it is the one string that has to survive.
            Name: part.Material,
            Layout: part.Mesh.Layout,
            // Its own entry in the table above — positional, so this is the part's own index.
            MaterialIndex: i,
            Bounds: part.Mesh.Bounds,
            VertexCount: part.Mesh.VertexCount,
            VertexBytes: part.Mesh.VertexBytes,
            IndexFormat: part.Mesh.IndexFormat,
            Lods: new[] { new BlixMeshLod(part.Mesh.Indices, part.Mesh.Indices32) })).ToArray();

        var stamp = CookStamp.Of(
            "omsh", Version, request.SourcePath, request.OutputPath,
            // recenter is recorded because it CHANGES THE VERTICES and the reader checks it:
            // WavefrontParts centres by default and ObjImporter can be told not to, so a cooked
            // file is only valid for the setting it was made with. Stamping it is what lets the
            // loader refuse rather than silently hand back geometry shifted by half a bounding box.
            $"layout={layout.Stride}B parts={primitives.Length} recenter={(recenter ? 1 : 0)}",
            // <b>Nothing is owed when the material library names no texture.</b> This declared
            // SourceRequired | SourceRequiredForImagesOnly unconditionally, on a kit whose .mtl
            // files contain no map_ line at all — so every cooked file claimed its source was
            // needed for image bytes that do not exist. That is exactly the overstatement K-F was
            // written to remove, reappearing in the one recipe that is not Blix's.
            //
            // Checked rather than assumed, because a .mtl that DOES name a texture is a real case
            // and WavefrontParts does not surface it: the cooked form would then be genuinely
            // incomplete, and saying so is the flag's whole job.
            ReferencesTextures(request.SourcePath)
                ? CookedFlags.SourceRequired | CookedFlags.SourceRequiredForImagesOnly
                : CookedFlags.None);

        BlixMeshWriter.Write(
            request.OutputPath, new BlixMeshFile(primitives, materials), stamp);

        return CookOutcome.Written(
            $"{primitives.Length} parts, {primitives.Sum(p => p.VertexCount)} vertices, " +
            $"{primitives.Sum(p => p.Lods[0].IndexCount) / 3} triangles, {layout.Stride}B layout");
    }

    /// <summary>True when any material library this OBJ names references a texture map.</summary>
    /// <remarks>
    /// <b>Reads the .mtl rather than the parts, because the parts cannot answer it.</b>
    /// <see cref="WavefrontParts"/> surfaces a material's NAME and its diffuse COLOUR and nothing
    /// else, so a cooked file built from it silently drops any map_Kd the author wrote. Whether
    /// that happened is the difference between a cooked mesh that stands alone and one that still
    /// needs its source, which is precisely what the flags exist to record.
    /// <para>
    /// Unreadable or missing libraries answer TRUE — the conservative direction. Claiming a debt
    /// that turns out not to exist costs a load report line; denying one that does exist ships an
    /// artifact that quietly lost a texture.
    /// </para>
    /// </remarks>
    private static bool ReferencesTextures(string objPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(objPath)) ?? ".";
        var libraries = new List<string>();

        try
        {
            foreach (var line in File.ReadLines(objPath))
            {
                if (!line.StartsWith("mtllib ", StringComparison.Ordinal)) continue;
                foreach (var name in line[7..].Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    libraries.Add(Path.Combine(dir, name.Trim()));
                }
            }
        }
        catch (IOException)
        {
            return true;
        }

        if (libraries.Count == 0) return false;

        foreach (var library in libraries)
        {
            if (!File.Exists(library)) return true;
            try
            {
                foreach (var line in File.ReadLines(library))
                {
                    if (line.TrimStart().StartsWith("map_", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (IOException)
            {
                return true;
            }
        }

        return false;
    }
}
