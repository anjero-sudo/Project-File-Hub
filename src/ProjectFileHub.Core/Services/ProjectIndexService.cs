using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using ProjectFileHub.Core.Models;

namespace ProjectFileHub.Core.Services;

public sealed class ProjectIndexService : IAsyncDisposable
{
    private readonly PathBoundary _boundary;
    private readonly string _databasePath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<IndexChange> _pendingChanges = new();
    private readonly Timer _changeTimer;
    private FileSystemWatcher? _watcher;
    private int _isPaused;
    private bool _disposed;

    public ProjectIndexService(string projectRoot, string databasePath)
    {
        _boundary = new PathBoundary(projectRoot);
        _boundary.EnsureSafe(_boundary.RootPath);
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
        _changeTimer = new Timer(_ => _ = FlushPendingChangesAsync(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event EventHandler? IndexChanged;

    public event EventHandler<string>? IndexingFailed;

    public int IndexedItemCount { get; private set; }

    public bool IsPaused => Volatile.Read(ref _isPaused) != 0;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)
            ?? throw new InvalidOperationException("索引数据库缺少父目录。"));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnsureSchemaAsync(connection, cancellationToken).ConfigureAwait(false);
            IndexedItemCount = await RebuildIndexUnsafeAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        StartWatcher();
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<FileSystemItem>> QueryAsync(
        FileItemCategory category,
        FileQueryOptions options,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name, path, is_directory, size, modified_utc_ticks, created_utc_ticks, extension, category
                FROM entries
                WHERE category = $category
                """;
            command.Parameters.AddWithValue("$category", (int)category);

            var items = new List<FileSystemItem>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var isDirectory = reader.GetInt64(2) != 0;
                items.Add(new FileSystemItem(
                    reader.GetString(0),
                    reader.GetString(1),
                    isDirectory,
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    new DateTimeOffset(reader.GetInt64(4), TimeSpan.Zero),
                    new DateTimeOffset(reader.GetInt64(5), TimeSpan.Zero),
                    reader.GetString(6),
                    (FileItemCategory)reader.GetInt64(7)));
            }

            return Sort(items, options);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void RequestRefresh()
    {
        ThrowIfDisposed();
        QueueChange(new IndexChange(IndexChangeKind.Rebuild, _boundary.RootPath));
    }

    public void Pause()
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _isPaused, 1) != 0)
        {
            return;
        }

        _changeTimer.Change(Timeout.Infinite, Timeout.Infinite);
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
        }
    }

    public void Resume()
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _isPaused, 0) == 0)
        {
            return;
        }

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = true;
        }

        QueueChange(new IndexChange(IndexChangeKind.Rebuild, _boundary.RootPath));
    }

    private async Task<int> RebuildIndexUnsafeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var scanId = Guid.NewGuid().ToString("N");
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var indexedCount = 0;
        var folders = new Stack<string>();
        folders.Push(_boundary.RootPath);

        while (folders.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folder = folders.Pop();
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(folder).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var path in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryCreateItem(path, out var item))
                {
                    continue;
                }

                await UpsertAsync(connection, transaction, item, scanId, cancellationToken).ConfigureAwait(false);
                indexedCount++;
                if (item.IsDirectory)
                {
                    folders.Push(item.FullPath);
                }
            }
        }

        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM entries WHERE scan_id <> $scan_id";
            cleanup.Parameters.AddWithValue("$scan_id", scanId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return indexedCount;
    }

    private void StartWatcher()
    {
        if (_disposed)
        {
            return;
        }

        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(_boundary.RootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite
                | NotifyFilters.Size
                | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024,
            EnableRaisingEvents = !IsPaused
        };
        _watcher.Created += (_, args) => QueueChange(new IndexChange(IndexChangeKind.UpsertTree, args.FullPath));
        _watcher.Changed += (_, args) => QueueChange(new IndexChange(IndexChangeKind.Upsert, args.FullPath));
        _watcher.Deleted += (_, args) => QueueChange(new IndexChange(IndexChangeKind.Delete, args.FullPath));
        _watcher.Renamed += (_, args) =>
        {
            QueueChange(new IndexChange(IndexChangeKind.Delete, args.OldFullPath));
            QueueChange(new IndexChange(IndexChangeKind.UpsertTree, args.FullPath));
        };
        _watcher.Error += (_, _) => QueueChange(new IndexChange(IndexChangeKind.Rebuild, _boundary.RootPath));
    }

    private void QueueChange(IndexChange change)
    {
        if (_disposed || !_boundary.Contains(change.Path))
        {
            return;
        }

        _pendingChanges.Enqueue(change);
        if (!IsPaused)
        {
            _changeTimer.Change(TimeSpan.FromMilliseconds(450), Timeout.InfiniteTimeSpan);
        }
    }

    private async Task FlushPendingChangesAsync()
    {
        if (_disposed || IsPaused || _pendingChanges.IsEmpty)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed || IsPaused)
            {
                return;
            }

            var changes = new List<IndexChange>();
            while (_pendingChanges.TryDequeue(out var change))
            {
                changes.Add(change);
            }

            if (changes.Count == 0)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync().ConfigureAwait(false);
            await EnsureSchemaAsync(connection, CancellationToken.None).ConfigureAwait(false);

            if (changes.Any(change => change.Kind == IndexChangeKind.Rebuild))
            {
                IndexedItemCount = await RebuildIndexUnsafeAsync(connection, CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync().ConfigureAwait(false);
                foreach (var change in CollapseChanges(changes))
                {
                    if (change.Kind == IndexChangeKind.Delete)
                    {
                        await DeletePathAsync(connection, transaction, change.Path).ConfigureAwait(false);
                    }
                    else
                    {
                        await UpsertPathAsync(
                            connection,
                            transaction,
                            change.Path,
                            includeChildren: change.Kind == IndexChangeKind.UpsertTree).ConfigureAwait(false);
                    }
                }

                await transaction.CommitAsync().ConfigureAwait(false);
                IndexedItemCount = await CountAsync(connection).ConfigureAwait(false);
            }

            IndexChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            IndexingFailed?.Invoke(this, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task UpsertPathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path,
        bool includeChildren)
    {
        if (!TryCreateItem(path, out var item))
        {
            await DeletePathAsync(connection, transaction, path).ConfigureAwait(false);
            return;
        }

        var scanId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        await UpsertAsync(connection, transaction, item, scanId, CancellationToken.None).ConfigureAwait(false);
        if (!includeChildren || !item.IsDirectory)
        {
            return;
        }

        var folders = new Stack<string>();
        folders.Push(item.FullPath);
        while (folders.Count > 0)
        {
            var folder = folders.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(folder);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var childPath in entries)
            {
                if (!TryCreateItem(childPath, out var child))
                {
                    continue;
                }

                await UpsertAsync(connection, transaction, child, scanId, CancellationToken.None).ConfigureAwait(false);
                if (child.IsDirectory)
                {
                    folders.Push(child.FullPath);
                }
            }
        }
    }

    private bool TryCreateItem(string path, out FileSystemItem item)
    {
        item = null!;
        try
        {
            if (!_boundary.Contains(path)
                || (!File.Exists(path) && !Directory.Exists(path))
                || !_boundary.IsSafeExistingPath(path))
            {
                return false;
            }

            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return false;
            }

            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            FileSystemInfo info = isDirectory ? new DirectoryInfo(path) : new FileInfo(path);
            var extension = isDirectory ? string.Empty : Path.GetExtension(path);
            item = new FileSystemItem(
                info.Name,
                info.FullName,
                isDirectory,
                isDirectory ? null : ((FileInfo)info).Length,
                info.LastWriteTimeUtc,
                info.CreationTimeUtc,
                extension,
                FileCategoryClassifier.Classify(extension, isDirectory));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            CREATE TABLE IF NOT EXISTS entries (
                path TEXT PRIMARY KEY COLLATE NOCASE,
                name TEXT NOT NULL,
                parent_path TEXT NOT NULL COLLATE NOCASE,
                is_directory INTEGER NOT NULL,
                size INTEGER NULL,
                modified_utc_ticks INTEGER NOT NULL,
                created_utc_ticks INTEGER NOT NULL,
                extension TEXT NOT NULL,
                category INTEGER NOT NULL,
                scan_id TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_entries_category ON entries(category);
            CREATE INDEX IF NOT EXISTS ix_entries_parent ON entries(parent_path);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FileSystemItem item,
        string scanId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO entries (
                path, name, parent_path, is_directory, size, modified_utc_ticks,
                created_utc_ticks, extension, category, scan_id)
            VALUES (
                $path, $name, $parent_path, $is_directory, $size, $modified,
                $created, $extension, $category, $scan_id)
            ON CONFLICT(path) DO UPDATE SET
                name = excluded.name,
                parent_path = excluded.parent_path,
                is_directory = excluded.is_directory,
                size = excluded.size,
                modified_utc_ticks = excluded.modified_utc_ticks,
                created_utc_ticks = excluded.created_utc_ticks,
                extension = excluded.extension,
                category = excluded.category,
                scan_id = excluded.scan_id
            """;
        command.Parameters.AddWithValue("$path", item.FullPath);
        command.Parameters.AddWithValue("$name", item.Name);
        command.Parameters.AddWithValue("$parent_path", Path.GetDirectoryName(item.FullPath) ?? string.Empty);
        command.Parameters.AddWithValue("$is_directory", item.IsDirectory ? 1 : 0);
        command.Parameters.AddWithValue("$size", item.Size is long size ? size : DBNull.Value);
        command.Parameters.AddWithValue("$modified", item.ModifiedAt.UtcTicks);
        command.Parameters.AddWithValue("$created", item.CreatedAt.UtcTicks);
        command.Parameters.AddWithValue("$extension", item.Extension);
        command.Parameters.AddWithValue("$category", (int)item.Category);
        command.Parameters.AddWithValue("$scan_id", scanId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeletePathAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string path)
    {
        var normalized = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefix = normalized + Path.DirectorySeparatorChar;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM entries
            WHERE path = $path COLLATE NOCASE
               OR lower(substr(path, 1, length($prefix))) = lower($prefix)
            """;
        command.Parameters.AddWithValue("$path", normalized);
        command.Parameters.AddWithValue("$prefix", prefix);
        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }

    private static async Task<int> CountAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM entries";
        return Convert.ToInt32(await command.ExecuteScalarAsync().ConfigureAwait(false));
    }

    private static IEnumerable<IndexChange> CollapseChanges(IEnumerable<IndexChange> changes) =>
        changes
            .GroupBy(change => (change.Kind, change.Path), IndexChangeKeyComparer.Instance)
            .Select(group => group.Last());

    private static IReadOnlyList<FileSystemItem> Sort(
        IEnumerable<FileSystemItem> source,
        FileQueryOptions options)
    {
        var comparer = NaturalStringComparer.OrdinalIgnoreCase;
        IOrderedEnumerable<FileSystemItem> ordered = options.SortField switch
        {
            FileSortField.ModifiedAt => source.OrderBy(item => item.ModifiedAt),
            FileSortField.CreatedAt => source.OrderBy(item => item.CreatedAt),
            FileSortField.Type => source.OrderBy(item => item.DisplayType, comparer),
            FileSortField.Size => source.OrderBy(item => item.Size ?? -1),
            _ => source.OrderBy(item => item.Name, comparer)
        };
        ordered = ordered.ThenBy(item => item.Name, comparer);
        return options.Direction == SortDirection.Descending
            ? ordered.Reverse().ToArray()
            : ordered.ToArray();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        await _changeTimer.DisposeAsync().ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _gate.Dispose();
    }

    private enum IndexChangeKind
    {
        Upsert,
        UpsertTree,
        Delete,
        Rebuild
    }

    private sealed record IndexChange(IndexChangeKind Kind, string Path);

    private sealed class IndexChangeKeyComparer : IEqualityComparer<(IndexChangeKind Kind, string Path)>
    {
        public static IndexChangeKeyComparer Instance { get; } = new();

        public bool Equals((IndexChangeKind Kind, string Path) x, (IndexChangeKind Kind, string Path) y) =>
            x.Kind == y.Kind && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((IndexChangeKind Kind, string Path) value) =>
            HashCode.Combine(value.Kind, StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }
}
