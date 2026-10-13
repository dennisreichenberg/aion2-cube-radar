namespace CubeRadar.Cli;

/// <summary>
/// Where the assets (web UI, default config) and the user data (captures, database, settings, logs) live.
/// <list type="bullet">
/// <item>Repository or unpacked folder (a <c>config\opcodes.json</c> next to the exe or above it, or in the working
/// directory): both are that folder, as before.</item>
/// <item>Single <c>Cube Radar.exe</c>: the assets are embedded and unpacked to
/// <c>%LOCALAPPDATA%\CubeRadar\app\&lt;version&gt;\</c>; data goes to <c>%LOCALAPPDATA%\CubeRadar\</c>. A config file
/// placed in <c>%LOCALAPPDATA%\CubeRadar\config\</c> overrides the built-in one (e.g. new opcodes after a patch).</item>
/// </list>
/// </summary>
internal static class AppPaths
{
    public static string AssetRoot { get; private set; } = "";
    public static string DataRoot { get; private set; } = "";
    public static bool SingleExe { get; private set; }

    public static string WebRoot => Path.Combine(AssetRoot, "wwwroot");
    public static string Captures => Path.Combine(DataRoot, "captures");
    public static string DataDir => Path.Combine(DataRoot, "data");

    /// <summary>A config file: the user's override (single exe only), else the built-in / repository one.</summary>
    public static string Config(string file)
    {
        var user = Path.Combine(DataRoot, "config", file);
        return SingleExe && File.Exists(user) ? user : Path.Combine(AssetRoot, "config", file);
    }

    public static void Init()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
                if (File.Exists(Path.Combine(d.FullName, "config", "opcodes.json")))
                {
                    AssetRoot = DataRoot = d.FullName;
                    return;
                }

        SingleExe = true;
        DataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CubeRadar");
        AssetRoot = Path.Combine(DataRoot, "app", typeof(AppPaths).Assembly.GetName().Version?.ToString(3) ?? "0");
        Unpack();
    }

    /// <summary>Writes the embedded web UI and default config next to each other (only files that changed).</summary>
    private static void Unpack()
    {
        var assembly = typeof(AppPaths).Assembly;
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith("wwwroot/", StringComparison.Ordinal) && !name.StartsWith("config/", StringComparison.Ordinal)) continue;
            var target = Path.Combine(AssetRoot, name.Replace('/', Path.DirectorySeparatorChar));
            using var source = assembly.GetManifestResourceStream(name)!;
            if (File.Exists(target) && new FileInfo(target).Length == source.Length) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var file = File.Create(target);
            source.CopyTo(file);
        }
    }
}

/// <summary>Whether this process has a console (the console build, or started from a terminal).</summary>
internal static class NativeConsole
{
    public static bool HasConsole => GetConsoleWindow() != IntPtr.Zero;

    /// <summary>Shows a message where the user can see it: the console, or a message box when there is none.</summary>
    public static void Notify(string message, bool error = true)
    {
        if (error) Console.Error.WriteLine(message);
        else Console.WriteLine(message);
        if (!HasConsole)
            System.Windows.Forms.MessageBox.Show(message, "Cube Radar", System.Windows.Forms.MessageBoxButtons.OK,
                error ? System.Windows.Forms.MessageBoxIcon.Warning : System.Windows.Forms.MessageBoxIcon.Information);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();
}
