using System.Numerics;
using Blix.Graphics;
using Blix.Graphics.Vulkan;

namespace Blix.Demos.VulkanSponza;

// Demo-owned synthetic IBL: an analytic-sky environment cube (+ mip chain
// standing in for prefiltered specular), a cosine-weighted irradiance cube, and
// a split-sum BRDF LUT — generated on the CPU at load. This is the FALLBACK for
// when no cooked .blixprobe ships; a real probe (EnvironmentBaker.UploadCookedProbe)
// is always preferred.
//
// This is content *synthesis* — a demo / tooling concern, not engine runtime.
// The engine loads cooked probes; faking a sky is the game's business. Lifted
// out of Program.cs so that boundary is explicit (and Program.cs is ~210 lines
// lighter). Same sky model + split-sum integration as VulkanLit's equivalent.
internal static class ProceduralSky
{
    public const int EnvFaceSize = 64;
    public const int EnvMips = 7;            // log2(64) + 1
    public const int IrradianceFaceSize = 16;
    public const int BrdfLutSize = 128;

    public readonly record struct Baked(
        TextureHandle EnvCube, TextureHandle Irradiance, TextureHandle BrdfLut, int PrefilterMips);

    // Bake the three IBL textures for a fixed (bake-time) sun direction. The
    // cubes encode this sun's glow, so live sun changes don't relight the IBL.
    public static Baked Bake(VulkanGraphicsDevice device, Vector3 sunDirection)
    {
        ArgumentNullException.ThrowIfNull(device);
        var envCube = device.CreateTextureCube(
            EnvFaceSize, TextureFormat.Rgba8, EnvMips,
            BuildEnvCubeWithMips(EnvFaceSize, EnvMips, sunDirection),
            SamplerDescription.LinearClamp, "sponza.ibl.env");
        var irradiance = device.CreateTextureCube(
            IrradianceFaceSize, TextureFormat.Rgba8, 1,
            BuildIrradianceCube(IrradianceFaceSize, sunDirection),
            SamplerDescription.LinearClamp, "sponza.ibl.irradiance");
        var brdfLut = device.CreateTexture2D(
            new TextureDescription(BrdfLutSize, BrdfLutSize, TextureFormat.Rgba8, SamplerDescription.LinearClamp),
            BuildBrdfLut(BrdfLutSize), "sponza.ibl.brdfLut");
        return new Baked(envCube, irradiance, brdfLut, EnvMips);
    }

    // Analytic sky radiance (linear) for a world direction. Zenith→horizon
    // gradient, dim ground below, plus a soft warm glow toward the sun.
    private static Vector3 SkyColor(Vector3 d, Vector3 sunDirection)
    {
        d = Vector3.Normalize(d);
        var zenith  = new Vector3(0.22f, 0.42f, 0.82f);
        var horizon = new Vector3(0.70f, 0.78f, 0.90f);
        var ground  = new Vector3(0.16f, 0.15f, 0.14f);
        Vector3 baseCol;
        if (d.Y >= 0f)
        {
            var k = MathF.Pow(Math.Clamp(d.Y, 0f, 1f), 0.5f);
            baseCol = Vector3.Lerp(horizon, zenith, k);
        }
        else
        {
            var k = Math.Clamp(-d.Y, 0f, 1f);
            baseCol = Vector3.Lerp(horizon, ground, k);
        }
        // Sun glow comes FROM the opposite of the sun travel direction.
        var toSun = -sunDirection;
        var glow = MathF.Pow(MathF.Max(Vector3.Dot(d, toSun), 0f), 32f);
        baseCol += new Vector3(0.5f, 0.42f, 0.30f) * glow;
        return Vector3.Clamp(baseCol, Vector3.Zero, Vector3.One);
    }

    // Cubemap face (u,v)∈[-1,1] → world direction. Canonical Vulkan/GL cube
    // convention; generation and samplerCube lookup agree.
    private static Vector3 CubeDir(int face, float u, float v) => face switch
    {
        0 => new Vector3( 1f, -v, -u),   // +X
        1 => new Vector3(-1f, -v,  u),   // -X
        2 => new Vector3( u,  1f,  v),   // +Y
        3 => new Vector3( u, -1f, -v),   // -Y
        4 => new Vector3( u, -v,  1f),   // +Z
        _ => new Vector3(-u, -v, -1f),   // -Z
    };

    private static byte LinByte(float c) => (byte)(Math.Clamp(c, 0f, 1f) * 255f + 0.5f);

    private static void WriteEnvFace(byte[] dst, int offset, int face, int size, Vector3 sunDirection)
    {
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var u = 2f * ((x + 0.5f) / size) - 1f;
            var v = 2f * ((y + 0.5f) / size) - 1f;
            var c = SkyColor(CubeDir(face, u, v), sunDirection);
            var i = offset + (y * size + x) * 4;
            dst[i] = LinByte(c.X); dst[i + 1] = LinByte(c.Y); dst[i + 2] = LinByte(c.Z); dst[i + 3] = 255;
        }
    }

    // Env cube, face-major then mip-major. Each mip re-evaluates the analytic
    // sky at that resolution; the mip chain stands in for prefiltered specular
    // at increasing roughness.
    private static byte[] BuildEnvCubeWithMips(int faceSize, int mips, Vector3 sunDirection)
    {
        long total = 0;
        for (var f = 0; f < 6; f++)
            for (var m = 0; m < mips; m++) { var s = faceSize >> m; total += s * s * 4; }
        var data = new byte[total];
        var offset = 0;
        for (var face = 0; face < 6; face++)
        for (var m = 0; m < mips; m++)
        {
            var s = faceSize >> m;
            WriteEnvFace(data, offset, face, s, sunDirection);
            offset += s * s * 4;
        }
        return data;
    }

    // Diffuse irradiance cube: for each output direction N, cosine-weighted
    // average of the sky over the hemisphere around N.
    private static byte[] BuildIrradianceCube(int faceSize, Vector3 sunDirection)
    {
        var data = new byte[6 * faceSize * faceSize * 4];
        var faceBytes = faceSize * faceSize * 4;
        for (var face = 0; face < 6; face++)
        for (var y = 0; y < faceSize; y++)
        for (var x = 0; x < faceSize; x++)
        {
            var u = 2f * ((x + 0.5f) / faceSize) - 1f;
            var v = 2f * ((y + 0.5f) / faceSize) - 1f;
            var N = Vector3.Normalize(CubeDir(face, u, v));
            var up = MathF.Abs(N.Y) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
            var tangent = Vector3.Normalize(Vector3.Cross(up, N));
            var bitangent = Vector3.Cross(N, tangent);

            var sum = Vector3.Zero;
            var weight = 0f;
            const int phiSteps = 24;
            const int thetaSteps = 12;
            for (var pi = 0; pi < phiSteps; pi++)
            for (var ti = 0; ti < thetaSteps; ti++)
            {
                var phi = 2f * MathF.PI * (pi + 0.5f) / phiSteps;
                var theta = 0.5f * MathF.PI * (ti + 0.5f) / thetaSteps;
                var st = MathF.Sin(theta);
                var local = new Vector3(st * MathF.Cos(phi), st * MathF.Sin(phi), MathF.Cos(theta));
                var dir = local.X * tangent + local.Y * bitangent + local.Z * N;
                var w = MathF.Cos(theta) * MathF.Sin(theta);
                sum += SkyColor(dir, sunDirection) * w;
                weight += w;
            }
            var irr = sum / MathF.Max(weight, 1e-4f);
            var idx = face * faceBytes + (y * faceSize + x) * 4;
            data[idx] = LinByte(irr.X); data[idx + 1] = LinByte(irr.Y); data[idx + 2] = LinByte(irr.Z); data[idx + 3] = 255;
        }
        return data;
    }

    // Split-sum BRDF integration LUT. R = scale on F0, G = bias (R/G of Rgba8).
    private static byte[] BuildBrdfLut(int size)
    {
        var data = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var NdotV = (x + 0.5f) / size;
            var roughness = (y + 0.5f) / size;
            var (a, b) = IntegrateBrdf(NdotV, roughness);
            var i = (y * size + x) * 4;
            data[i] = LinByte(a); data[i + 1] = LinByte(b); data[i + 2] = 0; data[i + 3] = 255;
        }
        return data;
    }

    private static (float A, float B) IntegrateBrdf(float NdotV, float roughness)
    {
        var V = new Vector3(MathF.Sqrt(1f - NdotV * NdotV), 0f, NdotV);
        float a = 0f, b = 0f;
        const int samples = 256;
        var N = new Vector3(0, 0, 1);
        for (var i = 0; i < samples; i++)
        {
            var xi = Hammersley(i, samples);
            var H = ImportanceSampleGgx(xi, roughness, N);
            var L = Vector3.Normalize(2f * Vector3.Dot(V, H) * H - V);
            var NdotL = MathF.Max(L.Z, 0f);
            var NdotH = MathF.Max(H.Z, 0f);
            var VdotH = MathF.Max(Vector3.Dot(V, H), 0f);
            if (NdotL > 0f)
            {
                var g = GeometrySmithIbl(NdotV, NdotL, roughness);
                var gVis = g * VdotH / MathF.Max(NdotH * NdotV, 1e-5f);
                var fc = MathF.Pow(1f - VdotH, 5f);
                a += (1f - fc) * gVis;
                b += fc * gVis;
            }
        }
        return (a / samples, b / samples);
    }

    private static Vector2 Hammersley(int i, int n)
    {
        uint bits = (uint)i;
        bits = (bits << 16) | (bits >> 16);
        bits = ((bits & 0x55555555u) << 1) | ((bits & 0xAAAAAAAAu) >> 1);
        bits = ((bits & 0x33333333u) << 2) | ((bits & 0xCCCCCCCCu) >> 2);
        bits = ((bits & 0x0F0F0F0Fu) << 4) | ((bits & 0xF0F0F0F0u) >> 4);
        bits = ((bits & 0x00FF00FFu) << 8) | ((bits & 0xFF00FF00u) >> 8);
        var rdi = bits * 2.3283064365386963e-10f;
        return new Vector2(i / (float)n, rdi);
    }

    private static Vector3 ImportanceSampleGgx(Vector2 xi, float roughness, Vector3 _)
    {
        var a = roughness * roughness;
        var phi = 2f * MathF.PI * xi.X;
        var cosT = MathF.Sqrt((1f - xi.Y) / (1f + (a * a - 1f) * xi.Y));
        var sinT = MathF.Sqrt(1f - cosT * cosT);
        return new Vector3(MathF.Cos(phi) * sinT, MathF.Sin(phi) * sinT, cosT);
    }

    private static float GeometrySmithIbl(float NdotV, float NdotL, float roughness)
    {
        var k = (roughness * roughness) / 2f;
        float GeomG(float c) => c / (c * (1f - k) + k);
        return GeomG(NdotV) * GeomG(NdotL);
    }
}
