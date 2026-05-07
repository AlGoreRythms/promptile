using Promptile.Sdk;

namespace Promptile.Host.Services;

public class InformationStoreService : IInformationStore
{
    public string RootPath { get; private set; }

    public InformationStoreService(string rootPath)
    {
        RootPath = rootPath;
        Directory.CreateDirectory(rootPath);
    }

    public string GetDataPath(string sourceName, string sourceType)
    {
        var path = Path.Combine(RootPath, Sanitize(sourceName), Sanitize(sourceType), "Data");
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetNotesPath(string sourceName, string sourceType)
    {
        var path = Path.Combine(RootPath, Sanitize(sourceName), Sanitize(sourceType), "Notes");
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task AppendDataAsync(string sourceName, string sourceType, string filename, string content)
    {
        var path = Path.Combine(GetDataPath(sourceName, sourceType), Sanitize(filename));
        await File.AppendAllTextAsync(path, content);
    }

    public async Task AppendDataAsync(string sourceName, string sourceType, string subType, string filename, string content)
    {
        var dir = Path.Combine(RootPath, Sanitize(sourceName), Sanitize(sourceType), "Data", Sanitize(subType));
        Directory.CreateDirectory(dir);
        await File.AppendAllTextAsync(Path.Combine(dir, Sanitize(filename)), content);
    }

    public async Task WriteDataAsync(string sourceName, string sourceType, string filename, string content)
    {
        var path = Path.Combine(GetDataPath(sourceName, sourceType), Sanitize(filename));
        await File.WriteAllTextAsync(path, content);
    }

    public async Task WriteNoteAsync(string sourceName, string sourceType, string filename, string content)
    {
        var path = Path.Combine(GetNotesPath(sourceName, sourceType), Sanitize(filename));
        await File.WriteAllTextAsync(path, content);
    }

    public async Task<string?> ReadNoteAsync(string sourceName, string sourceType, string filename)
    {
        var path = Path.Combine(GetNotesPath(sourceName, sourceType), Sanitize(filename));
        if (!File.Exists(path)) return null;
        return await File.ReadAllTextAsync(path);
    }

    public IReadOnlyList<string> ListNotes(string sourceName, string sourceType)
    {
        var path = GetNotesPath(sourceName, sourceType);
        if (!Directory.Exists(path)) return [];
        return Directory.GetFiles(path)
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .Select(f => f!)
            .OrderBy(f => f)
            .ToList();
    }

    public IReadOnlyList<(string Name, IReadOnlyList<string> Types)> ListSources()
    {
        if (!Directory.Exists(RootPath)) return [];
        return Directory.GetDirectories(RootPath)
            .Select(nameDir => (
                Name: Path.GetFileName(nameDir),
                Types: (IReadOnlyList<string>)Directory.GetDirectories(nameDir)
                    .Select(Path.GetFileName)
                    .Where(t => t != null)
                    .Select(t => t!)
                    .ToList()
            ))
            .ToList();
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
