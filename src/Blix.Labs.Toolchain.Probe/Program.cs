using Blix.Graphics.Vulkan;
using Blix.Labs.Toolchain;

namespace Blix.Labs.Toolchain.Probe;

// The lab's binding model, printed — and checked.
//
// ── Proves ──────────────────────────────────────────────────────────────────
//   • A second executable over one lab. This project declares no shaders, owns no
//     render code and never opens a window; the .spv sidecars and the lab's types
//     both arrive from Blix.Labs.Toolchain. That was the whole claim of splitting
//     the lab out, and until now nothing tested it.
//   • Reflection is worth something OFF the GPU: the binding model is a build
//     artifact, so it can be read, diffed and asserted against without a device.
//
// ── Why it checks rather than only prints ───────────────────────────────────
//   The lab's first run died on "payload length 96 does not match the shader's
//   declared total push-constant size 64" — a C# constant disagreeing with the
//   SPIR-V it describes. That is the exact drift the reflected path exists to
//   prevent, and the device caught it at draw time, which is late. Here it is a
//   non-zero exit code before anything is submitted.
public static class Program
{
    public static int Main(string[] args)
    {
        var shaderDirectory = Path.Combine(AppContext.BaseDirectory, "Shaders");
        if (!Directory.Exists(shaderDirectory))
        {
            Console.Error.WriteLine($"No Shaders directory at {shaderDirectory}.");
            return 2;
        }

        var programs = new (string Name, string[] Stages, int? ExpectedPush)[]
        {
            ("lab.shadow", new[] { "lab_shadow.vert", "lab_shadow.frag" }, LabRenderer.CasterPushBytes),
            ("lab.lit", new[] { "lab_lit.vert", "lab_lit.frag" }, LabRenderer.LitPushBytes),
            ("lab.present", new[] { "lab_present.vert", "lab_present.frag" }, null),
        };

        var failures = 0;
        Console.WriteLine($"lab binding model — reflected from {shaderDirectory}");
        Console.WriteLine();

        foreach (var (name, stages, expectedPush) in programs)
        {
            ShaderInterface iface;
            try
            {
                iface = ShaderReflection.MergeStages(
                    stages.Select(s => ShaderReflection.Load(
                        Path.Combine(shaderDirectory, s + ".spv.refl.json"))).ToArray());
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"{name}: could not reflect — {exception.Message}");
                failures++;
                continue;
            }

            Console.WriteLine($"{name}  [{string.Join(", ", stages)}]");

            // Ordered, so two runs of this are diffable and a shader change shows up as a
            // line rather than as a reshuffle.
            foreach (var slot in iface.Slots.OrderBy(s => s.Set).ThenBy(s => s.Binding))
            {
                Console.WriteLine(
                    $"  set {slot.Set} binding {slot.Binding}  {slot.Type,-13} {slot.Stages}");

                if (slot.BlockLayout is not { } block) continue;
                Console.WriteLine($"    block {block.TotalSize} bytes");
                foreach (var member in block.Members.OrderBy(m => m.Offset))
                {
                    Console.WriteLine($"      +{member.Offset,-4} {member.Size,4}B  {member.Name}");
                }
            }

            var pushTotal = 0;
            foreach (var range in iface.PushConstants.OrderBy(r => r.Offset))
            {
                Console.WriteLine($"  push  +{range.Offset} {range.Size}B  {range.Stages}");
                pushTotal = Math.Max(pushTotal, range.Offset + range.Size);
            }

            if (expectedPush is { } expected)
            {
                if (pushTotal == expected)
                {
                    Console.WriteLine($"  push total {pushTotal}B matches the renderer's constant");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"  MISMATCH: shader declares {pushTotal}B of push constants, " +
                        $"the renderer pushes {expected}B. One of them is wrong and the device " +
                        $"will refuse the draw.");
                    failures++;
                }
            }

            Console.WriteLine();
        }

        // Links the lab's own code, not just its build output — the scene is described
        // without a device anywhere in sight.
        var scene = LabScene.Default();
        Console.WriteLine(
            $"scene: {scene.Objects.Count} object(s), " +
            $"{scene.Objects.Count(o => o.IsGround)} ground, " +
            $"sun {scene.SunDirection.X:0.00}, {scene.SunDirection.Y:0.00}, {scene.SunDirection.Z:0.00}, " +
            $"shadow map {LabRenderer.ShadowMapSize}px");

        if (failures == 0) return 0;
        Console.Error.WriteLine($"{failures} problem(s).");
        return 1;
    }
}
