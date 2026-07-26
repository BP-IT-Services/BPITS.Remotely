namespace Remotely.Desktop.Shared.Abstractions;

public interface IShutdownService
{
    /// <summary>
    /// Shuts down the process.
    /// </summary>
    /// <param name="disconnectViewers">
    /// Whether to disconnect and notify connected viewers before exiting. This should be
    /// false when handing viewers off to a relaunched process (e.g. an elevation relaunch),
    /// otherwise the viewers are told the session ended and the reconnect is lost.
    /// </param>
    Task Shutdown(bool disconnectViewers = true);
}
