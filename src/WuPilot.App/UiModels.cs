using System.ComponentModel;
using System.Runtime.CompilerServices;
using WuPilot.Core.Models;
using WuPilot.Core.Services;

namespace WuPilot.App;

public sealed class ProviderOption(UpdateProviderDefinition provider, bool isSelected = false) : INotifyPropertyChanged
{
    private bool _isSelected = isSelected;
    public UpdateProviderDefinition Provider { get; } = provider;
    public string DisplayName => Provider.DisplayName;
    public string Description => Provider.Description;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record ScanPresetOption(ScanPreset Value, string DisplayName, string Description);

public sealed class UpdateListItem(UpdateRecord update)
{
    public UpdateRecord Update { get; } = update;
    public string Title => Update.Title;
    public string TypeLabel => Update.Kind.ToString();
    public string SourceLabel => string.Join(" · ", Update.ProviderNames);
    public string Metadata => Update.IsDriver
        ? string.Join(" · ", new[]
        {
            Update.Driver?.Manufacturer,
            Update.Driver?.DriverClass,
            Update.Driver?.InstalledMatch?.Driver.DriverVersion is { Length: > 0 } installed ? $"installed {installed}" : null,
            DriverVersionParser.InferFromTitle(Update.Title) is { Length: > 0 } offered ? $"offered {offered}" : null,
            Update.Driver?.VersionDate?.ToString("yyyy-MM-dd")
        }.Where(static value => !string.IsNullOrWhiteSpace(value)))
        : string.Join(" · ", Update.KbArticleIds.Select(static kb => kb.StartsWith("KB", StringComparison.OrdinalIgnoreCase) ? kb : $"KB{kb}"));
    public string SizeLabel => Update.MaximumDownloadBytes is null ? "Size unavailable" : FormatBytes(Update.MaximumDownloadBytes.Value);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
