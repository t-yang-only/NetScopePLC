using System.Diagnostics;
using System.Text;

namespace NetScopePLC;

internal static class CliRunner
{
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0) return false;

        var command = args[0];
        if (command is "-h" or "--help" or "-?" or "/?")
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.Write(HelpText);
            return true;
        }

        if (command is not "--adapters" and not "--scan") return false;

        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            exitCode = RunNativeAsync(FormatNativeArguments(args)).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            exitCode = 1;
            return true;
        }
    }

    private static string FormatNativeArguments(string[] args) => string.Join(' ', args);

    private static async Task<int> RunNativeAsync(string arguments)
    {
        var tool = NativeToolHost.Path;
        using var process = Process.Start(new ProcessStartInfo(tool, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        })!;

        var stdout = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync() is { } line)
                Console.WriteLine(line);
        });
        var stderr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync() is { } line)
                Console.Error.WriteLine(line);
        });
        await Task.WhenAll(stdout, stderr);
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    private static string HelpText => """
        NetScopePLC — 工业网段 PLC 扫描工具

        用法:
          NetScopePLC.exe                         启动图形界面（需管理员）
          NetScopePLC.exe --adapters              列出 IPv4 网卡
          NetScopePLC.exe --scan <IPv4> <prefix>  扫描指定网段（如 192.168.1.250 24）
          NetScopePLC.exe --help                  显示此帮助

        输出协议（UTF-8，制表符分隔）:
          HOST\t<ip>\t<rtt_ms>
          ARP\t<ip>\t<mac>
          DONE\t<scanned>\t<replied>
        """;
}
