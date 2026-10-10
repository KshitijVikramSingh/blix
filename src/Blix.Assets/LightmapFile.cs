using System.Numerics;
using System.Runtime.InteropServices;

namespace Blix.Assets;

/// <summary>A cooked mesh's lightmap unwrap (<c>.blixlightmap</c>, beside its <c>.blixmesh</c>; <c>blix cook lightmap
/// --write</c>): for each primitive it unwraps, the vertices the unwrap's seams split, each one's texel in the scene's
/// atlas, and the primitive's LOD chain over those vertices.</summary>
/// <remarks>
/// <para>
/// A separate file rather than a channel of the mesh while the representation is an experiment: the mesh's own
/// vertices and LOD chains stay as they are, and a reader that wants the lightmap swaps them for these.
/// </para>
/// <para>
/// <see cref="Entry.Xref"/> names, for each split vertex, the mesh vertex it copies -- a reader gathers the mesh's
/// vertex bytes through it. <see cref="Entry.Lods"/> index the split vertices; their chain was simplified with every
/// chart border locked, so no triangle at any level straddles two charts. Texels are in the scene atlas
/// (<see cref="Width"/> x <see cref="Height"/>, shared by every file of the scene), texel units.
/// </para>
/// </remarks>
public sealed record LightmapFile(int Width, int Height, float BaseTexelCm, IReadOnlyList<LightmapFile.Entry> Entries)
{
    public sealed record Lod(uint[] Indices, float Error);

    /// <param name="Mesh">The mesh's index in the file.</param>
    /// <param name="Primitive">The primitive's index in that mesh.</param>
    public sealed record Entry(int Mesh, int Primitive, int[] Xref, Vector2[] Texels, Lod[] Lods);

    private const string Magic = "blix-lightmap";
    private const int Version = 1;

    public static void Write(string path, LightmapFile file)
    {
        var temp = path + ".partial";
        using (var stream = new BufferedStream(File.Create(temp), 1 << 20))
        using (var w = new BinaryWriter(stream))
        {
            w.Write(Magic); w.Write(Version);
            w.Write(file.Width); w.Write(file.Height); w.Write(file.BaseTexelCm);
            w.Write(file.Entries.Count);
            foreach (var e in file.Entries)
            {
                w.Write(e.Mesh); w.Write(e.Primitive);
                w.Write(e.Xref.Length);
                w.Write(MemoryMarshal.AsBytes(e.Xref.AsSpan()));
                w.Write(MemoryMarshal.AsBytes(e.Texels.AsSpan()));
                w.Write(e.Lods.Length);
                foreach (var l in e.Lods)
                {
                    w.Write(l.Error);
                    w.Write(l.Indices.Length);
                    w.Write(MemoryMarshal.AsBytes(l.Indices.AsSpan()));
                }
            }
        }
        File.Move(temp, path, overwrite: true);
    }

    public static LightmapFile Read(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        using var r = new BinaryReader(stream);
        if (r.ReadString() != Magic) throw new InvalidDataException($"{path} is not a .blixlightmap.");
        var version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"{path} is lightmap version {version}; this reader is {Version}. Re-cook it (blix cook lightmap --write).");
        int width = r.ReadInt32(), height = r.ReadInt32();
        var baseCm = r.ReadSingle();
        var count = r.ReadInt32();
        var entries = new List<Entry>(count);
        for (var i = 0; i < count; i++)
        {
            int mesh = r.ReadInt32(), primitive = r.ReadInt32();
            var n = r.ReadInt32();
            var xref = new int[n];
            Fill(r, MemoryMarshal.AsBytes(xref.AsSpan()));
            var texels = new Vector2[n];
            Fill(r, MemoryMarshal.AsBytes(texels.AsSpan()));
            var lods = new Lod[r.ReadInt32()];
            for (var l = 0; l < lods.Length; l++)
            {
                var error = r.ReadSingle();
                var indices = new uint[r.ReadInt32()];
                Fill(r, MemoryMarshal.AsBytes(indices.AsSpan()));
                lods[l] = new Lod(indices, error);
            }
            entries.Add(new Entry(mesh, primitive, xref, texels, lods));
        }
        return new LightmapFile(width, height, baseCm, entries);
    }

    private static void Fill(BinaryReader r, Span<byte> bytes)
    {
        var read = 0;
        while (read < bytes.Length)
        {
            var got = r.Read(bytes[read..]);
            if (got == 0) throw new EndOfStreamException();
            read += got;
        }
    }
}
