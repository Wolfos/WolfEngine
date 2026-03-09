#nullable enable

namespace WolfEngine.AssetPipeline;

public struct AssetLink<T>
{
	public Guid Id;

	public T? Asset => AssetDatabase.GetInstance<T>(Id);
	public bool IsValid => Id != Guid.Empty;
}
