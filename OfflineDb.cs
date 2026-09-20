using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace NetScopePLC;

/// <summary>
/// 内置离线识别库（编译进 exe，识别不需要任何网络往返）：
///   data/oui.tsv      —— IEEE 注册的 MAC 前缀（MA-L /24 + MA-M /28 + MA-S /36，共 5 万余条）→ 厂商名
///   data/identify.tsv —— 主机名关键字与厂商关键字 → 中文设备标签
/// 生成 oui.tsv 用 tools/make-oui.ps1。
/// </summary>
internal static class OfflineDb
{
    private static readonly Lazy<Dictionary<string, string>> Oui = new(LoadOui);
    private static readonly Lazy<List<Rule>> Rules = new(LoadRules);

    private readonly record struct Rule(string Kind, string Key, string Label, Regex? Pattern)
    {
        public bool Matches(string text) =>
            Pattern is { } pattern
                ? pattern.IsMatch(text)
                : text.Contains(Key, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>按 MAC 查注册厂商；前缀长度依次尝试 36/28/24 位（9/7/6 个十六进制字符）。</summary>
    public static string? VendorFor(string? mac)
    {
        var hex = HexOf(mac);
        if (hex is null) return null;
        foreach (var length in new[] { 9, 7, 6 })
            if (hex.Length >= length && Oui.Value.TryGetValue(hex[..length], out var vendor))
                return vendor;
        return null;
    }

    public static string? LabelForVendor(string? vendor) => Match("vendor", vendor);

    /// <summary>MAC → 中文设备标签（厂商名经规则映射；映射不到就用注册厂商原名）；查不到返回 null。</summary>
    public static string? DeviceLabelForMac(string? mac)
    {
        var vendor = VendorFor(mac);
        return vendor is null ? null : LabelForVendor(vendor) ?? vendor;
    }

    public static string? LabelForHost(string? host) => Match("host", host);

    /// <summary>
    /// 首字节的本地管理位（bit 1）为 1 = 随机/隐私 MAC（iOS 14+ / Android 10+ 默认），
    /// 这类地址不在任何 OUI 注册表里，厂商查询对它结构性无效。
    /// </summary>
    public static bool IsRandomMac(string? mac)
    {
        var hex = HexOf(mac);
        if (hex is null || hex.Length < 2) return false;
        return (Convert.ToByte(hex[..2], 16) & 0x02) != 0;
    }

    private static string? Match(string kind, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var rule in Rules.Value)
            if (rule.Kind == kind && rule.Matches(text))
                return rule.Label;
        return null;
    }

    private static string? HexOf(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var text = mac.Trim();
        if (text.StartsWith("MAC", StringComparison.OrdinalIgnoreCase)) text = text[3..];
        var sb = new StringBuilder(12);
        foreach (var c in text)
            if (Uri.IsHexDigit(c)) sb.Append(char.ToUpperInvariant(c));
        return sb.Length >= 6 ? sb.ToString() : null;
    }

    private static Dictionary<string, string> LoadOui()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Read("oui.tsv"))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab == line.Length - 1) continue;
            map[line[..tab]] = line[(tab + 1)..];
        }
        return map;
    }

    private static List<Rule> LoadRules()
    {
        var list = new List<Rule>();
        foreach (var line in Read("identify.tsv"))
        {
            var f = line.Split('\t');
            if (f.Length < 3 || f[0].Length == 0 || f[1].Length == 0) continue;
            // 主机名是连写的（iQOO-10.lan），按子串匹配；厂商名是自然语言（Chengdu Quanjing Intelligent…），
            // 必须按词边界匹配，否则 "Intel" 会命中 "Intelligent"、"ABB" 会命中 "Abbott"。
            var pattern = f[0] == "vendor"
                ? new Regex($@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(f[1])}(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase)
                : null;
            list.Add(new Rule(f[0], f[1], f[2], pattern));
        }
        return list;
    }

    private static IEnumerable<string> Read(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) yield break;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } raw)
        {
            var line = raw.TrimStart('\uFEFF');
            if (line.Length > 0 && line[0] != '#') yield return line;
        }
    }
}
