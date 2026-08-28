using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;

namespace NetScopePLC;

public static class Program
{
    private const int SwHide = 0;

    [STAThread]
    public static int Main(string[] args)
    {
        if (CliRunner.TryRun(args, out var exitCode))
            return exitCode;

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        HideConsole();

        if (!Debugger.IsAttached && !IsAdministrator())
        {
            var executable = Environment.ProcessPath!;
            Process.Start(new ProcessStartInfo(executable, args) { UseShellExecute = true, Verb = "runas" });
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    private static void HideConsole()
    {
        var handle = GetConsoleWindow();
        if (handle != IntPtr.Zero)
            ShowWindow(handle, SwHide);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
