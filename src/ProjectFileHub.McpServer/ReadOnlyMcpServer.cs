using System.Text.Json;
using System.Text.Json.Nodes;
using ProjectFileHub.Core;
using ProjectFileHub.Core.Models;
using ProjectFileHub.Core.Services;

namespace ProjectFileHub.McpServer;

public sealed class ReadOnlyMcpServer
{
    private const string ServerName = "project-file-hub";
    private static readonly string ServerVersion =
        typeof(ReadOnlyMcpServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    private const string FallbackProtocolVersion = "2025-06-18";
    private const string Instructions =
        "Read-only access to the one active project explicitly registered in Project File Hub. "
        + "Every path is constrained to that project root; symbolic links and directory junctions are rejected. "
        + "Use get_active_project before file tools when project context is unclear. No tool writes, renames, moves, or deletes files.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly ProjectRegistryStore _registryStore;
    private readonly FileSystemBrowser _browser = new();

    public ReadOnlyMcpServer(string registryPath)
    {
        _registryStore = new ProjectRegistryStore(registryPath);
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonObject? request;
            try
            {
                request = JsonNode.Parse(line) as JsonObject;
            }
            catch (JsonException exception)
            {
                await WriteErrorAsync(output, null, -32700, $"Invalid JSON: {exception.Message}").ConfigureAwait(false);
                continue;
            }

            if (request is null)
            {
                await WriteErrorAsync(output, null, -32600, "Request must be a JSON object.").ConfigureAwait(false);
                continue;
            }

            var id = request["id"]?.DeepClone();
            var method = request["method"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(method))
            {
                if (id is not null)
                {
                    await WriteErrorAsync(output, id, -32600, "Request method is required.").ConfigureAwait(false);
                }

                continue;
            }

            if (id is null)
            {
                // MCP lifecycle and cancellation notifications do not receive responses.
                continue;
            }

            try
            {
                var result = method switch
                {
                    "initialize" => BuildInitializeResult(request["params"] as JsonObject),
                    "ping" => new JsonObject(),
                    "tools/list" => BuildToolsList(),
                    "tools/call" => await CallToolAsync(request["params"] as JsonObject, cancellationToken).ConfigureAwait(false),
                    _ => null
                };

                if (result is null)
                {
                    await WriteErrorAsync(output, id, -32601, $"Method not found: {method}").ConfigureAwait(false);
                }
                else
                {
                    await WriteResultAsync(output, id, result).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                await WriteErrorAsync(output, id, -32603, exception.Message).ConfigureAwait(false);
            }
        }
    }

    private static JsonObject BuildInitializeResult(JsonObject? parameters)
    {
        var requestedProtocol = parameters?["protocolVersion"]?.GetValue<string>();
        return new JsonObject
        {
            ["protocolVersion"] = string.IsNullOrWhiteSpace(requestedProtocol)
                ? FallbackProtocolVersion
                : requestedProtocol,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject { ["listChanged"] = false }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion
            },
            ["instructions"] = Instructions
        };
    }

    private static JsonObject BuildToolsList() => new()
    {
        ["tools"] = new JsonArray
        {
            Tool(
                "get_active_project",
                "Get active Project File Hub project",
                "Returns the one project currently active in Project File Hub. Use before other tools when project context is unclear.",
                ObjectSchema()),
            Tool(
                "list_project_files",
                "List project files",
                "Lists a folder inside the active project. Can optionally include descendants while preserving the project-root boundary.",
                ObjectSchema(
                    ("path", StringProperty("Project-relative folder path. Omit or use '.' for the project root.")),
                    ("recursive", BooleanProperty("Include descendants. Defaults to false.")),
                    ("category", CategoryProperty()),
                    ("limit", IntegerProperty("Maximum results from 1 to 500. Defaults to 200.", 1, 500)))),
            Tool(
                "search_project_files",
                "Search project file names",
                "Searches file and folder names across the active project. It never reads outside the registered project root.",
                ObjectSchemaRequired(
                    ["query"],
                    ("query", StringProperty("Case-insensitive filename fragment to find.")),
                    ("category", CategoryProperty()),
                    ("limit", IntegerProperty("Maximum results from 1 to 500. Defaults to 100.", 1, 500)))),
            Tool(
                "read_project_text",
                "Read a project text file",
                "Reads a bounded amount of a known text or code file inside the active project. Binary files and reparse points are rejected.",
                ObjectSchemaRequired(
                    ["path"],
                    ("path", StringProperty("Project-relative path of the text file.")),
                    ("maxChars", IntegerProperty("Maximum characters from 1 to 200000. Defaults to 40000.", 1, 200000))))
        }
    };

    private async Task<JsonObject> CallToolAsync(JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>();
        var arguments = parameters?["arguments"] as JsonObject ?? new JsonObject();
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolError("Tool name is required.");
        }

        try
        {
            var structured = name switch
            {
                "get_active_project" => await GetActiveProjectAsync(cancellationToken).ConfigureAwait(false),
                "list_project_files" => await ListProjectFilesAsync(arguments, cancellationToken).ConfigureAwait(false),
                "search_project_files" => await SearchProjectFilesAsync(arguments, cancellationToken).ConfigureAwait(false),
                "read_project_text" => await ReadProjectTextAsync(arguments, cancellationToken).ConfigureAwait(false),
                _ => throw new KeyNotFoundException($"Unknown tool: {name}")
            };

            var summary = name switch
            {
                "get_active_project" => $"Active project: {structured["name"]}",
                "read_project_text" => $"Read {structured["characterCount"]} characters from {structured["path"]}.",
                _ => $"Found {structured["count"]} project items."
            };
            return ToolSuccess(summary, structured);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return ToolError(exception.Message);
        }
    }

    private async Task<JsonObject> GetActiveProjectAsync(CancellationToken cancellationToken)
    {
        var project = await RequireActiveProjectAsync(cancellationToken).ConfigureAwait(false);
        return new JsonObject
        {
            ["id"] = project.Id.ToString("D"),
            ["name"] = project.Name,
            ["rootPath"] = project.RootPath,
            ["exists"] = Directory.Exists(project.RootPath)
        };
    }

    private async Task<JsonObject> ListProjectFilesAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var project = await RequireActiveProjectAsync(cancellationToken).ConfigureAwait(false);
        var boundary = new PathBoundary(project.RootPath);
        var relativePath = GetOptionalString(arguments, "path") ?? ".";
        var folder = ResolveInsideProject(boundary, relativePath, requireDirectory: true);
        var recursive = GetOptionalBoolean(arguments, "recursive") ?? false;
        var category = ParseCategory(GetOptionalString(arguments, "category"));
        var limit = GetBoundedInt(arguments, "limit", 200, 1, 500);

        var items = EnumerateProjectItems(boundary, folder, recursive, category, limit);
        return ItemsResult(project, items);
    }

    private async Task<JsonObject> SearchProjectFilesAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var project = await RequireActiveProjectAsync(cancellationToken).ConfigureAwait(false);
        var query = GetRequiredString(arguments, "query");
        var category = ParseCategory(GetOptionalString(arguments, "category"));
        var limit = GetBoundedInt(arguments, "limit", 100, 1, 500);
        var boundary = new PathBoundary(project.RootPath);

        var items = EnumerateProjectItems(boundary, project.RootPath, recursive: true, category, limit, query);
        return ItemsResult(project, items);
    }

    private async Task<JsonObject> ReadProjectTextAsync(JsonObject arguments, CancellationToken cancellationToken)
    {
        var project = await RequireActiveProjectAsync(cancellationToken).ConfigureAwait(false);
        var relativePath = GetRequiredString(arguments, "path");
        var maximumCharacters = GetBoundedInt(arguments, "maxChars", 40_000, 1, 200_000);
        var boundary = new PathBoundary(project.RootPath);
        var path = ResolveInsideProject(boundary, relativePath, requireDirectory: false);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The requested project file does not exist.", path);
        }

        if (!IsTextExtension(Path.GetExtension(path)))
        {
            throw new InvalidOperationException("Only known text, document-source, and code file types can be read.");
        }

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maximumCharacters + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        var truncated = read > maximumCharacters;
        var textLength = Math.Min(read, maximumCharacters);
        var text = new string(buffer, 0, textLength);
        var relative = NormalizeRelative(project.RootPath, path);
        return new JsonObject
        {
            ["path"] = relative,
            ["characterCount"] = textLength,
            ["truncated"] = truncated,
            ["text"] = text
        };
    }

    private IReadOnlyList<FileSystemItem> EnumerateProjectItems(
        PathBoundary boundary,
        string startFolder,
        bool recursive,
        FileItemCategory? category,
        int limit,
        string? nameQuery = null)
    {
        var results = new List<FileSystemItem>(Math.Min(limit, 500));
        var folders = new Queue<string>();
        folders.Enqueue(startFolder);

        while (folders.Count > 0 && results.Count < limit)
        {
            var folder = folders.Dequeue();
            var children = _browser.GetItems(boundary.RootPath, folder, new FileQueryOptions());
            foreach (var item in children)
            {
                if (recursive && item.IsDirectory)
                {
                    folders.Enqueue(item.FullPath);
                }

                if (category is not null && item.Category != category)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(nameQuery)
                    && !item.Name.Contains(nameQuery, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                results.Add(item);
                if (results.Count >= limit)
                {
                    break;
                }
            }

            if (!recursive)
            {
                break;
            }
        }

        return results;
    }

    private static JsonObject ItemsResult(RegisteredProject project, IReadOnlyList<FileSystemItem> items)
    {
        var values = new JsonArray();
        foreach (var item in items)
        {
            values.Add(new JsonObject
            {
                ["name"] = item.Name,
                ["path"] = NormalizeRelative(project.RootPath, item.FullPath),
                ["kind"] = item.IsDirectory ? "folder" : "file",
                ["category"] = item.Category.ToString().ToLowerInvariant(),
                ["size"] = item.Size,
                ["modifiedAt"] = item.ModifiedAt.ToString("O")
            });
        }

        return new JsonObject
        {
            ["projectId"] = project.Id.ToString("D"),
            ["projectName"] = project.Name,
            ["count"] = items.Count,
            ["items"] = values
        };
    }

    private async Task<RegisteredProject> RequireActiveProjectAsync(CancellationToken cancellationToken)
    {
        var state = await _registryStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        var project = state.ActiveProject
            ?? throw new InvalidOperationException("Project File Hub has no active registered project.");
        if (!Directory.Exists(project.RootPath))
        {
            throw new DirectoryNotFoundException($"The active project root no longer exists: {project.RootPath}");
        }

        var boundary = new PathBoundary(project.RootPath);
        boundary.EnsureSafe(project.RootPath);
        return project;
    }

    private static string ResolveInsideProject(PathBoundary boundary, string relativePath, bool requireDirectory)
    {
        var candidate = Path.GetFullPath(Path.Combine(boundary.RootPath, relativePath));
        var safe = boundary.EnsureSafe(candidate);
        if (requireDirectory && !Directory.Exists(safe))
        {
            throw new DirectoryNotFoundException($"Project folder does not exist: {relativePath}");
        }

        return safe;
    }

    private static FileItemCategory? ParseCategory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Enum.TryParse<FileItemCategory>(value, ignoreCase: true, out var category)
            ? category
            : throw new ArgumentException("Unknown category. Use folder, image, video, audio, document, code, archive, other, or all.");
    }

    private static JsonObject Tool(string name, string title, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["title"] = title,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
        ["annotations"] = new JsonObject
        {
            ["readOnlyHint"] = true,
            ["destructiveHint"] = false,
            ["openWorldHint"] = false
        }
    };

    private static JsonObject ObjectSchema(
        params (string Name, JsonObject Schema)[] properties) => ObjectSchema(properties, []);

    private static JsonObject ObjectSchemaRequired(
        string[] required,
        params (string Name, JsonObject Schema)[] properties) => ObjectSchema(properties, required);

    private static JsonObject ObjectSchema(
        (string Name, JsonObject Schema)[] properties,
        string[] required)
    {
        var propertyObject = new JsonObject();
        foreach (var property in properties)
        {
            propertyObject[property.Name] = property.Schema;
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = propertyObject,
            ["additionalProperties"] = false
        };
        if (required.Length > 0)
        {
            schema["required"] = new JsonArray(required.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        }

        return schema;
    }

    private static JsonObject StringProperty(string description) => new()
    {
        ["type"] = "string",
        ["description"] = description
    };

    private static JsonObject BooleanProperty(string description) => new()
    {
        ["type"] = "boolean",
        ["description"] = description
    };

    private static JsonObject IntegerProperty(string description, int minimum, int maximum) => new()
    {
        ["type"] = "integer",
        ["description"] = description,
        ["minimum"] = minimum,
        ["maximum"] = maximum
    };

    private static JsonObject CategoryProperty() => new()
    {
        ["type"] = "string",
        ["description"] = "Optional file category filter.",
        ["enum"] = new JsonArray("all", "folder", "image", "video", "audio", "document", "code", "archive", "other")
    };

    private static JsonObject ToolSuccess(string summary, JsonObject structuredContent) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = summary }),
        ["structuredContent"] = structuredContent,
        ["isError"] = false
    };

    private static JsonObject ToolError(string message) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
        ["isError"] = true
    };

    private static string GetRequiredString(JsonObject arguments, string name) =>
        GetOptionalString(arguments, name) is { Length: > 0 } value
            ? value
            : throw new ArgumentException($"Argument '{name}' is required.");

    private static string? GetOptionalString(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<string>();

    private static bool? GetOptionalBoolean(JsonObject arguments, string name) =>
        arguments[name]?.GetValue<bool>();

    private static int GetBoundedInt(JsonObject arguments, string name, int defaultValue, int minimum, int maximum)
    {
        var value = arguments[name]?.GetValue<int>() ?? defaultValue;
        if (value < minimum || value > maximum)
        {
            throw new ArgumentOutOfRangeException(name, $"Value must be between {minimum} and {maximum}.");
        }

        return value;
    }

    private static bool IsTextExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".c" or ".cpp" or ".cs" or ".css" or ".csv" or ".go" or ".h" or ".hpp" or ".html" or
        ".java" or ".js" or ".json" or ".jsx" or ".kt" or ".lua" or ".md" or ".php" or ".ps1" or
        ".py" or ".rb" or ".rs" or ".sql" or ".swift" or ".ts" or ".tsx" or ".txt" or ".xml" or
        ".xaml" or ".yaml" or ".yml" => true,
        _ => false
    };

    private static string NormalizeRelative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');

    private static async Task WriteResultAsync(TextWriter output, JsonNode id, JsonObject result) =>
        await WriteMessageAsync(output, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        }).ConfigureAwait(false);

    private static async Task WriteErrorAsync(TextWriter output, JsonNode? id, int code, string message) =>
        await WriteMessageAsync(output, new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = code, ["message"] = message }
        }).ConfigureAwait(false);

    private static async Task WriteMessageAsync(TextWriter output, JsonObject message)
    {
        await output.WriteLineAsync(message.ToJsonString(JsonOptions)).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}
