using System.Collections.Concurrent;
using System.Runtime.Versioning;
using DeviceAutoEnabler.Config;
using DeviceAutoEnabler.Devices;
using DeviceAutoEnabler.Events;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DeviceAutoEnabler;

/// <summary>
/// Orchestrates the WMI device-change watcher into a debounced, single-flight scan-and-enable
/// routine. Enable attempts are rate-limited per device (cooldown) and logging happens only on
/// state transitions to avoid log spam.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ConfigLoader _configLoader;
    private readonly DeviceManager _deviceManager;
    private readonly DeviceChangeWatcher _watcher;

    // Single-flight guard: only one scan runs at a time; overlapping triggers coalesce.
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private int _scanRequested;

    // Per-device state for cooldown + transition-only logging.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastEnableAttempt = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _lastKnownDisabled = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _debounceTimer;
    private CancellationToken _stoppingToken;

    // Snapshot of live config, refreshed on hot-reload.
    private volatile AppConfig _config;
    private volatile IReadOnlyList<CompiledRule> _rules = Array.Empty<CompiledRule>();

    public Worker(
        ILogger<Worker> logger,
        ConfigLoader configLoader,
        DeviceManager deviceManager,
        DeviceChangeWatcher watcher)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configLoader = configLoader ?? throw new ArgumentNullException(nameof(configLoader));
        _deviceManager = deviceManager ?? throw new ArgumentNullException(nameof(deviceManager));
        _watcher = watcher ?? throw new ArgumentNullException(nameof(watcher));
        _config = configLoader.Current;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;
        _logger.LogInformation("Device Auto Enabler service starting.");

        RecompileRules(_config);

        // Create the debounce timer once, dormant, before any device-change events can arrive.
        // This avoids a race on lazy initialization: EventArrived fires on WMI/threadpool worker
        // threads, so concurrent OnDeviceChanged() calls must not each construct their own Timer.
        _debounceTimer = new Timer(_ => RequestScan("device-change"), null, Timeout.Infinite, Timeout.Infinite);

        _configLoader.ConfigChanged += OnConfigChanged;
        _configLoader.StartWatching();

        _watcher.DeviceChanged += OnDeviceChanged;
        _watcher.Start();

        // One initial scan on startup so devices already disabled at boot get fixed.
        RequestScan("startup");

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            _watcher.DeviceChanged -= OnDeviceChanged;
            _configLoader.ConfigChanged -= OnConfigChanged;
            _logger.LogInformation("Device Auto Enabler service stopping.");
        }
    }

    private void OnDeviceChanged()
    {
        // Coalesce a burst of device-change events into a single debounced scan.
        var debounceMs = _config.EventDebounceMs;
        if (debounceMs <= 0)
        {
            RequestScan("device-change");
            return;
        }

        // Timer.Change is thread-safe; a burst of concurrent events simply resets the debounce window.
        _debounceTimer?.Change(debounceMs, Timeout.Infinite);
    }

    private void OnConfigChanged(AppConfig config)
    {
        _config = config;
        RecompileRules(config);

        _watcher.DeviceChanged -= OnDeviceChanged;
        _watcher.DeviceChanged += OnDeviceChanged;
        _watcher.Start();

        RequestScan("config-reload");
    }

    private void RecompileRules(AppConfig config)
    {
        var timeout = TimeSpan.FromMilliseconds(config.RegexMatchTimeoutMs);
        var compiled = new List<CompiledRule>(config.Devices.Count);
        foreach (var rule in config.Devices)
        {
            try
            {
                compiled.Add(CompiledRule.Create(rule, timeout));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Skipping invalid device rule '{Match}'.", rule.Match);
            }
        }

        _rules = compiled;
        _logger.LogInformation("Active configuration: {RuleCount} device rule(s), debounce={Debounce}ms, cooldown={Cooldown}s.",
            compiled.Count, config.EventDebounceMs, config.PerDeviceCooldownSeconds);
    }

    /// <summary>
    /// Request a scan. If one is already running, mark that another is needed so we re-run once
    /// (coalescing) rather than queueing many scans.
    /// </summary>
    private void RequestScan(string reason)
    {
        Interlocked.Exchange(ref _scanRequested, 1);
        _ = Task.Run(() => RunScanAsync(reason), _stoppingToken);
    }

    private async Task RunScanAsync(string reason)
    {
        if (!await _scanLock.WaitAsync(0).ConfigureAwait(false))
        {
            // A scan is already in flight; it will observe the pending request below.
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _scanRequested, 0);
                if (_stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                ExecuteScan(reason);
            }
            while (Interlocked.CompareExchange(ref _scanRequested, 0, 1) == 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error during device scan.");
        }
        finally
        {
            _scanLock.Release();
        }
    }

    private void ExecuteScan(string reason)
    {
        var rules = _rules;
        if (rules.Count == 0)
        {
            return;
        }

        var cooldown = TimeSpan.FromSeconds(_config.PerDeviceCooldownSeconds);
        var now = DateTimeOffset.UtcNow;

        _logger.LogDebug("Scan starting (trigger={Reason}).", reason);

        var outcomes = _deviceManager.ScanAndEnable(rules, device => EnableGate(device, cooldown, now));

        foreach (var outcome in outcomes)
        {
            var device = outcome.Device;
            var key = string.IsNullOrEmpty(device.InstanceId) ? device.DisplayName : device.InstanceId;

            // Transition-only logging: only log when the disabled-state flips or an action occurred.
            var previouslyDisabled = _lastKnownDisabled.TryGetValue(key, out var prev) && prev;
            _lastKnownDisabled[key] = device.IsDisabled;

            if (outcome.EnableAttempted)
            {
                _lastEnableAttempt[key] = now;
                if (outcome.EnableSucceeded)
                {
                    _logger.LogInformation("Enabled disabled device '{Device}' (instance {Instance}).", device.DisplayName, device.InstanceId);
                }
                else
                {
                    _logger.LogWarning("Failed to enable device '{Device}' (instance {Instance}): {Error}", device.DisplayName, device.InstanceId, outcome.EnableError);
                }
            }
            else if (device.IsDisabled && !previouslyDisabled)
            {
                // Newly observed as disabled but gated by cooldown; note the transition once.
                _logger.LogInformation("Device '{Device}' (instance {Instance}) is disabled; enable deferred by cooldown.", device.DisplayName, device.InstanceId);
            }
            else if (!device.IsDisabled && previouslyDisabled)
            {
                _logger.LogInformation("Device '{Device}' (instance {Instance}) is now enabled.", device.DisplayName, device.InstanceId);
            }
        }

        _logger.LogDebug("Scan finished (trigger={Reason}, matches={Count}).", reason, outcomes.Count);
    }

    private bool EnableGate(DeviceInfo device, TimeSpan cooldown, DateTimeOffset now)
    {
        if (cooldown <= TimeSpan.Zero)
        {
            return true;
        }

        var key = string.IsNullOrEmpty(device.InstanceId) ? device.DisplayName : device.InstanceId;
        if (_lastEnableAttempt.TryGetValue(key, out var last) && now - last < cooldown)
        {
            return false;
        }

        return true;
    }

    public override void Dispose()
    {
        _debounceTimer?.Dispose();
        _scanLock.Dispose();
        base.Dispose();
    }
}
