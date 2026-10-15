using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;

namespace CubeRadar.Cli;

/// <summary>
/// Admin rights for the overlay. AION 2's window runs elevated, and Windows hides the keyboard state from programs
/// with fewer rights while it is in front (UIPI): without them the overlay cannot tell "Alt held" (the game shows the
/// cursor) from an open menu. So the overlay-only radar restarts itself once with admin rights (UAC prompt); declined,
/// it simply runs without. Programs it opens (browser, Explorer) still start without admin rights.
/// </summary>
internal static class Elevation
{
    private const int ErrorCancelled = 1223;

    public static bool IsElevated { get; } = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    /// <summary>Starts this exe again with admin rights and the same arguments; false if declined or impossible.</summary>
    public static bool TryRestartElevated(IEnumerable<string> args)
    {
        if (Environment.ProcessPath is not { } exe) return false;
        try
        {
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
                // The console build gets a console of its own: minimized, like overlay.cmd starts it (it hides it later).
                WindowStyle = NativeConsole.HasConsole ? ProcessWindowStyle.Minimized : ProcessWindowStyle.Normal,
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            Process.Start(start);
            return true;
        }
        catch (Win32Exception ex)
        {
            Console.WriteLine(ex.NativeErrorCode == ErrorCancelled
                ? "admin rights declined: running without (Alt counts as a menu for the overlay)"
                : $"could not restart with admin rights: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Opens a URL or folder. With admin rights it goes through Explorer, so the browser does not run elevated too.
    /// </summary>
    public static void Open(string target)
    {
        var start = IsElevated
            ? new ProcessStartInfo("explorer.exe") { ArgumentList = { target } }
            : new ProcessStartInfo(target) { UseShellExecute = true };
        Process.Start(start);
    }

    /// <summary>A named mutex that radars with and without admin rights can both open (same user).</summary>
    public static Mutex SharedMutex(bool initiallyOwned, string name, out bool createdNew)
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(CurrentUser, MutexRights.FullControl, AccessControlType.Allow));
        return MutexAcl.Create(initiallyOwned, name, out createdNew, security);
    }

    /// <summary>A named event that radars with and without admin rights can both open (same user).</summary>
    public static EventWaitHandle SharedEvent(string name)
    {
        var security = new EventWaitHandleSecurity();
        security.AddAccessRule(new EventWaitHandleAccessRule(CurrentUser, EventWaitHandleRights.FullControl, AccessControlType.Allow));
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
    }

    private static SecurityIdentifier CurrentUser => WindowsIdentity.GetCurrent().User!;
}
