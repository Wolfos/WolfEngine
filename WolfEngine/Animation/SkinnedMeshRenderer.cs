using System.Diagnostics.CodeAnalysis;
using System.Numerics;
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
    public SkinnedMeshRenderer() { }
	public AssetRef<Mesh> MeshAsset;
	public AssetRef<Material> MaterialAsset;
	public AssetRef<Skeleton> SkeletonAsset;

	/// <summary>Entity carrying the driving <see cref="Animator"/>. Defaults to this entity when unset.</summary>
	public Entity AnimatorEntity;

    /// <summary>Imported mesh bind coordinates to the skeleton's model coordinates.</summary>
    public Matrix4x4? MeshBindToRig;
    [JsonIgnore] internal Matrix4x4[] LocalSkinningMatrices = [];
    [JsonIgnore] internal Matrix4x4[] PreviousLocalSkinningMatrices = [];
    [JsonIgnore] internal uint LocalPoseGeneration;
    [JsonIgnore] private uint _animatorPoseGeneration;
    [JsonIgnore] private Matrix4x4 _cachedBindToRig;
    [JsonIgnore] private Matrix4x4 _cachedRigToMesh;
    [JsonIgnore] private Matrix4x4? _capturedBindToRig;
    [JsonIgnore] private bool _paletteInitialized;
    [JsonIgnore] private Matrix4x4[]? _sourcePalette;

    internal void PrepareSkinningPalette(Matrix4x4[] current, Matrix4x4[] previous,
        uint generation, in Matrix4x4 fallbackBindToRig)
    {
        _capturedBindToRig ??= fallbackBindToRig;
        var bind = MeshBindToRig ?? _capturedBindToRig.Value;
        if (_paletteInitialized && ReferenceEquals(current, _sourcePalette) && generation == _animatorPoseGeneration && bind == _cachedBindToRig && LocalSkinningMatrices.Length == current.Length) return;
        if (!_paletteInitialized || bind != _cachedBindToRig)
        {
            if (!Matrix4x4.Invert(bind, out _cachedRigToMesh))
                throw new InvalidOperationException("Skinned mesh bind transform must be invertible.");
        }
        var rigToMesh = _cachedRigToMesh;
        if (LocalSkinningMatrices is null || LocalSkinningMatrices.Length != current.Length)
        {
            LocalSkinningMatrices = new Matrix4x4[current.Length];
            PreviousLocalSkinningMatrices = new Matrix4x4[current.Length];
        }
        for (var bone = 0; bone < current.Length; bone++)
        {
            // Row vectors: mesh -> rig -> deformed rig -> mesh. The draw transform then
            // places mesh-local output in the scene. Sockets keep using rig-space poses.
            LocalSkinningMatrices[bone] = bind * current[bone] * rigToMesh;
            PreviousLocalSkinningMatrices[bone] = bind * previous[bone] * rigToMesh;
        }
        _sourcePalette = current;
        _animatorPoseGeneration = generation; _cachedBindToRig = bind; _paletteInitialized = true;
        LocalPoseGeneration++;
    }
    private void ResetSkinningPalette()
    {
        LocalSkinningMatrices = []; PreviousLocalSkinningMatrices = [];
        _capturedBindToRig = null; _sourcePalette = null; _paletteInitialized = false; LocalPoseGeneration = 0;
    }

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
        ResetSkinningPalette();
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
        ResetSkinningPalette();
		if (BoundsExpansion <= 0.0f)
		{
			BoundsExpansion = DefaultBoundsExpansion;
		}
	}
}
