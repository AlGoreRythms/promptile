namespace Promptile.Host.Services.Tray;

public interface ITrayHost
{
    void Run(Action onQuit);
    void UpdateStatus(string label);
    void ShowNotification(string title, string message);
    void Shutdown();
}
