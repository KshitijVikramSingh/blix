namespace Blix.Runtime.OpenTK;

public sealed record WindowOptions(
    string Title,
    int Width,
    int Height)
{
    public static WindowOptions Default { get; } = new(
        Title: "Blix",
        Width: 1280,
        Height: 720);
}
