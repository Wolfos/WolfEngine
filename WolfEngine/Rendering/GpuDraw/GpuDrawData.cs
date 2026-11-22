using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine.Rendering;

public enum GpuDrawKind : uint
{
	Mesh = 0,
	DebugPrimitive = 1,
	Terrain = 2
}

/// <summary>
/// Layout of <c>GpuDrawCommand.flags</c>. Mirrored by the <c>DRAW_FLAG_*</c> constants in
/// <c>gpu_draw_cull.compute.slang</c>; change them together.
/// </summary>
public static class GpuDrawFlags
{
	public const uint Active = 1u << 0;

	// Execution lane (bucket). Five bits: the cull's participation mask caps lanes at 32.
	public const int BucketShift = 1;
	public const uint BucketMask = 0x1Fu;

	// Slot of the render view whose world owns the draw. Every view's draws live in one shared command table,
	// and each view's cull skips commands owned by another view. Seven bits cover the 64 view slots.
	public const int OwnerViewShift = 6;
	public const uint OwnerViewMask = 0x7Fu;

	public static uint Create(int bucketIndex, RenderViewId ownerView)
	{
		if ((uint)bucketIndex > BucketMask)
		{
			throw new ArgumentOutOfRangeException(nameof(bucketIndex), bucketIndex, $"Buckets must be below {BucketMask + 1}.");
		}

		if (ownerView.IsValid == false || (uint)ownerView.Index > OwnerViewMask)
		{
			throw new ArgumentOutOfRangeException(nameof(ownerView), ownerView, "Draw owner must be a valid view slot.");
		}

		return Active | ((uint)bucketIndex << BucketShift) | ((uint)ownerView.Index << OwnerViewShift);
	}

	public static RenderViewId GetOwnerView(uint flags) =>
		RenderViewId.FromIndex((int)((flags >> OwnerViewShift) & OwnerViewMask));

	public static int GetBucket(uint flags) => (int)((flags >> BucketShift) & BucketMask);
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuInstanceData
{
	public GpuInstanceData(
		Matrix4x4 previousWorld,
		Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Vector4 terrainChunkOriginSize,
		Vector4 terrainHeightmapUvScaleOffset,
		uint materialHandle,
		uint meshHandle,
		uint drawKind,
		uint flags)
	{
		PreviousWorld = previousWorld;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
		TerrainChunkOriginSize = terrainChunkOriginSize;
		TerrainHeightmapUvScaleOffset = terrainHeightmapUvScaleOffset;
		MaterialHandle = materialHandle;
		MeshHandle = meshHandle;
		DrawKind = drawKind;
		Flags = flags;
	}

	public readonly Matrix4x4 PreviousWorld;
	public readonly Matrix4x4 World;
	public readonly Vector4 BoundsCenterRadius;
	public readonly Vector4 TerrainChunkOriginSize;
	public readonly Vector4 TerrainHeightmapUvScaleOffset;
	public readonly uint MaterialHandle;
	public readonly uint MeshHandle;
	public readonly uint DrawKind;
	public readonly uint Flags;

	/// <summary>A <see cref="LodGroup"/>'s bounds in world space, which a grouped instance's level is measured on.</summary>
	public readonly Vector4 LodSphere;

	/// <summary>Packed by <see cref="GpuDrawLodParams"/>; zero for an instance outside any group.</summary>
	public readonly uint LodParams;
	private readonly uint _lodPad0;
	private readonly uint _lodPad1;
	private readonly uint _lodPad2;
}

/// <summary>
/// Packs a grouped instance's level of detail parameters into one word. Mirrors the LOD_PARAMS_ constants in
/// draw_data.slang.
/// </summary>
public static class GpuDrawLodParams
{
	public const uint GroupedBit = 1u;
	public const int FirstLevelShift = 1;
	public const int LastLevelShift = 4;
	public const int ThresholdSetShift = 7;
	public const uint LevelMask = 0x7u;

	/// <summary>Threshold sets a frame can name; the level table reserves this many entries for them.</summary>
	public const int MaxThresholdSetCount = 4096;

	public static uint Pack(int thresholdSetIndex, int firstLevel, int lastLevel)
	{
		return GroupedBit |
		       ((uint)Math.Clamp(firstLevel, 0, GpuDrawLod.MaxLevel) << FirstLevelShift) |
		       ((uint)Math.Clamp(lastLevel, 0, GpuDrawLod.MaxLevel) << LastLevelShift) |
		       ((uint)thresholdSetIndex << ThresholdSetShift);
	}
}

/// <summary>
/// Bits of <see cref="GpuMaterialData.MaterialFlags"/>. Mirrors the GPU_MATERIAL_FLAG_ constants in draw_data.slang.
/// </summary>
public static class GpuMaterialFlags
{
	/// <summary>
	/// The material renders both faces. Ray-traced hits on its back face are shaded as front faces, the way
	/// rasterisation flips the normal, rather than being treated as the inside of a closed surface.
	/// </summary>
	public const uint DoubleSided = 1u << 0;

	public static uint FromMaterial(Material material)
	{
		var flags = 0u;
		if (material.DoubleSided)
		{
			flags |= DoubleSided;
		}

		return flags;
	}
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuMaterialData
{
	public GpuMaterialData(
		ColorRGBA baseColor,
		Vector4 metallicRoughness,
		Vector4 emissiveFactorIntensity,
		Vector4 uvOffsetScale,
		uint albedoHandle,
		uint ormHandle,
		uint normalHandle,
		uint emissiveHandle,
		uint samplerHandle,
		uint materialFlags = 0u)
	{
		BaseColor = baseColor;
		MetallicRoughness = metallicRoughness;
		EmissiveFactorIntensity = emissiveFactorIntensity;
		UvOffsetScale = uvOffsetScale;
		AlbedoHandle = albedoHandle;
		OrmHandle = ormHandle;
		NormalHandle = normalHandle;
		EmissiveHandle = emissiveHandle;
		SamplerHandle = samplerHandle;
		MaterialFlags = materialFlags;
		_pad1 = 0;
		_pad2 = 0;
	}

	public readonly ColorRGBA BaseColor;
	public readonly Vector4 MetallicRoughness;
	public readonly Vector4 EmissiveFactorIntensity;
	public readonly Vector4 UvOffsetScale;
	public readonly uint AlbedoHandle;
	public readonly uint OrmHandle;
	public readonly uint NormalHandle;
	public readonly uint EmissiveHandle;
	public readonly uint SamplerHandle;

	/// <summary>Bits from <see cref="GpuMaterialFlags"/>.</summary>
	public readonly uint MaterialFlags;
	private readonly uint _pad1;
	private readonly uint _pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuTerrainMaterialData
{
	public GpuTerrainMaterialData(
		uint heightmapHandle,
		uint layerIndexMapHandle,
		uint layerWeightMapHandle,
		uint hasLayerMaps,
		uint heightmapSamplerHandle,
		uint layerSamplerHandle,
		uint layerMapSamplerHandle,
		uint layerStart,
		uint layerCount,
		float heightBlendSharpness,
		float heightScale,
		float autoMaterialBlendDegrees)
	{
		HeightmapHandle = heightmapHandle;
		LayerIndexMapHandle = layerIndexMapHandle;
		LayerWeightMapHandle = layerWeightMapHandle;
		HasLayerMaps = hasLayerMaps;
		HeightmapSamplerHandle = heightmapSamplerHandle;
		LayerSamplerHandle = layerSamplerHandle;
		LayerMapSamplerHandle = layerMapSamplerHandle;
		LayerStart = layerStart;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		HeightScale = heightScale;
		AutoMaterialBlendDegrees = autoMaterialBlendDegrees;
		_pad0 = 0;
		_pad1 = 0;
		_pad2 = 0;
		_pad3 = 0;
	}

	public readonly uint HeightmapHandle;
	public readonly uint LayerIndexMapHandle;
	public readonly uint LayerWeightMapHandle;
	public readonly uint HasLayerMaps;
	public readonly uint HeightmapSamplerHandle;
	public readonly uint LayerSamplerHandle;
	public readonly uint LayerMapSamplerHandle;
	public readonly uint LayerStart;
	public readonly uint LayerCount;
	public readonly float HeightBlendSharpness;
	public readonly float HeightScale;
	public readonly float AutoMaterialBlendDegrees;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
	private readonly uint _pad3;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuTerrainLayerData
{
	public GpuTerrainLayerData(
		uint albedoHandle,
		uint normalHandle,
		uint ormHandle,
		uint heightHandle,
		uint hasHeight,
		float scale,
		uint autoMaterial,
		uint useMinimumSlope,
		float minimumSlopeDegrees,
		uint hasNormal,
		uint hasOrm)
	{
		AlbedoHandle = albedoHandle;
		NormalHandle = normalHandle;
		OrmHandle = ormHandle;
		HeightHandle = heightHandle;
		HasHeight = hasHeight;
		Scale = scale;
		AutoMaterial = autoMaterial;
		UseMinimumSlope = useMinimumSlope;
		MinimumSlopeDegrees = minimumSlopeDegrees;
		HasNormal = hasNormal;
		HasOrm = hasOrm;
	}

	public readonly uint AlbedoHandle;
	public readonly uint NormalHandle;
	public readonly uint OrmHandle;
	public readonly uint HeightHandle;
	public readonly uint HasHeight;
	public readonly float Scale;
	public readonly uint AutoMaterial;
	public readonly uint UseMinimumSlope;
	public readonly float MinimumSlopeDegrees;
	public readonly uint HasNormal;
	public readonly uint HasOrm;
	private readonly uint _pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuMeshData
{
	public GpuMeshData(uint vertexBufferHandle, uint indexBufferHandle, uint indexCount, uint indexFormat, uint baseVertex, uint startIndex)
	{
		VertexBufferHandle = vertexBufferHandle;
		IndexBufferHandle = indexBufferHandle;
		IndexCount = indexCount;
		IndexFormat = indexFormat;
		BaseVertex = baseVertex;
		StartIndex = startIndex;
		_pad1 = 0;
		_pad2 = 0;
	}

	public readonly uint VertexBufferHandle;
	public readonly uint IndexBufferHandle;
	public readonly uint IndexCount;
	public readonly uint IndexFormat;
	public readonly uint BaseVertex;
	public readonly uint StartIndex;
	private readonly uint _pad1;
	private readonly uint _pad2;
}

/// <summary>
/// One instance's entry in the command table, which is indexed by instance index. Culling reads it to find the
/// batch whose visible list the instance is appended to.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuDrawCommand
{
	public readonly uint InstanceHandle;
	public readonly uint BatchIndex;
	public readonly uint Flags;

	/// <summary>The level of detail the camera cull last picked, which the next cull starts from for hysteresis.</summary>
	public readonly uint LodLevel;
}

/// <summary>
/// One batch's entry in the batch table, indexed by batch index (the draw index of its draw records). Its visible
/// instances are written from <see cref="VisibleOffset"/> into each view's slice of the visible instance list, and
/// never more than <see cref="Capacity"/> of them. A batch with no members has a capacity of zero.
/// </summary>
/// <summary>
/// How the cull picks a level of detail for the instances of one batch: the batch drawing each level, and the
/// screen sizes it switches at. Indexed like the batch table, by the instances' level 0 batch. A batch whose mesh has
/// no coarser levels has a level count of one. Mirrors GpuDrawBatchLodData in draw_data.slang; see docs/mesh-lod.md.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct GpuDrawBatchLodData
{
	/// <summary>Levels a batch can switch between, level 0 included.</summary>
	public const int MaxLevelCount = 8;

	public uint LevelCount;
	private uint _pad0;
	private uint _pad1;
	private uint _pad2;

	/// <summary>Batch drawing each level, level 0 first. Entry 0 is the batch this entry is indexed by.</summary>
	public LevelBatchIndices LevelBatches;

	/// <summary>Screen size each coarser level starts below: entry n is the threshold into level n + 1.</summary>
	public LevelScreenSizes ScreenSizes;

	[System.Runtime.CompilerServices.InlineArray(MaxLevelCount)]
	public struct LevelBatchIndices
	{
		private uint _element;
	}

	[System.Runtime.CompilerServices.InlineArray(MaxLevelCount)]
	public struct LevelScreenSizes
	{
		private float _element;
	}
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuDrawBatchData
{
	public GpuDrawBatchData(uint visibleOffset, uint capacity, uint laneIndex)
	{
		VisibleOffset = visibleOffset;
		Capacity = capacity;
		LaneIndex = laneIndex;
		_pad0 = 0;
	}

	public readonly uint VisibleOffset;
	public readonly uint Capacity;
	public readonly uint LaneIndex;
	private readonly uint _pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuDrawArgs
{
	public readonly uint IndexCount;
	public readonly uint InstanceCount;
	public readonly uint StartIndex;
	public readonly int BaseVertex;
	public readonly uint StartInstance;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuDrawInstanceUpdateData
{
	public GpuDrawInstanceUpdateData(
		Matrix4x4 previousWorld,
		Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Vector4 terrainChunkOriginSize,
		Vector4 terrainHeightmapUvScaleOffset,
		uint type,
		uint batchIndex,
		uint instanceHandle,
		uint drawKind,
		uint meshHandle,
		uint materialHandle,
		uint drawFlags,
		Vector4 lodSphere,
		uint lodParams)
	{
		PreviousWorld = previousWorld;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
		TerrainChunkOriginSize = terrainChunkOriginSize;
		TerrainHeightmapUvScaleOffset = terrainHeightmapUvScaleOffset;
		Type = type;
		BatchIndex = batchIndex;
		InstanceHandle = instanceHandle;
		DrawKind = drawKind;
		MeshHandle = meshHandle;
		MaterialHandle = materialHandle;
		DrawFlags = drawFlags;
		_pad0 = 0;
		LodSphere = lodSphere;
		LodParams = lodParams;
		_lodPad0 = 0;
		_lodPad1 = 0;
		_lodPad2 = 0;
	}

	public readonly Matrix4x4 PreviousWorld;
	public readonly Matrix4x4 World;
	public readonly Vector4 BoundsCenterRadius;
	public readonly Vector4 TerrainChunkOriginSize;
	public readonly Vector4 TerrainHeightmapUvScaleOffset;
	public readonly uint Type;
	public readonly uint BatchIndex;
	public readonly uint InstanceHandle;
	public readonly uint DrawKind;
	public readonly uint MeshHandle;
	public readonly uint MaterialHandle;
	public readonly uint DrawFlags;
	private readonly uint _pad0;

	public readonly Vector4 LodSphere;
	public readonly uint LodParams;
	private readonly uint _lodPad0;
	private readonly uint _lodPad1;
	private readonly uint _lodPad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuDrawMeshUpdateData
{
	public GpuDrawMeshUpdateData(
		uint meshHandle,
		uint vertexBufferHandle,
		uint indexBufferHandle,
		uint indexCount,
		uint indexFormat,
		int baseVertex,
		uint startIndex)
	{
		MeshHandle = meshHandle;
		VertexBufferHandle = vertexBufferHandle;
		IndexBufferHandle = indexBufferHandle;
		IndexCount = indexCount;
		IndexFormat = indexFormat;
		BaseVertex = baseVertex;
		StartIndex = startIndex;
		_pad1 = 0;
	}

	public readonly uint MeshHandle;
	public readonly uint VertexBufferHandle;
	public readonly uint IndexBufferHandle;
	public readonly uint IndexCount;
	public readonly uint IndexFormat;
	public readonly int BaseVertex;
	public readonly uint StartIndex;
	private readonly uint _pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuDrawMaterialUpdateData
{
	public GpuDrawMaterialUpdateData(
		uint materialHandle,
		ColorRGBA baseColor,
		Vector4 metallicRoughness,
		Vector4 emissiveFactorIntensity,
		Vector4 uvOffsetScale,
		uint albedoHandle,
		uint ormHandle,
		uint normalHandle,
		uint emissiveHandle,
		uint samplerHandle,
		uint materialFlags)
	{
		MaterialHandle = materialHandle;
		_pad0 = 0;
		_pad1 = 0;
		_pad2 = 0;
		BaseColor = baseColor;
		MetallicRoughness = metallicRoughness;
		EmissiveFactorIntensity = emissiveFactorIntensity;
		UvOffsetScale = uvOffsetScale;
		AlbedoHandle = albedoHandle;
		OrmHandle = ormHandle;
		NormalHandle = normalHandle;
		EmissiveHandle = emissiveHandle;
		SamplerHandle = samplerHandle;
		MaterialFlags = materialFlags;
		_pad4 = 0;
		_pad5 = 0;
	}

	public readonly uint MaterialHandle;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
	public readonly ColorRGBA BaseColor;
	public readonly Vector4 MetallicRoughness;
	public readonly Vector4 EmissiveFactorIntensity;
	public readonly Vector4 UvOffsetScale;
	public readonly uint AlbedoHandle;
	public readonly uint OrmHandle;
	public readonly uint NormalHandle;
	public readonly uint EmissiveHandle;
	public readonly uint SamplerHandle;

	/// <summary>Bits from <see cref="GpuMaterialFlags"/>.</summary>
	public readonly uint MaterialFlags;
	private readonly uint _pad4;
	private readonly uint _pad5;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuDecalProjectorData
{
	public GpuDecalProjectorData(
		Matrix4x4 localToWorld,
		Matrix4x4 worldToLocal,
		Vector4 uvScaleOffset,
		ColorRGBA tint,
		Vector4 opacities,
		Vector4 materialFactorsEmissive,
		uint channelMask,
		uint albedoHandle,
		uint normalHandle,
		uint materialHandle,
		uint emissiveHandle,
		uint samplerHandle)
	{
		LocalToWorld = localToWorld;
		WorldToLocal = worldToLocal;
		UvScaleOffset = uvScaleOffset;
		Tint = tint;
		Opacities = opacities;
		MaterialFactorsEmissive = materialFactorsEmissive;
		ChannelMask = channelMask;
		AlbedoHandle = albedoHandle;
		NormalHandle = normalHandle;
		MaterialHandle = materialHandle;
		EmissiveHandle = emissiveHandle;
		SamplerHandle = samplerHandle;
		_pad0 = 0;
		_pad1 = 0;
		_pad2 = 0;
	}

	public readonly Matrix4x4 LocalToWorld;
	public readonly Matrix4x4 WorldToLocal;
	public readonly Vector4 UvScaleOffset;
	public readonly ColorRGBA Tint;
	public readonly Vector4 Opacities;
	public readonly Vector4 MaterialFactorsEmissive;
	public readonly uint ChannelMask;
	public readonly uint AlbedoHandle;
	public readonly uint NormalHandle;
	public readonly uint MaterialHandle;
	public readonly uint EmissiveHandle;
	public readonly uint SamplerHandle;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuFogVolumeData
{
	public GpuFogVolumeData(Matrix4x4 worldToLocal, Vector4 albedoExtinction, Vector4 parameters)
	{
		WorldToLocal = worldToLocal;
		AlbedoExtinction = albedoExtinction;
		Parameters = parameters;
	}

	public readonly Matrix4x4 WorldToLocal;
	public readonly Vector4 AlbedoExtinction;
	/// <summary>x anisotropy, y blend distance in normalized local space, z shape.</summary>
	public readonly Vector4 Parameters;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuFogCellHeader
{
	public GpuFogCellHeader(uint offset, uint count)
	{
		Offset = offset;
		Count = count;
	}

	public readonly uint Offset;
	public readonly uint Count;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuTerrainMaterialUpdateData
{
	public GpuTerrainMaterialUpdateData(
		uint materialHandle,
		uint heightmapHandle,
		uint layerIndexMapHandle,
		uint layerWeightMapHandle,
		uint hasLayerMaps,
		uint heightmapSamplerHandle,
		uint layerSamplerHandle,
		uint layerMapSamplerHandle,
		uint layerStart,
		uint layerCount,
		float heightBlendSharpness,
		float heightScale,
		float autoMaterialBlendDegrees)
	{
		MaterialHandle = materialHandle;
		HeightmapHandle = heightmapHandle;
		LayerIndexMapHandle = layerIndexMapHandle;
		LayerWeightMapHandle = layerWeightMapHandle;
		HasLayerMaps = hasLayerMaps;
		HeightmapSamplerHandle = heightmapSamplerHandle;
		LayerSamplerHandle = layerSamplerHandle;
		LayerMapSamplerHandle = layerMapSamplerHandle;
		LayerStart = layerStart;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		HeightScale = heightScale;
		AutoMaterialBlendDegrees = autoMaterialBlendDegrees;
		_pad0 = 0;
		_pad1 = 0;
		_pad2 = 0;
	}

	public readonly uint MaterialHandle;
	public readonly uint HeightmapHandle;
	public readonly uint LayerIndexMapHandle;
	public readonly uint LayerWeightMapHandle;
	public readonly uint HasLayerMaps;
	public readonly uint HeightmapSamplerHandle;
	public readonly uint LayerSamplerHandle;
	public readonly uint LayerMapSamplerHandle;
	public readonly uint LayerStart;
	public readonly uint LayerCount;
	public readonly float HeightBlendSharpness;
	public readonly float HeightScale;
	public readonly float AutoMaterialBlendDegrees;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuTerrainLayerUpdateData
{
	public GpuTerrainLayerUpdateData(
		uint materialHandle,
		uint layerStart,
		uint layerIndex,
		uint albedoHandle,
		uint normalHandle,
		uint ormHandle,
		uint heightHandle,
		uint hasHeight,
		float scale,
		uint autoMaterial,
		uint useMinimumSlope,
		float minimumSlopeDegrees,
		uint hasNormal,
		uint hasOrm)
	{
		MaterialHandle = materialHandle;
		LayerStart = layerStart;
		LayerIndex = layerIndex;
		AlbedoHandle = albedoHandle;
		NormalHandle = normalHandle;
		OrmHandle = ormHandle;
		HeightHandle = heightHandle;
		HasHeight = hasHeight;
		Scale = scale;
		AutoMaterial = autoMaterial;
		UseMinimumSlope = useMinimumSlope;
		MinimumSlopeDegrees = minimumSlopeDegrees;
		HasNormal = hasNormal;
		HasOrm = hasOrm;
		_pad2 = 0;
	}

	public readonly uint MaterialHandle;
	public readonly uint LayerStart;
	public readonly uint LayerIndex;
	public readonly uint AlbedoHandle;
	public readonly uint NormalHandle;
	public readonly uint OrmHandle;
	public readonly uint HeightHandle;
	public readonly uint HasHeight;
	public readonly float Scale;
	public readonly uint AutoMaterial;
	public readonly uint UseMinimumSlope;
	public readonly float MinimumSlopeDegrees;
	public readonly uint HasNormal;
	public readonly uint HasOrm;
	private readonly uint _pad2;
	private readonly uint _pad3;
}
