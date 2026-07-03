using System.Runtime.Versioning;
using DeviceAutoEnabler;
using DeviceAutoEnabler.Config;
using DeviceAutoEnabler.Devices;
using DeviceAutoEnabler.Events;
using DeviceAutoEnabler.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("Device Auto Enabler only runs on Windows.");
    return 2;
}

return RunWindows(args);

[SupportedOSPlatform("windows")]
static int RunWindows(string[] args)
{
    var verb = args.Length > 0 ? args[0].Trim().ToLowerInvariant() : "run";

    switch (verb)
    {
        case "install":
            return ServiceControl.Install();
        case "uninstall":
        case "remove":
            return ServiceControl.Uninstall();
        case "scan":
            return RunOneShotScan();
        case "run":
        case "":
            RunService(args);
            return 0;
        case "-h":
        case "--help":
        case "help":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"Unknown command '{verb}'.");
            PrintUsage();
            return 1;
    }
}

[SupportedOSPlatform("windows")]
static void RunService(string[] args)
{
    var configLoader = CreateConfigLoader();

    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddWindowsService(options => options.ServiceName = AppPaths.ServiceName);

    ConfigureLogging(builder.Logging, configLoader.Current);

    builder.Services.AddSingleton(configLoader);
    builder.Services.AddSingleton<DeviceManager>();
    builder.Services.AddSingleton<DeviceChangeWatcher>();
    builder.Services.AddHostedService<Worker>();

    var host = builder.Build();
    host.Run();
}

[SupportedOSPlatform("windows")]
static int RunOneShotScan()
{
    using var loggerFactory = LoggerFactory.Create(b =>
    {
        b.SetMinimumLevel(LogLevel.Debug);
        b.AddSimpleConsole(o => o.SingleLine = true);
    });

    var configLoader = new ConfigLoader(AppPaths.ConfigPath, loggerFactory.CreateLogger<ConfigLoader>());
    var config = configLoader.Current;
    var deviceManager = new DeviceManager(loggerFactory.CreateLogger<DeviceManager>());

    var timeout = TimeSpan.FromMilliseconds(config.RegexMatchTimeoutMs);
    var rules = new List<CompiledRule>();
    foreach (var rule in config.Devices)
    {
        rules.Add(CompiledRule.Create(rule, timeout));
    }

    if (rules.Count == 0)
    {
        Console.WriteLine("No device rules configured; nothing to scan.");
        return 0;
    }

    // One-shot scan enables immediately (no cooldown gating).
    var outcomes = deviceManager.ScanAndEnable(rules, _ => true);
    if (outcomes.Count == 0)
    {
        Console.WriteLine("No matching devices found.");
        return 0;
    }

    foreach (var outcome in outcomes)
    {
        var d = outcome.Device;
        var state = d.IsDisabled ? "disabled" : "enabled";
        if (outcome.EnableAttempted)
        {
            Console.WriteLine(outcome.EnableSucceeded
                ? $"ENABLED: {d.DisplayName} ({d.InstanceId})"
                : $"FAILED : {d.DisplayName} ({d.InstanceId}) - {outcome.EnableError}");
        }
        else
        {
            Console.WriteLine($"MATCH  : {d.DisplayName} ({d.InstanceId}) [{state}]");
        }
    }

    return 0;
}

[SupportedOSPlatform("windows")]
static ConfigLoader CreateConfigLoader()
{
    // Bootstrap logger for config load before the host's logging pipeline exists.
    using var bootstrapFactory = LoggerFactory.Create(b => b.AddSimpleConsole());
    return new ConfigLoader(AppPaths.ConfigPath, bootstrapFactory.CreateLogger<ConfigLoader>());
}

[SupportedOSPlatform("windows")]
static void ConfigureLogging(ILoggingBuilder logging, AppConfig config)
{
    logging.ClearProviders();

    var minLevel = ParseLevel(config.LogLevel);
    logging.SetMinimumLevel(minLevel);

    // Windows Event Log (only active when actually running as a service host).
    if (WindowsServiceHelpers.IsWindowsService())
    {
        logging.AddEventLog(settings =>
        {
            settings.SourceName = AppPaths.EventLogSource;
            settings.LogName = "Application";
        });
    }
    else
    {
        logging.AddSimpleConsole(o => o.SingleLine = true);
    }

    // Size-capped rolling file log in ProgramData.
    logging.AddProvider(new RollingFileLoggerProvider(new RollingFileLoggerOptions
    {
        FilePath = AppPaths.LogFilePath,
        MaxFileBytes = config.LogMaxFileBytes,
        RetainedFileCount = config.LogRetainedFileCount,
        MinLevel = minLevel,
    }));
}

static LogLevel ParseLevel(string level) =>
    Enum.TryParse<LogLevel>(level, ignoreCase: true, out var parsed) ? parsed : LogLevel.Information;

static void PrintUsage()
{
    Console.WriteLine("Device Auto Enabler");
    Console.WriteLine("Usage: DeviceAutoEnabler.exe [command]");
    Console.WriteLine();
    Console.WriteLine("Commands:");
    Console.WriteLine("  run         Run the service (default; used by the Service Control Manager).");
    Console.WriteLine("  install     Register and start the Windows service (requires Administrator).");
    Console.WriteLine("  uninstall   Stop and remove the Windows service (requires Administrator).");
    Console.WriteLine("  scan        Perform a single scan-and-enable pass and print results.");
    Console.WriteLine("  help        Show this help.");
}
