using System.Text;
using ProjectFileHub.McpServer;

Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var registryPath = Path.Combine(localData, "ProjectFileHub", "projects.json");
var server = new ReadOnlyMcpServer(registryPath);
await server.RunAsync(Console.In, Console.Out, CancellationToken.None);
