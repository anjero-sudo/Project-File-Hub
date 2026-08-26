using System.Text.Json.Serialization;

namespace ProjectFileHub.Core.Models;

public sealed record ProjectRegistryState
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public long Revision { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public List<RegisteredProject> Projects { get; init; } = [];

    public Guid? ActiveProjectId { get; init; }

    [JsonIgnore]
    public RegisteredProject? ActiveProject =>
        ActiveProjectId is Guid id
            ? Projects.FirstOrDefault(project => project.Id == id)
            : null;
}
