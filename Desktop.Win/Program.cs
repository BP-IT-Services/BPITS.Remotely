using Avalonia;
using Remotely.Desktop.Native.Windows;
using Remotely.Desktop.Shared.Services;
using Remotely.Desktop.Shared.Startup;
using Remotely.Desktop.UI;
using Remotely.Desktop.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Remotely.Desktop.Win.Startup;
using Remotely.Shared.Services;
using Remotely.Shared.Utilities;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Remotely.Desktop.UI.Startup;

namespace Remotely.Desktop.Win;

public class Program
{
    // This is needed for the visual designer to work.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();


    [SupportedOSPlatform("windows")]
    public static async Task Main(string[] args)
    {
        // Hidden diagnostic switch: run the elevation logon ladder and report per-rung results
        // WITHOUT launching anything. Credentials are read from stdin, never the command line, so
        // they stay out of process lists and logs.
        if (args.Contains("--elevation-selftest"))
        {
            RunElevationSelfTest();
            return;
        }

        var version = AppVersionHelper.GetAppVersion();
        var logger = new FileLogger("Remotely_Desktop", version, "Program.cs");
        var filePath = Environment.ProcessPath ?? Environment.GetCommandLineArgs().First();
        var serverUrl = Debugger.IsAttached ? "https://localhost:5001" : string.Empty;
        var getEmbeddedResult =  EmbeddedServerDataProvider.Instance.TryGetEmbeddedData(filePath);
        if (getEmbeddedResult.IsSuccess)
        {
            serverUrl = getEmbeddedResult.Value.ServerUrl.AbsoluteUri;
        }
        else
        {
            logger.LogWarning(getEmbeddedResult.Exception, "Failed to extract embedded server data.");
        }
        var services = new ServiceCollection();

        services.AddSingleton<IEmbeddedServerDataProvider>(EmbeddedServerDataProvider.Instance);

        services.AddRemoteControlXplat();
        services.AddRemoteControlUi();
        services.AddRemoteControlWindows();

        services.AddLogging(builder =>
        {
            if (EnvironmentHelper.IsDebug)
            {
                builder.SetMinimumLevel(LogLevel.Debug);
            }
            builder.AddProvider(new FileLoggerProvider("Remotely_Desktop", version));
        });

        var provider = services.BuildServiceProvider();

        var appState = provider.GetRequiredService<IAppState>();

        if (getEmbeddedResult.IsSuccess)
        {
            appState.OrganizationId = getEmbeddedResult.Value.OrganizationId;
            appState.Host = getEmbeddedResult.Value.ServerUrl.AbsoluteUri;
        }

        if (appState.ArgDict.TryGetValue("org-id", out var orgId))
        {
            appState.OrganizationId = orgId;
        }

        var result = await provider.UseRemoteControlClient(
            args,
            "The remote control client for Remotely.",
            serverUrl,
            false);

        if (!result.IsSuccess)
        {
            logger.LogError(result.Exception, "Failed to start remote control client.");
            Environment.Exit(1);
        }

        var dispatcher = provider.GetRequiredService<IUiDispatcher>();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, dispatcher.ApplicationExitingToken);
        }
        catch (OperationCanceledException) { }

        // Output type is WinExe, so we need to explicitly exit.
        Environment.Exit(0);
    }

    [SupportedOSPlatform("windows")]
    private static void RunElevationSelfTest()
    {
        // Desktop.Win is a WinExe with no console of its own; attach to the launching terminal so
        // the operator sees the report and can pipe credentials in.
        Kernel32.AttachConsole(Kernel32.ATTACH_PARENT_PROCESS);

        Console.Error.WriteLine("Elevation self-test. Provide credentials on stdin, one per line: username, domain, password.");

        var username = Console.In.ReadLine() ?? string.Empty;
        var domain = Console.In.ReadLine() ?? string.Empty;
        var password = Console.In.ReadLine() ?? string.Empty;

        var report = Win32Interop.RunElevationSelfTest(username, domain, password);

        Console.Out.WriteLine(report);
        Console.Out.Flush();

        // Also persist the report to a file: a WinExe console attach fails when there is no parent
        // console (e.g. launched from Explorer), so a file guarantees the output is recoverable.
        try
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                $"Remotely_ElevationSelfTest_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            File.WriteAllText(path, report);
            Console.Error.WriteLine($"Report written to {path}");
        }
        catch { /* console output was already emitted */ }
    }
}