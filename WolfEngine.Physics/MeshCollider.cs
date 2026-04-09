using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine.Physics;

public struct MeshCollider : IEntityComponent, IJsonOnDeserialized
{
	public AssetRef<Mesh> MeshAsset;
	[JsonIgnore]
	public Mesh? Mesh;
	[NotSerialized]
	[HideFromEditor]
	internal bool PhysicsCacheValid;
	[NotSerialized]
	[HideFromEditor]
	internal Guid CachedMeshAssetId;
	[NotSerialized]
	[HideFromEditor]
	internal Mesh? CachedMesh;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedLayer;
	[NotSerialized]
	[HideFromEditor]
	internal uint CachedCollidesWith;

	public static MeshCollider CreateDefault()
	{
		return new MeshCollider
		{
			PhysicsCacheValid = false
		};
	}

	public void AssignMeshAsset(AssetRef<Mesh> meshAsset)
	{
		MeshAsset = meshAsset;
		Mesh = meshAsset.IsValid ? meshAsset.Asset : null;
		PhysicsCacheValid = false;
	}

	public void ApplyDefaultValues(World world, Entity entity)
	{
		PhysicsCacheValid = false;

		if (world.HasComponent<MeshRenderer>(entity) == false)
		{
			return;
		}

		var meshRenderer = world.GetComponent<MeshRenderer>(entity);
		MeshAsset = meshRenderer.MeshAsset;
		Mesh = meshRenderer.Mesh ?? (meshRenderer.MeshAsset.IsValid ? meshRenderer.MeshAsset.Asset : null);
	}

	public bool TryValidate()
	{
		if (Mesh == null && MeshAsset.IsValid)
		{
			Mesh = MeshAsset.Asset;
		}

		return Mesh != null;
	}

	public void OnDeserialized()
	{
		Mesh = MeshAsset.IsValid ? MeshAsset.Asset : null;
		PhysicsCacheValid = false;
	}
}
