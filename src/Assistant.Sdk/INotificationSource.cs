namespace Assistant.Sdk;

/// <summary>
/// Plugin implements this to push events to the host when external state changes.
/// The host shows a system notification and can activate the assistant to act on it.
/// </summary>
public interface INotificationSource
{
    /// <summary>
    /// Called once at startup. Subscribe to external sources and call
    /// bus.Publish() whenever a relevant event occurs.
    /// </summary>
    void Subscribe(INotificationBus bus);
}

public interface INotificationBus
{
    void Publish(AssistantNotification notification);
    void Subscribe(Action<AssistantNotification> handler);
}

public record AssistantNotification(
    string PluginId,
    string Title,
    string Body,
    DateTimeOffset Timestamp,
    string? ActionUrl = null,
    object? Payload = null,
    string? Category = null
);

public record NewTaskPayload(
    string Key,
    string Summary,
    string? Description,
    string SourceName);
