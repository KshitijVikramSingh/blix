using System.Numerics;

namespace Blix.Audio;

// Per-source playback state. Pushed to the device once per frame via
// IAudioDevice.UpdateSource — the device diffs against its current state and
// only forwards changed fields to the backend. State-based (whole struct each
// call) was chosen over field-based setters because it's simpler at the call
// site and the per-frame source count is small enough that diff cost is noise.
//
// Velocity is intentionally omitted (no Doppler in v0). Game code that wants
// Doppler later derives velocity from position deltas across frames; the
// device gets a `Velocity` field at that point.
//
// Cone parameters (directional spotlight-style audio) are out of scope for v0
// — all sources are omnidirectional. AL_INVERSE_DISTANCE_CLAMPED is the
// distance model used globally; per-source RolloffFactor stays at 1.0.
public readonly record struct AudioSourceState(
    Vector3 Position,
    float Gain = 1.0f,
    float Pitch = 1.0f,
    bool IsLooping = false,
    // Inside this distance gain == max; beyond it falls off per the distance model.
    float ReferenceDistance = 1.0f,
    // Hard clamp on the distance used for attenuation. Anything past MaxDistance
    // is treated as MaxDistance — prevents sounds going completely silent.
    float MaxDistance = 100.0f);

// Listener state. Position is the listener's world location; OrientationForward
// + OrientationUp describe the listener's head pose (analogous to a camera's
// look-at). Gain is the master volume — scales every source uniformly.
public readonly record struct AudioListenerState(
    Vector3 Position,
    Vector3 OrientationForward,
    Vector3 OrientationUp,
    float Gain = 1.0f);
