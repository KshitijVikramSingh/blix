using Blix.Audio;

namespace Blix.Core;

// Companion to IRenderHost: a host that owns an audio device. The runtime
// (Blix.Runtime.OpenTK's Window) implements both — game code accesses
// either facet by casting Host. Separated from IRenderHost so a headless / test
// host can implement just IAudioHost (or just IRenderHost) without dragging in
// the other subsystem's lifecycle.
public interface IAudioHost
{
    IAudioDevice AudioDevice { get; }
}
