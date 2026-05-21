namespace Blix.Assets;

public readonly record struct AssetId
{
    public string Value { get; }

    private AssetId(string value)
    {
        Value = value;
    }

    public static AssetId Parse(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            throw new ArgumentException("Asset id must not be empty.", nameof(value));
        }

        if (value.StartsWith('/') || value.EndsWith('/'))
        {
            throw new ArgumentException($"Asset id '{value}' must not start or end with '/'.", nameof(value));
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"Asset id '{value}' must not contain '..'.", nameof(value));
        }

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                throw new ArgumentException($"Asset id '{value}' must not contain whitespace.", nameof(value));
            }
        }

        return new AssetId(value);
    }

    public override string ToString() => Value;
}
