using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine;

public struct MeshRenderer: IEntityComponent
{
	public AssetLink<Material> MaterialAsset;
	public Material Material;
	public Mesh Mesh;

	public void AssignMaterialAsset(AssetLink<Material> materialAsset, RenderGraph renderGraph)
	{
		MaterialAsset = materialAsset;
		if (materialAsset.IsValid == false)
		{
			return;
		}

		Material = materialAsset.Asset;
		if (Material is not null)
		{
			renderGraph.EnsureMaterialResources(Material);
		}
	}

	public void AssignRuntimeMaterial(Material material, RenderGraph renderGraph)
	{
		MaterialAsset = default;
		Material = material;
		if (material is not null)
		{
			renderGraph.EnsureMaterialResources(material);
		}
	}

	public bool TryValidate()
	{
		if (Mesh == null)
		{
			return false; // TODO: Try link mesh asset
		}

		return IsValid;
	}

	public bool IsValid => Material != null && Mesh != null;
	
}
