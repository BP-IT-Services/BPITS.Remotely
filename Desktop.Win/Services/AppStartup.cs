using Remotely.Desktop.Native.Windows;
using Remotely.Desktop.Shared.Abstractions;
using Remotely.Desktop.Shared.Enums;
using Remotely.Desktop.Shared.Services;
using Remotely.Desktop.UI.Services;
using Remotely.Shared.Helpers;
using Remotely.Shared.Models;
using Remotely.Shared.Services;

namespace Remotely.Desktop.Win.Services;

internal class AppStartup : IAppStartup
{
    private readonly IAppState _appState;
    private readonly IKeyboardMouseInput _inputService;
    private readonly IDesktopHubConnection _desktopHub;
    private readonly IClipboardService _clipboardService;
    private readonly IChatHostService _chatHostService;
    private readonly ICursorIconWatcher _cursorIconWatcher;
    private readonly IMessageLoop _messageLoop;
    private readonly IUiDispatcher _uiDispatcher;
    private readonly IIdleTimer _idleTimer;
    private readonly IShutdownService _shutdownService;
    private readonly IBrandingProvider _brandingProvider;
    private readonly IElevationDetector _elevationDetector;
    private readonly ILogger<AppStartup> _logger;

    public AppStartup(
        IAppState appState,
        IKeyboardMouseInput inputService,
        IDesktopHubConnection desktopHub,
        IClipboardService clipboardService,
        IChatHostService chatHostService,
        ICursorIconWatcher iconWatcher,
        IMessageLoop messageLoop,
        IUiDispatcher uiDispatcher,
        IIdleTimer idleTimer,
        IShutdownService shutdownService,
        IBrandingProvider brandingProvider,
        IElevationDetector elevationDetector,
        ILogger<AppStartup> logger)
    {
        _appState = appState;
        _inputService = inputService;
        _desktopHub = desktopHub;
        _clipboardService = clipboardService;
        _chatHostService = chatHostService;
        _cursorIconWatcher = iconWatcher;
        _messageLoop = messageLoop;
        _uiDispatcher = uiDispatcher;
        _idleTimer = idleTimer;
        _shutdownService = shutdownService;
        _brandingProvider = brandingProvider;
        _elevationDetector = elevationDetector;
        _logger = logger;
    }

    public async Task Run()
    {
        await _brandingProvider.Initialize();

        _messageLoop.StartMessageLoop();

        if (_appState.Mode is AppMode.Unattended or AppMode.Attended)
        {
            _clipboardService.BeginWatching();
            _inputService.Init();
            _cursorIconWatcher.OnChange += CursorIconWatcher_OnChange;
        }

        switch (_appState.Mode)
        {
            case AppMode.Unattended:
                {
                    var result = await _uiDispatcher.StartHeadless().ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return;
                    }
                    await StartScreenCasting().ConfigureAwait(false);
                    break;
                }
            case AppMode.Attended:
                {
                    _desktopHub.ElevationRequested += OnElevationRequested;
                    _uiDispatcher.StartClassicDesktop();
                    break;
                }
            case AppMode.Chat:
                {
                    var result = await _uiDispatcher.StartHeadless().ConfigureAwait(false);
                    if (!result.IsSuccess)
                    {
                        return;
                    }
                    await _chatHostService
                        .StartChat(_appState.PipeName, _appState.OrganizationName)
                        .ConfigureAwait(false);
                    break;
                }
            default:
                break;
        }
    }


    private async Task StartScreenCasting()
    {
        if (!await _desktopHub.Connect(TimeSpan.FromSeconds(30), _uiDispatcher.ApplicationExitingToken))
        {
            await _shutdownService.Shutdown();
            return;
        }

        var result = await _desktopHub.SendUnattendedSessionInfo(
                 _appState.SessionId,
                 _appState.AccessKey,
                 Environment.MachineName,
                 _appState.RequesterName,
                 _appState.OrganizationName);

        if (!result.IsSuccess)
        {
            _logger.LogError(result.Exception, "An error occurred while trying to establish a session with the server.");
            await _shutdownService.Shutdown();
            return;
        }

        try
        {
            if (Win32Interop.GetCurrentDesktop(out var currentDesktopName))
            {
                _logger.LogInformation("Setting initial desktop to {currentDesktopName}.", currentDesktopName);
            }
            else
            {
                _logger.LogWarning("Failed to get initial desktop name.");
            }

            if (!Win32Interop.SwitchToInputDesktop())
            {
                _logger.LogWarning("Failed to set initial desktop.");
            }

            if (_appState.IsRelaunch)
            {
                _logger.LogInformation("Resuming after relaunch.");
                var viewerIDs = _appState.RelaunchViewers;
                await _desktopHub.NotifyViewersRelaunchedScreenCasterReady(viewerIDs);
            }
            else
            {
                await _desktopHub.NotifyRequesterUnattendedReady();
            }
        }
        finally
        {
            _idleTimer.Start();
        }
    }

    private async void OnElevationRequested(object? sender, ElevationRequestedEventArgs e)
    {
        _logger.LogInformation("Elevation requested for user {username}.", e.Username);

        try
        {
            var viewerIds = _appState.Viewers.Keys.ToArray();
            var newSessionId = Guid.NewGuid();
            var newAccessKey = RandomGenerator.GenerateAccessKey();

            var commandLineArgs =
                $" --mode Unattended" +
                $" --relaunch true" +
                $" --host \"{_appState.Host}\"" +
                $" --requester-name \"{_appState.RequesterName}\"" +
                $" --org-name \"{_appState.OrganizationName}\"" +
                $" --org-id \"{_appState.OrganizationId}\"" +
                $" --session-id \"{newSessionId}\"" +
                $" --access-key \"{newAccessKey}\"" +
                $" --viewers {string.Join(",", viewerIds)}";

            _logger.LogInformation("Attempting elevated relaunch. New session ID: {sessionId}.", newSessionId);

            var success = Win32Interop.RelaunchElevated(
                e.Username,
                e.Domain,
                e.Password,
                commandLineArgs,
                out var procInfo,
                out var elevationError,
                out var win32ErrorCode);

            if (success)
            {
                _logger.LogInformation(
                    "Elevated relaunch succeeded. New process ID: {processId}. Notifying server before shutdown.",
                    procInfo.dwProcessId);

                await _desktopHub.NotifyElevationRelaunch();
                await _shutdownService.Shutdown();
            }
            else
            {
                var message = GetElevationFailureMessage(win32ErrorCode, elevationError);
                _logger.LogWarning("Elevated relaunch failed: {message}", message);

                foreach (var viewer in _appState.Viewers.Values)
                {
                    await _desktopHub.SendMessageToViewer(viewer.ViewerConnectionId, message);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during elevation relaunch.");

            foreach (var viewer in _appState.Viewers.Values)
            {
                await _desktopHub.SendMessageToViewer(viewer.ViewerConnectionId, "Elevation failed due to an unexpected error.");
            }
        }
    }

    private static string GetElevationFailureMessage(int win32ErrorCode, string elevationError)
    {
        var reason = win32ErrorCode switch
        {
            1326 => "Incorrect username or password.",
            1327 => "A restriction on the account prevents this logon.",
            1330 => "The password has expired.",
            1385 => "This account is not permitted to log on interactively.",
            1909 => "The account is locked out.",
            _ => string.IsNullOrEmpty(elevationError) ? "Elevation failed." : elevationError,
        };

        return $"Elevation failed: {reason}";
    }

    private async void CursorIconWatcher_OnChange(object? sender, CursorInfo cursor)
    {
        if (_appState.Viewers.Any() == true &&
            _desktopHub.IsConnected)
        {
            foreach (var viewer in _appState.Viewers.Values)
            {
                await viewer.SendCursorChange(cursor);
            }
        }
    }
}
