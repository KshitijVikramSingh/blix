namespace Blix.Runtime.Silk;

// Match Blix.Runtime.OpenTK.WindowOptions surface so demos that move between
// runtimes only swap the using directive.
public sealed record WindowOptions(string Title, int Width, int Height)
{
    public static readonly WindowOptions Default = new("Blix", 1280, 720);
}
