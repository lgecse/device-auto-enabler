using System.Text.RegularExpressions;

namespace DeviceAutoEnabler.Config;

/// <summary>
/// Schema/range validation for <see cref="AppConfig"/>. Keeps out-of-range or malformed
/// values (e.g. broken regex patterns, absurd intervals) from ever taking effect.
/// </summary>
public static class ConfigValidator
{
    private static readonly string[] AllowedLogLevels =
    {
        "Trace", "Debug", "Information", "Warning", "Error", "Critical", "None",
    };

    public static bool Validate(AppConfig config, out string? error)
    {
        error = null;

        if (config.EventDebounceMs is < 0 or > 60_000)
        {
            error = $"eventDebounceMs must be between 0 and 60000 (was {config.EventDebounceMs}).";
            return false;
        }

        if (config.PerDeviceCooldownSeconds is < 0 or > 86_400)
        {
            error = $"perDeviceCooldownSeconds must be between 0 and 86400 (was {config.PerDeviceCooldownSeconds}).";
            return false;
        }

        if (config.RegexMatchTimeoutMs is < 10 or > 10_000)
        {
            error = $"regexMatchTimeoutMs must be between 10 and 10000 (was {config.RegexMatchTimeoutMs}).";
            return false;
        }

        if (config.LogMaxFileBytes is < 4_096 or > 1_073_741_824)
        {
            error = $"logMaxFileBytes must be between 4096 and 1073741824 (was {config.LogMaxFileBytes}).";
            return false;
        }

        if (config.LogRetainedFileCount is < 0 or > 1_000)
        {
            error = $"logRetainedFileCount must be between 0 and 1000 (was {config.LogRetainedFileCount}).";
            return false;
        }

        if (!AllowedLogLevels.Contains(config.LogLevel, StringComparer.OrdinalIgnoreCase))
        {
            error = $"logLevel '{config.LogLevel}' is not one of: {string.Join(", ", AllowedLogLevels)}.";
            return false;
        }

        if (config.Devices is null)
        {
            error = "devices must be an array (was null).";
            return false;
        }

        var timeout = TimeSpan.FromMilliseconds(config.RegexMatchTimeoutMs);
        for (var i = 0; i < config.Devices.Count; i++)
        {
            var rule = config.Devices[i];
            if (string.IsNullOrWhiteSpace(rule.Match))
            {
                error = $"devices[{i}].match must be a non-empty string.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(rule.ClassGuid) && !TryParseGuid(rule.ClassGuid, out _))
            {
                error = $"devices[{i}].classGuid '{rule.ClassGuid}' is not a valid GUID.";
                return false;
            }

            if (rule.Mode == MatchMode.Regex)
            {
                // Compile once here to reject a broken (or catastrophically slow) pattern up front.
                try
                {
                    _ = new Regex(rule.Match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, timeout);
                }
                catch (ArgumentException aex)
                {
                    error = $"devices[{i}].match is an invalid regex: {aex.Message}";
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>Parses a GUID accepting both braced and unbraced forms.</summary>
    public static bool TryParseGuid(string? value, out Guid guid)
    {
        guid = Guid.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Guid.TryParse(value.Trim().Trim('{', '}'), out guid);
    }
}
