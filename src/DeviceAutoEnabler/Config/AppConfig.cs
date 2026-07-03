namespace DeviceAutoEnabler.Config;

/// <summary>
/// How a device name/id is matched against a configured rule.
/// </summary>
public enum MatchMode
{
    Contains = 0,
    Exact = 1,
    Regex = 2,
}

/// <summary>
/// Which device property the <see cref="DeviceRule.Match"/> value is compared against.
/// </summary>
public enum MatchOn
{
    FriendlyName = 0,
    DeviceDesc = 1,
    HardwareId = 2,
}

/// <summary>
/// A single device-matching rule from configuration.
/// </summary>
public sealed class DeviceRule
{
    /// <summary>The value to match (substring, exact string, or regex pattern).</summary>
    public string Match { get; init; } = string.Empty;

    /// <summary>Which property to match against. Defaults to the device friendly name.</summary>
    public MatchOn MatchOn { get; init; } = MatchOn.FriendlyName;

    /// <summary>Match strategy. Defaults to case-insensitive substring.</summary>
    public MatchMode Mode { get; init; } = MatchMode.Contains;

    /// <summary>
    /// Optional setup class GUID (with or without braces). When set, enumeration is scoped
    /// to that class only, keeping scans cheap. When null/empty, all classes are searched.
    /// </summary>
    public string? ClassGuid { get; init; }
}

/// <summary>
/// Strongly-typed, immutable application configuration. Deserialized from JSON with a fixed
/// schema (no polymorphic/type-name handling).
/// </summary>
public sealed class AppConfig
{
    public int EventDebounceMs { get; init; } = 2000;

    public int PerDeviceCooldownSeconds { get; init; } = 60;

    /// <summary>Regex match timeout (ReDoS guard) applied to <see cref="MatchMode.Regex"/> rules.</summary>
    public int RegexMatchTimeoutMs { get; init; } = 250;

    public string LogLevel { get; init; } = "Information";

    /// <summary>Maximum size of a single rolling log file before it rolls over.</summary>
    public long LogMaxFileBytes { get; init; } = 5 * 1024 * 1024;

    /// <summary>Number of rolled log files to retain (excluding the active file).</summary>
    public int LogRetainedFileCount { get; init; } = 5;

    public IReadOnlyList<DeviceRule> Devices { get; init; } = Array.Empty<DeviceRule>();
}
