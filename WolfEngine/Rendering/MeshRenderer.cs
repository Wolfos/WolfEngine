using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine;

public struct MeshRenderer: IEntityComponent
{
	public AssetLink<Material> MaterialAsset;
	public Material Material;
	public Mesh Mesh;

	public bool TryValidate()
	{
		if (Mesh == null)
		{
			return false; // TODO: Try link mesh asset
		}

		if ((Material == null || Material != MaterialAsset.Asset) && MaterialAsset.IsValid)
		{
			Material = MaterialAsset.Asset;
		}

		return IsValid;
	}

	public bool IsValid => Material != null && Mesh != null;
}