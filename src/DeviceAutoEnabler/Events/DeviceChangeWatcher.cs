using System.Management;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace DeviceAutoEnabler.Events;

/// <summary>
/// Subscribes to the extrinsic WMI <c>Win32_DeviceChangeEvent</c>. Extrinsic events are pushed by
/// the OS (event-driven), so there is no internal polling. We deliberately avoid
/// <c>__InstanceCreationEvent</c>/<c>WITHIN</c> queries, which force expensive periodic WMI polling.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceChangeWatcher : IDisposable
{
    private readonly ILogger<DeviceChangeWatcher> _logger;
    private readonly object _sync = new();
    private ManagementEventWatcher? _watcher;
    private bool _disposed;

    /// <summary>Raised when the OS reports a device configuration change.</summary>
    public event Action? DeviceChanged;

    public DeviceChangeWatcher(ILogger<DeviceChangeWatcher> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_disposed || _watcher is not null)
            {
                return;
            }

            try
            {
                var query = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent");
                _watcher = new ManagementEventWatcher(query);
                _watcher.EventArrived += OnEventArrived;
                _watcher.Start();
                _logger.LogInformation("WMI device-change watcher started (Win32_DeviceChangeEvent).");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start WMI device-change watcher; enable scans will run at startup and on config reload only.");
                DisposeWatcherNoLock();
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            DisposeWatcherNoLock();
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
    {
        try
        {
            DeviceChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Device-change handler threw.");
        }
    }

    private void DisposeWatcherNoLock()
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            _watcher.EventArrived -= OnEventArrived;
            _watcher.Stop();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while stopping WMI watcher.");
        }
        finally
        {
            _watcher.Dispose();
            _watcher = null;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            DisposeWatcherNoLock();
        }
    }
}
