using Blix.Audio;
using Blix.Cooked;

namespace Blix.Assets;

// Bare-minimum RIFF/WAVE parser. Hand-rolled rather than NAudio'd because:
//   - PCM-only is roughly 60 lines of code, fits one file.
//   - Adds zero NuGet weight.
//   - The format is fully documented and the demo only needs uncompressed PCM.
//
// Recognises:
//   - 16-bit signed little-endian PCM (WAVE_FORMAT_PCM, format tag 1).
//   - 8-bit unsigned PCM (WAVE_FORMAT_PCM, format tag 1).
//   - Mono and stereo.
//
// Rejects (throws AssetImportException):
//   - Float WAVs (tag 3, WAVE_FORMAT_IEEE_FLOAT).
//   - 24/32-bit integer PCM.
//   - A-law, μ-law, ADPCM, anything compressed.
//   - Files with >2 channels.
//
// Tolerates LIST/JUNK/INFO chunks before the data chunk by skipping any chunk
// that isn't fmt or data — those metadata chunks are common in WAVs exported
// by Audacity/Reaper/etc. and would otherwise fail a strict-order parser.
public sealed class WavImporter : IAssetImporter<AudioClipData>
{
    public string Name => "audio.wav";

    public AudioClipData Import(AssetImportContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!File.Exists(context.SourcePath))
        {
            throw new FileNotFoundException($"WAV file not found: {context.SourcePath}", context.SourcePath);
        }

        var bytes = File.ReadAllBytes(context.SourcePath);
        return Parse(bytes, context.SourcePath);
    }

    private static AudioClipData Parse(byte[] bytes, string sourceLabel)
    {
        if (bytes.Length < 44)
        {
            throw new AssetImportException(sourceLabel, null, $"WAV too short ({bytes.Length} bytes) — minimum header is 44 bytes.");
        }

        // RIFF header: "RIFF" + chunkSize (4 bytes, ignored) + "WAVE".
        if (ReadAscii(bytes, 0, 4) != "RIFF")
        {
            throw new AssetImportException(sourceLabel, null, "missing RIFF header.");
        }
        if (ReadAscii(bytes, 8, 4) != "WAVE")
        {
            throw new AssetImportException(sourceLabel, null, "RIFF chunk is not WAVE.");
        }

        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        int audioFormat = 0;
        byte[]? pcmData = null;

        var cursor = 12;
        while (cursor + 8 <= bytes.Length)
        {
            var chunkId = ReadAscii(bytes, cursor, 4);
            var chunkSize = BitConverter.ToInt32(bytes, cursor + 4);
            var chunkStart = cursor + 8;

            // Bounds-check the declared chunk size before trusting it: a negative
            // or oversized value (malformed/malicious file) would otherwise either
            // loop the cursor backward or throw inside Array.Copy without a
            // structured error.
            if (chunkSize < 0 || (long)chunkStart + chunkSize > bytes.Length)
            {
                throw new AssetImportException(
                    sourceLabel, null,
                    $"chunk '{chunkId}' declares size {chunkSize} that exceeds the file.");
            }

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    throw new AssetImportException(sourceLabel, null, $"fmt chunk too small ({chunkSize} bytes).");
                }
                audioFormat = BitConverter.ToInt16(bytes, chunkStart + 0);
                channels = BitConverter.ToInt16(bytes, chunkStart + 2);
                sampleRate = BitConverter.ToInt32(bytes, chunkStart + 4);
                // bytes 8..12 = byteRate, 12..14 = blockAlign — both derivable.
                bitsPerSample = BitConverter.ToInt16(bytes, chunkStart + 14);
            }
            else if (chunkId == "data")
            {
                pcmData = new byte[chunkSize];
                Array.Copy(bytes, chunkStart, pcmData, 0, chunkSize);
                // Stop after data — WAVs occasionally carry extra junk after the
                // sample payload and parsing it adds nothing.
                break;
            }
            // Anything else (LIST, JUNK, INFO, …) is skipped silently.

            // Chunks are 2-byte-aligned: an odd chunkSize gets a pad byte.
            cursor = chunkStart + chunkSize + (chunkSize & 1);
        }

        if (pcmData is null)
        {
            throw new AssetImportException(sourceLabel, null, "no data chunk found.");
        }

        if (audioFormat != 1)
        {
            throw new AssetImportException(sourceLabel, null, $"unsupported WAVE format tag {audioFormat}. v0 covers only PCM (tag 1) — float/ADPCM/μ-law/A-law are explicitly out.");
        }

        if (channels is not (1 or 2))
        {
            throw new AssetImportException(sourceLabel, null, $"unsupported channel count {channels}. v0 covers mono (1) and stereo (2).");
        }

        if (bitsPerSample is not (8 or 16))
        {
            throw new AssetImportException(sourceLabel, null, $"unsupported bit depth {bitsPerSample}. v0 covers 8-bit unsigned and 16-bit signed PCM.");
        }

        return new AudioClipData(sampleRate, channels, bitsPerSample, pcmData);
    }

    private static string ReadAscii(byte[] bytes, int offset, int length)
    {
        return System.Text.Encoding.ASCII.GetString(bytes, offset, length);
    }
}
