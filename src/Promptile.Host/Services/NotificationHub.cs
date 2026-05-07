using Promptile.Host.Services.Tray;
using Promptile.Sdk;
using Microsoft.Extensions.Logging;

namespace Promptile.Host.Services;

/// <summary>
/// Receives events from plugins via INotificationBus and routes them to
/// the system notification service. Plugins that implement INotificationSource
/// are wired up here at startup.
/// </summary>
public class NotificationHub : INotificationBus
{
    private readonly ITrayHost _trayHost;
    private readonly ILogger<NotificationHub> _logger;
    private readonly SettingsService _settings;
    private readonly List<Action<AssistantNotification>> _subscribers = [];

    public NotificationHub(ITrayHost trayHost, ILogger<NotificationHub> logger, SettingsService settings)
    {
        _trayHost = trayHost;
        _logger = logger;
        _settings = settings;
    }

    public void Subscribe(Action<AssistantNotification> handler)
    {
        lock (_subscribers) _subscribers.Add(handler);
    }

    public void Publish(AssistantNotification notification)
    {
        _logger.LogInformation("Notification from {Plugin}: {Title}", notification.PluginId, notification.Title);

        List<Action<AssistantNotification>> handlers;
        lock (_subscribers) handlers = [.._subscribers];
        foreach (var h in handlers)
        {
            try { h(notification); } catch { }
        }

        if (notification.Category == "job-complete" && _settings.LoadSync().NotifyOnJobCompletion)
            _trayHost.ShowNotification(notification.Title, notification.Body);
    }

    public static void WirePlugins(IEnumerable<IPlugin> plugins, INotificationBus bus)
    {
        foreach (var plugin in plugins)
        {
            if (plugin is INotificationSource source)
                source.Subscribe(bus);
        }
    }
}
