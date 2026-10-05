using System.Numerics;

namespace Blix.Geometry;

/// <summary>Where the probes of a camera-relative clipmap are, and where each one lives in the atlas: the contract
/// Blix.Shaders' probe_clipmap.glsl mirrors.</summary>
/// <remarks>
/// <para>
/// <see cref="LevelCount"/> levels of the same <see cref="Dims"/> probes, level <c>l</c> spaced
/// <c>BaseSpacing * 2^l</c> apart, so each level covers twice the extent of the one inside it at half the density.
/// A probe sits at the centre of a world cell of its level's spacing, cell <c>c</c> at <c>(c + 0.5) * spacing</c>.
/// Each level's block of cells is centred on the camera and snapped to whole cells (<see cref="Follow"/>), so a
/// camera moving within a cell moves nothing.
/// </para>
/// <para>
/// A cell's slot is its coordinate modulo <see cref="Dims"/>, per axis: when the block scrolls, every cell still in
/// it keeps its slot and only the slab that entered lands in the slots the slab that left gave up. The atlas holds a
/// tile per slot: levels side by side in x, and within a level x across, y then z down (as the bounds-sized
/// atlas did), each tile <see cref="TileTexels"/> square.
/// </para>
/// <para>
/// A point is looked up at the finest level whose interior holds its whole trilinear neighbourhood, blending toward
/// the next level as it nears that interior's edge (<see cref="Locate"/>); beyond the coarsest level there is
/// nothing.
/// </para>
/// </remarks>
public sealed class ProbeClipmap
{
    public const int TileTexels = 8;

    /// <summary>The largest cell index, either sign, a slot is computed for: what the shader's integer wrap holds exactly.</summary>
    public const int MaxCell = 1 << 22;

    public ProbeClipmap(int levelCount, Int3 dims, float baseSpacing, float blendProbes = 2f)
    {
        if (levelCount < 1) throw new ArgumentOutOfRangeException(nameof(levelCount));
        if (dims.X < 4 || dims.Y < 4 || dims.Z < 4) throw new ArgumentOutOfRangeException(nameof(dims), "a level needs at least 4 probes per axis.");
        if (!(baseSpacing > 0f)) throw new ArgumentOutOfRangeException(nameof(baseSpacing));
        if (!(blendProbes >= 0f) || blendProbes * 2 + 1 >= Math.Min(dims.X, Math.Min(dims.Y, dims.Z)))
        {
            throw new ArgumentOutOfRangeException(nameof(blendProbes), "the blend band must fit inside a level.");
        }
        LevelCount = levelCount;
        Dims = dims;
        BaseSpacing = baseSpacing;
        BlendProbes = blendProbes;
        origins = new Int3[levelCount];
    }

    public int LevelCount { get; }
    public Int3 Dims { get; }
    public float BaseSpacing { get; }
    /// <summary>How many probe spacings before a level's interior edge the blend toward the next level begins.</summary>
    public float BlendProbes { get; }

    private readonly Int3[] origins;

    /// <summary>The lowest cell of each level's block.</summary>
    public IReadOnlyList<Int3> Origins => origins;

    public int ProbesPerLevel => Dims.X * Dims.Y * Dims.Z;
    public int AtlasWidth => LevelCount * Dims.X * TileTexels;
    public int AtlasHeight => Dims.Y * Dims.Z * TileTexels;

    public float Spacing(int level) => BaseSpacing * (1 << level);

    /// <summary>The cell a world point falls in, at a level.</summary>
    public Int3 CellOf(int level, Vector3 world)
    {
        var s = Spacing(level);
        return new Int3((int)MathF.Floor(world.X / s), (int)MathF.Floor(world.Y / s), (int)MathF.Floor(world.Z / s));
    }

    public Vector3 ProbePosition(int level, Int3 cell) =>
        (new Vector3(cell.X, cell.Y, cell.Z) + new Vector3(0.5f)) * Spacing(level);

    /// <summary>Centre every level on the camera, snapped to its cells. Returns, per level, how far its block moved in cells.</summary>
    public Int3[] Follow(Vector3 camera)
    {
        var moved = new Int3[LevelCount];
        for (var l = 0; l < LevelCount; l++)
        {
            var origin = CellOf(l, camera) - new Int3(Dims.X / 2, Dims.Y / 2, Dims.Z / 2);
            moved[l] = origin - origins[l];
            origins[l] = origin;
        }
        return moved;
    }

    public bool InBlock(int level, Int3 cell)
    {
        var o = origins[level];
        return cell.X >= o.X && cell.X < o.X + Dims.X && cell.Y >= o.Y && cell.Y < o.Y + Dims.Y && cell.Z >= o.Z && cell.Z < o.Z + Dims.Z;
    }

    /// <summary>The slot a cell occupies: its coordinate modulo the dimensions, per axis.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A coordinate beyond <see cref="MaxCell"/>: the world is past what a slot can be found for.</exception>
    public Int3 Slot(Int3 cell)
    {
        if (Math.Abs(cell.X) >= MaxCell || Math.Abs(cell.Y) >= MaxCell || Math.Abs(cell.Z) >= MaxCell)
        {
            throw new ArgumentOutOfRangeException(nameof(cell), $"cell {cell} is beyond the {MaxCell} a clipmap addresses.");
        }
        return new(Wrap(cell.X, Dims.X), Wrap(cell.Y, Dims.Y), Wrap(cell.Z, Dims.Z));
    }

    /// <summary>A slot's index within its level, x fastest: what a per-probe buffer is indexed by (plus level * ProbesPerLevel).</summary>
    public int SlotIndex(Int3 slot) => (slot.Z * Dims.Y + slot.Y) * Dims.X + slot.X;

    /// <summary>The atlas texel at a level's slot's tile corner.</summary>
    public (int X, int Y) TileOrigin(int level, Int3 slot) =>
        ((level * Dims.X + slot.X) * TileTexels, (slot.Y + slot.Z * Dims.Y) * TileTexels);

    /// <summary>The cell a slot holds in a level's current block: the inverse of <see cref="Slot"/> within it.</summary>
    public Int3 CellInSlot(int level, Int3 slot)
    {
        var o = origins[level];
        return new Int3(o.X + Wrap(slot.X - o.X, Dims.X), o.Y + Wrap(slot.Y - o.Y, Dims.Y), o.Z + Wrap(slot.Z - o.Z, Dims.Z));
    }

    /// <summary>The finest level that can answer for a point, and how much of the next level to blend in (0 none, 1 all).</summary>
    /// <remarks>
    /// A level can answer where the point's trilinear neighbourhood (the eight probes around it) lies in its block:
    /// in probe units, between the first and the last probe's centres. The blend weight rises from 0 at
    /// <see cref="BlendProbes"/> spacings inside that to 1 at its edge. Level -1 is past the coarsest level.
    /// </remarks>
    public (int Level, float Blend) Locate(Vector3 world)
    {
        for (var l = 0; l < LevelCount; l++)
        {
            var inside = InsideDistance(l, world);
            if (inside < 0f) continue;
            var blend = BlendProbes <= 0f || l == LevelCount - 1 ? 0f : Math.Clamp(1f - inside / BlendProbes, 0f, 1f);
            return (l, blend);
        }
        return (-1, 0f);
    }

    /// <summary>How many probe spacings a point is inside the region a level can interpolate (negative outside).</summary>
    public float InsideDistance(int level, Vector3 world)
    {
        var s = Spacing(level);
        var o = origins[level];
        // In probe units: probe i of the block is at (o + i + 0.5) * s, so the point's coordinate is p / s - o - 0.5.
        var g = world / s - new Vector3(o.X, o.Y, o.Z) - new Vector3(0.5f);
        var low = MathF.Min(g.X, MathF.Min(g.Y, g.Z));
        var high = MathF.Min(Dims.X - 1 - g.X, MathF.Min(Dims.Y - 1 - g.Y, Dims.Z - 1 - g.Z));
        return MathF.Min(low, high);
    }

    private static int Wrap(int v, int n) => ((v % n) + n) % n;
}

/// <summary>Three integers: a cell or a slot of a grid.</summary>
public readonly record struct Int3(int X, int Y, int Z)
{
    public static Int3 operator +(Int3 a, Int3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Int3 operator -(Int3 a, Int3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
}
