#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine.Rendering;

public enum GpuDrawKind : uint
{
	Mesh = 0,
	DebugPrimitive = 1,
	Terrain = 2
}

public static class GpuDrawFlags
{
	public const uint Active = 1u << 0;
	public const int BucketShift = 1;
	public const uint BucketMask = 0x7FFFFFFFu;
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
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuMaterialData
{
	public GpuMaterialData(
		ColorRGBA baseColor,
		Vector4 metallicRoughness,
		Vector4 emissiveFactorIntensity,
		uint albedoHandle,
		uint ormHandle,
		uint normalHandle,
		uint emissiveHandle,
		uint samplerHandle)
	{
		BaseColor = baseColor;
		MetallicRoughness = metallicRoughness;
		EmissiveFactorIntensity = emissiveFactorIntensity;
		AlbedoHandle = albedoHandle;
		OrmHandle = ormHandle;
		NormalHandle = normalHandle;
		EmissiveHandle = emissiveHandle;
		SamplerHandle = samplerHandle;
		_pad0 = 0;
		_pad1 = 0;
		_pad2 = 0;
	}

	public readonly ColorRGBA BaseColor;
	public readonly Vector4 MetallicRoughness;
	public readonly Vector4 EmissiveFactorIntensity;
	public readonly uint AlbedoHandle;
	public readonly uint OrmHandle;
	public readonly uint NormalHandle;
	public readonly uint EmissiveHandle;
	public readonly uint SamplerHandle;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuTerrainMaterialData
{
	public GpuTerrainMaterialData(
		uint heightmapHandle,
		uint controlMapHandle,
		uint hasControlMap,
		uint heightmapSamplerHandle,
		uint layerSamplerHandle,
		uint controlSamplerHandle,
		uint layerStart,
		uint layerCount,
		float heightBlendSharpness,
		float heightScale)
	{
		HeightmapHandle = heightmapHandle;
		ControlMapHandle = controlMapHandle;
		HasControlMap = hasControlMap;
		HeightmapSamplerHandle = heightmapSamplerHandle;
		LayerSamplerHandle = layerSamplerHandle;
		ControlSamplerHandle = controlSamplerHandle;
		LayerStart = layerStart;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		HeightScale = heightScale;
		_pad0 = 0;
		_pad1 = 0;
	}

	public readonly uint HeightmapHandle;
	public readonly uint ControlMapHandle;
	public readonly uint HasControlMap;
	public readonly uint HeightmapSamplerHandle;
	public readonly uint LayerSamplerHandle;
	public readonly uint ControlSamplerHandle;
	public readonly uint LayerStart;
	public readonly uint LayerCount;
	public readonly float HeightBlendSharpness;
	public readonly float HeightScale;
	private readonly uint _pad0;
	private readonly uint _pad1;
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
		float scale)
	{
		AlbedoHandle = albedoHandle;
		NormalHandle = normalHandle;
		OrmHandle = ormHandle;
		HeightHandle = heightHandle;
		HasHeight = hasHeight;
		Scale = scale;
		_pad0 = 0;
		_pad1 = 0;
	}

	public readonly uint AlbedoHandle;
	public readonly uint NormalHandle;
	public readonly uint OrmHandle;
	public readonly uint HeightHandle;
	public readonly uint HasHeight;
	public readonly float Scale;
	private readonly uint _pad0;
	private readonly uint _pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuMeshData
{
	public GpuMeshData(uint vertexBufferHandle, uint indexBufferHandle, uint indexCount, uint indexFormat, uint baseVertex)
	{
		VertexBufferHandle = vertexBufferHandle;
		IndexBufferHandle = indexBufferHandle;
		IndexCount = indexCount;
		IndexFormat = indexFormat;
		BaseVertex = baseVertex;
		_pad0 = 0;
		_pad1 = 0;
		_pad2 = 0;
	}

	public readonly uint VertexBufferHandle;
	public readonly uint IndexBufferHandle;
	public readonly uint IndexCount;
	public readonly uint IndexFormat;
	public readonly uint BaseVertex;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuDrawCommand
{
	public readonly uint InstanceHandle;
	public readonly uint DrawHandle;
	public readonly uint Flags;
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
		uint drawHandle,
		uint instanceHandle,
		uint drawKind,
		uint meshHandle,
		uint materialHandle,
		uint drawFlags)
	{
		PreviousWorld = previousWorld;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
		TerrainChunkOriginSize = terrainChunkOriginSize;
		TerrainHeightmapUvScaleOffset = terrainHeightmapUvScaleOffset;
		Type = type;
		DrawHandle = drawHandle;
		InstanceHandle = instanceHandle;
		DrawKind = drawKind;
		MeshHandle = meshHandle;
		MaterialHandle = materialHandle;
		DrawFlags = drawFlags;
		_pad0 = 0;
	}

	public readonly Matrix4x4 PreviousWorld;
	public readonly Matrix4x4 World;
	public readonly Vector4 BoundsCenterRadius;
	public readonly Vector4 TerrainChunkOriginSize;
	public readonly Vector4 TerrainHeightmapUvScaleOffset;
	public readonly uint Type;
	public readonly uint DrawHandle;
	public readonly uint InstanceHandle;
	public readonly uint DrawKind;
	public readonly uint MeshHandle;
	public readonly uint MaterialHandle;
	public readonly uint DrawFlags;
	private readonly uint _pad0;
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
		int baseVertex)
	{
		MeshHandle = meshHandle;
		VertexBufferHandle = vertexBufferHandle;
		IndexBufferHandle = indexBufferHandle;
		IndexCount = indexCount;
		IndexFormat = indexFormat;
		BaseVertex = baseVertex;
		_pad0 = 0;
		_pad1 = 0;
	}

	public readonly uint MeshHandle;
	public readonly uint VertexBufferHandle;
	public readonly uint IndexBufferHandle;
	public readonly uint IndexCount;
	public readonly uint IndexFormat;
	public readonly int BaseVertex;
	private readonly uint _pad0;
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
		uint albedoHandle,
		uint ormHandle,
		uint normalHandle,
		uint emissiveHandle,
		uint samplerHandle)
	{
		MaterialHandle = materialHandle;
		_pad0 = 0;
		_pad1 = 0;
		_pad2 = 0;
		BaseColor = baseColor;
		MetallicRoughness = metallicRoughness;
		EmissiveFactorIntensity = emissiveFactorIntensity;
		AlbedoHandle = albedoHandle;
		OrmHandle = ormHandle;
		NormalHandle = normalHandle;
		EmissiveHandle = emissiveHandle;
		SamplerHandle = samplerHandle;
		_pad3 = 0;
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
	public readonly uint AlbedoHandle;
	public readonly uint OrmHandle;
	public readonly uint NormalHandle;
	public readonly uint EmissiveHandle;
	public readonly uint SamplerHandle;
	private readonly uint _pad3;
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
public readonly struct GpuTerrainMaterialUpdateData
{
	public GpuTerrainMaterialUpdateData(
		uint materialHandle,
		uint heightmapHandle,
		uint controlMapHandle,
		uint hasControlMap,
		uint heightmapSamplerHandle,
		uint layerSamplerHandle,
		uint controlSamplerHandle,
		uint layerStart,
		uint layerCount,
		float heightBlendSharpness,
		float heightScale)
	{
		MaterialHandle = materialHandle;
		HeightmapHandle = heightmapHandle;
		ControlMapHandle = controlMapHandle;
		HasControlMap = hasControlMap;
		HeightmapSamplerHandle = heightmapSamplerHandle;
		LayerSamplerHandle = layerSamplerHandle;
		ControlSamplerHandle = controlSamplerHandle;
		LayerStart = layerStart;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		HeightScale = heightScale;
		_pad0 = 0;
	}

	public readonly uint MaterialHandle;
	public readonly uint HeightmapHandle;
	public readonly uint ControlMapHandle;
	public readonly uint HasControlMap;
	public readonly uint HeightmapSamplerHandle;
	public readonly uint LayerSamplerHandle;
	public readonly uint ControlSamplerHandle;
	public readonly uint LayerStart;
	public readonly uint LayerCount;
	public readonly float HeightBlendSharpness;
	public readonly float HeightScale;
	private readonly uint _pad0;
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
		float scale)
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
		_pad0 = 0;
		_pad1 = 0;
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
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
}
