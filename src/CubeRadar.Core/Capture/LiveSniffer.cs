// Derived from cyberbadger6969/aion2-dps-meter @ 414de5c (GPL-3.0), src/AionMeter.Core/Capture/LiveCapture.cs.
// Changes: hands out raw packets (for the pcapng recording) instead of game events; the BPF filter always includes
// the game port and only grows (each filter change flushes Npcap's buffer); status as plain text.
using System.Net;
using SharpPcap;
using SharpPcap.LibPcap;

namespace CubeRadar.Core.Capture;

public sealed class LiveSnifferOptions
{
    /// <summary>Npcap device name to force (e.g. a VPN adapter). Null = pick automatically from the game's connections.</summary>
    public string? DeviceName { get; set; }
}

public readonly record struct CaptureHealth(long Received, long Dropped);

/// <summary>
/// Passive capture of the AION 2 connection through Npcap. It looks up the game's own TCP connections in the Windows
/// TCP table, opens only the adapter that carries them (the loopback adapter when a ping accelerator relays the game
/// locally) and filters to the game port plus those servers. Nothing is ever sent or injected.
/// </summary>
public sealed class LiveSniffer : IDisposable
{
    private const int KernelBufferBytes = 16 * 1024 * 1024;

    private readonly LiveSnifferOptions _options;
    private readonly object _gate = new();
    private readonly SortedSet<string> _extraHosts = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _monitor;
    private LibPcapLiveDevice? _device;
    private string? _filter;

    public LiveSniffer(LiveSnifferOptions options) => _options = options;

    /// <summary>Raised on the capture thread for every packet that passes the filter.</summary>
    public event Action<RawCapture, LibPcapLiveDevice>? PacketArrived;
    public event Action<string>? StatusChanged;

    public string Status { get; private set; } = "stopped";
    public string? DeviceDescription { get; private set; }
    public string? Filter => _filter;

    public static bool IsNpcapInstalled() =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "Npcap", "wpcap.dll")) ||
        File.Exists(Path.Combine(Environment.SystemDirectory, "wpcap.dll"));

    public static IReadOnlyList<(string Name, string Description)> ListDevices() =>
        CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>().Select(d => (d.Name, Describe(d))).ToList();

    /// <summary>Packets Npcap delivered and dropped on the open adapter; null while none is open.</summary>
    public CaptureHealth? Health()
    {
        lock (_gate)
        {
            if (_device is null) return null;
            try
            {
                var s = _device.Statistics;
                return new CaptureHealth(s.ReceivedPackets, s.DroppedPackets + s.InterfaceDroppedPackets);
            }
            catch (PcapException)
            {
                return null;
            }
        }
    }

    public void Start()
    {
        if (_monitor is not null) return;
        _cts = new CancellationTokenSource();
        SetStatus("starting");
        _monitor = Task.Run(() => MonitorLoop(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _monitor?.Wait(3_000); } catch (AggregateException) { }
        _monitor = null;
        CloseDevice();
        SetStatus("stopped");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }

    private async Task MonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                Refresh();
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
            {
                SetStatus("Npcap is not installed (npcap.com)");
            }
            catch (PcapException ex)
            {
                SetStatus("capture error: " + ex.Message);
                CloseDevice();
            }
            try
            {
                await Task.Delay(2_000, ct).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void Refresh()
    {
        var devices = CaptureDeviceList.Instance.OfType<LibPcapLiveDevice>().ToList();
        var pids = GameProcessLocator.FindGameProcessIds();
        var connections = pids.Length == 0 ? [] : GameProcessLocator.FindConnections(pids);
        foreach (var c in connections.Where(c => c.RemotePort != StreamTracker.GamePort))
            _extraHosts.Add($"(host {c.RemoteIp} and port {c.RemotePort})");
        var filter = _extraHosts.Count == 0
            ? $"tcp port {StreamTracker.GamePort}"
            : $"tcp and (port {StreamTracker.GamePort} or " + string.Join(" or ", _extraHosts) + ")";

        if (_options.DeviceName is { Length: > 0 } forced)
        {
            var dev = devices.FirstOrDefault(d => d.Name == forced);
            if (dev is null)
            {
                SetStatus($"adapter not found: {forced}");
                return;
            }
            EnsureDevice(dev, filter);
            SetStatus($"capturing on {Describe(dev)}");
            return;
        }

        if (pids.Length == 0)
        {
            SetStatus(_device is null ? "AION 2 is not running" : $"AION 2 not running · still capturing on {DeviceDescription}");
            return;
        }
        if (connections.Count == 0)
        {
            SetStatus(_device is null ? "waiting for the game to connect" : $"no game connection · still capturing on {DeviceDescription}");
            return;
        }

        LibPcapLiveDevice? device = null;
        foreach (var c in connections.OrderByDescending(c => c.RemotePort == StreamTracker.GamePort))
        {
            device = IPAddress.IsLoopback(c.RemoteIp) || IPAddress.IsLoopback(c.LocalIp)
                ? devices.FirstOrDefault(IsLoopback)
                : devices.FirstOrDefault(d => d.Addresses.Any(a => c.LocalIp.Equals(a.Addr?.ipAddress)));
            if (device is not null) break;
        }
        if (device is null)
        {
            SetStatus("no Npcap adapter carries the game connection (VPN? use --device)");
            return;
        }
        EnsureDevice(device, filter);
        SetStatus($"capturing on {Describe(device)}");
    }

    private void EnsureDevice(LibPcapLiveDevice device, string filter)
    {
        lock (_gate)
        {
            if (_device is not null && _device.Name != device.Name) CloseDeviceLocked();
            if (_device is null)
            {
                device.OnPacketArrival += OnPacketArrival;
                // Bursts (a crowd at a world boss) overflow Npcap's default 1 MB kernel buffer; every drop is a hole.
                device.Open(new DeviceConfiguration { Mode = DeviceModes.None, ReadTimeout = 100, BufferSize = KernelBufferBytes });
                device.Filter = filter;
                _filter = filter;
                _device = device;
                DeviceDescription = Describe(device);
                device.StartCapture();
            }
            else if (_filter != filter)
            {
                _device.Filter = filter;
                _filter = filter;
            }
        }
    }

    private void CloseDevice()
    {
        lock (_gate) CloseDeviceLocked();
    }

    private void CloseDeviceLocked()
    {
        if (_device is null) return;
        try
        {
            _device.StopCapture();
        }
        catch (PcapException)
        {
        }
        _device.OnPacketArrival -= OnPacketArrival;
        _device.Close();
        _device = null;
        _filter = null;
    }

    private void OnPacketArrival(object sender, PacketCapture e)
    {
        try
        {
            PacketArrived?.Invoke(e.GetPacket(), (LibPcapLiveDevice)sender);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A malformed packet must never take the capture thread down.
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }

    private void SetStatus(string message)
    {
        if (message == Status) return;
        Status = message;
        StatusChanged?.Invoke(message);
    }

    public static bool IsLoopback(LibPcapLiveDevice d) =>
        d.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase) ||
        (d.Description?.Contains("loopback", StringComparison.OrdinalIgnoreCase) ?? false);

    public static string Describe(LibPcapLiveDevice d) =>
        IsLoopback(d) ? "Loopback" : string.IsNullOrWhiteSpace(d.Description) ? d.Name : d.Description!;
}
