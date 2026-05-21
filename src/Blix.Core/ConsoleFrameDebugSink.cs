using System.Text;
using Blix.Graphics;

namespace Blix.Core;

public sealed class ConsoleFrameDebugSink : IRuntimeDiagnosticsSink
{
    private readonly int interval;
    private long frameIndex;

    public ConsoleFrameDebugSink(int interval = 60)
    {
        if (interval <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Interval must be positive.");
        }

        this.interval = interval;
    }

    public void OnFrameDebug(FrameDebugPacket packet, ResourceRegistrySnapshot resources)
    {
        var index = frameIndex++;

        if (index % interval != 0)
        {
            return;
        }

        Console.Write(Format(index, packet, resources));
    }

    private static string Format(long index, FrameDebugPacket packet, ResourceRegistrySnapshot resources)
    {
        var builder = new StringBuilder();
        builder.Append("[frame ").Append(index).Append("] ")
            .Append(packet.TotalPasses).Append(" passes, ")
            .Append(packet.TotalDraws).AppendLine(" draws");

        foreach (var pass in packet.Passes)
        {
            var targetName = ResolveTargetName(pass.Target, resources);
            builder.Append("  ").Append(pass.Name.PadRight(8))
                .Append("  target=").Append(targetName.PadRight(14))
                .Append(' ').Append(pass.Width).Append('x').Append(pass.Height)
                .Append("  clear=").Append(FormatClear(pass))
                .Append("  draws=").Append(pass.Draws.Count)
                .AppendLine();
        }

        return builder.ToString();
    }

    private static string ResolveTargetName(RenderSurfaceHandle target, ResourceRegistrySnapshot resources)
    {
        if (target == RenderSurfaceHandle.Default)
        {
            return "(default)";
        }

        return resources.FindRenderSurface(target)?.Name ?? $"surface#{target.Id}";
    }

    private static string FormatClear(FrameDebugPass pass)
    {
        return (pass.ClearedColor, pass.ClearedDepth) switch
        {
            (true, true) => "color+depth",
            (true, false) => "color",
            (false, true) => "depth",
            _ => "none"
        };
    }
}
