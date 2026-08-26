# Optional read-only MCP adapter

`ProjectFileHub.McpServer` is a separate STDIO process. Project File Hub never launches it, and the desktop application does not depend on it.

The adapter exposes four read-only tools for the one active registered project:

- `get_active_project`
- `list_project_files`
- `search_project_files`
- `read_project_text`

Every path is resolved through the same project-root boundary used by the desktop application. Reparse points, symbolic links, directory junctions, binary reads, and paths outside the active project are rejected. No MCP tool can create, rename, move, copy, or delete files.

## Build output

After a Debug build, the standalone server executable is located at:

`src/ProjectFileHub.McpServer/bin/Debug/net10.0-windows10.0.22621.0/win-x64/ProjectFileHub.McpServer.exe`

## Optional Codex configuration

Do not add this entry unless MCP access is wanted. The example is deliberately disabled:

```toml
[mcp_servers.project_file_hub]
command = "C:/path/to/Project-File-Hub/src/ProjectFileHub.McpServer/bin/Debug/net10.0-windows10.0.22621.0/win-x64/ProjectFileHub.McpServer.exe"
enabled = false
required = false
enabled_tools = ["get_active_project", "list_project_files", "search_project_files", "read_project_text"]
default_tools_approval_mode = "prompt"
```

The ChatGPT desktop app, Codex CLI, and Codex IDE extension support local STDIO MCP servers on the same Codex host. See the [official OpenAI MCP documentation](https://developers.openai.com/codex/mcp/).
