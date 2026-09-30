using Blix;

namespace Blix.Tools.Studio;

// What the stage knows about each asset it loaded, keyed by the engine type a tool holds: the bone
// buffers, the resolved materials and the textures Studio applies its policy through. Private to the
// stage, and reached by a view through the StudioDraw it is handed, so a tool deals only in engine
// Models — loaded skinned for a RigView, static for a ModelView.
internal sealed class StudioAssets : IDisposable
{
    private readonly Dictionary<Model, StudioRig> rigs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Model, StudioModel> models = new(ReferenceEqualityComparer.Instance);

    public Model Add(StudioRig rig)
    {
        rigs.Add(rig.Model, rig);
        return rig.Model;
    }

    public Model Add(StudioModel model)
    {
        models.Add(model.Model, model);
        return model.Model;
    }

    public StudioRig RigFor(Model rig) => rigs.TryGetValue(rig, out var studio)
        ? studio
        : throw new InvalidOperationException($"'{rig.Name}' was not loaded as a rig through this stage (StudioRenderer.LoadRig).");

    public StudioModel ModelFor(Model model) => models.TryGetValue(model, out var studio)
        ? studio
        : throw new InvalidOperationException($"Model '{model.Name}' was not loaded through this stage (StudioRenderer.LoadModel).");

    public void Remove(Model model)
    {
        if (rigs.Remove(model, out var rig)) rig.Dispose();
        if (models.Remove(model, out var studio)) studio.Dispose();
    }

    public void Dispose()
    {
        foreach (var rig in rigs.Values) rig.Dispose();
        foreach (var model in models.Values) model.Dispose();
        rigs.Clear();
        models.Clear();
    }
}
