using System.Text.Json;
using Promptile.Sdk;
using Xunit;

namespace Promptile.Tests;

public class AssistantNotificationTests
{
    [Fact]
    public void NotificationRoundTripsJson()
    {
        var now = new DateTimeOffset(2026, 5, 7, 9, 0, 0, TimeSpan.Zero);
        var n = new AssistantNotification("slack", "New message", "Hello world", now,
            ActionUrl: "https://slack.com/123", Payload: null, Category: "message");

        var json = JsonSerializer.Serialize(n);
        var restored = JsonSerializer.Deserialize<AssistantNotification>(json);

        Assert.NotNull(restored);
        Assert.Equal(n.PluginId, restored.PluginId);
        Assert.Equal(n.Title, restored.Title);
        Assert.Equal(n.Body, restored.Body);
        Assert.Equal(n.Timestamp, restored.Timestamp);
        Assert.Equal(n.ActionUrl, restored.ActionUrl);
        Assert.Equal(n.Category, restored.Category);
    }

    [Fact]
    public void NewTaskPayloadRoundTripsJson()
    {
        var payload = new NewTaskPayload("LINEAR-123", "Fix login bug",
            "Users cannot log in with SSO", "Linear");

        var json = JsonSerializer.Serialize(payload);
        var restored = JsonSerializer.Deserialize<NewTaskPayload>(json);

        Assert.NotNull(restored);
        Assert.Equal(payload.Key, restored.Key);
        Assert.Equal(payload.Summary, restored.Summary);
        Assert.Equal(payload.SourceName, restored.SourceName);
    }
}
