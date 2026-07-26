using Remotely.Desktop.Shared.Abstractions;
using Remotely.Desktop.Shared.Enums;
using Remotely.Desktop.Shared.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Remotely.Shared.Primitives;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Remotely.Desktop.Native.Windows;

namespace Remotely.Desktop.Shared.Startup;

public static class IServiceProviderExtensions
{
    /// <summary>
    /// Runs the remote control startup with the specified arguments.
    /// </summary>
    public static async Task<Result> UseRemoteControlClient(
        this IServiceProvider services,
        string host,
        AppMode mode,
        string pipeName,
        string sessionId,
        string accessKey,
        string requesterName,
        string organizationName,
        bool relaunch,
        string viewers,
        bool elevate)
    {
        try
        {
            var logger = services.GetRequiredService<ILogger<IServiceProvider>>();
            TaskScheduler.UnobservedTaskException += (object? sender, UnobservedTaskExceptionEventArgs e) =>
            {
                HandleUnobservedTask(e, logger);
            };

            if (OperatingSystem.IsWindows() && elevate)
            {
                if (RelaunchElevated(logger))
                {
                    return Result.Ok();
                }

                // The SYSTEM hop failed; fall through and run the client normally. This
                // process is already high-integrity, so the operator still gets a correctly
                // elevated (if not SYSTEM-capable) session instead of a hard failure.
            }

            var appState = services.GetRequiredService<IAppState>();
            appState.Configure(
                host ?? string.Empty,
                mode,
                sessionId ?? string.Empty,
                accessKey ?? string.Empty,
                requesterName ?? string.Empty,
                organizationName ?? string.Empty,
                pipeName ?? string.Empty,
                relaunch,
                viewers ?? string.Empty,
                elevate);

            StaticServiceProvider.Instance = services;

            var appStartup = services.GetRequiredService<IAppStartup>();
            await appStartup.Run();
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ex);
        }
       
    }

    /// <summary>
    /// Runs the remote control startup as a root command.  This uses the System.CommandLine package.
    /// </summary>
    /// <param name="services">The service provider fo rthe app using this library.</param>
    /// <param name="args">The original command line arguments passed into the app.</param>
    /// <param name="commandLineDescription">The description to use for the remote control command.</param>
    /// <param name="serverUri">If provided, will be used as a fallback if --host option is missing.</param>
    /// <returns></returns>
    public static async Task<Result> UseRemoteControlClient(
        this IServiceProvider services,
        string[] args,
        string commandLineDescription,
        string serverUri = "",
        bool treatUnmatchedArgsAsErrors = true)
    {
        try
        {
            var rootCommand = CommandProvider.CreateRemoteControlCommand(true, commandLineDescription);

            rootCommand.Handler = CommandHandler.Create(async (
                string host,
                AppMode mode,
                string pipeName,
                string sessionId,
                string accessKey,
                string requesterName,
                string organizationName,
                bool relaunch,
                string viewers,
                bool elevate) =>
            {
                if (string.IsNullOrWhiteSpace(host) && !string.IsNullOrWhiteSpace(serverUri))
                {
                    host = serverUri;
                }

                return await services.UseRemoteControlClient(
                    host,
                    mode,
                    pipeName,
                    sessionId,
                    accessKey,
                    requesterName,
                    organizationName,
                    relaunch,
                    viewers,
                    elevate);
            });

            rootCommand.TreatUnmatchedTokensAsErrors = treatUnmatchedArgsAsErrors;

            var result = await rootCommand.InvokeAsync(args);

            if (result == 0)
            {
                return Result.Ok();
            }
            return Result.Fail($"Remote control command returned code {result}.");
        }
        catch (Exception ex)
        {
            return Result.Fail(ex);
        }
    }

    // This shouldn't be required in modern .NET to prevent the app from crashing,
    // but it could be useful to log it.
    private static void HandleUnobservedTask(
        UnobservedTaskExceptionEventArgs e, 
        ILogger<IServiceProvider> logger)
    {
        e.SetObserved();
        logger.LogError(e.Exception, "An unobserved task exception occurred.");
    }

    /// <summary>
    /// Stage B of the elevation chain: running as a high-integrity admin, steal winlogon's
    /// SYSTEM token and relaunch as stage C. Returns false (rather than exiting) if the SYSTEM
    /// hop fails, so the caller can fall back to running this already-elevated process directly.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool RelaunchElevated(ILogger logger)
    {
        if (!Win32Interop.EnablePrivilege("SeDebugPrivilege"))
        {
            logger.LogWarning("Failed to enable SeDebugPrivilege; SYSTEM relaunch may fail to open winlogon.");
        }

        var commandLine = Win32Interop.GetCommandLine().Replace(" --elevate", "");
        var sessionId = Process.GetCurrentProcess().SessionId;

        logger.LogInformation("Attempting SYSTEM relaunch in session {sessionId}: {commandLine}", sessionId, commandLine);

        if (!Win32Interop.CreateInteractiveSystemProcess(commandLine, sessionId, false, out var procInfo))
        {
            var win32Error = Marshal.GetLastWin32Error();
            logger.LogWarning(
                "SYSTEM relaunch failed. Win32 error {win32Error}: {message}",
                win32Error,
                new System.ComponentModel.Win32Exception(win32Error).Message);
            return false;
        }

        logger.LogInformation("SYSTEM relaunch succeeded. Process ID: {processId}.", procInfo.dwProcessId);
        return true;
    }
}
