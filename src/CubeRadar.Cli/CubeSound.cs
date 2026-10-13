using System.Runtime.InteropServices;
using CubeRadar.Core.Audio;

namespace CubeRadar.Cli;

/// <summary>
/// Plays the new-cube sound at the set volume (see <see cref="WavVolume"/>): asynchronously from memory through winmm
/// PlaySound. A new sound replaces one that is still playing, so dragging the volume slider never stacks sounds.
/// </summary>
internal static class CubeSound
{
    private const uint SndAsync = 0x0001, SndNoDefault = 0x0002, SndMemory = 0x0004;
    private static readonly object Gate = new();
    private static IntPtr _playing; // PlaySound reads from it until the sound ends or is stopped

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
        Start(WavVolume.Beep(volumePct));
        return note;
    }

    private static bool Start(byte[] wav)
    {
        lock (Gate)
        {
            // Stop the current sound first (synchronous), only then is its buffer free.
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            if (_playing != IntPtr.Zero) Marshal.FreeHGlobal(_playing);
            _playing = Marshal.AllocHGlobal(wav.Length);
            Marshal.Copy(wav, 0, _playing, wav.Length);
            return PlaySound(_playing, IntPtr.Zero, SndAsync | SndNoDefault | SndMemory);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
}
