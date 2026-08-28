using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
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
    private int _progressSegmentTotal = 1;
    private int _progressSegmentIndex;
    private int _progressHostTotal = 254;
    private int _progressHostsDone;
    private bool _progressSegmentActive;
    private bool _progressIndeterminate;
    private DispatcherTimer? _ringTimer;
    private double _ringIndeterminatePhase;
    private DateTime _scanStartedAt;
    private DateTime? _segmentStartedAt;
    private readonly List<double> _segmentDurationsSeconds = [];
    private string? _scanBindIp;
    private readonly List<string> _simulationRoutes = [];

    private const double RingCircumference = 163.36;
    private const double DefaultSegmentSeconds = 42;

    public MainWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _devices;
        SetScanBadge("空闲");
        UpdateEmptyState();
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

    private string NativeTool => NativeToolHost.Path;

    private async Task RefreshAdaptersAsync()
    {
        AdapterBox.ItemsSource = null;
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
            StatusText.Text = adapters.Count == 0 ? "未找到网卡" : "网卡列表已刷新";
        }
        catch (Exception ex)
        {
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
        if (AdapterBox.SelectedItem is Adapter adapter)
            PopulateNetworkFields(adapter);
        if (AdapterBox.SelectedItem is not Adapter sim || !IsSimulationAdapter(sim)) return;
        ScanModeBox.SelectedIndex = 2;
        ManualNetworkBox.Text = "192.168.0.0/24";
        StatusText.Text = "PLCSIM 仿真网卡：已切换手工网段 192.168.0.0/24（实例默认 IP 通常为 .1）";
    }

    private void PopulateNetworkFields(Adapter adapter)
    {
        if (IpBox is null) return;
        if (!adapter.HasAddress || adapter.DhcpEnabled)
        {
            IpBox.Text = "";
            MaskBox.Text = "";
            GatewayBox.Text = "";
        }
        else
        {
            IpBox.Text = adapter.Address;
            MaskBox.Text = PrefixToMask(adapter.Prefix);
            GatewayBox.Text = adapter.Gateway;
        }
        DnsBox.Text = ReadAdapterDns(adapter.Id);
    }

    private static string ReadAdapterDns(string adapterId)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(x => string.Equals(x.Id, adapterId, StringComparison.OrdinalIgnoreCase));
            var dns = nic?.GetIPProperties().DnsAddresses
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return dns?.ToString() ?? "";
        }
        catch (NetworkInformationException)
        {
            return "";
        }
    }

    private void ScanModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var manual = ScanModeBox.SelectedIndex == 2;
        if (ManualRowPanel is not null)
            ManualRowPanel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        if (ManualNetworkLabel is not null)
            ManualNetworkLabel.Visibility = manual ? Visibility.Visible : Visibility.Collapsed;
        if (manual) SyncPrefixChipsFromManualBox();
    }

    private void ManualPrefix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var prefix)) return;
        SetManualPrefix(prefix);
    }

    private void ManualNetworkBox_TextChanged(object sender, TextChangedEventArgs e) => SyncPrefixChipsFromManualBox();

    private void SetManualPrefix(int prefix)
    {
        var text = ManualNetworkBox.Text.Trim();
        var slash = text.LastIndexOf('/');
        var baseAddr = slash > 0 ? text[..slash] : text;
        if (string.IsNullOrWhiteSpace(baseAddr) || !baseAddr.Contains('.')) baseAddr = "192.168.0.0";
        ManualNetworkBox.Text = $"{baseAddr}/{prefix}";
        UpdatePrefixChipHighlight(prefix);
    }

    private void SyncPrefixChipsFromManualBox()
    {
        var prefix = 24;
        var text = ManualNetworkBox.Text.Trim();
        var slash = text.LastIndexOf('/');
        if (slash > 0 && int.TryParse(text[(slash + 1)..], out var parsed) && parsed is 8 or 16 or 24)
            prefix = parsed;
        UpdatePrefixChipHighlight(prefix);
    }

    private bool IsPlcTarget => TargetModeBox?.SelectedIndex != 1;

    private static bool IsSimulationAdapter(Adapter adapter) =>
        adapter.Name.Contains("PLCSIM", StringComparison.OrdinalIgnoreCase) ||
        adapter.Name.Contains("Virtual Switch", StringComparison.OrdinalIgnoreCase) ||
        adapter.Name.Contains("仿真", StringComparison.Ordinal);

    private static bool AdapterOnNetwork(Adapter adapter, string source, int prefix)
    {
        if (!adapter.HasAddress) return false;
        return NetworkLabel(adapter.Address, prefix) == NetworkLabel(source, prefix);
    }

    private static bool NeedsTemporaryIp(Adapter adapter, int mode, string source, int prefix) =>
        mode != 0 || !adapter.HasAddress || !AdapterOnNetwork(adapter, source, prefix) || IsSimulationAdapter(adapter);

    private void UpdatePrefixChipHighlight(int selected)
    {
        if (Prefix24Btn is null || Prefix16Btn is null || Prefix8Btn is null) return;
        Prefix24Btn.Style = (Style)FindResource(selected == 24 ? "PrefixChipActive" : "PrefixChip");
        Prefix16Btn.Style = (Style)FindResource(selected == 16 ? "PrefixChipActive" : "PrefixChip");
        Prefix8Btn.Style = (Style)FindResource(selected == 8 ? "PrefixChipActive" : "PrefixChip");
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not Adapter adapter) return;
        if (!File.Exists(NativeTool)) { StatusText.Text = "未找到 C 扫描核心"; return; }
        var mode = ScanModeBox.SelectedIndex;
        if (mode == 0 && !adapter.HasAddress)
        {
            StatusText.Text = IsSimulationAdapter(adapter)
                ? "仿真网卡请使用「扫描手工网段」，填写 PLCSIM 网段（如 192.168.0.0/24）"
                : "该网卡没有 IPv4，请选择常见内网段或手工网段模式";
            return;
        }
        _devices.Clear();
        UpdateEmptyState();
        _stopRequested = false;
        _scanActive = true;
        ScanButton.IsEnabled = false; PauseButton.IsEnabled = true; StopButton.IsEnabled = true;
        AdapterBox.IsEnabled = false;
        TargetModeBox.IsEnabled = false;
        SetScanBadge("扫描中");
        UpdateEmptyState();
        var restored = mode == 0 && adapter.HasAddress && !IsSimulationAdapter(adapter);
        var hadAddressBeforeScan = adapter.HasAddress;
        var scanned = 0;
        var skipped = 0;
        try
        {
            var networks = mode switch
            {
                0 => new[] { (source: adapter.Address, prefix: CapScanPrefix(adapter.Prefix), temporary: false) },
                1 => CommonNetworks(),
                _ => new[] { ParseNetwork(ManualNetworkBox.Text) }
            };
            ResultSummary.Text = mode == 0
                ? "正在扫描当前网络"
                : mode == 1 ? "正在依次查看常见内网（共 24 个网段）"
                : "正在扫描指定网段";
            StatusText.Text = mode == 1
                ? "按 192.168 → 10 → 172 交错轮询，进度条反映段数"
                : "C 扫描核心正在并发探测设备";
            ScanProgress.IsIndeterminate = false;
            ScanProgress.Minimum = 0;
            ScanProgress.Maximum = networks.Length;
            ScanProgress.Value = 0;
            _progressSegmentTotal = networks.Length;
            _progressSegmentIndex = 0;
            _progressHostsDone = 0;
            _progressSegmentActive = false;
            BeginScanRing();
            var identifyTasks = new List<Task>();
            var identifying = new HashSet<string>(StringComparer.Ordinal);
            for (var segmentIndex = 0; segmentIndex < networks.Length; segmentIndex++)
            {
                var network = networks[segmentIndex];
                if (_stopRequested) break;
                var label = NetworkLabel(network.source, network.prefix);
                try
                {
                    if (NeedsTemporaryIp(adapter, mode, network.source, network.prefix))
                    {
                        var temporary = IsSimulationAdapter(adapter)
                            ? SimulationHostAddress(network.source, network.prefix)
                            : TemporaryAddress(network.source, network.prefix);
                        _scanBindIp = temporary;
                        StatusText.Text = $"({segmentIndex + 1}/{networks.Length}) 配置 {adapter.Name} → {temporary}/{network.prefix}";
                        await EnsureAdapterEnabledAsync(adapter);
                        await RunNetshAsync($"interface ipv4 set address name=\"{adapter.Name}\" static {temporary} {PrefixToMask(network.prefix)} none");
                        var waitSeconds = IsSimulationAdapter(adapter) ? 15 : 8;
                        await WaitForAddressAsync(adapter.Id, temporary, TimeSpan.FromSeconds(waitSeconds));
                        if (IsSimulationAdapter(adapter))
                        {
                            var targets = SimulationProbeAddresses(network.source, network.prefix).ToList();
                            await InstallSimulationRoutesAsync(adapter, temporary, targets);
                            if (HasSubnetConflict(network.source, network.prefix, adapter.Id))
                            {
                                var net = NetworkLabel(network.source, network.prefix);
                                StatusText.Text += $"（检测到其他网卡也在 {net}，已强制走仿真网卡）";
                            }
                        }
                    }
                    var scanSource = _scanBindIp ?? network.source;
                    StatusText.Text = $"正在扫描第 {segmentIndex + 1}/{networks.Length} 段：{label}";
                    ResultSummary.Text = networks.Length > 1
                        ? $"正在查看第 {segmentIndex + 1} 个网段，已找到 {_devices.Count} 台设备"
                        : $"正在扫描当前网络，已找到 {_devices.Count} 台设备";
                    _progressSegmentIndex = segmentIndex;
                    _progressHostTotal = HostCountForPrefix(network.prefix);
                    _progressHostsDone = 0;
                    _progressSegmentActive = true;
                    _segmentStartedAt = DateTime.UtcNow;
                    UpdateScanRing();
                    _scanProcess = new Process {
                    StartInfo = new ProcessStartInfo(NativeTool, $"--scan {scanSource} {network.prefix}") {
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
                    if (IsSimulationAdapter(adapter))
                        await ProbeSimulationHostsAsync(network.source, network.prefix, identifying, identifyTasks);
                    scanned++;
                    foreach (var d in _devices.Where(NeedsIdentify))
                        if (identifying.Add(d.Address))
                            identifyTasks.Add(IdentifyDeviceAsync(d.Address));
                }
                catch (Exception ex) when (!_stopRequested)
                {
                    skipped++;
                    StatusText.Text = $"跳过 {label}：{ex.Message}";
                    _scanProcess?.Dispose(); _scanProcess = null;
                    _progressSegmentActive = false;
                    _progressHostsDone = _progressHostTotal;
                }
                finally
                {
                    await RemoveSimulationRoutesAsync();
                }
                RecordSegmentDuration();
                _progressSegmentActive = false;
                _progressHostsDone = _progressHostTotal;
                ScanProgress.Value = segmentIndex + 1;
                UpdateScanRing();
            }
            if (NeedsTemporaryIp(adapter, mode, networks[0].source, networks[0].prefix) || mode != 0)
            {
                try
                {
                    if (IsSimulationAdapter(adapter) && !hadAddressBeforeScan) restored = true;
                    else { await RestoreAdapterAsync(adapter); restored = true; }
                }
                catch (Exception ex) { StatusText.Text = $"扫描完成，恢复网卡失败：{ex.Message}"; }
            }
            await Task.WhenAll(identifyTasks);
            if (!_paused && !_stopRequested)
            {
                SetScanBadge("完成");
                StatusText.Text = skipped == 0
                    ? $"扫描完成（{scanned} 段含 192.168/10/172），发现 {_devices.Count} 台"
                    : $"扫描完成（成功 {scanned} 段，跳过 {skipped} 段），发现 {_devices.Count} 台";
            }
        }
        catch (Exception ex) { StatusText.Text = ex.Message; SetScanBadge("错误"); }
        finally
        {
            if (!restored)
            {
                try { await RestoreAdapterAsync(adapter); } catch { }
            }
            _scanProcess?.Dispose(); _scanProcess = null; _paused = false;
            _scanActive = false;
            _scanBindIp = null;
            await RemoveSimulationRoutesAsync();
            EndScanRing();
            ScanButton.IsEnabled = true; PauseButton.IsEnabled = false; StopButton.IsEnabled = false; AdapterBox.IsEnabled = true; TargetModeBox.IsEnabled = true;
            ScanProgress.IsIndeterminate = false;
            if (ScanProgress.Maximum < 1) ScanProgress.Maximum = 1;
            ScanProgress.Value = ScanProgress.Maximum;
            ResultSummary.Text = $"共找到 {_devices.Count} 台设备";
        }
    }

    private void HandleScanLine(string line)
    {
        var parts = line.Split('\t');
        if (parts.Length >= 2 && parts[0] == "DONE" && long.TryParse(parts[1], out var scanned))
        {
            _progressHostsDone = (int)Math.Min(scanned, _progressHostTotal);
            UpdateScanRing();
            return;
        }
        if (parts.Length >= 3 && (parts[0] == "HOST" || parts[0] == "ARP"))
        {
            var existing = -1;
            for (var i = 0; i < _devices.Count; i++)
                if (_devices[i].Address == parts[1]) { existing = i; break; }
            var mac = parts[0] == "ARP" ? parts[2] : "";
            if (existing < 0)
            {
                var latency = parts[0] == "HOST" ? $"{parts[2]} ms" : "ARP";
                _devices.Add(new Device(parts[1], latency, "识别中…", mac));
            }
            else if (parts[0] == "ARP" && string.IsNullOrWhiteSpace(_devices[existing].Mac))
                _devices[existing] = _devices[existing] with { Mac = mac, Latency = "ARP" };
            ResultSummary.Text = $"共找到 {_devices.Count} 台设备";
            ScrollResultsToEnd();
            UpdateEmptyState();
        }
    }

    private static bool NeedsIdentify(Device d) => d.Model.StartsWith("识别中", StringComparison.Ordinal);

    private async Task IdentifyDeviceAsync(string address)
    {
        var index = -1;
        for (var i = 0; i < _devices.Count; i++)
            if (_devices[i].Address == address) { index = i; break; }
        if (index < 0 || index >= _devices.Count) return;
        var mac = _devices[index].Mac;
        if (IsPlcTarget)
            _devices[index] = await IdentifyPlcDeviceAsync(address, mac, _devices[index].Latency);
        else
            _devices[index] = await IdentifyOtherDeviceAsync(address, mac, _devices[index].Latency);
    }

    private async Task<Device> IdentifyPlcDeviceAsync(string address, string mac, string latency)
    {
        var portDefs = new (int Port, string Label, int TimeoutMs)[] {
            (102, "S7/ISO", 400), (502, "Modbus TCP", 2000), (44818, "EtherNet/IP", 400), (4840, "OPC UA", 400), (80, "HTTP", 400), (8080, "HTTP", 400) };
        var openPorts = await ProbeOpenPortsAsync(address, portDefs);
        string? model = await PlcFingerprint.ProbeAsync(address, mac, BindAddress());
        model ??= PlcFingerprint.IdentifyFromMac(mac);
        string? detail = null;
        if (openPorts.Any(p => p.Port is 80 or 8080))
        {
            var (hmiModel, hmiDetail) = await DeviceFingerprint.IdentifyHmiAsync(address, mac);
            if (model is null || model.Contains("未知", StringComparison.Ordinal) || model.Contains("二层", StringComparison.Ordinal))
                model = hmiModel;
            detail = hmiDetail;
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            if (openPorts.Count > 0)
                model = string.Join(" · ", openPorts.Select(p => p.Label));
            else
                model = "未知 PLC（仅二层可见）";
            detail ??= "未能读取具体型号";
        }
        else if (openPorts.Count > 0)
        {
            var protocols = openPorts.Select(p => p.Label)
                .Where(l => !model.Contains(l.Split('/')[0], StringComparison.OrdinalIgnoreCase)).ToList();
            if (protocols.Count > 0)
                detail = string.IsNullOrWhiteSpace(detail) ? $"支持 {string.Join("、", protocols)}" : $"{detail} · 支持 {string.Join("、", protocols)}";
        }
        return new Device(address, latency, model, mac, detail ?? "");
    }

    private async Task<Device> IdentifyOtherDeviceAsync(string address, string mac, string latency)
    {
        var (model, detail) = await DeviceFingerprint.IdentifyOtherAsync(address, mac);
        return new Device(address, latency, model, mac, detail ?? "");
    }

    private async Task<List<(int Port, string Label)>> ProbeOpenPortsAsync(string address,
        (int Port, string Label, int TimeoutMs)[] portDefs)
    {
        var bind = BindAddress();
        var portTasks = portDefs.Select(async p =>
        {
            if (await SocketProbe.TryConnectAsync(address, p.Port, bind, TimeSpan.FromMilliseconds(p.TimeoutMs)))
                return ((int Port, string Label)?)(p.Port, p.Label);
            return null;
        }).ToArray();
        await Task.WhenAll(portTasks);
        return portTasks.Select(t => t.Result).Where(x => x is not null).Select(x => x!.Value).ToList();
    }

    private static IEnumerable<string> SimulationProbeAddresses(string source, int prefix)
    {
        var value = BitConverter.ToUInt32(IPAddress.Parse(source).GetAddressBytes().Reverse().ToArray());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var baseAddr = value & mask;
        foreach (var offset in new uint[] { 1, 2, 100 })
            yield return new IPAddress(BitConverter.GetBytes(baseAddr + offset).Reverse().ToArray()).ToString();
    }

    private IPAddress? BindAddress() =>
        IPAddress.TryParse(_scanBindIp, out var ip) ? ip : null;

    private async Task ProbeSimulationHostsAsync(string source, int prefix, HashSet<string> identifying, List<Task> identifyTasks)
    {
        var bind = BindAddress();
        foreach (var ip in SimulationProbeAddresses(source, prefix))
        {
            if (_devices.Any(d => d.Address == ip)) continue;
            if (!await SocketProbe.TryConnectAsync(ip, 102, bind, TimeSpan.FromMilliseconds(2000))) continue;
            _devices.Add(new Device(ip, "仿真", "识别中…", ""));
            ResultSummary.Text = $"共找到 {_devices.Count} 台设备";
            ScrollResultsToEnd();
            UpdateEmptyState();
            if (identifying.Add(ip))
                identifyTasks.Add(IdentifyDeviceAsync(ip));
        }
    }

    // interleave 192.168 / 10 / 172 so a mid-scan abort still covers all families
    private static (string source, int prefix, bool temporary)[] CommonNetworks() =>
    [
        ("192.168.1.250", 24, true), ("10.0.0.250", 24, true), ("172.16.0.250", 24, true),
        ("192.168.0.250", 24, true), ("10.0.1.250", 24, true), ("172.16.1.250", 24, true),
        ("192.168.2.250", 24, true), ("10.1.0.250", 24, true), ("172.17.0.250", 24, true),
        ("192.168.3.250", 24, true), ("10.10.0.250", 24, true), ("172.18.0.250", 24, true),
        ("192.168.4.250", 24, true), ("10.10.10.250", 24, true), ("172.20.0.250", 24, true),
        ("192.168.5.250", 24, true), ("10.20.0.250", 24, true), ("172.21.0.250", 24, true),
        ("192.168.10.250", 24, true), ("10.0.10.250", 24, true), ("172.22.0.250", 24, true),
        ("192.168.20.250", 24, true), ("192.168.100.250", 24, true), ("192.168.200.250", 24, true)
    ];

    private static int CapScanPrefix(int prefix) => prefix is >= 24 and <= 30 ? prefix : 24;

    private static int HostCountForPrefix(int prefix)
    {
        if (prefix >= 31) return 2;
        if (prefix <= 0) return 254;
        var hosts = (1 << (32 - prefix)) - 2;
        return Math.Max(1, hosts);
    }

    private void BeginScanRing()
    {
        _ringIndeterminatePhase = 0;
        _scanStartedAt = DateTime.UtcNow;
        _segmentStartedAt = null;
        _segmentDurationsSeconds.Clear();
        _ringTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _ringTimer.Tick -= RingTimer_Tick;
        _ringTimer.Tick += RingTimer_Tick;
        ScanProgressCluster.Visibility = Visibility.Visible;
        UpdateScanRing();
        _ringTimer.Start();
    }

    private void EndScanRing()
    {
        _ringTimer?.Stop();
        _progressSegmentActive = false;
        _progressIndeterminate = false;
        if (ScanProgress.Maximum > 0)
        {
            _progressSegmentIndex = (int)ScanProgress.Maximum - 1;
            _progressHostsDone = _progressHostTotal;
            UpdateScanRing();
        }
        ScanProgressCluster.Visibility = Visibility.Collapsed;
    }

    private double GetSegmentFraction()
    {
        if (_progressHostsDone > 0)
            return Math.Min(1.0, (double)_progressHostsDone / Math.Max(1, _progressHostTotal));
        if (!_progressSegmentActive || _segmentStartedAt is not { } start) return 0;
        var elapsed = (DateTime.UtcNow - start).TotalSeconds;
        var expected = _segmentDurationsSeconds.Count > 0 ? _segmentDurationsSeconds.Average() : DefaultSegmentSeconds;
        return Math.Min(0.92, elapsed / expected);
    }

    private void RecordSegmentDuration()
    {
        if (_segmentStartedAt is not { } start) return;
        _segmentDurationsSeconds.Add((DateTime.UtcNow - start).TotalSeconds);
        _segmentStartedAt = null;
    }

    private void RingTimer_Tick(object? sender, EventArgs e)
    {
        if (!_scanActive) return;
        var segmentFraction = GetSegmentFraction();
        _progressIndeterminate = _progressSegmentActive && segmentFraction < 0.03 && !_paused;
        if (_progressIndeterminate)
        {
            _ringIndeterminatePhase = (_ringIndeterminatePhase + 10) % 360;
            var sweep = RingCircumference * 0.25;
            ScanRingArc.StrokeDashArray = new DoubleCollection { sweep, RingCircumference - sweep };
            ScanRingArc.StrokeDashOffset = -_ringIndeterminatePhase / 360 * RingCircumference;
            ScanRingArc.Opacity = _paused ? 0.45 : 1;
            ScanRingPercent.Text = "…";
        }
        else
        {
            var segmentTotal = Math.Max(1, _progressSegmentTotal);
            var value = Math.Min(segmentTotal, _progressSegmentIndex + segmentFraction);
            var percent = (int)Math.Round(value / segmentTotal * 100);
            ScanRingPercent.Text = $"{percent}%";
            var dash = RingCircumference * value / segmentTotal;
            ScanRingArc.StrokeDashArray = new DoubleCollection { dash, RingCircumference };
            ScanRingArc.StrokeDashOffset = 0;
            ScanRingArc.Opacity = _paused ? 0.45 : 1;
        }
        UpdateScanRingLabels(segmentFraction);
    }

    private void UpdateScanRing()
    {
        if (!_scanActive) return;
        var segmentFraction = GetSegmentFraction();
        _progressIndeterminate = _progressSegmentActive && segmentFraction < 0.03 && !_paused;
        if (!_progressIndeterminate)
        {
            var segmentTotal = Math.Max(1, _progressSegmentTotal);
            var value = Math.Min(segmentTotal, _progressSegmentIndex + segmentFraction);
            var percent = (int)Math.Round(value / segmentTotal * 100);
            ScanRingPercent.Text = $"{percent}%";
            var dash = RingCircumference * value / segmentTotal;
            ScanRingArc.StrokeDashArray = new DoubleCollection { dash, RingCircumference };
            ScanRingArc.StrokeDashOffset = 0;
            ScanRingArc.Opacity = _paused ? 0.45 : 1;
        }
        UpdateScanRingLabels(segmentFraction);
    }

    private void UpdateScanRingLabels(double segmentFraction)
    {
        var segmentTotal = Math.Max(1, _progressSegmentTotal);
        ScanRingSegment.Text = segmentTotal > 1
            ? $"第 {_progressSegmentIndex + 1}/{segmentTotal} 段"
            : $"已扫描 {_progressHostsDone}/{Math.Max(1, _progressHostTotal)} 台";
        var progress = Math.Min(1.0, (_progressSegmentIndex + segmentFraction) / segmentTotal);
        UpdateEtaText(progress);
    }

    private void UpdateEtaText(double progress)
    {
        if (!_scanActive) return;
        if (_paused)
        {
            ScanEtaText.Text = "已暂停";
            return;
        }
        if (progress >= 0.995)
        {
            ScanEtaText.Text = "剩余约 5 秒";
            return;
        }
        TimeSpan remaining;
        if (progress < 0.03)
        {
            remaining = _segmentDurationsSeconds.Count > 0
                ? EstimateFromSegments()
                : TimeSpan.FromSeconds(_progressSegmentTotal > 1 ? DefaultSegmentSeconds * _progressSegmentTotal : DefaultSegmentSeconds);
        }
        else
        {
            var elapsed = DateTime.UtcNow - _scanStartedAt;
            remaining = TimeSpan.FromTicks((long)(elapsed.Ticks * (1 - progress) / progress));
        }
        ScanEtaText.Text = $"剩余 {FormatRemainingPrecise(remaining)}";
    }

    private TimeSpan EstimateFromSegments()
    {
        var avg = _segmentDurationsSeconds.Average();
        var left = Math.Max(0, _progressSegmentTotal - _progressSegmentIndex - 0.5);
        return TimeSpan.FromSeconds(avg * left);
    }

    private static string FormatRemainingPrecise(TimeSpan remaining)
    {
        var seconds = Math.Max(5, (int)Math.Round(remaining.TotalSeconds / 5.0) * 5);
        var minutes = seconds / 60;
        var secs = seconds % 60;
        if (minutes == 0) return $"{secs} 秒";
        return secs == 0 ? $"{minutes} 分" : $"{minutes} 分 {secs:D2} 秒";
    }

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

    private static string SimulationHostAddress(string source, int prefix)
    {
        var value = BitConverter.ToUInt32(IPAddress.Parse(source).GetAddressBytes().Reverse().ToArray());
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        return new IPAddress(BitConverter.GetBytes((value & mask) + 2).Reverse().ToArray()).ToString();
    }

    private static bool HasSubnetConflict(string source, int prefix, string adapterId)
    {
        var label = NetworkLabel(source, prefix);
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => !string.Equals(n.Id, adapterId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(n =>
            {
                try { return n.GetIPProperties().UnicastAddresses; }
                catch (NetworkInformationException) { return []; }
            })
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Any(a => NetworkLabel(a.Address.ToString(), prefix) == label);
    }

    private static int? GetInterfaceIndex(string adapterId)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(x => string.Equals(x.Id, adapterId, StringComparison.OrdinalIgnoreCase));
        if (nic is null) return null;
        try { return nic.GetIPProperties().GetIPv4Properties()?.Index; }
        catch (NetworkInformationException) { return null; }
    }

    private async Task EnsureAdapterEnabledAsync(Adapter adapter) =>
        await RunNetshAsync($"interface set interface \"{adapter.Name}\" admin=enabled");

    private async Task InstallSimulationRoutesAsync(Adapter adapter, string localIp, IEnumerable<string> targets)
    {
        var ifIndex = GetInterfaceIndex(adapter.Id);
        if (ifIndex is null) return;
        foreach (var target in targets)
        {
            if (_simulationRoutes.Contains(target, StringComparer.Ordinal)) continue;
            try
            {
                await RunRouteAsync($"ADD {target} MASK 255.255.255.255 {localIp} METRIC 1 IF {ifIndex}");
                _simulationRoutes.Add(target);
            }
            catch { }
        }
    }

    private async Task RemoveSimulationRoutesAsync()
    {
        foreach (var target in _simulationRoutes.ToList())
        {
            try { await RunRouteAsync($"DELETE {target}"); }
            catch { }
        }
        _simulationRoutes.Clear();
    }

    private static async Task RunRouteAsync(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("route", arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        })!;
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0 && !stderr.Contains("对象已存在", StringComparison.Ordinal) &&
            !stderr.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(stderr.Trim());
    }

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
                await Task.Delay(120);
                return;
            }
            await Task.Delay(100);
        }
        throw new InvalidOperationException($"网卡临时地址 {address} 未在 {timeout.TotalSeconds:0} 秒内生效，已停止本段扫描");
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
        SetScanBadge(_paused ? "已暂停" : "扫描中");
        StatusText.Text = _paused ? "扫描已暂停" : "扫描已继续";
        UpdateScanRing();
    }

    private void Stop_Click(object sender, RoutedEventArgs e) => StopScan();
    private void StopScan()
    {
        _stopRequested = true;
        if (_scanProcess is { HasExited: false }) _scanProcess.Kill(true);
        _paused = false;
        SetScanBadge("已停止");
    }

    private void SetScanBadge(string text)
    {
        ScanBadge.Text = text;
        var (bg, fg, dot) = text switch
        {
            "扫描中" => ("#D1FAE5", "#176044", (Brush)FindResource("Accent")),
            "已暂停" => ("#FEF3C7", "#92400E", (Brush)FindResource("Warning")),
            "完成" => ("#D1FAE5", "#176044", (Brush)FindResource("Success")),
            "已停止" => ("#EDF1EF", "#596B65", (Brush)FindResource("Muted")),
            "错误" => ("#FEE2E2", "#B91C1C", (Brush)FindResource("Danger")),
            _ => ("#EDF1EF", "#596B65", (Brush)FindResource("Accent"))
        };
        ScanBadge.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bg));
        ScanBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fg));
        StatusDot.Fill = dot;
    }

    private void UpdateEmptyState() =>
        EmptyState.Visibility = _devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void ScrollResultsToEnd()
    {
        if (ResultsGrid.Items.Count == 0) return;
        ResultsGrid.ScrollIntoView(ResultsGrid.Items[^1]);
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not Adapter adapter || ResultsGrid.SelectedItem is not Device device) return;
        const int prefix = 24;
        var local = ChooseLocalAddress(IPAddress.Parse(device.Address), prefix, adapter.Gateway);
        IpBox.Text = local.ToString();
        MaskBox.Text = PrefixToMask(prefix);
        if (MessageBox.Show($"将 {adapter.Name} 设置为 {local}/{prefix}（与设备 {device.Address} 同网段）。\n\n当前网络连接会短暂中断。", "确认配置网卡", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await ApplyNetworkCoreAsync(adapter);
    }

    private async void ApplyNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not Adapter adapter) return;
        if (string.IsNullOrWhiteSpace(IpBox.Text))
        {
            if (MessageBox.Show($"将 {adapter.Name} 恢复为 DHCP 自动获取地址与 DNS。", "应用 DHCP", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            await ApplyDhcpAsync(adapter);
            return;
        }
        if (MessageBox.Show($"将 {adapter.Name} 应用以下网络设置：\nIP {IpBox.Text.Trim()}\n掩码 {ResolveMask()}\n网关 {(string.IsNullOrWhiteSpace(GatewayBox.Text) ? "无" : GatewayBox.Text.Trim())}\nDNS {(string.IsNullOrWhiteSpace(DnsBox.Text) ? "不修改" : DnsBox.Text.Trim())}", "确认网络设置", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        await ApplyNetworkCoreAsync(adapter);
    }

    private string ResolveMask()
    {
        var mask = MaskBox.Text.Trim();
        return string.IsNullOrWhiteSpace(mask) ? "255.255.255.0" : mask;
    }

    private async Task ApplyNetworkCoreAsync(Adapter adapter)
    {
        var ip = IpBox.Text.Trim();
        try
        {
            if (string.IsNullOrWhiteSpace(ip))
            {
                await ApplyDhcpAsync(adapter);
                return;
            }
            if (!IPAddress.TryParse(ip, out _))
                throw new InvalidOperationException("IP 地址格式无效");
            var mask = ResolveMask();
            if (!IPAddress.TryParse(mask, out _))
                throw new InvalidOperationException("子网掩码格式无效");
            var gateway = GatewayBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(gateway) && !IPAddress.TryParse(gateway, out _))
                throw new InvalidOperationException("网关格式无效");
            var gwPart = string.IsNullOrWhiteSpace(gateway) ? "none" : gateway;
            await RunNetshAsync($"interface ipv4 set address name=\"{adapter.Name}\" static {ip} {mask} {gwPart}");
            var dns = DnsBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(dns))
            {
                if (!IPAddress.TryParse(dns, out _))
                    throw new InvalidOperationException("DNS 格式无效");
                await RunNetshAsync($"interface ipv4 set dnsservers name=\"{adapter.Name}\" static {dns} primary");
            }
            StatusText.Text = $"已将 {adapter.Name} 设置为 {ip}";
            MessageBox.Show($"已成功配置 {adapter.Name}。\nIP：{ip}\n掩码：{mask}", "配置完成", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAdaptersAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = FormatNetshError(ex.Message);
            MessageBox.Show(FormatNetshError(ex.Message), "配置网卡失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task ApplyDhcpAsync(Adapter adapter)
    {
        try
        {
            await RunNetshAsync($"interface ipv4 set address name=\"{adapter.Name}\" source=dhcp");
            try { await RunNetshAsync($"interface ipv4 set dnsservers name=\"{adapter.Name}\" source=dhcp"); }
            catch (Exception ex)
            {
                StatusText.Text = $"IP 已恢复 DHCP，DNS 未完成：{FormatNetshError(ex.Message)}";
                MessageBox.Show($"IP 地址已改为自动获取，但 DNS 恢复失败：\n{FormatNetshError(ex.Message)}", "部分完成", MessageBoxButton.OK, MessageBoxImage.Warning);
                await RefreshAdaptersAsync();
                return;
            }
            IpBox.Text = "";
            MaskBox.Text = "";
            GatewayBox.Text = "";
            DnsBox.Text = "";
            StatusText.Text = $"已将 {adapter.Name} 恢复为 DHCP";
            MessageBox.Show($"已成功将 {adapter.Name} 恢复为自动获取 IP 和 DNS。", "恢复完成", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshAdaptersAsync();
        }
        catch (Exception ex)
        {
            StatusText.Text = FormatNetshError(ex.Message);
            MessageBox.Show(FormatNetshError(ex.Message), "恢复 DHCP 失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Dhcp_Click(object sender, RoutedEventArgs e)
    {
        if (AdapterBox.SelectedItem is not Adapter adapter) return;
        if (MessageBox.Show($"将 {adapter.Name} 恢复为 DHCP 自动获取地址与 DNS。", "恢复 DHCP", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await ApplyDhcpAsync(adapter);
    }

    private static string FormatNetshError(string detail)
    {
        if (string.IsNullOrWhiteSpace(detail)) return "网络配置失败，请确认已以管理员身份运行。";
        if (detail.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("拒绝访问", StringComparison.Ordinal) ||
            detail.Contains("requires elevation", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("提升", StringComparison.Ordinal) ||
            detail.Contains("请求的操作需要提升", StringComparison.Ordinal))
            return "需要管理员权限。\n请关闭程序后重新以管理员身份运行（右键 → 以管理员身份运行）。";
        if (detail.Contains("DHCP", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("自动配置", StringComparison.Ordinal) ||
            detail.Contains("没有为", StringComparison.Ordinal) && detail.Contains("DHCP", StringComparison.OrdinalIgnoreCase))
            return "该网卡无法使用 DHCP（常见于仿真虚拟网卡）。\n请手动填写 IP、掩码后点击「应用网络设置」。";
        if (detail.Contains("找不到元素", StringComparison.Ordinal) ||
            detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("不存在", StringComparison.Ordinal))
            return "未找到指定网卡，请刷新网卡列表后重试。";
        if (detail.Contains("参数", StringComparison.Ordinal) && detail.Contains("不正确", StringComparison.Ordinal))
            return "网络参数格式不正确，请检查 IP、掩码、网关和 DNS。";
        if (ContainsMojibake(detail))
            return "网络配置失败。仿真网卡通常不支持 DHCP，请手动填写 IP（如 192.168.0.2）和掩码 255.255.255.0。";
        return detail;
    }

    private static bool ContainsMojibake(string text) =>
        text.Contains("锟", StringComparison.Ordinal) ||
        text.Contains("鍙", StringComparison.Ordinal) ||
        text.Any(c => c is >= '\uE000' and <= '\uF8FF');

    private static readonly Encoding NetshEncoding = Encoding.GetEncoding(936);

    private static async Task RunNetshAsync(params string[] commands)
    {
        foreach (var command in commands)
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", command)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = NetshEncoding,
                StandardErrorEncoding = NetshEncoding
            })!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            if (process.ExitCode == 0) continue;
            var detail = PickNetshError(stdout, stderr);
            if (IsBenignNetshFailure(detail)) continue;
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(detail) ? "网络配置失败" : detail.Trim());
        }
    }

    private static string PickNetshError(string stdout, string stderr)
    {
        foreach (var line in (stdout + "\n" + stderr).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            if (trimmed.StartsWith("请求的操作需要", StringComparison.Ordinal)) return trimmed;
            if (trimmed.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("失败", StringComparison.Ordinal) ||
                trimmed.Contains("denied", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("拒绝", StringComparison.Ordinal))
                return trimmed;
        }
        return string.IsNullOrWhiteSpace(stderr) ? stdout.Trim() : stderr.Trim();
    }

    private static bool IsBenignNetshFailure(string detail) =>
        detail.Contains("already", StringComparison.OrdinalIgnoreCase) ||
        detail.Contains("已经是", StringComparison.Ordinal) ||
        detail.Contains("无需更改", StringComparison.Ordinal) ||
        detail.Contains("已启用", StringComparison.Ordinal) && detail.Contains("DHCP", StringComparison.OrdinalIgnoreCase);

    private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is Device device) ApplyButton.IsEnabled = true;
        else ApplyButton.IsEnabled = false;
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
public sealed record Device(string Address, string Latency, string Model, string Mac = "", string Detail = "")
{
    public string MacDisplay => string.IsNullOrWhiteSpace(Mac) ? "" : Mac.Contains('-') ? $"MAC {Mac}" : Mac;
    public bool HasMac => !string.IsNullOrWhiteSpace(Mac);
    public string ModelLine => string.IsNullOrWhiteSpace(Detail) ? Model : $"{Model} · {Detail}";
}
