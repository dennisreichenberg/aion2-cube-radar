using System.Runtime.InteropServices;
using CubeRadar.Core.Audio;

namespace CubeRadar.Cli;

/// <summary>
/// Plays the new-cube sound at the set volume (see <see cref="WavVolume"/>) through a waveOut device that stays open
/// between sounds. An open device keeps the radar's own audio session alive, so Windows lists "Cube Radar" in the volume
/// mixer from the start (own slider, independent of the muted-or-not "System sounds"); with PlaySound the session only
/// came with the first sound. The session is active only while a sound plays. The device is reopened for every sound
/// (current default output, format of that sound); a new sound replaces one that is still playing.
/// </summary>
internal static class CubeSound
{
    private const uint WaveMapper = 0xFFFFFFFF;
    private static readonly int HeaderSize = Marshal.SizeOf<WaveHeader>();
    private static readonly object Gate = new();
    private static IntPtr _device, _header, _samples;

    /// <summary>Opens the device without playing anything, so the radar shows up in the volume mixer right away.</summary>
    public static void Prepare()
    {
        lock (Gate)
        {
            Release();
            var beep = WavVolume.Beep(0);
            Open(beep, WavVolume.Find(beep)!.Value);
        }
    }

    /// <summary>Plays the user's .wav, or the beep; returns a note for the log when the .wav could not be used as is.</summary>
    public static string? Play(string? wavFile, int volumePct)
    {
        string? note = null;
        byte[]? wav = null;
        if (wavFile is { Length: > 0 })
        {
            try
            {
                wav = File.ReadAllBytes(wavFile);
                switch (WavVolume.Scale(wav, volumePct))
                {
                    case WavVolume.ScaleResult.NotWav:
                        note = $"sound file is not a WAV file, using the beep: {wavFile}";
                        wav = null;
                        break;
                    case WavVolume.ScaleResult.UnsupportedFormat:
                        note = $"sound file is compressed, volume not adjustable: {wavFile}";
                        break;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                note = $"sound file not readable ({ex.Message}), using the beep";
            }
        }
        if (wav is not null && Start(wav)) return note;
        if (wav is not null) note = $"sound file not playable, using the beep: {wavFile}";
        if (!Start(WavVolume.Beep(volumePct))) note = (note is null ? "" : note + "; ") + "no sound output device";
        return note;
    }

    public static void Close()
    {
        lock (Gate) Release();
    }

    private static bool Start(byte[] wav)
    {
        lock (Gate)
        {
            Release();
            if (WavVolume.Find(wav) is not { } layout || !Open(wav, layout)) return false;
            _samples = Marshal.AllocHGlobal(layout.DataLength);
            Marshal.Copy(wav, layout.DataOffset, _samples, layout.DataLength);
            _header = Marshal.AllocHGlobal(HeaderSize);
            Marshal.StructureToPtr(new WaveHeader { Data = _samples, BufferLength = (uint)layout.DataLength }, _header, false);
            if (waveOutPrepareHeader(_device, _header, HeaderSize) == 0 && waveOutWrite(_device, _header, HeaderSize) == 0) return true;
            Release();
            return false;
        }
    }

    private static bool Open(byte[] wav, WavVolume.Layout layout)
    {
        // The fmt chunk is the WAVEFORMATEX; a plain 16-byte PCM one lacks the trailing cbSize (0).
        var size = Math.Max(layout.FormatLength, 18);
        var format = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, format, size);
            Marshal.Copy(wav, layout.FormatOffset, format, layout.FormatLength);
            if (waveOutOpen(out _device, WaveMapper, format, IntPtr.Zero, IntPtr.Zero, 0) == 0) return true;
            _device = IntPtr.Zero;
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(format);
        }
    }

    /// <summary>Stops the current sound and closes the device.</summary>
    private static void Release()
    {
        if (_device != IntPtr.Zero)
        {
            waveOutReset(_device);
            if (_header != IntPtr.Zero) waveOutUnprepareHeader(_device, _header, HeaderSize);
            waveOutClose(_device);
            _device = IntPtr.Zero;
        }
        if (_header != IntPtr.Zero) Marshal.FreeHGlobal(_header);
        if (_samples != IntPtr.Zero) Marshal.FreeHGlobal(_samples);
        _header = _samples = IntPtr.Zero;
    }

    /// <summary>WAVEHDR.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength, BytesRecorded;
        public IntPtr User;
        public uint Flags, Loops;
        public IntPtr Next, Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, uint deviceId, IntPtr format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);
}
