namespace Blix.Audio;

// CPU-side PCM. The WAV importer produces this; IAudioDevice.CreateClip takes it
// to upload into a backend audio buffer.
//
// Format constraints carried by v0:
//   - Channels: 1 (mono, positional-eligible) or 2 (stereo, plays unpositionalised).
//   - BitsPerSample: 8 (unsigned) or 16 (signed little-endian) — the two PCM
//     formats OpenAL supports natively. 24/32-bit and float reject in the importer.
//   - SampleRate: any rate the backend accepts (OpenAL Soft resamples on its own).
//
// PcmData is interleaved sample bytes, exactly the layout OpenAL expects to take
// in alBufferData — no per-channel split, no padding between samples.
public sealed record AudioClipData(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    byte[] PcmData)
{
    // Total playback time at the source's reference pitch. Useful for one-shot
    // SFX schedulers to know when a non-looping source has naturally finished
    // even without polling IsPlaying.
    public double DurationSeconds =>
        Channels == 0 || SampleRate == 0 || BitsPerSample == 0
            ? 0.0
            : (double)PcmData.Length / (Channels * (BitsPerSample / 8)) / SampleRate;
}
