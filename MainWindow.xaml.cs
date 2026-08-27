using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Runtime.InteropServices;
using System.ComponentModel;

namespace NetScopePLC;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<Device> _devices = [];
    private Process? _scanProcess;
    private bool _paused;
    private bool _stopRequested;
    private bool _scanActive;

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _devices;
        Loaded += async (_, _) => await RefreshAdaptersAsync();
        Closing += Window_Closing;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_scanActive) return;
        e.Cancel = true;
        StopScan();
        StatusText.Text = "正在停止扫描并恢复网卡配置，完成后可关闭窗口";
    }

    private string NativeTool => Path.Combine(AppContext.BaseDirectory, "NetScopeNative.exe");

    private async Task RefreshAdaptersAsync()
    {
        AdapterBox.ItemsSource = null;
        AdapterDetail.Text = "正在读取可用 IPv4 网卡...";
        StatusText.Text = "正在刷新网卡";
        try
        {
            // Enumerate in-process so the UI cannot lose interfaces while
            // parsing child-process text. The C core remains responsible for
            // the actual bound-interface ICMP scan.
            var adapters = NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(IsRealAdapter)
                .Select(nic =>
                {
                    try { return Adapter.FromNetworkInterface(nic); }
                    catch (NetworkInformationException) { return null; }
                })
                .Where(x => x is not null).Cast<Adapter>()
                .DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            AdapterBox.ItemsSource = adapters;
            AdapterBox.SelectedIndex = adapters.FindIndex(x => string.Equals(x.Name, "以太网", StringComparison.OrdinalIgnoreCase));
            if (AdapterBox.SelectedIndex < 0) AdapterBox.SelectedIndex = adapters.FindIndex(x => x.Name.Contains("以太网", StringComparison.OrdinalIgnoreCase));
            if (AdapterBox.SelectedIndex < 0 && adapters.Count > 0) AdapterBox.SelectedIndex = 0;
            AdapterDetail.Text = adapters.Count == 0 ? "未找到网络接口" : $"r6：已读取 {adapters.Count} 个真实网络接口；未知网段扫描会逐段修改网卡地址";
            StatusText.Text = adapters.Count == 0 ? "未找到网卡" : "r6 网卡列表已刷新";
        }
        catch (Exception ex)
        {
            AdapterDetail.Text = "网卡读取失败";
            StatusText.Text = ex.Message;
        }
    }

    private static bool IsRealAdapter(NetworkInterface nic)
    {
        if (nic.OperationalStatus == OperationalStatus.NotPresent) return false;
        var text = $"{nic.Name} {nic.Description}";
        if (text.Contains("WFP", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Npcap", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("QoS", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Filter Driver", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("WAN Miniport", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Virtual Switch Extension", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase)) return false;
        return nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ppp or NetworkInterfaceType.GenericModem or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.FastEthernetT;
    }

    private async Task<string> RunNativeAsync(string arguments)
    {
        if (!File.Exists(NativeTool)) throw new FileNotFoundException("缺少 C 扫描核心 NetScopeNative.exe", NativeTool);
        using var process = Process.Start(new ProcessStartInfo(NativeTool, arguments) {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        })!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(error)) throw new InvalidOperationException(error.Trim());
        return output;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAdaptersAsync();

    private void AdapterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterBox.SelectedItem is Adapter a)
            AdapterDetail.Text = a.HasAddress
                ? $"{a.Address}/{a.Prefix}    网关: {(string.IsNullOrEmpty(a.Gateway) ? "未设置" : a.Gateway)}"
                : "未配置 IPv4；选择“扫描常见内网段”后程序会临时设置地址并在结束后恢复";
    }

    private void ScanModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ManualNetworkBox is not null)
            ManualNetworkBox.Visibility = ScanModeBox.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not Adapter adapter) return;
        if (!File.Exists(NativeTool)) { StatusText.Text = "未找到 C 扫描核心"; return; }
        var mode = ScanModeBox.SelectedIndex;
        if (mode == 0 && !adapter.HasAddress) { StatusText.Text = "该网卡没有 IPv4，请选择常见内网段或手工网段模式"; return; }
        _devices.Clear();
        _stopRequested = false;
        _scanActive = true;
        ScanButton.IsEnabled = false; PauseButton.IsEnabled = true; StopButton.IsEnabled = true;
        AdapterBox.IsEnabled = false; ScanProgress.IsIndeterminate = true; ScanBadge.Text = "扫描中";
        ResultSummary.Text = mode == 0 ? $"正在扫描 {adapter.Address}/{adapter.Prefix}" : "正在轮询目标网段";
        StatusText.Text = "C 扫描核心正在并发探测设备";
        var restored = mode == 0;
        try
        {
            var networks = mode switch
            {
                0 => new[] { (source: adapter.Address, prefix: adapter.Prefix, temporary: false) },
                1 => CommonNetworks(),
                _ => new[] { ParseNetwork(ManualNetworkBox.Text) }
            };
            for (var segmentIndex = 0; segmentIndex < networks.Length; segmentIndex++)
            {
                var network = networks[segmentIndex];
                if (_stopRequested) break;
                if (mode != 0)
                {
                    var temporary = TemporaryAddress(network.source, network.prefix);
                    StatusText.Text = $"正在把 {adapter.Name} 切换到 {temporary}/{network.prefix}";
                    await RunNetshAsync($"interface ipv4 set address name=\"{adapter.Name}\" static {temporary} {PrefixToMask(network.prefix)} none");
                    await WaitForAddressAsync(adapter.Id, temporary, TimeSpan.FromSeconds(8));
                }
                StatusText.Text = $"正在扫描第 {segmentIndex + 1}/{networks.Length} 段：{NetworkLabel(network.source, network.prefix)}";
                ResultSummary.Text = $"当前网段 {NetworkLabel(network.source, network.prefix)}，已发现 {_devices.Count} 台设备";
                _scanProcess = new Process {
                StartInfo = new ProcessStartInfo(NativeTool, $"--scan {network.source} {network.prefix}") {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                }, EnableRaisingEvents = true
                };
                _scanProcess.Start();
            _scanProcess.BeginErrorReadLine();
            while (await _scanProcess.StandardOutput.ReadLineAsync() is { } line)
                HandleScanLine(line);
                await _scanProcess.WaitForExitAsync();
                _scanProcess.Dispose(); _scanProcess = null;
                await Task.WhenAll(_devices.Where(x => x.Host == "识别中…").Select(x => IdentifyDeviceAsync(x.Address)));
            }
            if (mode != 0) { await RestoreAdapterAsync(adapter); restored = true; }
            if (!_paused && !_stopRequested)
            {
                ScanBadge.Text = "完成";
                StatusText.Text = $"扫描完成，发现 {_devices.Count} 台响应设备";
            }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; ScanBadge.Text = "错误"; }
        finally
        {
            if (!restored)
            {
                try { await RestoreAdapterAsync(adapter); } catch { }
            }
            _scanProcess?.Dispose(); _scanProcess = null; _paused = false;
            _scanActive = false;
            ScanButton.IsEnabled = true; PauseButton.IsEnabled = false; StopButton.IsEnabled = false; AdapterBox.IsEnabled = true;
            ScanProgress.IsIndeterminate = false; ScanProgress.Value = ScanProgress.Maximum;
            ResultSummary.Text = $"发现 {_devices.Count} 台响应设备";
        }
    }

    private void HandleScanLine(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length >= 3 && (parts[0] == "HOST" || parts[0] == "ARP"))
        {
            var existing = -1;
            for (var i = 0; i < _devices.Count; i++)
                if (_devices[i].Address == parts[1]) { existing = i; break; }
            if (existing < 0)
            {
                var latency = parts[0] == "HOST" ? $"{parts[2]} ms" : "ARP";
                var evidence = parts[0] == "ARP" ? $"识别中… · MAC {parts[2]}" : "识别中…";
                _devices.Add(new Device(parts[1], latency, evidence));
            }
            else if (parts[0] == "ARP" && !_devices[existing].Host.Contains("MAC "))
                _devices[existing] = _devices[existing] with { Host = $"{_devices[existing].Host} · MAC {parts[2]}" };
            ResultSummary.Text = $"已发现 {_devices.Count} 台响应设备";
        }
    }

    private async Task IdentifyDeviceAsync(string address)
    {
        var labels = new List<string>();
        try { labels.Add((await Dns.GetHostEntryAsync(address)).HostName); } catch { }
        foreach (var port in new[] { 102, 502, 44818, 4840 })
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(address, port).WaitAsync(TimeSpan.FromMilliseconds(180));
                labels.Add(port switch { 102 => "S7/ISO", 502 => "Modbus TCP", 44818 => "EtherNet/IP", 4840 => "OPC UA", _ => $"TCP {port}" });
            }
            catch { }
        }
        var index = -1;
        for (var i = 0; i < _devices.Count; i++)
            if (_devices[i].Address == address) { index = i; break; }
        if (index >= 0 && index < _devices.Count)
        {
            var mac = _devices[index].Host.Split('·').FirstOrDefault(x => x.TrimStart().StartsWith("MAC "))?.Trim();
            var identity = labels.Count == 0 ? "二层可见（未识别工业协议）" : string.Join(" · ", labels);
            _devices[index] = new Device(address, _devices[index].Latency, string.IsNullOrEmpty(mac) ? identity : $"{identity} · {mac}");
        }
    }

    private static (string source, int prefix, bool temporary)[] CommonNetworks() =>
    [
        ("192.168.1.250", 24, true), ("192.168.0.250", 24, true),
        ("192.168.2.250", 24, true), ("192.168.3.250", 24, true),
        ("192.168.4.250", 24, true), ("192.168.5.250", 24, true),
        ("192.168.10.250", 24, true), ("192.168.20.250", 24, true),
        ("192.168.100.250", 24, true), ("192.168.200.250", 24, true),
        ("10.0.0.250", 24, true), ("10.0.1.250", 24, true),
        ("10.1.0.250", 24, true), ("10.10.0.250", 24, true),
        ("10.10.10.250", 24, true), ("172.16.0.250", 24, true),
        ("172.16.1.250", 24, true), ("172.20.0.250", 24, true)
    ];

    private static (string source, int prefix, bool temporary) ParseNetwork(string value)
    {
        var parts = value.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var ip) || !int.TryParse(parts[1], out var prefix) || prefix < 1 || prefix > 30)
            throw new InvalidOperationException("手工网段格式应为 192.168.1.0/24");
        var bytes = ip.GetAddressBytes(); var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var value32 = BitConverter.ToUInt32(bytes.Reverse().ToArray()); var host = (value32 & mask) + 250;
        return (new IPAddress(BitConverter.GetBytes(host).Reverse().ToArray()).ToString(), prefix, true);
    }

    private static string TemporaryAddress(string source, int prefix) => source;

    private static string NetworkLabel(string source, int prefix)
    {
        var value = BitConverter.ToUInt32(IPAddress.Parse(source).GetAddressBytes().Reverse().ToArray());
        var mask = uint.MaxValue << (32 - prefix);
        return $"{new IPAddress(BitConverter.GetBytes(value & mask).Reverse().ToArray())}/{prefix}";
    }

    private static async Task WaitForAddressAsync(string adapterId, string address, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => string.Equals(x.Id, adapterId, StringComparison.OrdinalIgnoreCase));
            if (nic?.GetIPProperties().UnicastAddresses.Any(x => x.Address.ToString() == address) == true)
            {
                await Task.Delay(350);
                return;
            }
            await Task.Delay(200);
        }
        throw new InvalidOperationException($"网卡临时地址 {address} 未在 8 秒内生效，已停止本段扫描");
    }

    private async Task RestoreAdapterAsync(Adapter adapter)
    {
        if (!adapter.DhcpEnabled && adapter.HasAddress)
            await RunNetshAsync($"interface ipv4 set address name=\"{adapter.Name}\" static {adapter.Address} {PrefixToMask(adapter.Prefix)} {(string.IsNullOrWhiteSpace(adapter.Gateway) ? "none" : adapter.Gateway)}");
        else
            await RunNetshAsync($"interface ipv4 set address name=\"{adapter.Name}\" source=dhcp", $"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=dhcp");
    }

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_scanProcess is null) return;
        _paused = !_paused;
        var status = _paused ? NtSuspendProcess(_scanProcess.Handle) : NtResumeProcess(_scanProcess.Handle);
        if (status != 0) { _paused = false; StatusText.Text = "暂停操作失败"; return; }
        PauseButton.Content = _paused ? "继续" : "暂停";
        ScanBadge.Text = _paused ? "已暂停" : "扫描中";
        StatusText.Text = _paused ? "扫描已暂停" : "扫描已继续";
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopScan();
    private void StopScan()
    {
        _stopRequested = true;
        if (_scanProcess is { HasExited: false }) _scanProcess.Kill(true);
        _paused = false;
        ScanBadge.Text = "已停止";
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not Adapter adapter || ResultsGrid.SelectedItem is not Device device) return;
        var local = ChooseLocalAddress(IPAddress.Parse(device.Address), adapter.Prefix, adapter.Gateway);
        var mask = PrefixToMask(adapter.Prefix);
        if (MessageBox.Show($"将 {adapter.Name} 设置为 {local}/{adapter.Prefix}。\n目标设备: {device.Address}\n\n当前网络连接会短暂中断。", "确认配置网卡", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await RunNetshAsync($"interface ipv4 set address name=\"{adapter.Name}\" static {local} {mask} {(string.IsNullOrWhiteSpace(adapter.Gateway) ? "none" : adapter.Gateway)}");
        StatusText.Text = $"已向 {adapter.Name} 发送静态 IPv4 配置";
    }

    private async void Dhcp_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not Adapter adapter) return;
        if (MessageBox.Show($"将 {adapter.Name} 恢复为 DHCP 自动获取地址与 DNS。", "恢复 DHCP", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await RunNetshAsync($"interface ipv4 set address name=\"{adapter.Name}\" source=dhcp", $"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=dhcp");
        StatusText.Text = $"已向 {adapter.Name} 发送 DHCP 恢复命令";
    }

    private static async Task RunNetshAsync(params string[] commands)
    {
        foreach (var command in commands)
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", command) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
            var error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "网络配置失败" : error.Trim());
        }
    }

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is Device device) { SelectedDevice.Text = $"已选择 {device.Address}，将为本机分配同网段且不冲突的地址。"; ApplyButton.IsEnabled = true; }
        else { SelectedDevice.Text = "选择一个设备以配置本机地址"; ApplyButton.IsEnabled = false; }
    }

    private static IPAddress ChooseLocalAddress(IPAddress target, int prefix, string gateway)
    {
        var value = BitConverter.ToUInt32(target.GetAddressBytes().Reverse().ToArray());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var candidate = (value & mask) + 2;
        var gatewayValue = IPAddress.TryParse(gateway, out var gw) ? BitConverter.ToUInt32(gw.GetAddressBytes().Reverse().ToArray()) : 0;
        while (candidate == value || candidate == gatewayValue) candidate++;
        return new IPAddress(BitConverter.GetBytes(candidate).Reverse().ToArray());
    }

    private static string PrefixToMask(int prefix) => new IPAddress(BitConverter.GetBytes(prefix == 0 ? 0u : uint.MaxValue << (32 - prefix)).Reverse().ToArray()).ToString();

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr processHandle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr processHandle);
}

public sealed record Adapter(string Id, string Name, string Address, int Prefix, string Gateway, bool DhcpEnabled)
{
    public string Display => HasAddress ? $"{Name}    {Address}/{Prefix}" : $"{Name}    未配置 IPv4";
    public bool HasAddress => IPAddress.TryParse(Address, out var ip) && !ip.Equals(IPAddress.Any) && Prefix > 0;
    public static Adapter? FromNetworkInterface(NetworkInterface nic)
    {
        if (string.IsNullOrWhiteSpace(nic.Id) || string.IsNullOrWhiteSpace(nic.Name)) return null;
        var config = nic.GetIPProperties();
        var v4 = config.UnicastAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork);
        var gateway = config.GatewayAddresses.FirstOrDefault(x => x.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString() ?? "";
        var dhcp = true;
        try { dhcp = config.GetIPv4Properties()?.IsDhcpEnabled ?? true; }
        catch (NetworkInformationException) { /* miniport / filter bindings lack IPv4 props */ }
        if (v4 is null) return new Adapter(nic.Id, nic.Name, "0.0.0.0", 0, gateway, dhcp);
        var prefix = PrefixFromMask(v4.IPv4Mask);
        return new Adapter(nic.Id, nic.Name, v4.Address.ToString(), prefix, gateway, dhcp);
    }

    private static int PrefixFromMask(IPAddress? mask)
    {
        if (mask is null) return 0;
        var value = BitConverter.ToUInt32(mask.GetAddressBytes().Reverse().ToArray());
        var prefix = 0;
        while ((value & 0x80000000u) != 0) { prefix++; value <<= 1; }
        return prefix;
    }
    public static Adapter? TryParse(string line)
    {
        var values = line.Trim().Split('\t');
        return values.Length >= 5 && IPAddress.TryParse(values[2], out _) && int.TryParse(values[3], out var prefix)
            ? new Adapter(values[0], values[1], values[2], prefix, values[4], false) : null;
    }
}
public sealed record Device(string Address, string Latency, string Host);
