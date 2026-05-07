using Promptile.Host.Services.Tray;
using Promptile.Sdk;

namespace Promptile.Host.Services;

public class NotificationService : INotificationService
{
    private readonly ITrayHost _trayHost;

    public NotificationService(ITrayHost trayHost)
    {
        _trayHost = trayHost;
    }

    public void ShowNotification(string title, string message)
    {
        _trayHost.ShowNotification(title, message);
    }
}
