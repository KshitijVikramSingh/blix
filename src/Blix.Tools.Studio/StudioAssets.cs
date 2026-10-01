using Blix;

namespace Blix.Tools.Studio;

// What the stage knows about each model it loaded, keyed by the engine type a tool holds: the bone
// buffers, the resolved materials and the textures Studio applies its policy through. Private to the
// stage, and reached by a view through the StudioDraw it is handed, so a tool deals only in engine
// Models.
internal sealed class StudioAssets : IDisposable
{
    private readonly Dictionary<Model, StudioModel> models = new(ReferenceEqualityComparer.Instance);

    public Model Add(StudioModel model)
    {
        models.Add(model.Model, model);
        return model.Model;
    }

    public StudioModel For(Model model) => models.TryGetValue(model, out var studio)
        ? studio
        : throw new InvalidOperationException($"Model '{model.Name}' was not loaded through this stage (StudioRenderer.LoadModel).");

    public void Remove(Model model)
    {
        if (models.Remove(model, out var studio)) studio.Dispose();
    }

    public void Dispose()
    {
        foreach (var model in models.Values) model.Dispose();
        models.Clear();
    }
}
