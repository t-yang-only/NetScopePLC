using System.Security.Principal;
using System.Diagnostics;
using System.Windows;

namespace NetScopePLC;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        if (!principal.IsInRole(WindowsBuiltInRole.Administrator))
        {
            var executable = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" });
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }
}
