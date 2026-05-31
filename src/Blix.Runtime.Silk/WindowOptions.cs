namespace Blix.Runtime.Silk;

// Window configuration passed to the runtime at construction.
public sealed record WindowOptions(string Title, int Width, int Height)
{
    public static readonly WindowOptions Default = new("Blix", 1280, 720);
}
