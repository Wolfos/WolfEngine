using System.Text.Json.Serialization;

namespace Wolfie.IAE.ManagedAssets;

public sealed class ManagedAssetRecord
{
	public const int CurrentVersion = 1;

	public int Version { get; set; } = CurrentVersion;
	public Guid SourceId { get; set; }
	public string ImporterId { get; set; } = "texture";
	public int ImporterVersion { get; set; } = 1;
	public string ImportSettingsJson { get; set; } = "{}";
	public List<ManagedSubAssetRecord> SubAssets { get; set; } = [];
	public string SourcePath { get; set; } = string.Empty;
	public List<ManagedOutputRecord> Outputs { get; set; } = [];
}

public sealed class ManagedSubAssetRecord
{
	public string Key { get; set; } = string.Empty;
	public Guid NodeId { get; set; }
	public string Type { get; set; } = string.Empty;
	public string Name { get; set; } = string.Empty;
}

public sealed class ManagedOutputRecord
{
	public string Path { get; set; } = string.Empty;
	[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
	public string? UnityGuid { get; set; }
}
