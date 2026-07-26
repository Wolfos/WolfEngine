using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace WolfEngine;

public struct MeshRenderer: IEntityComponent, IJsonOnDeserialized
{
	public AssetRef<Mesh> MeshAsset;
	public AssetRef<Material> MaterialAsset;
	// Null until the asset references are resolved; see IsValid / TryValidate.
	[JsonIgnore]
	public Material? Material;
	[JsonIgnore]
	public Mesh? Mesh;

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

	[MemberNotNullWhen(true, nameof(Mesh), nameof(Material))]
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

	public void RefreshResolvedAssets(RenderGraph renderGraph)
	{
		ArgumentNullException.ThrowIfNull(renderGraph);

		Mesh = MeshAsset.IsValid ? MeshAsset.Asset : null;
		Material = MaterialAsset.IsValid ? MaterialAsset.Asset : null;
		if (Material is not null)
		{
			renderGraph.EnsureMaterialResources(Material);
		}
	}

	[MemberNotNullWhen(true, nameof(Material), nameof(Mesh))]
	public bool IsValid => Material != null && Mesh != null;

	public void OnDeserialized()
	{
		Mesh = MeshAsset.IsValid ? MeshAsset.Asset : null;
		Material = MaterialAsset.IsValid ? MaterialAsset.Asset : null;
	}
}
