namespace CubeRadar.Core.Update;

/// <summary>
/// Puts a downloaded exe in place of the installed one. A running .NET single-file exe must not be renamed or replaced:
/// it loads parts of itself from its path on demand (seen: after a rename the old radar crashed while closing). So the
/// running radar only downloads the new exe next to itself (<see cref="StagingPath"/>), starts it as installer and
/// quits; the installer waits until the old radar is gone, copies itself over the installed exe (<see cref="Install"/>)
/// and starts it. That start removes the installer copy again (<see cref="CleanUp"/>).
/// </summary>
public static class SelfReplace
{
    /// <summary>Where the new exe is downloaded to and started from as installer: next to the installed one.</summary>
    public static string StagingPath(string exe) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, Path.GetFileNameWithoutExtension(exe) + ".update.exe");

    /// <summary>
    /// Replaces <paramref name="exe"/> with a copy of <paramref name="staged"/>: copy next to it, then one atomic move, so
    /// a failed copy never leaves a broken exe. Retries until <paramref name="patience"/> runs out while the exe is
    /// still in use (a radar that is closing, a virus scanner).
    /// </summary>
    public static void Install(string staged, string exe, TimeSpan patience)
    {
        var temp = exe + ".new";
        var until = DateTime.UtcNow + patience;
        while (true)
        {
            try
            {
                File.Copy(staged, temp, overwrite: true);
                File.Move(temp, exe, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (DateTime.UtcNow >= until)
                {
                    TryDelete(temp);
                    throw;
                }
                Thread.Sleep(250);
            }
        }
    }

    /// <summary>Removes what an update left next to the exe: the installer copy and an unfinished replacement.</summary>
    /// <returns>How many files were removed (a copy that still runs stays until a later start).</returns>
    public static int CleanUp(string exe) =>
        new[] { StagingPath(exe), exe + ".new" }.Count(f => File.Exists(f) && TryDelete(f));

    internal static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
