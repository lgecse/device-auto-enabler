using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace DeviceAutoEnabler;

/// <summary>
/// Self-management verbs so the exe can register/remove its own Windows service. This keeps the
/// installer simple and makes manual testing easy (install | uninstall | run | scan).
/// </summary>
[SupportedOSPlatform("windows")]
public static class ServiceControl
{
    /// <summary>Register the service as LocalSystem with automatic start, then start it.</summary>
    public static int Install()
    {
        var exePath = GetExecutablePath();
        if (exePath is null)
        {
            Console.Error.WriteLine("Could not determine the executable path.");
            return 1;
        }

        // sc.exe stores this verbatim as the service ImagePath and launches it with the "run"
        // argument so the exe hosts the worker. The executable path is quoted so the Service
        // Control Manager can distinguish it from the trailing argument and because the default
        // install location (C:\Program Files\...) contains a space. Those inner quotes are
        // backslash-escaped so the whole value reaches sc.exe as a single argument; without the
        // escaping the doubled quotes make sc.exe misparse the path and silently fail to create
        // the service.
        var binPath = $"\\\"{exePath}\\\" run";

        var create = RunSc($"create {AppPaths.ServiceName} binPath= \"{binPath}\" start= auto obj= LocalSystem DisplayName= \"Device Auto Enabler\"");
        if (create != 0)
        {
            Console.Error.WriteLine("Service creation failed. Are you running as Administrator?");
            return create;
        }

        RunSc($"description {AppPaths.ServiceName} \"Keeps configured devices enabled by re-enabling them when found disabled.\"");
        // Restart automatically on failure (5s), reset counter daily.
        RunSc($"failure {AppPaths.ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/5000");

        var start = RunSc($"start {AppPaths.ServiceName}");
        if (start != 0)
        {
            Console.Error.WriteLine("Service created but failed to start; check the Event Log.");
            return start;
        }

        Console.WriteLine($"Service '{AppPaths.ServiceName}' installed and started.");
        return 0;
    }

    /// <summary>Stop and delete the service. Idempotent-ish: ignores "not started" on stop.</summary>
    public static int Uninstall()
    {
        RunSc($"stop {AppPaths.ServiceName}");
        var delete = RunSc($"delete {AppPaths.ServiceName}");
        if (delete != 0)
        {
            Console.Error.WriteLine("Service deletion failed. Are you running as Administrator?");
            return delete;
        }

        Console.WriteLine($"Service '{AppPaths.ServiceName}' removed.");
        return 0;
    }

    /// <summary>
    /// True when the current process is running with an elevated (Administrator) token. Enabling
    /// devices via SetupAPI requires this; the LocalSystem service account satisfies it implicitly.
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            // If we cannot determine the token, don't block execution; device calls will surface
            // their own access errors if elevation is actually missing.
            return true;
        }
    }

    private static string? GetExecutablePath()
    {
        // Single-file publish: the host process path is the exe.
        var path = Environment.ProcessPath;
        return string.IsNullOrEmpty(path) ? null : path;
    }

    private static int RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return 1;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                Console.WriteLine(stdout.Trim());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Console.Error.WriteLine(stderr.Trim());
            }

            return process.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to run sc.exe {arguments}: {ex.Message}");
            return 1;
        }
    }
}
