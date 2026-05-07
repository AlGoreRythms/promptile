namespace Promptile.Sdk;

/// <summary>
/// Plugins implement this to contribute status info to the tray menu.
/// </summary>
public interface ITrayStatusProvider
{
    Task<TrayStatus> GetStatusAsync();
}

public record TrayStatus(string Label, int? BadgeCount);
