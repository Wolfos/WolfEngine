#nullable enable

namespace WolfEngine.AssetPipeline;

public struct AssetRef<T>
{
	public Guid NodeId { get; set; }

	public T? Asset => AssetDatabase.GetInstance<T>(NodeId);
	public bool IsValid => NodeId != Guid.Empty;
}
