using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace RTSGame.Simulation.Persistence;

/// <summary>
/// Writes simulation state to a stream, bit for bit.
/// </summary>
/// <remarks>
/// Binary rather than text, for one reason that matters and one that follows from it. A saved
/// career has to resume as the identical world — the determinism fingerprint is the acceptance
/// test, and it compares float bits — so a format that round-trips a float through a decimal
/// representation is a format that can lose the last bit and pass every eyeball test while doing
/// it. And because the state is plain data all the way down, whole arrays go out as bytes with no
/// per-field code at all, which is what keeps this file from growing every time a body gains a
/// field.
/// <para>
/// <see cref="Blob{T}"/> is the reason for the <c>unmanaged</c> constraint, and the constraint is
/// the point: the compiler refuses to let a reference into anything saved this way. That is the
/// same rule the determinism schema enforces at runtime, enforced earlier and harder.
/// </para>
/// </remarks>
internal sealed class WorldWriter : IDisposable
{
    private readonly BinaryWriter writer;

    public WorldWriter(Stream stream) =>
        writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

    public void Int(int value) => writer.Write(value);

    public void Long(long value) => writer.Write(value);

    public void Float(float value) => writer.Write(value);

    public void Bool(bool value) => writer.Write(value);

    public void Vector(Vector2 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
    }

    /// <summary>Writes a run of plain values as a length and then their bytes.</summary>
    public void Blob<T>(ReadOnlySpan<T> values) where T : unmanaged
    {
        writer.Write(values.Length);
        writer.Write(MemoryMarshal.AsBytes(values));
    }

    public void Dispose() => writer.Dispose();
}

/// <summary>Reads back what <see cref="WorldWriter"/> wrote.</summary>
internal sealed class WorldReader : IDisposable
{
    private readonly BinaryReader reader;

    public WorldReader(Stream stream) =>
        reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

    public int Int() => reader.ReadInt32();

    public long Long() => reader.ReadInt64();

    public float Float() => reader.ReadSingle();

    public bool Bool() => reader.ReadBoolean();

    public Vector2 Vector() => new(reader.ReadSingle(), reader.ReadSingle());

    /// <summary>Reads a run of plain values into a fresh array.</summary>
    public T[] Blob<T>() where T : unmanaged
    {
        var values = new T[reader.ReadInt32()];
        Fill(values.AsSpan());
        return values;
    }

    /// <summary>
    /// Reads a run of plain values into somewhere that already exists, which must be exactly the
    /// length that was written.
    /// </summary>
    /// <remarks>
    /// For the arrays a component allocates in its constructor from the world's dimensions — the
    /// terrain's heights, a region table. Requiring the length to match is the check that a save
    /// from a differently sized world is refused here rather than half-loaded.
    /// </remarks>
    public void Blob<T>(Span<T> into) where T : unmanaged
    {
        var length = reader.ReadInt32();
        if (length != into.Length)
        {
            throw new InvalidDataException(
                $"Saved run of {length} {typeof(T).Name} does not fit {into.Length}. The save was " +
                "made by a world of different dimensions.");
        }

        Fill(into);
    }

    private void Fill<T>(Span<T> into) where T : unmanaged
    {
        var bytes = MemoryMarshal.AsBytes(into);
        var read = 0;
        while (read < bytes.Length)
        {
            // Streams are allowed to hand back less than was asked for, and a file stream reading
            // a megabyte of raster is exactly where that shows up.
            var got = reader.Read(bytes[read..]);
            if (got <= 0) throw new EndOfStreamException("The save ends mid-value.");
            read += got;
        }
    }

    public void Dispose() => reader.Dispose();
}
