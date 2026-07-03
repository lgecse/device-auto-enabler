namespace DeviceAutoEnabler;

/// <summary>
/// Well-known filesystem locations for the service. Data lives under
/// <c>%ProgramData%\DeviceAutoEnabler\</c> which the installer locks down with ACLs.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "DeviceAutoEnabler";

    public const string ServiceName = "DeviceAutoEnabler";

    public const string EventLogSource = "DeviceAutoEnabler";

    public static string DataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            AppFolderName);

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");

    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    public static string LogFilePath => Path.Combine(LogDirectory, "device-auto-enabler.log");
}
