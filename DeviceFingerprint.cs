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
        var dnsTask = TryDnsAsync(address);
        var httpTask = TryHttpTitleAsync(address);
        await Task.WhenAll(dnsTask, httpTask);
        var host = await dnsTask;
        var http = await httpTask;
        var macHint = PlcFingerprint.IdentifyFromMac(mac);

        var model = ClassifyOther(host, http, macHint, mac);
        var detail = BuildDetail(host, http);
        return (model, detail);
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
        return ("工业 HMI / 触摸屏", http);
    }

    private static string ClassifyOther(string? host, string? http, string? macHint, string? mac)
    {
        var text = $"{host} {http} {macHint}".ToUpperInvariant();
        if (host is not null)
        {
            if (host.Contains("iphone", StringComparison.OrdinalIgnoreCase) || host.Contains("ipad", StringComparison.OrdinalIgnoreCase))
                return host.Contains("ipad", StringComparison.OrdinalIgnoreCase) ? "iPad" : "iPhone / iOS";
            if (host.Contains("android", StringComparison.OrdinalIgnoreCase)) return "Android 手机";
            if (host.StartsWith("DESKTOP-", StringComparison.OrdinalIgnoreCase)) return "Windows 电脑";
            if (host.Contains("macbook", StringComparison.OrdinalIgnoreCase) || host.Contains(".local", StringComparison.OrdinalIgnoreCase))
                return "Mac 电脑";
            if (host.Contains("raspberrypi", StringComparison.OrdinalIgnoreCase)) return "树莓派";
        }
        if (text.Contains("APPLE") || MacPrefix(mac, "F0-D4-15", "3C-06-30", "A4-83-E7")) return "Apple 设备";
        if (text.Contains("SAMSUNG") || MacPrefix(mac, "8C-FD-F0", "C0-BD-D1")) return "三星手机 / 设备";
        if (text.Contains("HUAWEI") || MacPrefix(mac, "00-E0-FC", "48-DB-50")) return "华为手机 / 设备";
        if (text.Contains("XIAOMI") || MacPrefix(mac, "64-09-80", "F8-A4-5F")) return "小米手机 / 设备";
        if (text.Contains("OPPO") || MacPrefix(mac, "AC-DE-48")) return "OPPO 手机";
        if (text.Contains("VIVO") || MacPrefix(mac, "D8-49-0B")) return "vivo 手机";
        if (text.Contains("DELL") || MacPrefix(mac, "F8-BC-12", "00-14-22")) return "戴尔电脑";
        if (text.Contains("LENOVO") || MacPrefix(mac, "54-EE-75", "8C-EC-4B")) return "联想电脑";
        if (text.Contains("HP ") || text.Contains("HEWLETT") || MacPrefix(mac, "00-1E-0B")) return "惠普电脑";
        if (text.Contains("ASUS") || MacPrefix(mac, "38-D5-47")) return "华硕电脑";
        if (text.Contains("VMWARE") || text.Contains("VIRTUAL")) return "虚拟机";
        if (!string.IsNullOrWhiteSpace(macHint) && !macHint.EndsWith("PLC", StringComparison.Ordinal))
            return macHint;
        if (!string.IsNullOrWhiteSpace(host)) return host;
        return "网络设备";
    }

    private static string? BuildDetail(string? host, string? http)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(http)) parts.Add(http);
        if (!string.IsNullOrWhiteSpace(host) && (parts.Count == 0 || !parts[0].Contains(host, StringComparison.OrdinalIgnoreCase)))
            parts.Add(host);
        return parts.Count == 0 ? null : string.Join(" · ", parts);
    }

    private static bool MacPrefix(string? mac, params string[] prefixes)
    {
        if (string.IsNullOrWhiteSpace(mac)) return false;
        var norm = mac.Replace("MAC", "", StringComparison.OrdinalIgnoreCase).Replace(':', '-').Trim().ToUpperInvariant();
        return prefixes.Any(p => norm.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> TryDnsAsync(string address)
    {
        try { return (await System.Net.Dns.GetHostEntryAsync(address).WaitAsync(ProbeTimeout)).HostName; }
        catch { return null; }
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
