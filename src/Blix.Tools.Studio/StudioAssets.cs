using Blix;

namespace Blix.Tools.Studio;

// What the stage knows about each asset it loaded, keyed by the engine type a tool holds: the bone
// buffers, the resolved materials and the textures Studio applies its policy through. Private to the
// stage, and reached by a view through the StudioDraw it is handed, so a tool deals only in engine
// Models and Rigs.
internal sealed class StudioAssets : IDisposable
{
    private readonly Dictionary<Rig, StudioRig> rigs = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Model, StudioModel> models = new(ReferenceEqualityComparer.Instance);

    public Rig Add(StudioRig rig)
    {
        rigs.Add(rig.Rig, rig);
        return rig.Rig;
    }

    public Model Add(StudioModel model)
    {
        models.Add(model.Model, model);
        return model.Model;
    }

    public StudioRig For(Rig rig) => rigs.TryGetValue(rig, out var studio)
        ? studio
        : throw new InvalidOperationException($"Rig '{rig.Name}' was not loaded through this stage (StudioRenderer.LoadRig).");

    public StudioModel For(Model model) => models.TryGetValue(model, out var studio)
        ? studio
        : throw new InvalidOperationException($"Model '{model.Name}' was not loaded through this stage (StudioRenderer.LoadModel).");

    public void Remove(Rig rig)
    {
        if (rigs.Remove(rig, out var studio)) studio.Dispose();
    }

    public void Remove(Model model)
    {
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
