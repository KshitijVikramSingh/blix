using Blix.Graphics;
using Blix.Render;

namespace Blix;

// Not sealed — AnimatedGameObject extends this to add animation behavior. The base
// stays a plain data container; subclasses opt into specific capabilities (IUpdateable,
// IAnimated, etc.) explicitly. Game code can subclass for its own per-object behavior
// without touching the engine.
//
// Material is a backend-resolved MaterialHandle (set-2 descriptor binding on the
// Vulkan backend), not a name-keyed material bag. The handle is opaque; the device
// owns what it points at.
public class GameObject
{
    public GameObject(string name, Mesh mesh, MaterialHandle material, Transform3D? transform = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(mesh);

        Name = name;
        Mesh = mesh;
        Material = material;
        Transform = transform ?? new Transform3D();
    }

    public string Name { get; }

    public Mesh Mesh { get; set; }

    public MaterialHandle Material { get; set; }

    public Transform3D Transform { get; set; }
}
