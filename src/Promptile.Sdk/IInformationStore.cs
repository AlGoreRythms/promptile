namespace Promptile.Sdk;

public interface IInformationStore
{
    string RootPath { get; }

    string GetDataPath(string sourceName, string sourceType);
    string GetNotesPath(string sourceName, string sourceType);

    Task AppendDataAsync(string sourceName, string sourceType, string filename, string content);
    Task AppendDataAsync(string sourceName, string sourceType, string subType, string filename, string content);
    Task WriteDataAsync(string sourceName, string sourceType, string filename, string content);
    Task WriteNoteAsync(string sourceName, string sourceType, string filename, string content);
    Task<string?> ReadNoteAsync(string sourceName, string sourceType, string filename);

    IReadOnlyList<string> ListNotes(string sourceName, string sourceType);
    IReadOnlyList<(string Name, IReadOnlyList<string> Types)> ListSources();
}
