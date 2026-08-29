using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CNS.StorageCluster.Client.Services;
using CNS.StorageCluster.Shared;

namespace CNS.StorageCluster.Client;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _logs = [];
    private readonly ObservableCollection<DiskMetrics> _disks = [];
    private readonly DispatcherTimer _clockTimer;
    private IStorageClientService? _client;

    public MainWindow()
    {
        InitializeComponent();
        RegionCombo.ItemsSource = RegionCatalog.All;
        RegionCombo.SelectedIndex = 0;
        ServerHostText.Text = NetworkDefaults.ServerHost;
        ServerPortText.Text = NetworkDefaults.WebSocketPort.ToString();
        IntervalText.Text = NetworkDefaults.DefaultReportIntervalSeconds.ToString();
        LogList.ItemsSource = _logs;
        DiskList.ItemsSource = _disks;

        var (mac, ip) = DiskMetricsProvider.GetNetworkIdentity();
        LocalIpBadge.Text = ip;
        LocalMacBadge.Text = mac;
        SideIpText.Text = ip;
        SideMacText.Text = mac;

        UpdateClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        Closed += async (_, _) =>
        {
            _clockTimer.Stop();
            if (_client is not null) await _client.StopAsync();
        };
    }

    private void UpdateClock()
    {
        var now = DateTime.Now.ToString("HH:mm:ss");
        ClockText.Text = now;
        SideTimeText.Text = now;
    }

    private async void Connect_Click(object? sender, RoutedEventArgs e)
    {
        if (RegionCombo.SelectedItem is not RegionDefinition region) return;
        if (!int.TryParse(ServerPortText.Text, out var port) || port is < 1 or > 65535)
        {
            AddLog("Puerto inválido.");
            return;
        }

        var host = string.IsNullOrWhiteSpace(ServerHostText.Text)
            ? NetworkDefaults.ServerHost
            : ServerHostText.Text.Trim();
        ServerHostText.Text = host;

        if (!int.TryParse(IntervalText.Text, out var interval))
            interval = NetworkDefaults.DefaultReportIntervalSeconds;
        interval = Math.Clamp(interval, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
        IntervalText.Text = interval.ToString();

        if (_client is not null) await _client.StopAsync();
        _client = port == NetworkDefaults.WebSocketPort
            ? new WebSocketStorageClientService(region.Code, host, port, interval)
            : new StorageClientService(region.Code, host, port, interval);
        _client.Log += AddLog;
        _client.ConnectionChanged += connected => Dispatcher.UIThread.Post(() =>
        {
            SetConnectionVisual(
                connected ? "CONECTADO" : "RECONECTANDO",
                connected ? "#0E9F6E" : "#D97706");
            ConnectButton.IsEnabled = false;
            DisconnectButton.IsEnabled = true;
            RegionCombo.IsEnabled = false;
            ServerHostText.IsEnabled = false;
            ServerPortText.IsEnabled = false;
        });
        _client.MetricsProduced += m => Dispatcher.UIThread.Post(() => UpdateMetrics(m));
        _client.IntervalChanged += seconds => Dispatcher.UIThread.Post(() => IntervalText.Text = seconds.ToString());

        ConnectButton.IsEnabled = false;
        DisconnectButton.IsEnabled = true;
        RegionCombo.IsEnabled = false;
        ServerHostText.IsEnabled = false;
        ServerPortText.IsEnabled = false;
        SetConnectionVisual("CONECTANDO", "#D97706");
        var transport = port == NetworkDefaults.WebSocketPort ? "WSS" : "TCP";
        AddLog($"Iniciando cliente {region.Name} ({region.Code}) por {transport} contra {host}:{port}...");
        await _client.StartAsync();
    }

    private async void Disconnect_Click(object? sender, RoutedEventArgs e)
    {
        if (_client is not null) await _client.StopAsync();
        ConnectButton.IsEnabled = true;
        DisconnectButton.IsEnabled = false;
        RegionCombo.IsEnabled = true;
        ServerHostText.IsEnabled = true;
        ServerPortText.IsEnabled = true;
        SetConnectionVisual("DESCONECTADO", "#8A98A3");
    }

    private async void ApplyInterval_Click(object? sender, RoutedEventArgs e)
    {
        if (!int.TryParse(IntervalText.Text, out var seconds))
        {
            AddLog("Intervalo inválido.");
            return;
        }
        seconds = Math.Clamp(seconds, NetworkDefaults.MinimumReportIntervalSeconds, NetworkDefaults.MaximumReportIntervalSeconds);
        IntervalText.Text = seconds.ToString();
        if (_client is null)
        {
            AddLog($"Intervalo local preparado: {seconds} s. Se enviará al conectar.");
            return;
        }
        await _client.SetLocalIntervalAsync(seconds);
    }

    private void ClearLog_Click(object? sender, RoutedEventArgs e) => _logs.Clear();

    private void UpdateMetrics(MetricsMessage m)
    {
        _disks.Clear();
        foreach (var disk in m.Disks) _disks.Add(disk);

        DiskCountText.Text = m.DiskCount.ToString();
        DiskNameText.Text = m.DiskCount switch
        {
            0 => "Sin discos detectados",
            1 => m.Disks[0].DiskName,
            _ => $"{m.DiskCount} discos"
        };
        DiskTypeText.Text = m.DiskCount == 0
            ? "-"
            : string.Join(", ", m.Disks.Select(x => x.DiskType).Distinct(StringComparer.OrdinalIgnoreCase));
        TotalText.Text = $"{m.TotalGb:N1} GB";
        UsedText.Text = $"{m.UsedGb:N1} GB";
        FreeText.Text = $"{m.FreeGb:N1} GB";
        UsageText.Text = $"{m.UtilizationPercent:N1} %";
        UtilizationBar.Value = m.UtilizationPercent;
        IopsOnlyText.Text = $"{m.Iops:N0}";
        LatencyText.Text = $"{m.LatencyMs:N1} ms";
        LastReportText.Text = $"Reporte {m.TimestampUtc.ToLocalTime():HH:mm:ss}";
        if (!string.IsNullOrWhiteSpace(m.IpAddress))
        {
            LocalIpBadge.Text = m.IpAddress;
            SideIpText.Text = m.IpAddress;
        }
        if (!string.IsNullOrWhiteSpace(m.MacAddress))
        {
            LocalMacBadge.Text = m.MacAddress;
            SideMacText.Text = m.MacAddress;
        }
    }

    private void AddLog(string text)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _logs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {text}");
            while (_logs.Count > 150) _logs.RemoveAt(_logs.Count - 1);
        });
    }

    private void SetConnectionVisual(string status, string color)
    {
        ConnectionBadge.Text = status;
        ConnectionIndicator.Background = new SolidColorBrush(Color.Parse(color));
    }
}
