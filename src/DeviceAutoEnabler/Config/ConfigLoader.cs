using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace DeviceAutoEnabler.Config;

/// <summary>
/// Loads and validates <see cref="AppConfig"/> from disk and hot-reloads it on change.
/// Invalid configs are rejected; the last-good config is retained so a bad edit never
/// crash-loops the service.
/// </summary>
public sealed class ConfigLoader : IDisposable
{
    /// <summary>
    /// Fixed, explicit deserialization options. Reflection-based binding of a closed POCO schema:
    /// no polymorphic type resolution and no TypeNameHandling-style type embedding.
    /// </summary>
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly string _configPath;
    private readonly ILogger<ConfigLoader> _logger;
    private readonly object _sync = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private volatile AppConfig _current;
    private bool _disposed;

    /// <summary>Raised (after debounce) when a new, valid config has been loaded.</summary>
    public event Action<AppConfig>? ConfigChanged;

    public ConfigLoader(string configPath, ILogger<ConfigLoader> logger)
    {
        _configPath = configPath ?? throw new ArgumentNullException(nameof(configPath));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _current = LoadOrDefault();
    }

    /// <summary>The current, validated configuration.</summary>
    public AppConfig Current => _current;

    /// <summary>
    /// Begin watching the config file for changes. Safe to call once after construction.
    /// </summary>
    public void StartWatching()
    {
        var dir = Path.GetDirectoryName(_configPath);
        var file = Path.GetFileName(_configPath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(file))
        {
            _logger.LogWarning("Config path {Path} has no directory/file component; hot-reload disabled.", _configPath);
            return;
        }

        try
        {
            Directory.CreateDirectory(dir);
            _watcher = new FileSystemWatcher(dir, file)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _logger.LogInformation("Watching configuration file for changes: {Path}", _configPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start configuration file watcher; hot-reload disabled.");
        }
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // Coalesce the burst of events editors produce into a single reload.
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _debounceTimer ??= new Timer(_ => ReloadFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
            _debounceTimer.Change(500, Timeout.Infinite);
        }
    }

    private void ReloadFromDisk()
    {
        AppConfig? loaded = TryLoad(out var error);
        if (loaded is null)
        {
            _logger.LogWarning("Configuration reload rejected; keeping last-good config. Reason: {Reason}", error);
            return;
        }

        _current = loaded;
        _logger.LogInformation("Configuration reloaded successfully ({RuleCount} device rule(s)).", loaded.Devices.Count);
        ConfigChanged?.Invoke(loaded);
    }

    private AppConfig LoadOrDefault()
    {
        var loaded = TryLoad(out var error);
        if (loaded is not null)
        {
            _logger.LogInformation("Loaded configuration from {Path} ({RuleCount} device rule(s)).", _configPath, loaded.Devices.Count);
            return loaded;
        }

        var defaults = new AppConfig();

        // Seed a default config on first run so there is always a discoverable file to edit.
        // Only do this when the file is genuinely absent: never clobber an existing file that
        // merely failed to parse/validate, so a bad edit stays visible instead of being erased.
        if (!File.Exists(_configPath))
        {
            if (TryWriteDefaultConfig(defaults, out var writeError))
            {
                _logger.LogInformation("No configuration found; wrote a default config to {Path}.", _configPath);
            }
            else
            {
                _logger.LogWarning("No configuration found and could not create a default at {Path} ({Reason}); using built-in defaults.", _configPath, writeError);
            }
        }
        else
        {
            _logger.LogWarning("Could not load configuration from {Path}; using built-in defaults. Reason: {Reason}", _configPath, error);
        }

        return defaults;
    }

    /// <summary>
    /// Write a default configuration file, creating the parent directory if needed. Uses a
    /// temp-file-then-move so a concurrent reader never observes a half-written file.
    /// </summary>
    private bool TryWriteDefaultConfig(AppConfig config, out string? error)
    {
        error = null;
        try
        {
            var dir = Path.GetDirectoryName(_configPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(config, SerializerOptions);
            var tempPath = _configPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _configPath, overwrite: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Attempt to read and validate the config file. Returns null and sets <paramref name="error"/>
    /// on any failure (missing file, malformed JSON, or failing validation).
    /// </summary>
    private AppConfig? TryLoad(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(_configPath))
            {
                error = "file does not exist";
                return null;
            }

            // Read via a share-friendly stream so we don't fight an editor still holding the file.
            string json;
            using (var fs = new FileStream(_configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
            {
                json = reader.ReadToEnd();
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                error = "file is empty";
                return null;
            }

            AppConfig? config;
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            }
            catch (JsonException jex)
            {
                error = $"invalid JSON: {jex.Message}";
                return null;
            }

            if (config is null)
            {
                error = "config deserialized to null";
                return null;
            }

            if (!ConfigValidator.Validate(config, out var validationError))
            {
                error = validationError;
                return null;
            }

            return config;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
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
            if (_watcher is not null)
            {
                _watcher.Changed -= OnFileEvent;
                _watcher.Created -= OnFileEvent;
                _watcher.Renamed -= OnFileEvent;
                _watcher.Dispose();
                _watcher = null;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }
}
