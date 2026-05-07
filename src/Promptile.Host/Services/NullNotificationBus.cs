using Promptile.Sdk;

namespace Promptile.Host.Services;

public class NullNotificationBus : INotificationBus
{
    public void Publish(AssistantNotification notification) { }
    public void Subscribe(Action<AssistantNotification> handler) { }
}
