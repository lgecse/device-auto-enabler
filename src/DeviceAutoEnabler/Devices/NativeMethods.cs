using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace DeviceAutoEnabler.Devices;

/// <summary>
/// P/Invoke declarations for SetupAPI + CfgMgr32 used to enumerate, inspect and enable devices.
/// Struct layouts are x64-aware: pointer-sized native fields use <see cref="IntPtr"/> / <see cref="UIntPtr"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class NativeMethods
{
    // --- SetupDiGetClassDevs flags ---
    internal const uint DIGCF_PRESENT = 0x00000002;
    internal const uint DIGCF_ALLCLASSES = 0x00000004;

    // --- SPDRP registry property ids ---
    internal const uint SPDRP_DEVICEDESC = 0x00000000;
    internal const uint SPDRP_HARDWAREID = 0x00000001;
    internal const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    // --- Device install function + state-change constants ---
    internal const uint DIF_PROPERTYCHANGE = 0x00000012;
    internal const uint DICS_ENABLE = 0x00000001;

    // Scope flags. A device can be disabled globally (all hardware profiles) or only for the
    // current profile (config-specific). Tools like the NVIDIA Control Panel apply a
    // config-specific disable, which a global-only enable will NOT clear — so we must enable both.
    internal const uint DICS_FLAG_GLOBAL = 0x00000001;
    internal const uint DICS_FLAG_CONFIGSPECIFIC = 0x00000002;

    // --- Config Manager status / problem codes ---
    internal const uint CR_SUCCESS = 0x00000000;
    internal const uint DN_HAS_PROBLEM = 0x00000400;
    internal const uint CM_PROB_DISABLED = 22;

    internal static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_CLASSINSTALL_HEADER
    {
        public uint cbSize;
        public uint InstallFunction;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SP_PROPCHANGE_PARAMS
    {
        public SP_CLASSINSTALL_HEADER ClassInstallHeader;
        public uint StateChange;
        public uint Scope;
        public uint HwProfile;
    }

    /// <summary>Enumerate a single setup class (classGuid passed by ref).</summary>
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    internal static extern IntPtr SetupDiGetClassDevsByClass(
        ref Guid classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string? enumerator,
        IntPtr hwndParent,
        uint flags);

    /// <summary>Enumerate all classes (classGuid pointer is null).</summary>
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    internal static extern IntPtr SetupDiGetClassDevsAll(
        IntPtr classGuid,
        [MarshalAs(UnmanagedType.LPWStr)] string? enumerator,
        IntPtr hwndParent,
        uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiEnumDeviceInfo(
        IntPtr deviceInfoSet,
        uint memberIndex,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceRegistryProperty(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        uint property,
        out uint propertyRegDataType,
        byte[]? propertyBuffer,
        uint propertyBufferSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiGetDeviceInstanceId(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        char[]? deviceInstanceId,
        uint deviceInstanceIdSize,
        out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiSetClassInstallParams(
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData,
        ref SP_PROPCHANGE_PARAMS classInstallParams,
        uint classInstallParamsSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiCallClassInstaller(
        uint installFunction,
        IntPtr deviceInfoSet,
        ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("cfgmgr32.dll", SetLastError = true)]
    internal static extern uint CM_Get_DevNode_Status(
        out uint pulStatus,
        out uint pulProblemNumber,
        uint dnDevInst,
        uint ulFlags);

    /// <summary>
    /// SafeHandle wrapper around an HDEVINFO device information set. Released with
    /// <see cref="SetupDiDestroyDeviceInfoList"/> so the handle is never leaked.
    /// </summary>
    [SupportedOSPlatform("windows")]
    internal sealed class SafeDeviceInfoSetHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeDeviceInfoSetHandle()
            : base(true)
        {
        }

        public SafeDeviceInfoSetHandle(IntPtr handle)
            : base(true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle()
        {
            return SetupDiDestroyDeviceInfoList(handle);
        }
    }
}
