namespace Assistant.Host.Services.Tray;

/// <summary>
/// Windows system tray icon. Stub — implement when testing on Windows.
/// </summary>
public class WindowsTrayHost : ITrayHost
{
    private Action? _onQuit;

    public void Run(Action onQuit)
    {
        _onQuit = onQuit;
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        try { Task.Delay(Timeout.Infinite, cts.Token).Wait(); } catch { }
        _onQuit?.Invoke();
    }

    public void UpdateStatus(string label) { }
    public void ShowNotification(string title, string message) { }
    public void Shutdown() { }
}
