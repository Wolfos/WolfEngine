using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine;

public struct MeshRenderer: IEntityComponent
{
	public AssetRef<Mesh> MeshAsset;
	public AssetRef<Material> MaterialAsset;
	public Material Material;
	public Mesh Mesh;

	public void AssignMeshAsset(AssetRef<Mesh> meshAsset)
	{
		MeshAsset = meshAsset;
		Mesh = meshAsset.IsValid ? meshAsset.Asset : null;
	}

	public void AssignMaterialAsset(AssetRef<Material> materialAsset, RenderGraph renderGraph)
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
		if (Mesh == null && MeshAsset.IsValid)
		{
			Mesh = MeshAsset.Asset;
		}

		if (Material == null && MaterialAsset.IsValid)
		{
			Material = MaterialAsset.Asset;
		}

		if (Mesh == null)
		{
			return false; // TODO: Try link mesh asset
		}

		return IsValid;
	}

	public bool IsValid => Material != null && Mesh != null;
	
}
