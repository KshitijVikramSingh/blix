using Blix.Render;

namespace Blix;

// Not sealed — AnimatedGameObject extends this to add animation behavior. The base
// stays a plain data container; subclasses opt into specific capabilities (IUpdateable,
// IAnimated, etc.) explicitly. Game code can subclass for its own per-object behavior
// without touching the engine.
public class GameObject
{
    public GameObject(string name, Mesh mesh, Material material, Transform3D? transform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(material);

        Name = name;
        Mesh = mesh;
        Material = material;
        Transform = transform ?? new Transform3D();
    }

    public string Name { get; }

    public Mesh Mesh { get; set; }

    public Material Material { get; set; }

    public Transform3D Transform { get; set; }
}
