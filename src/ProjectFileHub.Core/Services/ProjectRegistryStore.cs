using System.Text.Json;
using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public sealed class ProjectRegistryStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    private readonly string _stateFilePath;
    private readonly string _backupFilePath;
    private readonly string _previousFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProjectRegistryStore(string stateFilePath, string? backupFilePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateFilePath);
        _stateFilePath = Path.GetFullPath(stateFilePath);
        _backupFilePath = Path.GetFullPath(backupFilePath ?? (_stateFilePath + ".backup"));
        _previousFilePath = _stateFilePath + ".previous";
    }

    public bool LastLoadRecoveredFromBackup { get; private set; }

    public async Task<ProjectRegistryState> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectRegistryState> AddAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        var normalizedRoot = PathBoundary.NormalizeRoot(rootPath);

        if (!Directory.Exists(normalizedRoot))
        {
            throw new DirectoryNotFoundException(normalizedRoot);
        }

        var boundary = new PathBoundary(normalizedRoot);
        boundary.EnsureSafe(normalizedRoot);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var existing = state.Projects.FirstOrDefault(project =>
                string.Equals(project.RootPath, normalizedRoot, StringComparison.OrdinalIgnoreCase));

            if (existing is not null)
            {
                return await SaveMutationUnsafeAsync(
                    state with { ActiveProjectId = existing.Id },
                    cancellationToken).ConfigureAwait(false);
            }

            var project = RegisteredProject.Create(normalizedRoot);
            return await SaveMutationUnsafeAsync(
                state with
                {
                    Projects = [.. state.Projects, project],
                    ActiveProjectId = project.Id
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectRegistryState> SetActiveAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);

            if (state.Projects.All(project => project.Id != projectId))
            {
                throw new KeyNotFoundException("指定项目尚未注册。");
            }

            return await SaveMutationUnsafeAsync(
                state with { ActiveProjectId = projectId },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ProjectRegistryState> RemoveAsync(
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var remaining = state.Projects.Where(project => project.Id != projectId).ToList();
            var nextActive = state.ActiveProjectId == projectId
                ? remaining.FirstOrDefault()?.Id
                : state.ActiveProjectId;

            return await SaveMutationUnsafeAsync(
                state with { Projects = remaining, ActiveProjectId = nextActive },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProjectRegistryState> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        LastLoadRecoveredFromBackup = false;

        var candidates = new List<RegistryCandidate>();
        var failures = new List<Exception>();
        var anyCopyExists = false;

        await AddCandidateAsync(_stateFilePath, RegistryCopy.Primary).ConfigureAwait(false);
        if (!string.Equals(_backupFilePath, _stateFilePath, StringComparison.OrdinalIgnoreCase))
        {
            await AddCandidateAsync(_backupFilePath, RegistryCopy.Backup).ConfigureAwait(false);
        }

        if (!string.Equals(_previousFilePath, _stateFilePath, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(_previousFilePath, _backupFilePath, StringComparison.OrdinalIgnoreCase))
        {
            await AddCandidateAsync(_previousFilePath, RegistryCopy.Previous).ConfigureAwait(false);
        }

        if (candidates.Count == 0)
        {
            if (!anyCopyExists)
            {
                return new ProjectRegistryState();
            }

            throw new InvalidDataException(
                "项目列表文件无法读取，已停止写入以避免用空列表覆盖原有项目。",
                failures.Count == 0 ? null : new AggregateException(failures));
        }

        var selected = candidates
            .OrderByDescending(candidate => candidate.State.Revision)
            .ThenByDescending(candidate => candidate.State.UpdatedAt)
            .ThenBy(candidate => candidate.Copy)
            .First();
        var primary = candidates.FirstOrDefault(candidate => candidate.Copy == RegistryCopy.Primary);
        var recovered = selected.Copy != RegistryCopy.Primary
            || primary is null
            || !RegistryContentsEqual(primary.State, selected.State);

        if (recovered)
        {
            LastLoadRecoveredFromBackup = true;
            if (primary is not null && !RegistryContentsEqual(primary.State, selected.State))
            {
                await TryWriteAtomicAsync(_previousFilePath, primary.State, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await TryWriteAtomicAsync(_stateFilePath, selected.State, CancellationToken.None)
                .ConfigureAwait(false);
        }

        var backup = candidates.FirstOrDefault(candidate => candidate.Copy == RegistryCopy.Backup);
        if (backup is null || !RegistryContentsEqual(backup.State, selected.State))
        {
            await TryWriteAtomicAsync(_backupFilePath, selected.State, CancellationToken.None)
                .ConfigureAwait(false);
        }

        return selected.State;

        async Task AddCandidateAsync(string path, RegistryCopy copy)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(path))
            {
                return;
            }

            anyCopyExists = true;
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096,
                    useAsync: true);
                var state = await JsonSerializer.DeserializeAsync<ProjectRegistryState>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException($"项目列表为空：{path}");
                var timestamp = File.GetLastWriteTimeUtc(path);
                candidates.Add(new RegistryCandidate(copy, Normalize(state, timestamp)));
            }
            catch (Exception exception) when (exception is JsonException
                                               or NotSupportedException
                                               or InvalidDataException
                                               or IOException
                                               or UnauthorizedAccessException)
            {
                failures.Add(new InvalidDataException($"无法读取项目列表副本：{path}", exception));
            }
        }
    }

    private async Task<ProjectRegistryState> SaveMutationUnsafeAsync(
        ProjectRegistryState state,
        CancellationToken cancellationToken)
    {
        var persisted = Normalize(
            state with
            {
                SchemaVersion = ProjectRegistryState.CurrentSchemaVersion,
                Revision = checked(state.Revision + 1),
                UpdatedAt = DateTimeOffset.UtcNow
            },
            DateTime.UtcNow);

        var currentPrimary = await TryReadSingleAsync(_stateFilePath, cancellationToken)
            .ConfigureAwait(false);
        if (currentPrimary is not null && !RegistryContentsEqual(currentPrimary, persisted))
        {
            await TryWriteAtomicAsync(_previousFilePath, currentPrimary, CancellationToken.None)
                .ConfigureAwait(false);
        }

        await WriteAtomicAsync(_stateFilePath, persisted, cancellationToken).ConfigureAwait(false);
        await TryWriteAtomicAsync(_backupFilePath, persisted, CancellationToken.None).ConfigureAwait(false);
        LastLoadRecoveredFromBackup = false;
        return persisted;
    }

    private static async Task<ProjectRegistryState?> TryReadSingleAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                useAsync: true);
            var state = await JsonSerializer.DeserializeAsync<ProjectRegistryState>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);
            return state is null ? null : Normalize(state, File.GetLastWriteTimeUtc(path));
        }
        catch (Exception exception) when (exception is JsonException
                                           or NotSupportedException
                                           or InvalidDataException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ProjectRegistryState Normalize(ProjectRegistryState state, DateTime fallbackTimestamp)
    {
        var projects = new List<RegisteredProject>();
        var projectIds = new HashSet<Guid>();
        var projectRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in state.Projects ?? [])
        {
            if (project.Id == Guid.Empty || string.IsNullOrWhiteSpace(project.RootPath))
            {
                throw new InvalidDataException("项目列表包含无效记录。");
            }

            var rootPath = PathBoundary.NormalizeRoot(project.RootPath);
            if (!projectIds.Add(project.Id) || !projectRoots.Add(rootPath))
            {
                continue;
            }

            var name = string.IsNullOrWhiteSpace(project.Name)
                ? new DirectoryInfo(rootPath).Name
                : project.Name.Trim();
            projects.Add(project with { Name = name, RootPath = rootPath });
        }

        var activeProjectId = state.ActiveProjectId is Guid activeId
                              && projects.Any(project => project.Id == activeId)
            ? activeId
            : projects.FirstOrDefault()?.Id;
        var updatedAt = state.UpdatedAt == default
            ? new DateTimeOffset(DateTime.SpecifyKind(fallbackTimestamp, DateTimeKind.Utc))
            : state.UpdatedAt;

        return state with
        {
            SchemaVersion = ProjectRegistryState.CurrentSchemaVersion,
            Revision = Math.Max(0, state.Revision),
            UpdatedAt = updatedAt,
            Projects = projects,
            ActiveProjectId = activeProjectId
        };
    }

    private static bool RegistryContentsEqual(ProjectRegistryState left, ProjectRegistryState right) =>
        left.SchemaVersion == right.SchemaVersion
        && left.Revision == right.Revision
        && left.UpdatedAt.Equals(right.UpdatedAt)
        && left.ActiveProjectId == right.ActiveProjectId
        && left.Projects.SequenceEqual(right.Projects);

    private static async Task<bool> TryWriteAtomicAsync(
        string path,
        ProjectRegistryState state,
        CancellationToken cancellationToken)
    {
        try
        {
            await WriteAtomicAsync(path, state, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (exception is IOException
                                           or UnauthorizedAccessException
                                           or NotSupportedException)
        {
            return false;
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        ProjectRegistryState state,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("项目状态文件缺少父目录。");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A successful replace matters more than best-effort temporary-file cleanup.
            }
        }
    }

    private enum RegistryCopy
    {
        Primary,
        Backup,
        Previous
    }

    private sealed record RegistryCandidate(RegistryCopy Copy, ProjectRegistryState State);
}
