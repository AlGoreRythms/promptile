using System.Text.Json;
using System.Text.Json.Serialization;
using Promptile.Sdk;

namespace Promptile.Host.Services;

public class DataSourcesService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    public DataSourcesService(string dataDir)
    {
        _path = Path.Combine(dataDir, "datasources.json");
    }

    public async Task<List<DataSourceConfig>> LoadAsync()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = await File.ReadAllTextAsync(_path);
                return JsonSerializer.Deserialize<List<DataSourceConfig>>(json, JsonOpts) ?? [];
            }
        }
        catch { }
        return [];
    }

    public async Task SaveAsync(List<DataSourceConfig> configs)
    {
        var json = JsonSerializer.Serialize(configs, JsonOpts);
        await File.WriteAllTextAsync(_path, json);
    }

    public async Task<DataSourceConfig> UpsertAsync(DataSourceConfig config)
    {
        var all = await LoadAsync();
        var idx = all.FindIndex(c => c.Id == config.Id);
        if (idx >= 0)
            all[idx] = config;
        else
            all.Add(config);
        await SaveAsync(all);
        return config;
    }

    public async Task DeleteAsync(string id)
    {
        var all = await LoadAsync();
        all.RemoveAll(c => c.Id == id);
        await SaveAsync(all);
    }
}
