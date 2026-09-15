using Blix.Cooked;

namespace Blix.Assets;

// Engine-native baked font atlas. Cooked from a .font.json spec (which names a
// TTF and the pixel sizes to bake); loaded at runtime as a header read plus one
// memcpy per size, replacing stb_truetype's BakeFontBitmap — which rasterises
// every glyph at every requested size, on every launch, forever.
//
// File layout (little-endian), v1:
//
//   The shared Blix cooked preamble first -- see Blix.Cooked/CookPreamble.cs.
//   Then this format's own header:
//
//   offset  size  field
//   ---------------------------------
//   0       4     nameLen
//   4       n     name (UTF-8)
//   ...     4     sizeCount
//   For each size, sequentially:
//     pixelSize[4]  atlasWidth[4]  atlasHeight[4]
//     ascent[4]  descent[4]  lineGap[4]
//     glyphCount[4]
//     For each glyph: codepoint[2] atlasX[4] atlasY[4] atlasW[4] atlasH[4]
//                     offsetX[4] offsetY[4] advance[4]
//     alphaLen[4]  alpha[alphaLen]      single-channel coverage
//
// Alpha is stored uncompressed. An atlas is coverage, not colour: at the sizes
// this bakes it is tens to a few hundred KB, and a decompress at load would put
// back a cost the cook exists to remove.
public static class BlixFont
{
    public const uint Magic = 0x46584C42; // "BLXF" little-endian
    public const uint Version1 = 1;

    /// <summary>The recipe id the shipped font cook stamps.</summary>
    public const string ShippedRecipe = "fnt1";

    /// <summary>The font cook's own version — see BlixMesh.MeshRecipeVersion for why.</summary>
    public const uint ShippedRecipeVersion = 1;
}

public static class BlixFontWriter
{
    /// <param name="stamp">See <c>BlixMeshWriter.Write</c> — required, for the same reason.</param>
    public static void Write(string path, FontData font, in CookStamp stamp)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(font);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        CookPreamble.Write(fs, BlixFont.Magic, BlixFont.Version1, stamp);
        using var bw = new BinaryWriter(fs);

        var nameBytes = System.Text.Encoding.UTF8.GetBytes(font.Name);
        bw.Write(nameBytes.Length);
        bw.Write(nameBytes);
        bw.Write(font.Sizes.Count);

        foreach (var size in font.Sizes)
        {
            bw.Write(size.PixelSize);
            bw.Write(size.AtlasWidth);
            bw.Write(size.AtlasHeight);
            bw.Write(size.Ascent);
            bw.Write(size.Descent);
            bw.Write(size.LineGap);

            // Ordered, so two cooks of one source produce identical bytes. A dictionary's
            // enumeration order is not a promise, and the reproducibility check would have caught
            // it eventually — after blaming the cook.
            var glyphs = size.Glyphs.OrderBy(g => g.Key).ToArray();
            bw.Write(glyphs.Length);
            foreach (var (codepoint, g) in glyphs)
            {
                bw.Write((ushort)codepoint);
                bw.Write(g.AtlasX); bw.Write(g.AtlasY); bw.Write(g.AtlasW); bw.Write(g.AtlasH);
                bw.Write(g.OffsetX); bw.Write(g.OffsetY); bw.Write(g.Advance);
            }

            bw.Write(size.AlphaPixels.Length);
            bw.Write(size.AlphaPixels);
        }
    }
}

public static class BlixFontReader
{
    /// <summary>Reads a baked font, refusing anything that is not one by name.</summary>
    public static FontData Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        CookPreamble.Read(fs, path).Require(BlixFont.Magic, BlixFont.Version1, path, ".blixfont");
        return AssetImportException.Refusing(path, () => ReadBody(fs), ".blixfont");
    }

    private static FontData ReadBody(Stream fs)
    {
        using var br = new BinaryReader(fs);

        var name = System.Text.Encoding.UTF8.GetString(br.ReadBytes(br.ReadInt32()));
        var sizeCount = br.ReadInt32();
        var sizes = new List<FontSizeData>(sizeCount);

        for (var i = 0; i < sizeCount; i++)
        {
            var pixelSize = br.ReadSingle();
            var width = br.ReadInt32();
            var height = br.ReadInt32();
            var ascent = br.ReadSingle();
            var descent = br.ReadSingle();
            var lineGap = br.ReadSingle();

            var glyphCount = br.ReadInt32();
            var glyphs = new Dictionary<char, FontGlyph>(glyphCount);
            for (var g = 0; g < glyphCount; g++)
            {
                var codepoint = (char)br.ReadUInt16();
                glyphs[codepoint] = new FontGlyph(
                    br.ReadInt32(), br.ReadInt32(), br.ReadInt32(), br.ReadInt32(),
                    br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
            }

            var alpha = br.ReadBytes(br.ReadInt32());
            sizes.Add(new FontSizeData(pixelSize, width, height, alpha, ascent, descent, lineGap, glyphs));
        }

        return new FontData(name, sizes);
    }
}
