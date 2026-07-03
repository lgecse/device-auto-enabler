using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using static DeviceAutoEnabler.Devices.NativeMethods;

namespace DeviceAutoEnabler.Devices;

/// <summary>
/// Outcome of a scan-and-enable pass for a single matched device.
/// </summary>
public sealed class DeviceScanOutcome
{
    public required DeviceInfo Device { get; init; }

    public bool EnableAttempted { get; init; }

    public bool EnableSucceeded { get; init; }

    public string? EnableError { get; init; }
}

/// <summary>
/// Enumerates devices via SetupAPI, detects the CM_PROB_DISABLED problem code, and enables
/// matching disabled devices via DIF_PROPERTYCHANGE / DICS_ENABLE. All native handles are
/// SafeHandle-wrapped so nothing leaks across long uptime.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DeviceManager
{
    private readonly ILogger<DeviceManager> _logger;

    public DeviceManager(ILogger<DeviceManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Scan for devices matching any of <paramref name="rules"/> and enable those found disabled.
    /// Enumeration is grouped by class GUID so each scoped class is read at most once per pass.
    /// </summary>
    /// <param name="rules">Compiled matching rules.</param>
    /// <param name="enableGate">
    /// Called for a disabled, matching device; return true to attempt the enable (used for per-device cooldown).
    /// </param>
    public IReadOnlyList<DeviceScanOutcome> ScanAndEnable(
        IReadOnlyList<CompiledRule> rules,
        Func<DeviceInfo, bool> enableGate)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(enableGate);

        var outcomes = new List<DeviceScanOutcome>();
        if (rules.Count == 0)
        {
            return outcomes;
        }

        // Group by scope: each distinct class GUID (and the "all classes" bucket) is enumerated once.
        var groups = rules
            .GroupBy(r => r.ClassGuid)
            .ToList();

        foreach (var group in groups)
        {
            var groupRules = group.ToList();
            ScanGroup(group.Key, groupRules, enableGate, outcomes);
        }

        return outcomes;
    }

    private void ScanGroup(
        Guid? classGuid,
        IReadOnlyList<CompiledRule> rules,
        Func<DeviceInfo, bool> enableGate,
        List<DeviceScanOutcome> outcomes)
    {
        using var handle = OpenDeviceInfoSet(classGuid, out var errorCode);
        if (handle is null || handle.IsInvalid)
        {
            _logger.LogWarning(
                "SetupDiGetClassDevs failed for scope {Scope} (Win32 error {Error}); skipping this group.",
                classGuid?.ToString() ?? "ALLCLASSES",
                errorCode);
            return;
        }

        var setHandle = handle.DangerousGetHandle();
        uint index = 0;
        while (true)
        {
            var devInfo = new SP_DEVINFO_DATA();
            devInfo.cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>();

            if (!SetupDiEnumDeviceInfo(setHandle, index, ref devInfo))
            {
                // ERROR_NO_MORE_ITEMS (259) marks the end of enumeration.
                break;
            }

            index++;

            var device = ReadDeviceInfo(setHandle, ref devInfo);
            if (device is null)
            {
                continue;
            }

            if (!rules.Any(r => r.Matches(device)))
            {
                continue;
            }

            if (!device.IsDisabled)
            {
                // Report matched-but-enabled so the caller can track state transitions.
                outcomes.Add(new DeviceScanOutcome { Device = device });
                continue;
            }

            if (!enableGate(device))
            {
                outcomes.Add(new DeviceScanOutcome { Device = device });
                continue;
            }

            var (ok, err) = TryEnable(setHandle, ref devInfo);

            // The 'device' snapshot was read before the enable attempt, so its IsDisabled is stale
            // (always true here). Re-read the node so the outcome — and the caller's transition
            // tracking/logging — reflects the device's actual post-enable state.
            var refreshed = ReadDeviceInfo(setHandle, ref devInfo) ?? device;

            outcomes.Add(new DeviceScanOutcome
            {
                Device = refreshed,
                EnableAttempted = true,
                EnableSucceeded = ok,
                EnableError = err,
            });
        }
    }

    private static SafeDeviceInfoSetHandle? OpenDeviceInfoSet(Guid? classGuid, out int errorCode)
    {
        errorCode = 0;
        IntPtr raw;
        if (classGuid is Guid g)
        {
            var guid = g;
            raw = SetupDiGetClassDevsByClass(ref guid, null, IntPtr.Zero, DIGCF_PRESENT);
        }
        else
        {
            raw = SetupDiGetClassDevsAll(IntPtr.Zero, null, IntPtr.Zero, DIGCF_PRESENT | DIGCF_ALLCLASSES);
        }

        if (raw == IntPtr.Zero || raw == INVALID_HANDLE_VALUE)
        {
            errorCode = Marshal.GetLastWin32Error();
            return null;
        }

        return new SafeDeviceInfoSetHandle(raw);
    }

    private DeviceInfo? ReadDeviceInfo(IntPtr setHandle, ref SP_DEVINFO_DATA devInfo)
    {
        try
        {
            var friendlyName = ReadStringProperty(setHandle, ref devInfo, SPDRP_FRIENDLYNAME);
            var deviceDesc = ReadStringProperty(setHandle, ref devInfo, SPDRP_DEVICEDESC);
            var hardwareIds = ReadMultiStringProperty(setHandle, ref devInfo, SPDRP_HARDWAREID);
            var instanceId = ReadInstanceId(setHandle, ref devInfo);
            var disabled = IsDisabled(devInfo.DevInst);

            return new DeviceInfo
            {
                FriendlyName = friendlyName,
                DeviceDescription = deviceDesc,
                HardwareIds = hardwareIds,
                InstanceId = instanceId,
                IsDisabled = disabled,
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read a device node; skipping it.");
            return null;
        }
    }

    private static string? ReadStringProperty(IntPtr setHandle, ref SP_DEVINFO_DATA devInfo, uint property)
    {
        var bytes = ReadRawProperty(setHandle, ref devInfo, property);
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        return BytesToString(bytes).Trim('\0');
    }

    private static IReadOnlyList<string> ReadMultiStringProperty(IntPtr setHandle, ref SP_DEVINFO_DATA devInfo, uint property)
    {
        var bytes = ReadRawProperty(setHandle, ref devInfo, property);
        if (bytes is null || bytes.Length == 0)
        {
            return Array.Empty<string>();
        }

        var full = BytesToString(bytes);
        return full
            .Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .ToArray();
    }

    private static byte[]? ReadRawProperty(IntPtr setHandle, ref SP_DEVINFO_DATA devInfo, uint property)
    {
        // First call learns the required buffer size.
        SetupDiGetDeviceRegistryProperty(setHandle, ref devInfo, property, out _, null, 0, out var required);
        if (required == 0)
        {
            return null;
        }

        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(setHandle, ref devInfo, property, out _, buffer, required, out _))
        {
            return null;
        }

        return buffer;
    }

    private static string BytesToString(byte[] bytes)
    {
        // SetupAPI unicode buffers are UTF-16LE.
        return System.Text.Encoding.Unicode.GetString(bytes);
    }

    private static string ReadInstanceId(IntPtr setHandle, ref SP_DEVINFO_DATA devInfo)
    {
        SetupDiGetDeviceInstanceId(setHandle, ref devInfo, null, 0, out var required);
        if (required == 0)
        {
            return string.Empty;
        }

        var buffer = new char[required];
        if (!SetupDiGetDeviceInstanceId(setHandle, ref devInfo, buffer, required, out _))
        {
            return string.Empty;
        }

        return new string(buffer).Trim('\0');
    }

    private static bool IsDisabled(uint devInst)
    {
        var cr = CM_Get_DevNode_Status(out var status, out var problem, devInst, 0);
        if (cr != CR_SUCCESS)
        {
            return false;
        }

        return (status & DN_HAS_PROBLEM) != 0 && problem == CM_PROB_DISABLED;
    }

    private (bool ok, string? error) TryEnable(IntPtr setHandle, ref SP_DEVINFO_DATA devInfo)
    {
        // A device may be disabled globally (Device Manager) or config-specific for the current
        // hardware profile (e.g. NVIDIA Control Panel "disconnect"). Enabling only one scope leaves
        // the other disable in place, so we apply the enable to both scopes — matching devcon.
        // The global pass is best-effort; the config-specific pass is the one whose failure counts.
        ApplyStateChange(setHandle, ref devInfo, DICS_FLAG_GLOBAL);

        var (configOk, configError) = ApplyStateChange(setHandle, ref devInfo, DICS_FLAG_CONFIGSPECIFIC);
        if (!configOk)
        {
            return (false, configError);
        }

        // SetupDiCallClassInstaller returning success only means the request was accepted; it does
        // not guarantee the node left the disabled state. Re-read the devnode status so we report
        // (and log) the real outcome instead of a misleading success.
        if (IsDisabled(devInfo.DevInst))
        {
            return (false, "Enable call succeeded but the device is still reporting CM_PROB_DISABLED (a reboot may be required, or another component is re-disabling it).");
        }

        return (true, null);
    }

    private static (bool ok, string? error) ApplyStateChange(IntPtr setHandle, ref SP_DEVINFO_DATA devInfo, uint scope)
    {
        var propChange = new SP_PROPCHANGE_PARAMS
        {
            ClassInstallHeader = new SP_CLASSINSTALL_HEADER
            {
                cbSize = (uint)Marshal.SizeOf<SP_CLASSINSTALL_HEADER>(),
                InstallFunction = DIF_PROPERTYCHANGE,
            },
            StateChange = DICS_ENABLE,
            Scope = scope,
            HwProfile = 0,
        };

        var paramsSize = (uint)Marshal.SizeOf<SP_PROPCHANGE_PARAMS>();
        if (!SetupDiSetClassInstallParams(setHandle, ref devInfo, ref propChange, paramsSize))
        {
            return (false, $"SetupDiSetClassInstallParams (scope 0x{scope:X}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        if (!SetupDiCallClassInstaller(DIF_PROPERTYCHANGE, setHandle, ref devInfo))
        {
            return (false, $"SetupDiCallClassInstaller (scope 0x{scope:X}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        return (true, null);
    }
}
