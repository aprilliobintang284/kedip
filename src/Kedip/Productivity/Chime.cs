using System.IO;
using System.Media;
using Kedip.Core;

namespace Kedip.Productivity;

/// <summary>
/// Plays a short generated two-tone "ding" (no asset files). The WAV is synthesized once, in memory,
/// on first use; playback is asynchronous and audio errors are swallowed.
/// </summary>
public static class Chime
{
    private const int SampleRate = 22050;
    private const double LengthSeconds = 0.72;

    private static SoundPlayer? _player;
    private static bool _failed;

    /// <summary>Call on the UI thread. Never blocks on playback.</summary>
    public static void Play()
    {
        if (_failed)
        {
            return;
        }

        try
        {
            if (_player is null)
            {
                var player = new SoundPlayer(new MemoryStream(BuildWave(), writable: false));
                player.Load();
                _player = player;
            }

            _player.Play();
        }
        catch (Exception ex)
        {
            // No audio device, or the sound system is unavailable: stay silent from now on.
            _failed = true;
            Log.Warn($"Chime unavailable: {ex.Message}");
        }
    }

    /// <summary>16-bit mono PCM WAV: 880 Hz, then 1320 Hz (a fifth above), soft attack, exponential decay.</summary>
    internal static byte[] BuildWave()
    {
        int samples = (int)(SampleRate * LengthSeconds);
        var pcm = new short[samples];
        int fadeOut = SampleRate * 30 / 1000;

        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / SampleRate;
            double value = Note(t, 0.00, 880, 0.16) * 0.55 + Note(t, 0.13, 1320, 0.22) * 0.50;
            if (i > samples - fadeOut)
            {
                value *= (double)(samples - i) / fadeOut;
            }

            // Moderate volume: peaks stay well below full scale.
            pcm[i] = (short)Math.Clamp(Math.Round(value * 0.42 * short.MaxValue), short.MinValue, short.MaxValue);
        }

        using var stream = new MemoryStream(44 + samples * 2);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            int dataBytes = samples * 2;
            writer.Write("RIFF"u8);
            writer.Write(36 + dataBytes);
            writer.Write("WAVE"u8);
            writer.Write("fmt "u8);
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write((short)1); // mono
            writer.Write(SampleRate);
            writer.Write(SampleRate * 2); // byte rate
            writer.Write((short)2); // block align
            writer.Write((short)16); // bits per sample
            writer.Write("data"u8);
            writer.Write(dataBytes);
            foreach (short sample in pcm)
            {
                writer.Write(sample);
            }
        }

        return stream.ToArray();
    }

    /// <summary>A bell-ish partial: fundamental plus a quiet octave, 6 ms attack, exponential decay.</summary>
    private static double Note(double t, double start, double frequency, double decaySeconds)
    {
        double local = t - start;
        if (local < 0)
        {
            return 0;
        }

        double attack = Math.Min(1, local / 0.006);
        double envelope = attack * Math.Exp(-local / decaySeconds);
        double phase = 2 * Math.PI * frequency * local;
        return envelope * (Math.Sin(phase) + 0.18 * Math.Sin(2 * phase) * Math.Exp(-local / 0.08));
    }
}
