using System.Collections.Frozen;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace NetScopePLC;

internal static partial class PlcFingerprint
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(1200);

    private static readonly FrozenDictionary<string, string> MacVendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["70-B3-D5"] = "汇川技术",
        ["8C-59-DC"] = "汇川技术",
        ["00-80-9F"] = "台达电子",
        ["00-0B-AB"] = "台达电子",
        ["00-30-DC"] = "永宏电机",
        ["E4-1F-13"] = "和利时",
        ["00-90-E8"] = "信捷电气",
        ["AC-64-DD"] = "信捷电气",
        ["F4-4D-30"] = "英威腾",
        ["C8-5B-76"] = "英威腾",
        ["00-1E-C9"] = "雷赛智能",
        ["54-C4-15"] = "雷赛智能",
        ["04-23-22"] = "雷赛智能",
        ["74-DD-CB"] = "雷赛智能",
        ["00-60-6E"] = "研华科技",
        ["00-0E-8C"] = "西门子",
        ["00-1B-1B"] = "西门子",
        ["00-0A-3A"] = "欧姆龙",
        ["00-21-85"] = "施耐德电气",
        ["00-80-F4"] = "施耐德电气",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<ushort, string> EnipVendors = new Dictionary<ushort, string>
    {
        [1] = "罗克韦尔",
        [2] = "罗克韦尔",
        [43] = "施耐德",
        [47] = "欧姆龙",
        [448] = "台达",
        [634] = "信捷",
        [1159] = "汇川",
        [1165] = "汇川",
        [1433] = "英威腾",
    }.ToFrozenDictionary();

    public static async Task<string?> ProbeAsync(string address, string? mac = null, IPAddress? bindAddress = null)
    {
        var s7Task = TryS7Async(address, bindAddress);
        var modbusTask = TryModbusAsync(address);
        var enipTask = TryEnipAsync(address);
        var leadshineTask = TryLeadshineAsync(address);
        await Task.WhenAll(s7Task, modbusTask, enipTask, leadshineTask);
        var s7 = await s7Task;
        var modbus = await modbusTask;
        var enip = await enipTask;
        var leadshine = await leadshineTask;

        var candidates = new[] { s7, modbus, enip, leadshine }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .Where(x => !IsGenericLabel(x))
            .Distinct()
            .ToList();

        if (candidates.Count > 0)
            return PickBest(candidates);

        var merged = string.Join(' ', new[] { s7, modbus, enip, leadshine }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (MatchDomesticKeyword(merged, out var keywordModel))
            return keywordModel;

        if (!string.IsNullOrWhiteSpace(mac) && IdentifyFromMac(mac) is { } fromMac)
            return fromMac;

        if (!string.IsNullOrWhiteSpace(mac) && TryMacVendor(mac, out var vendor))
            return InferFromVendor(vendor, merged, s7, modbus, enip, leadshine);

        return FirstNonGeneric(s7, modbus, enip, leadshine);
    }

    public static string? IdentifyFromMac(string? mac)
    {
        if (string.IsNullOrWhiteSpace(mac)) return null;
        var norm = NormalizeMac(mac);
        if (norm.Length < 8) return null;
        var oui = norm[..8];
        if (!MacVendors.TryGetValue(oui, out var vendor)) return null;
        return vendor switch
        {
            "雷赛智能" => InferLeadshineModel(norm),
            "汇川技术" => "汇川 PLC",
            "台达电子" => "台达 PLC",
            "信捷电气" => "信捷 PLC",
            "和利时" => "和利时 PLC",
            "英威腾" => "英威腾 PLC",
            "永宏电机" => "永宏 PLC",
            "西门子" => "西门子 PLC",
            _ => $"{vendor} PLC"
        };
    }

    private static string InferLeadshineModel(string normMac) =>
        normMac.StartsWith("04-23-22", StringComparison.OrdinalIgnoreCase)
            ? "雷赛 SC2-C32ADS"
            : "雷赛 SC2 系列 PLC";

    private static async Task<string?> TryLeadshineAsync(string address)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, 502).WaitAsync(TimeSpan.FromMilliseconds(2000));
            client.NoDelay = true;
            using var stream = client.GetStream();
            var buf = new byte[512];
            foreach (var deviceId in new byte[] { 0x01, 0x03 })
            {
                byte[] req = [0x00, 0x3C, 0x00, 0x00, 0x00, 0x05, 0x01, 0x2B, 0x0E, deviceId, 0x00];
                if (await WriteReadAsync(stream, req, buf) < 8) continue;
                if (ParseModbusIdentity(buf) is { } identity && !IsGenericLabel(identity))
                    return MapProductHint(identity);
                if (MatchFromBuffer(buf) is { } hit) return hit;
            }
            byte[] readRegs = [0x00, 0x3D, 0x00, 0x00, 0x00, 0x06, 0x01, 0x03, 0x00, 0x00, 0x00, 0x20];
            if (await WriteReadAsync(stream, readRegs, buf) >= 8 && MatchFromBuffer(buf) is { } regHit)
                return regHit;
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? PickBest(List<string> candidates) =>
        candidates.OrderByDescending(ScoreModel).FirstOrDefault();

    private static int ScoreModel(string model)
    {
        var score = model.Length;
        if (model.Contains("CPU", StringComparison.OrdinalIgnoreCase)) score += 20;
        if (model.Contains('(')) score += 10;
        if (model.Contains('系')) score += 5;
        if (IsGenericLabel(model)) score -= 50;
        return score;
    }

    private static string? FirstNonGeneric(params string?[] values) =>
        values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !IsGenericLabel(x!))
        ?? values.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    private static bool IsGenericLabel(string value) =>
        value is "Modbus TCP 设备" or "EtherNet/IP 设备" or "西门子 S7 PLC" or "未知型号（仅二层可见）";

    private static async Task<string?> TryS7Async(string address, IPAddress? bindAddress = null)
    {
        try
        {
            using var socket = await SocketProbe.ConnectAsync(address, 102, bindAddress, ProbeTimeout);
            socket.NoDelay = true;
            using var stream = new NetworkStream(socket, ownsSocket: true);

            byte[] isoCr =
            [
                0x03, 0x00, 0x00, 0x16, 0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00, 0xC0, 0x01, 0x0A,
                0xC1, 0x02, 0x01, 0x00, 0xC2, 0x02, 0x01, 0x02
            ];
            byte[] setup =
            [
                0x03, 0x00, 0x00, 0x19, 0x02, 0xF0, 0x80, 0x32, 0x01, 0x00, 0x00, 0x04, 0x00, 0x00,
                0x08, 0x00, 0x00, 0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x1E
            ];
            byte[] readSzl =
            [
                0x03, 0x00, 0x00, 0x21, 0x02, 0xF0, 0x80, 0x32, 0x07, 0x00, 0x00, 0x05, 0x00, 0x00,
                0x08, 0x00, 0x00, 0x00, 0x01, 0x12, 0x04, 0x11, 0x44, 0x01, 0x00, 0xFF, 0x09, 0x00,
                0x04, 0x00, 0x00, 0x1C, 0x00
            ];
            byte[] readName =
            [
                0x03, 0x00, 0x00, 0x21, 0x02, 0xF0, 0x80, 0x32, 0x07, 0x00, 0x00, 0x05, 0x00, 0x00,
                0x08, 0x00, 0x00, 0x00, 0x01, 0x12, 0x04, 0x11, 0x44, 0x01, 0x00, 0xFF, 0x09, 0x00,
                0x04, 0x00, 0x00, 0x11, 0x00
            ];

            var buf = new byte[512];
            var all = new List<byte>(2048);
            if (await AppendReadAsync(stream, isoCr, buf, all) < 20) return MatchFromBuffer(all);
            if (await AppendReadAsync(stream, setup, buf, all) < 20) return MatchFromBuffer(all);
            if (await AppendReadAsync(stream, readSzl, buf, all) >= 20)
            {
                var order = ExtractOrderNumber(buf);
                if (order is not null) return MapSiemensOrder(order);
            }
            if (await AppendReadAsync(stream, readName, buf, all) >= 20)
            {
                var name = ExtractModuleName(buf);
                if (name is not null) return name;
            }
            return MatchFromBuffer(all) ?? "西门子 S7 PLC";
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryModbusAsync(string address)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, 502).WaitAsync(ProbeTimeout);
            client.NoDelay = true;
            using var stream = client.GetStream();
            var buf = new byte[512];
            string? best = null;

            foreach (var deviceId in new byte[] { 0x01, 0x03 })
            {
                byte[] req = [0x00, 0x2B, 0x00, 0x00, 0x00, 0x05, 0x01, 0x2B, 0x0E, deviceId, 0x00];
                if (await WriteReadAsync(stream, req, buf) < 8) continue;
                if (ParseModbusIdentity(buf) is { } identity)
                {
                    best = identity;
                    if (!IsGenericLabel(identity)) return identity;
                }
                if (MatchFromBuffer(buf) is { } hit) best ??= hit;
            }

            if (best is not null) return best;
            return MatchFromBuffer(buf);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryEnipAsync(string address)
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(address, 44818).WaitAsync(ProbeTimeout);
            client.NoDelay = true;
            using var stream = client.GetStream();

            byte[] register =
            [
                0x65, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00
            ];
            var buf = new byte[1024];
            if (await WriteReadAsync(stream, register, buf) < 8) return null;

            var session = BitConverter.ToUInt32(buf, 4);
            byte[] listIdentity =
            [
                0x6F, 0x00, 0x1E, 0x00,
                (byte)session, (byte)(session >> 8), (byte)(session >> 16), (byte)(session >> 24),
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,
                0x00, 0x00, 0x63, 0x00
            ];
            if (await WriteReadAsync(stream, listIdentity, buf) < 24) return null;

            if (ParseEnipIdentity(buf) is { } identity) return identity;
            if (MatchFromBuffer(buf) is { } hit) return hit;

            var product = ExtractAscii(buf, "1766-", "1756-", "5069-", "CompactLogix", "ControlLogix",
                "AM600", "AM400", "AM320", "H5U", "Easy", "AS3", "DVP", "XC", "XD");
            return product is not null ? MapProductHint(product) : "EtherNet/IP 设备";
        }
        catch
        {
            return null;
        }
    }

    private static string? ParseEnipIdentity(byte[] buffer)
    {
        for (var off = 24; off < buffer.Length - 24; off++)
        {
            var vendor = (ushort)(buffer[off] | (buffer[off + 1] << 8));
            if (!EnipVendors.TryGetValue(vendor, out var vendorName)) continue;
            if (off + 18 >= buffer.Length) continue;
            var nameLen = buffer[off + 16];
            if (nameLen is < 2 or > 48 || off + 17 + nameLen > buffer.Length) continue;
            var product = Encoding.ASCII.GetString(buffer, off + 17, nameLen).Trim();
            if (product.Length < 2) continue;
            return FormatVendorProduct(vendorName, product);
        }

        var text = Encoding.ASCII.GetString(buffer);
        if (MatchDomesticKeyword(text, out var model)) return model;
        return null;
    }

    private static string? ParseModbusIdentity(byte[] buffer)
    {
        string? vendor = null;
        string? product = null;
        string? revision = null;
        for (var i = 0; i < buffer.Length - 6; i++)
        {
            if (buffer[i] != 0x2B || buffer[i + 1] != 0x0E) continue;
            var offset = i + 6;
            while (offset + 2 < buffer.Length)
            {
                var id = buffer[offset];
                var len = buffer[offset + 1];
                offset += 2;
                if (len == 0 || offset + len > buffer.Length) break;
                var text = Encoding.ASCII.GetString(buffer, offset, len).Trim();
                switch (id)
                {
                    case 0x00: vendor = text; break;
                    case 0x01:
                    case 0x03: product ??= text; break;
                    case 0x02: revision = text; break;
                }
                offset += len;
            }
        }

        if (vendor is null && product is null) return null;
        vendor = NormalizeVendorName(vendor ?? "");
        if (!string.IsNullOrWhiteSpace(product))
            return FormatVendorProduct(vendor, product, revision);
        if (!string.IsNullOrWhiteSpace(vendor)) return $"{vendor} Modbus 设备";
        return null;
    }

    private static string FormatVendorProduct(string vendor, string product, string? revision = null)
    {
        var mapped = MapProductHint(product);
        if (mapped.StartsWith(vendor, StringComparison.Ordinal)) return mapped;
        if (mapped != product) return mapped;
        return string.IsNullOrWhiteSpace(revision)
            ? $"{vendor} {product}"
            : $"{vendor} {product} (Rev {revision})";
    }

    private static string NormalizeVendorName(string vendor)
    {
        if (vendor.Contains("INOVANCE", StringComparison.OrdinalIgnoreCase) || vendor.Contains("汇川", StringComparison.Ordinal))
            return "汇川";
        if (vendor.Contains("DELTA", StringComparison.OrdinalIgnoreCase) || vendor.Contains("台达", StringComparison.Ordinal))
            return "台达";
        if (vendor.Contains("XINJE", StringComparison.OrdinalIgnoreCase) || vendor.Contains("信捷", StringComparison.Ordinal))
            return "信捷";
        if (vendor.Contains("HOLLYSYS", StringComparison.OrdinalIgnoreCase) || vendor.Contains("和利时", StringComparison.Ordinal))
            return "和利时";
        if (vendor.Contains("INVT", StringComparison.OrdinalIgnoreCase) || vendor.Contains("英威腾", StringComparison.Ordinal))
            return "英威腾";
        if (vendor.Contains("FATEK", StringComparison.OrdinalIgnoreCase) || vendor.Contains("永宏", StringComparison.Ordinal))
            return "永宏";
        if (vendor.Contains("LEADSHINE", StringComparison.OrdinalIgnoreCase) || vendor.Contains("雷赛", StringComparison.Ordinal))
            return "雷赛";
        if (vendor.Contains("VEICHI", StringComparison.OrdinalIgnoreCase) || vendor.Contains("伟创", StringComparison.Ordinal))
            return "伟创";
        if (vendor.Contains("SIEMENS", StringComparison.OrdinalIgnoreCase) || vendor.Contains("西门子", StringComparison.Ordinal))
            return "西门子";
        return vendor.Trim();
    }

    private static string MapProductHint(string product)
    {
        var p = product.Trim();
        var upper = p.ToUpperInvariant();
        return upper switch
        {
            _ when upper.Contains("AM600") => "汇川 AM600 系列",
            _ when upper.Contains("AM400") => "汇川 AM400 系列",
            _ when upper.Contains("AM320") => "汇川 AM320 系列",
            _ when upper.Contains("H5U") => $"汇川 H5U ({p})",
            _ when upper.Contains("EASY") && upper.Contains("32") => "汇川 Easy320 系列",
            _ when upper.Contains("EASY") => "汇川 Easy 系列",
            _ when upper.Contains("MD") && upper.Contains("INOVANCE") => $"汇川 {p}",
            _ when upper.Contains("AS332") => "台达 AS332 系列",
            _ when upper.Contains("AS328") => "台达 AS328 系列",
            _ when upper.Contains("AS3") => $"台达 AS 系列 ({p})",
            _ when upper.Contains("DVP-ES3") => "台达 DVP-ES3 系列",
            _ when upper.Contains("DVP") => $"台达 DVP 系列 ({p})",
            _ when upper.Contains("XC3") => "信捷 XC3 系列",
            _ when upper.Contains("XD3") => "信捷 XD3 系列",
            _ when upper.Contains("XL5") => "信捷 XL5 系列",
            _ when upper.Contains("XC") || upper.Contains("XD") || upper.Contains("XL") => $"信捷 {p}",
            _ when upper.Contains("LK910") => "和利时 LK910",
            _ when upper.Contains("LK920") => "和利时 LK920",
            _ when upper.Contains("LK") => $"和利时 LK 系列 ({p})",
            _ when upper.Contains("LE5109") => "和利时 LE5109",
            _ when upper.Contains("GD20") || upper.Contains("GD35") => $"英威腾 {p}",
            _ when upper.Contains("SC2-C32ADS") || upper.Contains("SC2C32ADS") => "雷赛 SC2-C32ADS",
            _ when upper.Contains("SC2-C32") => $"雷赛 SC2-C32 系列 ({p})",
            _ when upper.Contains("SC2-C") || upper.Contains("SC2C") => $"雷赛 SC2-C 系列 ({p})",
            _ when upper.Contains("LEADSHINE") || upper.Contains("LEISAI") => $"雷赛 {p}",
            _ when upper.Contains("CP1") => $"欧姆龙 CP 系列 ({p})",
            _ when upper.Contains("CJ2") => $"欧姆龙 CJ 系列 ({p})",
            _ when upper.Contains("PLCSIM") || upper.Contains("PLC SIM") => "西门子 PLCSIM 仿真 PLC",
            _ when upper.Contains("6ES7") => MapSiemensOrder(p.Replace(" ", "")) ?? $"西门子 {p}",
            _ => p
        };
    }

    private static bool MatchDomesticKeyword(string text, out string model)
    {
        model = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var hint in DomesticHints)
        {
            if (!text.Contains(hint.Key, StringComparison.OrdinalIgnoreCase)) continue;
            model = hint.Value;
            return true;
        }
        return false;
    }

    private static readonly (string Key, string Value)[] DomesticHints =
    [
        ("INOVANCE", "汇川 PLC"),
        ("AM600", "汇川 AM600 系列"),
        ("AM400", "汇川 AM400 系列"),
        ("H5U-", "汇川 H5U 系列"),
        ("EASY320", "汇川 Easy320 系列"),
        ("DELTA", "台达 PLC"),
        ("AS332", "台达 AS332 系列"),
        ("DVP-ES", "台达 DVP 系列"),
        ("XINJE", "信捷 PLC"),
        ("XC3", "信捷 XC3 系列"),
        ("XD5", "信捷 XD5 系列"),
        ("HOLLYSYS", "和利时 PLC"),
        ("LK910", "和利时 LK910"),
        ("INVT", "英威腾 PLC"),
        ("FATEK", "永宏 PLC"),
        ("LEADSHINE", "雷赛 PLC"),
        ("SC2-C32", "雷赛 SC2-C32ADS"),
        ("SC2-C", "雷赛 SC2 系列 PLC"),
        ("PLCSIM", "西门子 PLCSIM 仿真 PLC"),
        ("SIMATIC", "西门子 SIMATIC 设备"),
        ("VEICHI", "伟创电气 PLC"),
    ];

    private static string? MatchFromBuffer(IReadOnlyList<byte> buffer) =>
        buffer.Count == 0 ? null :
        MatchDomesticKeyword(Encoding.ASCII.GetString(buffer.ToArray()), out var model) ? model : null;

    private static async Task<int> AppendReadAsync(NetworkStream stream, byte[] request, byte[] buffer, List<byte> sink)
    {
        var read = await WriteReadAsync(stream, request, buffer);
        if (read > 0) sink.AddRange(buffer.AsSpan(0, read).ToArray());
        return read;
    }

    private static string? ExtractModuleName(byte[] buffer)
    {
        var text = Encoding.ASCII.GetString(buffer);
        foreach (var hint in new[] { "CPU", "S7-", "6ES7", "SIMATIC", "PLCSIM", "PLC SIM", "AM600", "AM400", "H5U", "INOVANCE", "DELTA", "XINJE" })
        {
            var idx = text.IndexOf(hint, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var end = idx;
            while (end < text.Length && text[end] >= ' ' && text[end] <= '~') end++;
            var slice = text[idx..end].Trim();
            if (slice.Length >= 3) return MapProductHint(slice);
        }
        return null;
    }

    private static bool TryMacVendor(string mac, out string vendor)
    {
        vendor = "";
        var norm = NormalizeMac(mac);
        if (norm.Length < 8) return false;
        var prefix = norm[..8];
        return MacVendors.TryGetValue(prefix, out vendor!);
    }

    private static string InferFromVendor(string vendor, string merged, params string?[] probes)
    {
        if (MatchDomesticKeyword(merged, out var model)) return model;
        foreach (var probe in probes)
            if (probe is not null && MatchDomesticKeyword(probe, out model)) return model;

        return vendor switch
        {
            "汇川技术" => probes.Any(p => p?.Contains("S7", StringComparison.OrdinalIgnoreCase) == true)
                ? "汇川 PLC（兼容 S7 协议，疑似 AM/H5U 系列）"
                : "汇川 PLC",
            "台达电子" => "台达 PLC（疑似 DVP/AS 系列）",
            "信捷电气" => "信捷 PLC（疑似 XC/XD 系列）",
            "和利时" => "和利时 PLC（疑似 LK/LE 系列）",
            "英威腾" => "英威腾 PLC",
            "永宏电机" => "永宏 PLC",
            "雷赛智能" => "雷赛 SC2 系列 PLC",
            "西门子" => probes.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && p.Contains("6ES7", StringComparison.OrdinalIgnoreCase))
                        ?? "西门子 S7 PLC",
            _ => $"{vendor} PLC"
        };
    }

    private static string NormalizeMac(string mac) =>
        mac.Replace("MAC", "", StringComparison.OrdinalIgnoreCase)
            .Replace(":", "-", StringComparison.Ordinal)
            .Trim()
            .ToUpperInvariant();

    private static async Task<int> WriteReadAsync(NetworkStream stream, byte[] request, byte[] buffer)
    {
        await stream.WriteAsync(request);
        var total = 0;
        using var cts = new CancellationTokenSource(ProbeTimeout);
        while (total < buffer.Length)
        {
            int read;
            try { read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cts.Token); }
            catch (OperationCanceledException) { break; }
            if (read == 0) break;
            total += read;
            if (total >= 4 && buffer[0] == 0x03 && buffer[1] == 0x00)
            {
                var len = (buffer[2] << 8) | buffer[3];
                if (len >= 4 && total >= len) break;
            }
            if (total >= 6 && buffer[2] == 0x00 && buffer[3] == 0x00)
            {
                var len = (buffer[4] << 8) | buffer[5];
                if (total >= 6 + len) break;
            }
            if (total >= 24 && (buffer[0] == 0x65 || buffer[0] == 0x6F)) break;
        }
        return total;
    }

    private static string? ExtractOrderNumber(byte[] buffer)
    {
        var text = Encoding.ASCII.GetString(buffer);
        var match = OrderNumberRegex().Match(text);
        return match.Success ? match.Value.Replace(" ", "") : null;
    }

    private static string MapSiemensOrder(string order)
    {
        var compact = order.Replace(" ", "").ToUpperInvariant();
        var family = compact switch
        {
            _ when compact.Contains("6ES7211") => "西门子 S7-1200 CPU 1211C",
            _ when compact.Contains("6ES7212") => "西门子 S7-1200 CPU 1212C",
            _ when compact.Contains("6ES7214") => "西门子 S7-1200 CPU 1214C",
            _ when compact.Contains("6ES7215") => "西门子 S7-1200 CPU 1215C",
            _ when compact.Contains("6ES7217") => "西门子 S7-1200 CPU 1217C",
            _ when compact.Contains("6ES7314") => "西门子 S7-300 CPU 314",
            _ when compact.Contains("6ES7315") => "西门子 S7-300 CPU 315",
            _ when compact.Contains("6ES7316") => "西门子 S7-300 CPU 316",
            _ when compact.Contains("6ES7317") => "西门子 S7-300 CPU 317",
            _ when compact.Contains("6ES7318") => "西门子 S7-300 CPU 318",
            _ when compact.Contains("6ES7414") => "西门子 S7-400 CPU 414",
            _ when compact.Contains("6ES7415") => "西门子 S7-400 CPU 415",
            _ when compact.Contains("6ES7416") => "西门子 S7-400 CPU 416",
            _ when compact.Contains("6ES7511") => "西门子 S7-1500 CPU 1511",
            _ when compact.Contains("6ES7513") => "西门子 S7-1500 CPU 1513",
            _ when compact.Contains("6ES7515") => "西门子 S7-1500 CPU 1515",
            _ when compact.Contains("6ES7516") => "西门子 S7-1500 CPU 1516",
            _ when compact.Contains("6ES7517") => "西门子 S7-1500 CPU 1517",
            _ when compact.Contains("6ES7518") => "西门子 S7-1500 CPU 1518",
            _ when compact.Contains("6ES7") => "西门子 S7 PLC",
            _ => null
        };
        return family is null ? $"西门子 {order}" : $"{family} ({order})";
    }

    private static string? ExtractAscii(byte[] buffer, params string[] hints)
    {
        var text = Encoding.ASCII.GetString(buffer);
        foreach (var hint in hints)
            if (text.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                var start = text.IndexOf(hint, StringComparison.OrdinalIgnoreCase);
                var end = start;
                while (end < text.Length && text[end] >= ' ' && text[end] <= '~') end++;
                return text[start..end].Trim();
            }
        return null;
    }

    [GeneratedRegex(@"6ES7[\s-]?\d{3}[\s-]?[\dA-Z-]{6,}", RegexOptions.IgnoreCase)]
    private static partial Regex OrderNumberRegex();
}
