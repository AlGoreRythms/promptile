namespace Promptile.Sdk;

/// <summary>
/// Provides access to running data source instances. Available in DI for MCP tool classes.
/// </summary>
public interface IDataSourceManager
{
    IReadOnlyList<IDataSourceInstance> GetInstances(string? type = null);
    IDataSourceInstance? GetInstance(string name, string? type = null);
    IReadOnlyList<IDataSourceProvider> GetProviders();
}
