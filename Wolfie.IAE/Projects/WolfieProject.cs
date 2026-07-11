using System.Text.Json.Serialization;

namespace Wolfie.IAE.Projects;

public sealed record WolfieProject
{
	public const int CurrentFormatVersion = 1;

	[JsonPropertyName("formatVersion")]
	public int FormatVersion { get; init; } = CurrentFormatVersion;

	[JsonPropertyName("projectId")]
	public Guid ProjectId { get; init; } = Guid.NewGuid();

	[JsonPropertyName("name")]
	public required string Name { get; init; }

	[JsonPropertyName("unityProjectPath")]
	public required string UnityProjectPath { get; init; }
}
