using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assistant.Host.Services;

public record ToolCallRecord(string Name, string ArgsJson, string ResultJson);

public record ChatMessage(string Role, string Content, DateTimeOffset Timestamp,
    List<ToolCallRecord>? ToolCalls = null);

public record Conversation(string Id, string Title, DateTimeOffset Created,
    DateTimeOffset Updated, List<ChatMessage> Messages);

public class ConversationStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _dir;

    public ConversationStore(string dataDir)
    {
        _dir = Path.Combine(dataDir, "conversations");
        Directory.CreateDirectory(_dir);
    }

    public async Task<List<Conversation>> ListAsync()
    {
        var convs = new List<Conversation>();
        foreach (var file in Directory.GetFiles(_dir, "*.json"))
        {
            try
            {
                var json = await File.ReadAllTextAsync(file);
                var conv = JsonSerializer.Deserialize<Conversation>(json, JsonOpts);
                if (conv != null) convs.Add(conv);
            }
            catch { }
        }
        return [.. convs.OrderByDescending(c => c.Updated)];
    }

    public async Task<Conversation?> GetAsync(string id)
    {
        var path = Path.Combine(_dir, $"{id}.json");
        if (!File.Exists(path)) return null;
        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<Conversation>(json, JsonOpts);
    }

    public async Task SaveAsync(Conversation conv)
    {
        var path = Path.Combine(_dir, $"{conv.Id}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(conv, JsonOpts));
    }

    public async Task<Conversation> CreateAsync(string firstMessage)
    {
        var id = Guid.NewGuid().ToString("N");
        var title = firstMessage.Length > 60 ? firstMessage[..60] + "…" : firstMessage;
        var now = DateTimeOffset.UtcNow;
        var conv = new Conversation(id, title, now, now, []);
        await SaveAsync(conv);
        return conv;
    }

    public void Delete(string id)
    {
        var path = Path.Combine(_dir, $"{id}.json");
        if (File.Exists(path)) File.Delete(path);
    }
}
