namespace Blix.Diagnostics;

public interface IDebuggable
{
    string DebugName { get; }

    void Debug(DebugContext debug);
}
