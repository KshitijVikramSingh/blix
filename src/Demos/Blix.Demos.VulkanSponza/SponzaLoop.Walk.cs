using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// --walk x,y,z,yaw,pitch: walk, then stop -- what a person does, and what neither the orbit (a slow circle that barely
// scrolls the clipmap) nor a still camera exercises. The camera holds its start pose (--cam, or the profile's) until
// post-load frame --walk-at (1500), walks at constant speed to the --walk pose over --walk-frames (120), and stays
// there. --dump-at f1,f2,...: at those post-load frames, the incident light and the presented image are written as
// RGB floats beside the shot (<shot>.f<frame>.incident.rgb / .resolved.rgb), so the seconds after stopping can be held
// against a run that sat at the destination long enough to converge. Frame-indexed, so it does not depend on speed.
internal sealed partial class SponzaLoop
{
    private (Vector3 Position, float Yaw, float Pitch)? walkTo;
    private (Vector3 Position, float Yaw, float Pitch) walkFrom;
    private bool walkStarted;
    private int walkAt = 1500;
    private int walkFrames = 120;
    private int[] dumpAt = System.Array.Empty<int>();
    private int dumpNext;

    private void ReadWalkArgs(AppArgs args)
    {
        if (args.String("walk") is { } walk)
        {
            var n = walk.Split(',').Select(s => float.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture)).ToArray();
            if (n.Length != 5) throw new AppArgsException($"--walk expects x,y,z,yaw,pitch (degrees), got '{walk}'.");
            walkTo = (new Vector3(n[0], n[1], n[2]), n[3], n[4]);
        }
        if (args.Int("walk-at") is { } at) walkAt = Math.Max(0, at);
        if (args.Int("walk-frames") is { } frames) walkFrames = Math.Max(1, frames);
        if (args.String("dump-at") is { } dumps)
            dumpAt = dumps.Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).OrderBy(f => f).ToArray();
    }

    private void ApplyWalk()
    {
        if (walkTo is not { } to || !fullyLoaded) return;
        if (postLoadFrames < walkAt) return;
        if (!walkStarted)
        {
            walkStarted = true;
            walkFrom = (camera.Position, camera.Yaw, camera.Pitch);
        }
        var t = Math.Clamp((postLoadFrames - walkAt) / (float)walkFrames, 0f, 1f);
        // The shorter way round in yaw.
        var dYaw = MathF.IEEERemainder(to.Yaw - walkFrom.Yaw, 360f);
        camera.Position = Vector3.Lerp(walkFrom.Position, to.Position, t);
        camera.Yaw = walkFrom.Yaw + dYaw * t;
        camera.Pitch = walkFrom.Pitch + (to.Pitch - walkFrom.Pitch) * t;
        UpdateCamera();
    }

    // Called each frame: writes what the last completed frame left, at the frames asked for.
    private void WriteWalkDumps()
    {
        if (shotPath is null || !fullyLoaded) return;
        while (dumpNext < dumpAt.Length && postLoadFrames >= dumpAt[dumpNext])
        {
            var basePath = Path.ChangeExtension(shotPath, null) + $".f{dumpAt[dumpNext]}";
            WriteRgb(basePath + ".incident.rgb", incidentHandle);
            WriteRgb(basePath + ".resolved.rgb", render.Taa > 0f ? taaHandles[taaWrite] : hdrHandle);
            Console.WriteLine($"[VulkanSponza] dump at post-load frame {postLoadFrames} (asked {dumpAt[dumpNext]}), camera {camera.Position}");
            dumpNext++;
        }
    }

    private void WriteRgb(string path, GraphResourceHandle target)
    {
        // Every second pixel each way: a quarter of the bytes (a full-resolution frame is 56 MB of floats), and a
        // probe-sized spot is many pixels wide.
        var pixels = device.ReadTexture(graph.GetColorTexture(target), out var fw, out var fh, out var format);
        var (w, h) = (fw / 2, fh / 2);
        var rgb = new float[w * h * 3];
        for (var o = 0; o < w * h; o++)
        {
            var i = (o / w) * 2 * fw + (o % w) * 2;
            if (format == TextureFormat.Rgba16F)
            {
                for (var c = 0; c < 3; c++) rgb[o * 3 + c] = (float)BitConverter.ToHalf(pixels, i * 8 + c * 2);
            }
            else
            {
                var p = BitConverter.ToUInt32(pixels, i * 4);
                rgb[o * 3] = UnpackFloat(p & 0x7FF, 6);
                rgb[o * 3 + 1] = UnpackFloat((p >> 11) & 0x7FF, 6);
                rgb[o * 3 + 2] = UnpackFloat((p >> 22) & 0x3FF, 5);
            }
        }
        using var f = File.Create(path);
        f.Write(BitConverter.GetBytes(w));
        f.Write(BitConverter.GetBytes(h));
        f.Write(MemoryMarshal.AsBytes(rgb.AsSpan()));
    }
}
