using System.Numerics;

namespace Blix.Graphics.Primitives;

public static class TorusKnot
{
    // p=2, q=3 is the classic trefoil knot. Larger p/q produce more elaborate knots.
    private const int P = 2;
    private const int Q = 3;
    private const int TubeSegments = 128;
    private const int RadialSegments = 16;
    private const float TubeRadius = 0.10f;

    public static VertexPosition3NormalTexture[] Vertices { get; } = BuildVertices();

    public static ushort[] Indices { get; } = BuildIndices();

    private static VertexPosition3NormalTexture[] BuildVertices()
    {
        var ringCount = TubeSegments + 1;
        var positions = new Vector3[ringCount];
        var tangents = new Vector3[ringCount];
        for (var i = 0; i < ringCount; i++)
        {
            var t = 2.0f * MathF.PI * i / TubeSegments;
            positions[i] = KnotPoint(t);
            // Central difference for a clean tangent (avoids the forward-difference's
            // half-segment lag at the curve's high-curvature parts).
            var pNext = KnotPoint(t + 0.005f);
            var pPrev = KnotPoint(t - 0.005f);
            tangents[i] = Vector3.Normalize(pNext - pPrev);
        }

        // Parallel-transport frame: propagate the previous ring's normal forward by
        // removing the component along the new tangent, then re-normalizing. This
        // avoids the frame discontinuity that an arbitrary up-vector causes, which
        // would show up as a visible twist in the tube.
        var normals = new Vector3[ringCount];
        var binormals = new Vector3[ringCount];

        var firstUp = MathF.Abs(tangents[0].Y) < 0.95f ? Vector3.UnitY : Vector3.UnitX;
        normals[0] = Vector3.Normalize(Vector3.Cross(tangents[0], firstUp));
        binormals[0] = Vector3.Normalize(Vector3.Cross(tangents[0], normals[0]));
        for (var i = 1; i < ringCount; i++)
        {
            var projected = normals[i - 1] - Vector3.Dot(normals[i - 1], tangents[i]) * tangents[i];
            var lengthSq = projected.LengthSquared();
            normals[i] = lengthSq > 1e-12f
                ? projected / MathF.Sqrt(lengthSq)
                : Vector3.Normalize(Vector3.Cross(tangents[i], Vector3.UnitY));
            binormals[i] = Vector3.Normalize(Vector3.Cross(tangents[i], normals[i]));
        }

        // Closure correction: the propagated normal at the end of the loop doesn't
        // equal the initial normal because the knot has non-zero holonomy. Compute
        // the residual angle, then distribute an opposite twist evenly along the
        // tube so the seam between ring[n] and ring[0] joins up smoothly.
        var residualAngle = SignedAngleAroundAxis(normals[TubeSegments], normals[0], tangents[0]);
        for (var i = 0; i < ringCount; i++)
        {
            var fraction = (float)i / TubeSegments;
            var correction = fraction * residualAngle;
            var rot = Quaternion.CreateFromAxisAngle(tangents[i], correction);
            normals[i] = Vector3.Transform(normals[i], rot);
            binormals[i] = Vector3.Transform(binormals[i], rot);
        }

        var vertices = new VertexPosition3NormalTexture[ringCount * (RadialSegments + 1)];
        for (var i = 0; i < ringCount; i++)
        {
            var pos = positions[i];
            var nrm = normals[i];
            var bnm = binormals[i];
            for (var j = 0; j <= RadialSegments; j++)
            {
                var u = 2.0f * MathF.PI * j / RadialSegments;
                var cosU = MathF.Cos(u);
                var sinU = MathF.Sin(u);
                var nxFinal = cosU * nrm.X + sinU * bnm.X;
                var nyFinal = cosU * nrm.Y + sinU * bnm.Y;
                var nzFinal = cosU * nrm.Z + sinU * bnm.Z;
                var px = pos.X + TubeRadius * nxFinal;
                var py = pos.Y + TubeRadius * nyFinal;
                var pz = pos.Z + TubeRadius * nzFinal;

                vertices[i * (RadialSegments + 1) + j] = new VertexPosition3NormalTexture(
                    new GraphicsVector3(px, py, pz),
                    new GraphicsVector3(nxFinal, nyFinal, nzFinal),
                    // UV.x tile count picked so texels are square on the surface:
                    // centerline arc length (~7.75) divided by tube circumference
                    // (2*pi*0.10 ~= 0.628) gives ~12.3 wraps. Anything less stretches
                    // each cell along the tube axis. Combined with the Repeat-sampled
                    // hex texture this gives hexagonal cells, not rectangles.
                    new GraphicsVector2((float)i / TubeSegments * 12.0f, (float)j / RadialSegments));
            }
        }
        return vertices;
    }

    private static Vector3 KnotPoint(float t)
    {
        // (p, q) torus knot centerline. Scale so the knot fits inside about
        // [-0.55, 0.55] on each axis.
        var s = 0.18f;
        var r = 2.0f + MathF.Cos(P * t);
        return new Vector3(
            s * r * MathF.Cos(Q * t),
            s * MathF.Sin(P * t) * 1.5f,
            s * r * MathF.Sin(Q * t));
    }

    private static float SignedAngleAroundAxis(Vector3 from, Vector3 to, Vector3 axis)
    {
        // Project both vectors onto the plane perpendicular to axis, then take the
        // signed angle from `from` to `to` using cross dot axis for the sign.
        var fp = from - Vector3.Dot(from, axis) * axis;
        var tp = to - Vector3.Dot(to, axis) * axis;
        var fpLen = fp.Length();
        var tpLen = tp.Length();
        if (fpLen < 1e-6f || tpLen < 1e-6f)
        {
            return 0.0f;
        }
        fp /= fpLen;
        tp /= tpLen;
        var cos = Math.Clamp(Vector3.Dot(fp, tp), -1.0f, 1.0f);
        var angle = MathF.Acos(cos);
        var sign = MathF.Sign(Vector3.Dot(Vector3.Cross(fp, tp), axis));
        return sign == 0 ? angle : sign * angle;
    }

    private static ushort[] BuildIndices()
    {
        var rowSize = RadialSegments + 1;
        var indices = new ushort[TubeSegments * RadialSegments * 6];
        var write = 0;
        for (var i = 0; i < TubeSegments; i++)
        {
            for (var j = 0; j < RadialSegments; j++)
            {
                var a = (ushort)(i * rowSize + j);
                var b = (ushort)(i * rowSize + j + 1);
                var c = (ushort)((i + 1) * rowSize + j);
                var d = (ushort)((i + 1) * rowSize + j + 1);
                indices[write++] = a;
                indices[write++] = b;
                indices[write++] = c;
                indices[write++] = b;
                indices[write++] = d;
                indices[write++] = c;
            }
        }
        return indices;
    }
}
