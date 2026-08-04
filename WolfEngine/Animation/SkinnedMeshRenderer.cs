using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Animation;

/// <summary>
/// Draws a mesh deformed by an <see cref="Animator"/>'s pose.
/// </summary>
/// <remarks>
/// Each renderer owns a private copy of the mesh's GPU vertex range, which the skinning compute
/// pass writes every frame. That costs memory per instance, but it is what makes a skinned
/// character real geometry rather than a vertex-shader illusion — which in turn is what lets it
/// appear correctly in ray-traced reflections and in DDGI, and lets it reuse the existing culling
/// and indirect-draw path unchanged.
/// </remarks>
public struct SkinnedMeshRenderer : IEntityComponent, IJsonOnDeserialized
{
	public AssetRef<Mesh> MeshAsset;
	public AssetRef<Material> MaterialAsset;
	public AssetRef<Skeleton> SkeletonAsset;

	/// <summary>Entity carrying the driving <see cref="Animator"/>. Defaults to this entity when unset.</summary>
	public Entity AnimatorEntity;

	/// <summary>
	/// Multiplier on the bind-pose bounds used for culling. A deformed pose reaches outside the
	/// bind pose, and exact bounds are not known until skinning has already run on the GPU.
	/// </summary>
	public float BoundsExpansion;

	[JsonIgnore] public Material? Material;

	/// <summary>The shared bind-pose mesh, including its skin influences.</summary>
	[JsonIgnore] public Mesh? Mesh;

	/// <summary>This instance's deformed geometry. Created lazily by the renderer.</summary>
	[JsonIgnore] public Mesh? SkinnedInstance;

	[JsonIgnore] public Skeleton? Skeleton;

	public static SkinnedMeshRenderer Create(
		AssetRef<Mesh> mesh,
		AssetRef<Material> material,
		AssetRef<Skeleton> skeleton,
		Entity animatorEntity) =>
		new()
		{
			MeshAsset = mesh,
			MaterialAsset = material,
			SkeletonAsset = skeleton,
			AnimatorEntity = animatorEntity,
			BoundsExpansion = DefaultBoundsExpansion,
			Mesh = mesh.IsValid ? mesh.Asset : null,
			Material = material.IsValid ? material.Asset : null,
			Skeleton = skeleton.IsValid ? skeleton.Asset : null
		};

	public const float DefaultBoundsExpansion = 1.5f;

	[MemberNotNullWhen(true, nameof(Mesh), nameof(Material))]
	public bool TryValidate()
	{
		Mesh ??= MeshAsset.IsValid ? MeshAsset.Asset : null;
		Material ??= MaterialAsset.IsValid ? MaterialAsset.Asset : null;
		Skeleton ??= SkeletonAsset.IsValid ? SkeletonAsset.Asset : null;

		if (BoundsExpansion <= 0.0f)
		{
			BoundsExpansion = DefaultBoundsExpansion;
		}

		return Mesh is not null && Material is not null && Mesh.IsSkinned;
	}

	public void RefreshResolvedAssets(RenderGraph renderGraph)
	{
		ArgumentNullException.ThrowIfNull(renderGraph);

		Mesh = MeshAsset.IsValid ? MeshAsset.Asset : null;
		Material = MaterialAsset.IsValid ? MaterialAsset.Asset : null;
		Skeleton = SkeletonAsset.IsValid ? SkeletonAsset.Asset : null;
		SkinnedInstance = null;
		if (Material is not null)
		{
			renderGraph.EnsureMaterialResources(Material);
		}
	}

	public void OnDeserialized()
	{
		Mesh = MeshAsset.IsValid ? MeshAsset.Asset : null;
		Material = MaterialAsset.IsValid ? MaterialAsset.Asset : null;
		Skeleton = SkeletonAsset.IsValid ? SkeletonAsset.Asset : null;
		SkinnedInstance = null;
		if (BoundsExpansion <= 0.0f)
		{
			BoundsExpansion = DefaultBoundsExpansion;
		}
	}
}
