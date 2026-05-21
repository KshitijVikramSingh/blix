namespace Blix.Diagnostics;

public interface IDebugHost
{
    DebugContext? CurrentDebug { get; }
}
