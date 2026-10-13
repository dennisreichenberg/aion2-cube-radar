using System.Buffers.Binary;

namespace CubeRadar.Core.Audio;

/// <summary>
/// The radar's sound at an adjustable volume, as WAV bytes: the built-in two-tone beep is generated, a user .wav has
/// its samples scaled. Pure byte work; playing is up to the caller.
/// </summary>
public static class WavVolume
{
    public const int SampleRate = 44_100;

    /// <summary>Peak of the beep at 100 % (a full-scale sine at 1–2 kHz is piercing).</summary>
    private const double BeepPeak = 0.7;

    /// <summary>Volume 0–100 % to an amplitude factor. Squared, so the slider feels even to the ear
    /// (50 % ≈ −12 dB) instead of only changing near the bottom.</summary>
    public static double Gain(int volumePct) => Math.Pow(Math.Clamp(volumePct, 0, 100) / 100.0, 2);

    /// <summary>1200 Hz for 120 ms, 20 ms pause, 1600 Hz for 160 ms (the former Console.Beep pair), with 5 ms fades
    /// against clicks; 16-bit mono.</summary>
    public static byte[] Beep(int volumePct)
    {
        (int Hz, int Ms)[] parts = [(1200, 120), (0, 20), (1600, 160)];
        var amplitude = Gain(volumePct) * BeepPeak * short.MaxValue;
        var fade = SampleRate * 5 / 1000;
        var samples = new List<short>();
        foreach (var (hz, ms) in parts)
        {
            var n = SampleRate * ms / 1000;
            for (var i = 0; i < n; i++)
            {
                var envelope = Math.Min(1.0, Math.Min(i, n - 1 - i) / (double)fade);
                samples.Add(hz == 0 ? (short)0 : (short)Math.Round(Math.Sin(2 * Math.PI * hz * i / SampleRate) * envelope * amplitude));
            }
        }

        var data = samples.Count * 2;
        var wav = new byte[44 + data];
        var s = wav.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteInt32LittleEndian(s[4..], 36 + data);
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(s[20..], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(s[22..], 1); // mono
        BinaryPrimitives.WriteInt32LittleEndian(s[24..], SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(s[28..], SampleRate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[32..], 2);
        BinaryPrimitives.WriteInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(s[40..], data);
        for (var i = 0; i < samples.Count; i++) BinaryPrimitives.WriteInt16LittleEndian(s[(44 + 2 * i)..], samples[i]);
        return wav;
    }

    /// <summary>Where a .wav keeps its format (the WAVEFORMATEX in the "fmt " chunk) and its samples.</summary>
    public readonly record struct Layout(int FormatOffset, int FormatLength, int DataOffset, int DataLength);

    /// <summary>The <see cref="Layout"/> of a .wav, or null if it is not a WAV file.</summary>
    public static Layout? Find(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8)) return null;
        int fmtAt = -1, fmtSize = 0;
        for (var at = 12; at + 8 <= wav.Length;)
        {
            var id = wav.Slice(at, 4);
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav[(at + 4)..]), (uint)(wav.Length - at - 8));
            if (id.SequenceEqual("fmt "u8) && size >= 16) (fmtAt, fmtSize) = (at + 8, size);
            else if (id.SequenceEqual("data"u8)) return fmtAt < 0 ? null : new Layout(fmtAt, fmtSize, at + 8, size);
            at += 8 + size + (size & 1);
        }
        return null;
    }

    public enum ScaleResult { Scaled, NotWav, UnsupportedFormat }

    /// <summary>Scales the samples of a .wav in place. PCM 8/16/24/32 bit and 32-bit float (also as
    /// WAVE_FORMAT_EXTENSIBLE); anything else (ADPCM, MP3 in WAV, …) is left as it is.</summary>
    public static ScaleResult Scale(byte[] wav, int volumePct)
    {
        if (Find(wav) is not { } w) return ScaleResult.NotWav;
        var fmt = wav.AsSpan(w.FormatOffset, w.FormatLength);
        int tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt), bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]);
        if (tag == 0xFFFE && fmt.Length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt[24..]); // sub-format GUID starts with the tag
        return ScaleSamples(wav.AsSpan(w.DataOffset, w.DataLength), tag, bits, Gain(volumePct))
            ? ScaleResult.Scaled
            : ScaleResult.UnsupportedFormat;
    }

    private static bool ScaleSamples(Span<byte> data, int tag, int bits, double gain)
    {
        switch (tag, bits)
        {
            case (1, 8): // unsigned around 128
                for (var i = 0; i < data.Length; i++) data[i] = (byte)Math.Clamp(Math.Round((data[i] - 128) * gain) + 128, 0, 255);
                return true;
            case (1, 16):
                for (var i = 0; i + 2 <= data.Length; i += 2)
                    BinaryPrimitives.WriteInt16LittleEndian(data[i..], (short)Math.Round(BinaryPrimitives.ReadInt16LittleEndian(data[i..]) * gain));
                return true;
            case (1, 24):
                for (var i = 0; i + 3 <= data.Length; i += 3)
                {
                    var v = (int)Math.Round(((data[i] | data[i + 1] << 8 | (sbyte)data[i + 2] << 16)) * gain);
                    data[i] = (byte)v;
                    data[i + 1] = (byte)(v >> 8);
                    data[i + 2] = (byte)(v >> 16);
                }
                return true;
            case (1, 32):
                for (var i = 0; i + 4 <= data.Length; i += 4)
                    BinaryPrimitives.WriteInt32LittleEndian(data[i..], (int)Math.Round(BinaryPrimitives.ReadInt32LittleEndian(data[i..]) * gain));
                return true;
            case (3, 32):
                for (var i = 0; i + 4 <= data.Length; i += 4)
                    BinaryPrimitives.WriteSingleLittleEndian(data[i..], (float)(BinaryPrimitives.ReadSingleLittleEndian(data[i..]) * gain));
                return true;
            default:
                return false;
        }
    }
}
