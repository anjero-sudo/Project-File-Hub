using System.Text.Json;
using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _stateFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettingsStore(string stateFilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        _stateFilePath = Path.GetFullPath(stateFilePath);
    }

    public async Task<AppSettingsState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_stateFilePath))
            {
                return new AppSettingsState();
            }

            await using var stream = File.OpenRead(_stateFilePath);
            var state = await JsonSerializer.DeserializeAsync<AppSettingsState>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false) ?? new AppSettingsState();
            return Normalize(state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        AppSettingsState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(_stateFilePath)
                ?? throw new InvalidOperationException("设置文件缺少父目录。");
            Directory.CreateDirectory(directory);

            var temporaryPath = _stateFilePath + ".tmp";
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    Normalize(state),
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, _stateFilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AppSettingsState Normalize(AppSettingsState state) => state with
    {
        Theme = AppThemeNames.IsValid(state.Theme) ? state.Theme : AppThemeNames.Midnight,
        Density = AppDensityNames.IsValid(state.Density) ? state.Density : AppDensityNames.Comfortable,
        ProjectWorkspaces = state.ProjectWorkspaces ?? []
    };
}
