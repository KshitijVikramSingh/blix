using System.Numerics;
using Blix.Audio;

namespace Blix;

// Game-layer wrapper for the listener. Holds a Transform3D reference (typically
// the main camera's Transform) so Sync can read pose from a single source of
// truth — point the listener at a Camera.Transform and 3D audio follows the
// view without any per-frame plumbing on the caller's side.
//
// One listener per scene. OpenAL itself only has one global listener slot, so
// instantiating a second AudioListener and calling Sync on it just overwrites
// the first listener's pose on the next frame.
public sealed class AudioListener
{
    public AudioListener(Transform3D transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        Transform = transform;
    }

    public Transform3D Transform { get; set; }

    // Master output gain. 1.0 = unity, 0.0 = silent. Game-side mute/volume
    // sliders work by setting this — every source is scaled by it before
    // distance attenuation runs in the backend.
    public float Gain { get; set; } = 1.0f;

    public void Sync(IAudioDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        device.UpdateListener(new AudioListenerState(
            Position: Transform.Position,
            OrientationForward: Transform.Forward,
            OrientationUp: Transform.Up,
            Gain: Gain));
    }
}
