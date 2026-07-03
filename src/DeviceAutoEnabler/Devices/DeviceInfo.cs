namespace DeviceAutoEnabler.Devices;

/// <summary>
/// A snapshot of a single enumerated device node.
/// </summary>
public sealed class DeviceInfo
{
    public string? FriendlyName { get; init; }

    public string? DeviceDescription { get; init; }

    /// <summary>Multi-valued hardware IDs joined for matching/logging.</summary>
    public IReadOnlyList<string> HardwareIds { get; init; } = Array.Empty<string>();

    /// <summary>Stable instance id used as a per-device cooldown key and for logging.</summary>
    public string InstanceId { get; init; } = string.Empty;

    /// <summary>True when the device node's problem code is CM_PROB_DISABLED (22).</summary>
    public bool IsDisabled { get; init; }

    /// <summary>Best available human-readable label for logging (never a secret).</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName!
        : !string.IsNullOrWhiteSpace(DeviceDescription) ? DeviceDescription!
        : !string.IsNullOrWhiteSpace(InstanceId) ? InstanceId
        : "(unknown device)";
}
