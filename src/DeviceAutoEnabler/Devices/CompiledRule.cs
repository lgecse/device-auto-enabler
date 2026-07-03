using System.Text.RegularExpressions;
using DeviceAutoEnabler.Config;

namespace DeviceAutoEnabler.Devices;

/// <summary>
/// A <see cref="DeviceRule"/> pre-processed for fast, safe matching. Regex rules carry a
/// compiled <see cref="Regex"/> with a match timeout (ReDoS guard).
/// </summary>
public sealed class CompiledRule
{
    private readonly Regex? _regex;

    public string Match { get; }

    public MatchOn MatchOn { get; }

    public MatchMode Mode { get; }

    /// <summary>Null means "search all classes"; otherwise enumeration is scoped to this class.</summary>
    public Guid? ClassGuid { get; }

    private CompiledRule(string match, MatchOn matchOn, MatchMode mode, Guid? classGuid, Regex? regex)
    {
        Match = match;
        MatchOn = matchOn;
        Mode = mode;
        ClassGuid = classGuid;
        _regex = regex;
    }

    public static CompiledRule Create(DeviceRule rule, TimeSpan regexTimeout)
    {
        Guid? classGuid = null;
        if (ConfigValidator.TryParseGuid(rule.ClassGuid, out var parsed))
        {
            classGuid = parsed;
        }

        Regex? regex = null;
        if (rule.Mode == MatchMode.Regex)
        {
            regex = new Regex(
                rule.Match,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                regexTimeout);
        }

        return new CompiledRule(rule.Match, rule.MatchOn, rule.Mode, classGuid, regex);
    }

    /// <summary>True if the given device matches this rule.</summary>
    public bool Matches(DeviceInfo device)
    {
        return MatchOn switch
        {
            MatchOn.FriendlyName => MatchesValue(device.FriendlyName),
            MatchOn.DeviceDesc => MatchesValue(device.DeviceDescription),
            MatchOn.HardwareId => device.HardwareIds.Any(MatchesValue),
            _ => false,
        };
    }

    private bool MatchesValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        switch (Mode)
        {
            case MatchMode.Exact:
                return string.Equals(value, Match, StringComparison.OrdinalIgnoreCase);
            case MatchMode.Regex:
                try
                {
                    return _regex is not null && _regex.IsMatch(value);
                }
                catch (RegexMatchTimeoutException)
                {
                    // Treat a timed-out (adversarial) pattern as a non-match rather than throwing.
                    return false;
                }
            case MatchMode.Contains:
            default:
                return value.Contains(Match, StringComparison.OrdinalIgnoreCase);
        }
    }
}
