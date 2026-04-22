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
		uint materialHandle,
		uint meshHandle,
		uint drawKind,
		uint flags)
	{
		PreviousWorld = previousWorld;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
		MaterialHandle = materialHandle;
		MeshHandle = meshHandle;
		DrawKind = drawKind;
		Flags = flags;
	}

	public readonly Matrix4x4 PreviousWorld;
	public readonly Matrix4x4 World;
	public readonly Vector4 BoundsCenterRadius;
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
		uint metallicRoughnessHandle,
		uint normalHandle,
		uint occlusionHandle,
		uint emissiveHandle,
		uint samplerHandle)
	{
		BaseColor = baseColor;
		MetallicRoughness = metallicRoughness;
		EmissiveFactorIntensity = emissiveFactorIntensity;
		AlbedoHandle = albedoHandle;
		MetallicRoughnessHandle = metallicRoughnessHandle;
		NormalHandle = normalHandle;
		OcclusionHandle = occlusionHandle;
		EmissiveHandle = emissiveHandle;
		SamplerHandle = samplerHandle;
		_pad0 = 0;
		_pad1 = 0;
	}

	public readonly ColorRGBA BaseColor;
	public readonly Vector4 MetallicRoughness;
	public readonly Vector4 EmissiveFactorIntensity;
	public readonly uint AlbedoHandle;
	public readonly uint MetallicRoughnessHandle;
	public readonly uint NormalHandle;
	public readonly uint OcclusionHandle;
	public readonly uint EmissiveHandle;
	public readonly uint SamplerHandle;
	private readonly uint _pad0;
	private readonly uint _pad1;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuTerrainMaterialData
{
	public GpuTerrainMaterialData(
		uint controlMapHandle,
		uint hasControlMap,
		uint layerSamplerHandle,
		uint controlSamplerHandle,
		uint layerStart,
		uint layerCount,
		float heightBlendSharpness)
	{
		ControlMapHandle = controlMapHandle;
		HasControlMap = hasControlMap;
		LayerSamplerHandle = layerSamplerHandle;
		ControlSamplerHandle = controlSamplerHandle;
		LayerStart = layerStart;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		_pad0 = 0;
		_pad1 = 0;
		_pad2 = 0;
		_pad3 = 0;
		_pad4 = 0;
	}

	public readonly uint ControlMapHandle;
	public readonly uint HasControlMap;
	public readonly uint LayerSamplerHandle;
	public readonly uint ControlSamplerHandle;
	public readonly uint LayerStart;
	public readonly uint LayerCount;
	public readonly float HeightBlendSharpness;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
	private readonly uint _pad3;
	private readonly uint _pad4;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuTerrainLayerData
{
	public GpuTerrainLayerData(
		uint albedoHandle,
		uint normalHandle,
		uint metallicRoughnessHandle,
		uint occlusionHandle,
		uint heightHandle,
		uint hasHeight,
		float scale)
	{
		AlbedoHandle = albedoHandle;
		NormalHandle = normalHandle;
		MetallicRoughnessHandle = metallicRoughnessHandle;
		OcclusionHandle = occlusionHandle;
		HeightHandle = heightHandle;
		HasHeight = hasHeight;
		Scale = scale;
		_pad0 = 0;
	}

	public readonly uint AlbedoHandle;
	public readonly uint NormalHandle;
	public readonly uint MetallicRoughnessHandle;
	public readonly uint OcclusionHandle;
	public readonly uint HeightHandle;
	public readonly uint HasHeight;
	public readonly float Scale;
	private readonly uint _pad0;
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
		uint metallicRoughnessHandle,
		uint normalHandle,
		uint occlusionHandle,
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
		MetallicRoughnessHandle = metallicRoughnessHandle;
		NormalHandle = normalHandle;
		OcclusionHandle = occlusionHandle;
		EmissiveHandle = emissiveHandle;
		SamplerHandle = samplerHandle;
		_pad3 = 0;
		_pad4 = 0;
	}

	public readonly uint MaterialHandle;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
	public readonly ColorRGBA BaseColor;
	public readonly Vector4 MetallicRoughness;
	public readonly Vector4 EmissiveFactorIntensity;
	public readonly uint AlbedoHandle;
	public readonly uint MetallicRoughnessHandle;
	public readonly uint NormalHandle;
	public readonly uint OcclusionHandle;
	public readonly uint EmissiveHandle;
	public readonly uint SamplerHandle;
	private readonly uint _pad3;
	private readonly uint _pad4;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuTerrainMaterialUpdateData
{
	public GpuTerrainMaterialUpdateData(
		uint materialHandle,
		uint controlMapHandle,
		uint hasControlMap,
		uint layerSamplerHandle,
		uint controlSamplerHandle,
		uint layerStart,
		uint layerCount,
		float heightBlendSharpness)
	{
		MaterialHandle = materialHandle;
		ControlMapHandle = controlMapHandle;
		HasControlMap = hasControlMap;
		LayerSamplerHandle = layerSamplerHandle;
		ControlSamplerHandle = controlSamplerHandle;
		LayerStart = layerStart;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
	}

	public readonly uint MaterialHandle;
	public readonly uint ControlMapHandle;
	public readonly uint HasControlMap;
	public readonly uint LayerSamplerHandle;
	public readonly uint ControlSamplerHandle;
	public readonly uint LayerStart;
	public readonly uint LayerCount;
	public readonly float HeightBlendSharpness;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuTerrainLayerUpdateData
{
	public GpuTerrainLayerUpdateData(
		uint materialHandle,
		uint layerIndex,
		uint albedoHandle,
		uint normalHandle,
		uint metallicRoughnessHandle,
		uint occlusionHandle,
		uint heightHandle,
		uint hasHeight,
		float scale)
	{
		MaterialHandle = materialHandle;
		LayerIndex = layerIndex;
		AlbedoHandle = albedoHandle;
		NormalHandle = normalHandle;
		MetallicRoughnessHandle = metallicRoughnessHandle;
		OcclusionHandle = occlusionHandle;
		HeightHandle = heightHandle;
		HasHeight = hasHeight;
		Scale = scale;
		_pad0 = 0;
		_pad1 = 0;
		_pad2 = 0;
	}

	public readonly uint MaterialHandle;
	public readonly uint LayerIndex;
	public readonly uint AlbedoHandle;
	public readonly uint NormalHandle;
	public readonly uint MetallicRoughnessHandle;
	public readonly uint OcclusionHandle;
	public readonly uint HeightHandle;
	public readonly uint HasHeight;
	public readonly float Scale;
	private readonly uint _pad0;
	private readonly uint _pad1;
	private readonly uint _pad2;
}
