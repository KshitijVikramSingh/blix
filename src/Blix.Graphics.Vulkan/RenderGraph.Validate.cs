namespace Blix.Graphics.Vulkan;

// Pure-function validation over the graph's pass + resource tables.
// Checks: non-empty, unique pass names, color-or-depth attachment,
// at least one shader per pass, Reads reference a resource declared
// by an EARLIER pass (the declaration-order = execution-order rule
// catches both unknown resources and cycles in one check).
internal static class RenderGraphValidation
{
    public static void Validate(RenderGraph graph)
    {
        ValidateNotEmpty(graph);
        ValidatePassNamesUnique(graph);
        ValidateGraphicsPassAttachments(graph);
        ValidatePassShaders(graph);
        ValidateReadsDeclaredEarlier(graph);
    }

    private static void ValidateNotEmpty(RenderGraph graph)
    {
        if (graph.PassOrder.Count == 0)
        {
            throw new InvalidOperationException(
                "RenderGraph has no declared passes. Declare at least one GraphicsPass or ComputePass before Compile().");
        }
    }

    private static void ValidatePassNamesUnique(RenderGraph graph)
    {
        var seen = new HashSet<string>();
        foreach (var id in graph.PassOrder)
        {
            var name = LookupPassName(graph, id);
            if (!seen.Add(name))
            {
                throw new InvalidOperationException(
                    $"Duplicate pass name '{name}'. Each pass declared via GraphicsPass(name) or ComputePass(name) must have a unique name within a graph.");
            }
        }
    }

    private static void ValidateGraphicsPassAttachments(RenderGraph graph)
    {
        foreach (var pass in graph.GraphicsPasses.Values)
        {
            if (pass.ColorTargets.Count == 0 && pass.Depth is null)
            {
                throw new InvalidOperationException(
                    $"GraphicsPass '{pass.Name}' has no Target or Depth attachment. A graphics pass must declare at least one render target.");
            }
        }
    }

    private static void ValidatePassShaders(RenderGraph graph)
    {
        foreach (var pass in graph.GraphicsPasses.Values)
        {
            if (pass.Shaders.Count == 0)
            {
                throw new InvalidOperationException(
                    $"GraphicsPass '{pass.Name}' has no Shader declared. Declare the supported shader interfaces via .Shader(...).");
            }
        }
        foreach (var pass in graph.ComputePasses.Values)
        {
            if (pass.Shader is null)
            {
                throw new InvalidOperationException(
                    $"ComputePass '{pass.Name}' has no Shader declared. Declare its shader interface via .Shader(...).");
            }
        }
    }

    // Every Read in declaration order must reference a resource that was
    // declared as a Target / Depth / Write by an EARLIER pass. Walking
    // in declaration order makes the cycle case fall out naturally:
    // a cycle would require a later pass to be the producer, which means
    // the Read sees an empty producer set and fails here with a
    // 'not declared by any prior pass' message.
    private static void ValidateReadsDeclaredEarlier(RenderGraph graph)
    {
        var declaredEarlier = new HashSet<int>();

        foreach (var passId in graph.PassOrder)
        {
            // Check this pass's Reads against everything declared by prior passes.
            if (graph.GraphicsPasses.TryGetValue(passId, out var gpass))
            {
                foreach (var read in gpass.Reads)
                {
                    if (!declaredEarlier.Contains(read.Resource.Id))
                    {
                        var resourceName = LookupResourceName(graph, read.Resource.Id);
                        throw new InvalidOperationException(
                            $"GraphicsPass '{gpass.Name}' reads resource '{resourceName}' (id {read.Resource.Id}) which is not declared as a Target/Depth/Write by any prior pass.");
                    }
                }
            }
            else if (graph.ComputePasses.TryGetValue(passId, out var cpass))
            {
                foreach (var read in cpass.Reads)
                {
                    if (!declaredEarlier.Contains(read.Resource.Id))
                    {
                        var resourceName = LookupResourceName(graph, read.Resource.Id);
                        throw new InvalidOperationException(
                            $"ComputePass '{cpass.Name}' reads resource '{resourceName}' (id {read.Resource.Id}) which is not declared as a Target/Depth/Write by any prior pass.");
                    }
                }
            }

            // Add this pass's outputs to the declared set for subsequent passes.
            if (gpass is not null)
            {
                foreach (var t in gpass.ColorTargets) declaredEarlier.Add(t.View.Resource.Id);
                if (gpass.Depth is { } d) declaredEarlier.Add(d.View.Resource.Id);
                // <b>A resolve target is written by this pass too.</b> Omitting them made the
                // validator reject the only arrangement resolves exist for: a pass renders
                // multisampled and resolves to 1x SO THAT a later pass can sample it, and a later
                // pass that samples it was then told its producer did not exist. The hole went
                // unnoticed because every existing resolve was consumed by a present pass recorded
                // straight on the command list, outside the graph and so outside this check.
                foreach (var r in gpass.ResolveTargets) declaredEarlier.Add(r.Resource.Id);
                if (gpass.DepthResolveTarget is { } dr) declaredEarlier.Add(dr.Resource.Id);
            }
            else if (graph.ComputePasses.TryGetValue(passId, out var cpassOut))
            {
                foreach (var w in cpassOut.Writes) declaredEarlier.Add(w.Resource.Id);
            }
        }
    }

    private static string LookupPassName(RenderGraph graph, int id) =>
        graph.GraphicsPasses.TryGetValue(id, out var gpass) ? gpass.Name :
        graph.ComputePasses.TryGetValue(id, out var cpass) ? cpass.Name :
        $"<unknown:{id}>";

    private static string LookupResourceName(RenderGraph graph, int id) =>
        graph.Resources.TryGetValue(id, out var res) ? res.Name : $"<unknown:{id}>";
}
