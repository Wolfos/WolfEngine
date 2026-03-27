using System.Text.Json.Serialization;

#nullable enable

namespace WolfEngine.AssetPipeline;

public struct AssetRef<T>
{
	public Guid NodeId { get; set; }

	[JsonIgnore]
	public T? Asset => AssetDatabase.GetInstance<T>(NodeId);
	[JsonIgnore]
	public bool IsValid => NodeId != Guid.Empty;
}
