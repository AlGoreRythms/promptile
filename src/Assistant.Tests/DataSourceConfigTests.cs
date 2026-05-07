using System.Text.Json;
using Assistant.Sdk;
using Xunit;

namespace Assistant.Tests;

public class DataSourceConfigTests
{
    [Fact]
    public void ConfigsWithSameFieldsAreJsonEqual()
    {
        var a = new DataSourceConfig("id1", "slack", "Work Slack", true,
            new Dictionary<string, string> { ["token"] = "xoxb-123", ["channel"] = "general" });
        var b = new DataSourceConfig("id1", "slack", "Work Slack", true,
            new Dictionary<string, string> { ["token"] = "xoxb-123", ["channel"] = "general" });

        Assert.Equal(JsonSerializer.Serialize(a.Config), JsonSerializer.Serialize(b.Config));
    }

    [Fact]
    public void ConfigsWithDifferentValuesAreNotJsonEqual()
    {
        var a = new DataSourceConfig("id1", "slack", "Work Slack", true,
            new Dictionary<string, string> { ["token"] = "xoxb-123" });
        var b = new DataSourceConfig("id1", "slack", "Work Slack", true,
            new Dictionary<string, string> { ["token"] = "xoxb-456" });

        Assert.NotEqual(JsonSerializer.Serialize(a.Config), JsonSerializer.Serialize(b.Config));
    }

    [Fact]
    public void RecordEqualityCoversScalarFields()
    {
        var a = new DataSourceConfig("id1", "slack", "Work", true, []);
        var b = new DataSourceConfig("id1", "slack", "Work", true, []);
        var c = new DataSourceConfig("id1", "slack", "Personal", true, []);

        Assert.Equal(a.Id, b.Id);
        Assert.Equal(a.Type, b.Type);
        Assert.NotEqual(a.Name, c.Name);
    }
}
