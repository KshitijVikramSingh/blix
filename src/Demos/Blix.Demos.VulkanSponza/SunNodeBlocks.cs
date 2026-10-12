using System.Numerics;

namespace Blix.Demos.VulkanSponza;

// Potential suns' storage (SponzaLoop.SunNodes.cs): a block of texels' light at every node -- a row a texel, a column a
// (node, channel) -- held as Rank basis columns shared by the block and Rank coefficients a texel. On Cornell a basis per
// block of 4096 at rank 8 (16 B a texel in halves) kept 17 x 9 nodes within 0.9% of their light, where one basis for the
// whole scene needed rank 24 for the same (plan.md, potential suns).
//
// The basis is the top eigenvectors of the block's Gram matrix (A^T A, uncentred), found by subspace iteration with a
// Rayleigh-Ritz rotation at the end: a good subspace is all the reconstruction needs, not exact eigenvectors.
internal static class SunNodeBlocks
{
    public const int Block = 4096;
    public const int Rank = 8;
    private const int Extra = 4;
    private const int Iterations = 24;

    /// <summary>Compresses rows x cols (row-major) into a basis (Rank x cols, a row a basis vector) and coefficients
    /// (rows x Rank).</summary>
    public static (float[] Basis, float[] Coefs) Compress(float[] a, int rows, int cols)
    {
        var gram = Gram(a, rows, cols);
        var m = Rank + Extra;
        // Starting subspace: fixed seed, so a cook is repeatable.
        var rng = new Random(0x5EED);
        var x = new double[m * cols];
        for (var i = 0; i < x.Length; i++) x[i] = rng.NextDouble() - 0.5;
        Orthonormalize(x, m, cols);
        var y = new double[m * cols];
        for (var it = 0; it < Iterations; it++)
        {
            Multiply(gram, x, y, m, cols);
            (x, y) = (y, x);
            Orthonormalize(x, m, cols);
        }
        // Rayleigh-Ritz: T = X^T G X (m x m), its eigenvectors rotate X so the first Rank carry the most.
        Multiply(gram, x, y, m, cols);
        var t = new double[m * m];
        for (var i = 0; i < m; i++)
        for (var j = 0; j < m; j++)
        {
            double s = 0;
            for (var c = 0; c < cols; c++) s += x[i * cols + c] * y[j * cols + c];
            t[i * m + j] = s;
        }
        var (values, vectors) = Jacobi(t, m);
        var order = Enumerable.Range(0, m).OrderByDescending(i => values[i]).ToArray();
        var basis = new float[Rank * cols];
        for (var r = 0; r < Rank; r++)
        {
            var e = order[r];
            for (var c = 0; c < cols; c++)
            {
                double s = 0;
                for (var i = 0; i < m; i++) s += vectors[i * m + e] * x[i * cols + c];
                basis[r * cols + c] = (float)s;
            }
        }
        var coefs = new float[rows * Rank];
        for (var row = 0; row < rows; row++)
        for (var r = 0; r < Rank; r++)
        {
            var s = 0f;
            for (var c = 0; c < cols; c++) s += a[row * cols + c] * basis[r * cols + c];
            coefs[row * Rank + r] = s;
        }
        return (basis, coefs);
    }

    // A^T A, the upper triangle accumulated a row at a time (SIMD across the row), mirrored.
    private static double[] Gram(float[] a, int rows, int cols)
    {
        var g = new float[cols * cols];
        var w = Vector<float>.Count;
        for (var row = 0; row < rows; row++)
        {
            var r = a.AsSpan(row * cols, cols);
            for (var i = 0; i < cols; i++)
            {
                var ai = r[i];
                if (ai == 0f) continue;
                var gi = g.AsSpan(i * cols, cols);
                var vi = new Vector<float>(ai);
                var j = i;
                for (; j + w <= cols; j += w)
                    (new Vector<float>(gi[j..]) + vi * new Vector<float>(r[j..])).CopyTo(gi[j..]);
                for (; j < cols; j++) gi[j] += ai * r[j];
            }
        }
        var d = new double[cols * cols];
        for (var i = 0; i < cols; i++)
        for (var j = i; j < cols; j++)
            d[i * cols + j] = d[j * cols + i] = g[i * cols + j];
        return d;
    }

    // y_i = G x_i for each of the m vectors (G symmetric).
    private static void Multiply(double[] g, double[] x, double[] y, int m, int cols)
    {
        for (var i = 0; i < m; i++)
        for (var r = 0; r < cols; r++)
        {
            double s = 0;
            var gr = r * cols;
            for (var c = 0; c < cols; c++) s += g[gr + c] * x[i * cols + c];
            y[i * cols + r] = s;
        }
    }

    // Modified Gram-Schmidt over the m vectors (rows of x). A vector with (next to) nothing left once the earlier ones
    // are taken out -- a block whose light has fewer than m directions in it, Cornell's walls -- is replaced by the first
    // unit vector that still has something left after the same projection, so the set stays orthonormal (a plain unit
    // vector in its place left Cornell's bases off by up to 2 in orthogonality, and 36% of the light lost).
    private static void Orthonormalize(double[] x, int m, int cols)
    {
        for (var i = 0; i < m; i++)
        {
            if (Reduce(x, i, cols) > 1e-12) continue;
            for (var e = 0; e < cols; e++)
            {
                for (var c = 0; c < cols; c++) x[i * cols + c] = c == e ? 1.0 : 0.0;
                if (Reduce(x, i, cols) > 1e-6) break;
            }
        }
    }

    // Vector i less its projections on the vectors before it, normalised; returns the norm it had before.
    private static double Reduce(double[] x, int i, int cols)
    {
        for (var pass = 0; pass < 2; pass++)   // twice: one pass leaves rounding's share of the earlier vectors in
        for (var j = 0; j < i; j++)
        {
            double d = 0;
            for (var c = 0; c < cols; c++) d += x[i * cols + c] * x[j * cols + c];
            for (var c = 0; c < cols; c++) x[i * cols + c] -= d * x[j * cols + c];
        }
        double n = 0;
        for (var c = 0; c < cols; c++) n += x[i * cols + c] * x[i * cols + c];
        n = Math.Sqrt(n);
        if (n > 1e-300)
            for (var c = 0; c < cols; c++) x[i * cols + c] /= n;
        return n;
    }

    // Cyclic Jacobi for a small symmetric matrix: eigenvalues and eigenvectors (columns of v).
    private static (double[] Values, double[] Vectors) Jacobi(double[] a, int n)
    {
        a = (double[])a.Clone();
        var v = new double[n * n];
        for (var i = 0; i < n; i++) v[i * n + i] = 1;
        for (var sweep = 0; sweep < 50; sweep++)
        {
            double off = 0;
            for (var p = 0; p < n; p++)
            for (var q = p + 1; q < n; q++) off += a[p * n + q] * a[p * n + q];
            if (off < 1e-22) break;
            for (var p = 0; p < n; p++)
            for (var q = p + 1; q < n; q++)
            {
                var apq = a[p * n + q];
                if (Math.Abs(apq) < 1e-300) continue;
                var theta = (a[q * n + q] - a[p * n + p]) / (2 * apq);
                var t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                if (theta == 0) t = 1;
                var c = 1 / Math.Sqrt(t * t + 1);
                var s = t * c;
                for (var k = 0; k < n; k++)
                {
                    var akp = a[k * n + p];
                    var akq = a[k * n + q];
                    a[k * n + p] = c * akp - s * akq;
                    a[k * n + q] = s * akp + c * akq;
                }
                for (var k = 0; k < n; k++)
                {
                    var apk = a[p * n + k];
                    var aqk = a[q * n + k];
                    a[p * n + k] = c * apk - s * aqk;
                    a[q * n + k] = s * apk + c * aqk;
                }
                for (var k = 0; k < n; k++)
                {
                    var vkp = v[k * n + p];
                    var vkq = v[k * n + q];
                    v[k * n + p] = c * vkp - s * vkq;
                    v[k * n + q] = s * vkp + c * vkq;
                }
            }
        }
        var values = new double[n];
        for (var i = 0; i < n; i++) values[i] = a[i * n + i];
        return (values, v);
    }
}
