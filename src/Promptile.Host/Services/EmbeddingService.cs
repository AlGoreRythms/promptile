using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Promptile.Sdk;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Promptile.Host.Services;

/// <summary>
/// Maintains a local SQLite embedding index over all Data/ store content.
/// Provides semantic search via the store_search MCP tool.
/// </summary>
public class EmbeddingService : IHostedService
{
    private readonly IInformationStore _store;
    private readonly INotificationBus _bus;
    private readonly SettingsService _settings;
    private readonly ILogger<EmbeddingService> _logger;
    private readonly string _dbPath;

    private const int ChunkSize = 800;        // chars per chunk
    private const int MaxResults = 8;

    public EmbeddingService(
        IInformationStore store,
        INotificationBus bus,
        SettingsService settings,
        ILogger<EmbeddingService> logger)
    {
        _store = store;
        _bus = bus;
        _settings = settings;
        _logger = logger;
        _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".promptile", "embeddings.db");
    }

    public Task StartAsync(CancellationToken ct)
    {
        EnsureDb();
        _bus.Subscribe(OnNotification);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private void OnNotification(AssistantNotification n)
    {
        if (n.Category != "sync-summary") return;
        _ = Task.Run(async () =>
        {
            try { await IndexNewFilesAsync(CancellationToken.None); }
            catch (Exception ex) { _logger.LogError(ex, "Embedding index failed"); }
        });
    }

    private async Task IndexNewFilesAsync(CancellationToken ct)
    {
        var s = _settings.LoadSync();
        if (string.IsNullOrWhiteSpace(s.EmbeddingBaseUrl)) return;

        var since = GetLastIndexedTime();

        foreach (var (sourceName, types) in _store.ListSources())
        {
            foreach (var sourceType in types)
            {
                var dataPath = _store.GetDataPath(sourceName, sourceType);
                if (!Directory.Exists(dataPath)) continue;

                foreach (var file in Directory.GetFiles(dataPath, "*.md")
                    .Where(f => File.GetLastWriteTimeUtc(f) > since))
                {
                    ct.ThrowIfCancellationRequested();
                    var content = await File.ReadAllTextAsync(file, ct);
                    var chunks = ChunkText(content);
                    for (var i = 0; i < chunks.Count; i++)
                    {
                        var vec = await GetEmbeddingAsync(chunks[i], s.EmbeddingBaseUrl, s.EmbeddingModel, ct);
                        if (vec == null) continue;
                        UpsertChunk(sourceName, sourceType, Path.GetFileName(file), i, chunks[i], vec);
                    }
                }
            }
        }
        SaveLastIndexedTime(DateTime.UtcNow);
    }

    public async Task<string> SearchAsync(string query, int limit = MaxResults)
    {
        var s = _settings.LoadSync();
        if (string.IsNullOrWhiteSpace(s.EmbeddingBaseUrl))
            return "Semantic search is not configured — set EmbeddingBaseUrl in Settings.";

        var vec = await GetEmbeddingAsync(query, s.EmbeddingBaseUrl, s.EmbeddingModel, CancellationToken.None);
        if (vec == null) return "Failed to embed query.";

        var results = CosineSimilaritySearch(vec, limit);
        if (results.Count == 0) return "No matching content found.";

        var sb = new StringBuilder();
        foreach (var r in results)
            sb.AppendLine($"[{r.Source}/{r.SourceType}/{r.Filename} chunk {r.ChunkIndex}] (score: {r.Score:F3})\n{r.Text}\n---");
        return sb.ToString();
    }

    // ---- DB helpers ----

    private void EnsureDb()
    {
        using var conn = Open();
        conn.Execute("""
            CREATE TABLE IF NOT EXISTS chunks (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                source TEXT, source_type TEXT, filename TEXT, chunk_idx INTEGER,
                text TEXT, embedding BLOB,
                UNIQUE(source, source_type, filename, chunk_idx)
            );
            CREATE TABLE IF NOT EXISTS meta (key TEXT PRIMARY KEY, value TEXT);
            """);
    }

    private void UpsertChunk(string source, string sourceType, string filename, int idx, string text, float[] vec)
    {
        using var conn = Open();
        var blob = VecToBlob(vec);
        conn.Execute("""
            INSERT INTO chunks (source, source_type, filename, chunk_idx, text, embedding)
            VALUES (@source, @sourceType, @filename, @idx, @text, @blob)
            ON CONFLICT(source, source_type, filename, chunk_idx) DO UPDATE SET text=@text, embedding=@blob;
            """,
            new { source, sourceType, filename, idx, text, blob });
    }

    private List<SearchResult> CosineSimilaritySearch(float[] query, int limit)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT source, source_type, filename, chunk_idx, text, embedding FROM chunks";
        using var reader = cmd.ExecuteReader();
        var results = new List<(float Score, SearchResult R)>();
        while (reader.Read())
        {
            var blob = (byte[])reader["embedding"];
            var vec = BlobToVec(blob);
            var score = CosineSimilarity(query, vec);
            results.Add((score, new SearchResult(
                reader.GetString(0), reader.GetString(1),
                reader.GetString(2), reader.GetInt32(3),
                reader.GetString(4), score)));
        }
        return results.OrderByDescending(r => r.Score).Take(limit).Select(r => r.R).ToList();
    }

    private DateTime GetLastIndexedTime()
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key='lastIndexed'";
        var val = cmd.ExecuteScalar() as string;
        return val != null && DateTime.TryParse(val, out var dt) ? dt : DateTime.MinValue;
    }

    private void SaveLastIndexedTime(DateTime dt)
    {
        using var conn = Open();
        conn.Execute("INSERT OR REPLACE INTO meta(key,value) VALUES('lastIndexed',@val)", new { val = dt.ToString("O") });
    }

    private SqliteConnection Open()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        return conn;
    }

    // ---- Embedding API ----

    private static readonly HttpClient _http = new();

    private static async Task<float[]?> GetEmbeddingAsync(string text, string baseUrl, string? model, CancellationToken ct)
    {
        try
        {
            var url = baseUrl.TrimEnd('/') + "/v1/embeddings";
            var body = JsonSerializer.Serialize(new
            {
                input = text,
                model = !string.IsNullOrWhiteSpace(model) ? model : "text-embedding-ada-002"
            });
            using var resp = await _http.PostAsync(url,
                new StringContent(body, Encoding.UTF8, "application/json"), ct);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var data = json.GetProperty("data")[0].GetProperty("embedding");
            return data.EnumerateArray().Select(e => e.GetSingle()).ToArray();
        }
        catch { return null; }
    }

    // ---- Vector math ----

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        var dot = 0f; var na = 0f; var nb = 0f;
        for (var i = 0; i < a.Length; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        return (na == 0 || nb == 0) ? 0 : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }

    private static byte[] VecToBlob(float[] vec)
    {
        var bytes = new byte[vec.Length * 4];
        Buffer.BlockCopy(vec, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] BlobToVec(byte[] blob)
    {
        var vec = new float[blob.Length / 4];
        Buffer.BlockCopy(blob, 0, vec, 0, blob.Length);
        return vec;
    }

    private static List<string> ChunkText(string text)
    {
        var chunks = new List<string>();
        var paragraphs = text.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);
        var current = new StringBuilder();
        foreach (var para in paragraphs)
        {
            if (current.Length + para.Length > ChunkSize && current.Length > 0)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            current.AppendLine(para);
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        return chunks;
    }

    private record SearchResult(string Source, string SourceType, string Filename, int ChunkIndex, string Text, float Score);
}

internal static class SqliteConnectionExtensions
{
    internal static void Execute(this SqliteConnection conn, string sql, object? parameters = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (parameters != null)
        {
            foreach (var prop in parameters.GetType().GetProperties())
                cmd.Parameters.AddWithValue("@" + prop.Name, prop.GetValue(parameters) ?? DBNull.Value);
        }
        cmd.ExecuteNonQuery();
    }
}
