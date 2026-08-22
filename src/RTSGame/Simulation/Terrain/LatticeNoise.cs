namespace RTSGame.Simulation.Terrain;

using System.Numerics;

/// <summary>
/// Smooth value noise on a unit lattice, in [0,1]. One implementation, two callers.
/// </summary>
/// <remarks>
/// Written here rather than reached for, because it has to be identical on any machine: the relief plan
/// decides ground the simulation agrees with, and that gets fingerprinted and saved.
/// <para>
/// <b>Shared implementation, unshared responsibility.</b> <see cref="ReliefPlan"/> calls this to build the
/// height field, which is generation; <c>GroundCover</c> calls it to warp a colour boundary, which is
/// dressing and is never saved or fingerprinted. §52's split is about which layer <em>owns a fact</em>, not
/// about arithmetic — and two copies of a hashed lattice are two copies that drift.
/// </para>
/// </remarks>
internal static class LatticeNoise
{
    public static float Value(Vector2 at)
    {
        var x0 = (int)MathF.Floor(at.X);
        var y0 = (int)MathF.Floor(at.Y);
        var tx = at.X - x0;
        var ty = at.Y - y0;
        // <b>Quintic rather than cubic, because the gradient has to be continuous and not just the
        // value.</b> Smoothstep has a second derivative that jumps at every lattice line, which on a
        // height field is a faint crease every wavelength — a grid of them, axis-aligned, which is one of
        // the ways ground "changes in weird ways". The quintic is flat to second order at both ends, so the
        // lattice leaves no trace in the slope.
        tx = tx * tx * tx * (tx * (tx * 6f - 15f) + 10f);
        ty = ty * ty * ty * (ty * (ty * 6f - 15f) + 10f);
        var a = Lattice(x0, y0);
        var b = Lattice(x0 + 1, y0);
        var c = Lattice(x0, y0 + 1);
        var d = Lattice(x0 + 1, y0 + 1);
        return (a + (b - a) * tx) * (1f - ty) + (c + (d - c) * tx) * ty;
    }

    private static float Lattice(int x, int y)
    {
        var hash = (uint)(x * 374761393) ^ (uint)(y * 668265263);
        hash = (hash ^ (hash >> 13)) * 1274126177u;
        return ((hash ^ (hash >> 16)) & 0xFFFFFFu) / (float)0x1000000u;
    }
}
