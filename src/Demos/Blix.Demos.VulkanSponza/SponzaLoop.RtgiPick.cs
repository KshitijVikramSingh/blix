using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Graphics;
using ImGuiNET;

namespace Blix.Demos.VulkanSponza;

// The RTGI picker: P puts the pixel under the mouse through the readout step by step (rtgi_readout.frag's RtgiPick) and
// the log prints it a few frames on -- its point, side and level, each cell its level blend reads (found or not, the
// samples it holds, its light, its weight), the blend, the near field over it, the final light. What colour views can
// only hint at (which term carries a white blob), one click names.
internal sealed partial class SponzaLoop
{
    private GpuBufferHandle rtgiPickBuf;
    private Vector2 rtgiPickUv;
    private int rtgiPickFrames;   // frames left with the pick on (then read)
    private const int RtgiPickWords = 40;

    // ReadInput: P picks the pixel under the mouse (normalised by ImGui's display size, the mouse's coordinates).
    private void ReadRtgiPick(Vector2 mouse)
    {
        var size = ImGui.GetIO().DisplaySize;
        if (size.X <= 0f || size.Y <= 0f) return;
        rtgiPickUv = new Vector2(mouse.X / size.X, mouse.Y / size.Y);
        rtgiPickFrames = 4;
    }

    private GpuBufferHandle RtgiPickBuffer
    {
        get
        {
            if (rtgiPickBuf.Equals(default(GpuBufferHandle))) rtgiPickBuf = Own(device.CreateGpuBuffer(RtgiPickWords * 16, new byte[RtgiPickWords * 16], "sponza.rtgi.pick"));
            return rtgiPickBuf;
        }
    }

    // The readout's uPick: the half-res pixel while the pick is on.
    private Vector4 RtgiPickUniform(int width, int height)
    {
        // BLIX_RTGI_PICK="u,v,frame": a pick at that frame (a scripted one, for runs without a hand on the keys).
        if (rtgiPickFrames <= 0 && Environment.GetEnvironmentVariable("BLIX_RTGI_PICK") is { Length: > 0 } scripted)
        {
            var f = scripted.Split(',').Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            if (f.Length == 3 && rtgiFrameCount == (uint)f[2]) { rtgiPickUv = new Vector2(f[0], f[1]); rtgiPickFrames = 4; }
        }
        if (rtgiPickFrames <= 0) return Vector4.Zero;
        var (hw, hh) = HalfLightingSize(width, height);
        return new Vector4(MathF.Floor(rtgiPickUv.X * hw), MathF.Floor(rtgiPickUv.Y * hh), 1f, 0f);
    }

    // After the frame's readout: the last frame of the pick reads the buffer and logs it.
    private void FinishRtgiPick()
    {
        if (rtgiPickFrames <= 0) return;
        if (--rtgiPickFrames > 0) return;
        device.WaitIdle();
        var v = MemoryMarshal.Cast<byte, Vector4>(device.ReadGpuBuffer(RtgiPickBuffer, 0, RtgiPickWords * 16)).ToArray();
        static string F(Vector4 a) => string.Create(CultureInfo.InvariantCulture, $"({a.X:0.###}, {a.Y:0.###}, {a.Z:0.###}, {a.W:0.###})");
        static float L(Vector4 a) => 0.2126f * a.X + 0.7152f * a.Y + 0.0722f * a.Z;
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"[VulkanSponza] rtgi pick at uv ({rtgiPickUv.X:0.###}, {rtgiPickUv.Y:0.###}): p ({v[0].X:0.###}, {v[0].Y:0.###}, {v[0].Z:0.###}) side {v[0].W:0} n ({v[1].X:0.##}, {v[1].Y:0.##}, {v[1].Z:0.##}) level {v[1].W:0.##}"),
            string.Create(CultureInfo.InvariantCulture, $"  own level e0 {F(v[2])} lum {L(v[2]):0.###}; next e1 {F(v[3])} lum {L(v[3]):0.###}; blend {L(v[4]):0.###} via {(v[4].W switch { 1 => "both", 2 => "own only", 3 => "next only", _ => "two levels up" })}"),
            string.Create(CultureInfo.InvariantCulture, $"  after side blend / normal turn {L(v[5]):0.###}; near light {F(v[6])} found {v[7].X:0.##} escape {v[7].Y:0.##} far share {v[7].Z:0.##} near share {v[7].W:0.##}; final {F(v[8])} lum {L(v[8]):0.###}"),
        };
        string[] names = { "own", "next", "fallback" };
        for (var lv = 0; lv < 3; lv++)
            for (var k = 0; k < 4; k++)
            {
                var a = v[10 + 8 * lv + 2 * k];
                var b = v[11 + 8 * lv + 2 * k];
                if (a == Vector4.Zero && b == Vector4.Zero) continue;
                lines.Add(string.Create(CultureInfo.InvariantCulture,
                    $"  {names[lv]} tap {k}: cell ({a.X:0}, {a.Y:0}, {a.Z:0}) region {a.W:0} -- {(b.X < 0.5 ? "masked (not reached)" : b.Y < 0f ? "seen, no cell" : $"held {b.Y:0}, lum {b.Z:0.###}, weight {b.W:0.###}")}"));
            }
        foreach (var line in lines) Console.WriteLine(line);
        device.WriteGpuBuffer(RtgiPickBuffer, 0, new byte[RtgiPickWords * 16]);
    }
}
