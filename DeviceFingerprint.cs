using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NetScopePLC;

internal static partial class DeviceFingerprint
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1200);
    private static readonly HttpClient Http = new() { Timeout = ProbeTimeout };

    public static async Task<(string Model, string? Detail)> IdentifyOtherAsync(string address, string? mac)
    {
        var http = await TryHttpTitleAsync(address);
        var macHint = PlcFingerprint.IdentifyFromMac(mac);
        return (ClassifyOther(http, macHint, mac), BuildDetail(http, mac));
    }

    public static async Task<(string Model, string? Detail)> IdentifyHmiAsync(string address, string? mac)
    {
        var http = await TryHttpTitleAsync(address);
        if (http is not null)
        {
            if (http.Contains("WEINTEK", StringComparison.OrdinalIgnoreCase) || http.Contains("威纶", StringComparison.Ordinal))
                return ("威纶通 HMI", http);
            if (http.Contains("MCGS", StringComparison.OrdinalIgnoreCase) || http.Contains("昆仑", StringComparison.Ordinal))
                return ("昆仑通态 HMI", http);
            if (http.Contains("SIMATIC", StringComparison.OrdinalIgnoreCase) || http.Contains("WinCC", StringComparison.OrdinalIgnoreCase))
                return ("西门子 HMI / WinCC", http);
            if (http.Contains("PROFACE", StringComparison.OrdinalIgnoreCase))
                return ("Pro-face HMI", http);
            if (http.Contains("FLEXEM", StringComparison.OrdinalIgnoreCase) || http.Contains("繁易", StringComparison.Ordinal))
                return ("繁易 HMI", http);
        }
        var plc = await PlcFingerprint.ProbeAsync(address, mac);
        if (plc is not null && plc.Contains("HMI", StringComparison.OrdinalIgnoreCase))
            return (plc, null);
        // 没有 HMI 特征时先看内置厂商库，避免把开了 80 端口的路由器/交换机一律喊成「工业 HMI / 触摸屏」
        if (OfflineDb.DeviceLabelForMac(mac) is { } fromMac) return (fromMac, http);
        return ("工业 HMI / 触摸屏", http);
    }

    private static string ClassifyOther(string? http, string? macHint, string? mac)
    {
        if (!string.IsNullOrWhiteSpace(macHint) && !macHint.EndsWith("PLC", StringComparison.Ordinal)) return macHint;
        // 内置离线 OUI 库（5 万余条注册前缀）—— 不依赖任何网络往返
        if (OfflineDb.DeviceLabelForMac(mac) is { } fromMac) return fromMac;
        if (http is not null && (http.Contains("VMWARE", StringComparison.OrdinalIgnoreCase) || http.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase)))
            return "虚拟机";
        return "网络设备";
    }

    private static string? BuildDetail(string? http, string? mac)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(http)) parts.Add(http);
        if (OfflineDb.IsRandomMac(mac)) parts.Add("随机 MAC（隐私地址）");
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static async Task<string?> TryHttpTitleAsync(string address)
    {
        foreach (var port in new[] { 80, 8080, 443 })
        {
            try
            {
                using var stream = new TcpClient();
                await stream.ConnectAsync(address, port).WaitAsync(ProbeTimeout);
                using var net = stream.GetStream();
                var req = Encoding.ASCII.GetBytes($"GET / HTTP/1.0\r\nHost: {address}\r\n\r\n");
                await net.WriteAsync(req);
                var buf = new byte[1024];
                var read = await net.ReadAsync(buf.AsMemory(0, buf.Length)).AsTask().WaitAsync(ProbeTimeout);
                var text = Encoding.ASCII.GetString(buf, 0, read);
                var title = TitleRegex().Match(text);
                if (title.Success) return title.Groups[1].Value.Trim();
                if (text.Contains("Server:", StringComparison.OrdinalIgnoreCase))
                {
                    var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("Server:", StringComparison.OrdinalIgnoreCase));
                    if (line is not null) return line["Server:".Length..].Trim();
                }
            }
            catch { }
        }
        return null;
    }

    [GeneratedRegex(@"<title[^>]*>([^<]+)</title>", RegexOptions.IgnoreCase)]
    private static partial Regex TitleRegex();
}
