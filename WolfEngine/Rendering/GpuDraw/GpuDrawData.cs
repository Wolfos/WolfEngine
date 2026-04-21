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
		uint layerCount,
		float heightBlendSharpness,
		uint layer0AlbedoHandle,
		uint layer0NormalHandle,
		uint layer0MetallicRoughnessHandle,
		uint layer0OcclusionHandle,
		uint layer0HeightHandle,
		uint layer0HasHeight,
		float layer0Scale,
		uint layer1AlbedoHandle,
		uint layer1NormalHandle,
		uint layer1MetallicRoughnessHandle,
		uint layer1OcclusionHandle,
		uint layer1HeightHandle,
		uint layer1HasHeight,
		float layer1Scale,
		uint layer2AlbedoHandle,
		uint layer2NormalHandle,
		uint layer2MetallicRoughnessHandle,
		uint layer2OcclusionHandle,
		uint layer2HeightHandle,
		uint layer2HasHeight,
		float layer2Scale,
		uint layer3AlbedoHandle,
		uint layer3NormalHandle,
		uint layer3MetallicRoughnessHandle,
		uint layer3OcclusionHandle,
		uint layer3HeightHandle,
		uint layer3HasHeight,
		float layer3Scale)
	{
		ControlMapHandle = controlMapHandle;
		HasControlMap = hasControlMap;
		LayerSamplerHandle = layerSamplerHandle;
		ControlSamplerHandle = controlSamplerHandle;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		_pad0 = 0;
		_pad1 = 0;
		Layer0AlbedoHandle = layer0AlbedoHandle;
		Layer0NormalHandle = layer0NormalHandle;
		Layer0MetallicRoughnessHandle = layer0MetallicRoughnessHandle;
		Layer0OcclusionHandle = layer0OcclusionHandle;
		Layer0HeightHandle = layer0HeightHandle;
		Layer0HasHeight = layer0HasHeight;
		Layer0Scale = layer0Scale;
		_layer0Pad = 0;
		Layer1AlbedoHandle = layer1AlbedoHandle;
		Layer1NormalHandle = layer1NormalHandle;
		Layer1MetallicRoughnessHandle = layer1MetallicRoughnessHandle;
		Layer1OcclusionHandle = layer1OcclusionHandle;
		Layer1HeightHandle = layer1HeightHandle;
		Layer1HasHeight = layer1HasHeight;
		Layer1Scale = layer1Scale;
		_layer1Pad = 0;
		Layer2AlbedoHandle = layer2AlbedoHandle;
		Layer2NormalHandle = layer2NormalHandle;
		Layer2MetallicRoughnessHandle = layer2MetallicRoughnessHandle;
		Layer2OcclusionHandle = layer2OcclusionHandle;
		Layer2HeightHandle = layer2HeightHandle;
		Layer2HasHeight = layer2HasHeight;
		Layer2Scale = layer2Scale;
		_layer2Pad = 0;
		Layer3AlbedoHandle = layer3AlbedoHandle;
		Layer3NormalHandle = layer3NormalHandle;
		Layer3MetallicRoughnessHandle = layer3MetallicRoughnessHandle;
		Layer3OcclusionHandle = layer3OcclusionHandle;
		Layer3HeightHandle = layer3HeightHandle;
		Layer3HasHeight = layer3HasHeight;
		Layer3Scale = layer3Scale;
		_layer3Pad = 0;
		_pad2 = 0;
		_pad3 = 0;
		_pad4 = 0;
		_pad5 = 0;
	}

	public readonly uint ControlMapHandle;
	public readonly uint HasControlMap;
	public readonly uint LayerSamplerHandle;
	public readonly uint ControlSamplerHandle;
	public readonly uint LayerCount;
	public readonly float HeightBlendSharpness;
	private readonly uint _pad0;
	private readonly uint _pad1;
	public readonly uint Layer0AlbedoHandle;
	public readonly uint Layer0NormalHandle;
	public readonly uint Layer0MetallicRoughnessHandle;
	public readonly uint Layer0OcclusionHandle;
	public readonly uint Layer0HeightHandle;
	public readonly uint Layer0HasHeight;
	public readonly float Layer0Scale;
	private readonly uint _layer0Pad;
	public readonly uint Layer1AlbedoHandle;
	public readonly uint Layer1NormalHandle;
	public readonly uint Layer1MetallicRoughnessHandle;
	public readonly uint Layer1OcclusionHandle;
	public readonly uint Layer1HeightHandle;
	public readonly uint Layer1HasHeight;
	public readonly float Layer1Scale;
	private readonly uint _layer1Pad;
	public readonly uint Layer2AlbedoHandle;
	public readonly uint Layer2NormalHandle;
	public readonly uint Layer2MetallicRoughnessHandle;
	public readonly uint Layer2OcclusionHandle;
	public readonly uint Layer2HeightHandle;
	public readonly uint Layer2HasHeight;
	public readonly float Layer2Scale;
	private readonly uint _layer2Pad;
	public readonly uint Layer3AlbedoHandle;
	public readonly uint Layer3NormalHandle;
	public readonly uint Layer3MetallicRoughnessHandle;
	public readonly uint Layer3OcclusionHandle;
	public readonly uint Layer3HeightHandle;
	public readonly uint Layer3HasHeight;
	public readonly float Layer3Scale;
	private readonly uint _layer3Pad;
	private readonly uint _pad2;
	private readonly uint _pad3;
	private readonly uint _pad4;
	private readonly uint _pad5;
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
public readonly struct GpuDrawUpdateData
{
	public GpuDrawUpdateData(
		Matrix4x4 previousWorld,
		Matrix4x4 world,
		Vector4 boundsCenterRadius,
		ColorRGBA baseColor,
		Vector4 metallicRoughness,
		Vector4 emissiveFactorIntensity,
		uint type,
		uint drawHandle,
		uint instanceHandle,
		uint drawKind,
		uint meshHandle,
		uint materialHandle,
		uint drawFlags,
		uint vertexBufferHandle,
		uint indexBufferHandle,
		uint indexCount,
		uint indexFormat,
		int baseVertex,
		uint albedoHandle,
		uint metallicRoughnessHandle,
		uint normalHandle,
		uint occlusionHandle,
		uint emissiveHandle,
		uint samplerHandle,
		uint controlMapHandle,
		uint hasControlMap,
		uint layerSamplerHandle,
		uint controlSamplerHandle,
		uint layerCount,
		float heightBlendSharpness,
		uint layer0AlbedoHandle,
		uint layer0NormalHandle,
		uint layer0MetallicRoughnessHandle,
		uint layer0OcclusionHandle,
		uint layer0HeightHandle,
		uint layer0HasHeight,
		float layer0Scale,
		uint layer1AlbedoHandle,
		uint layer1NormalHandle,
		uint layer1MetallicRoughnessHandle,
		uint layer1OcclusionHandle,
		uint layer1HeightHandle,
		uint layer1HasHeight,
		float layer1Scale,
		uint layer2AlbedoHandle,
		uint layer2NormalHandle,
		uint layer2MetallicRoughnessHandle,
		uint layer2OcclusionHandle,
		uint layer2HeightHandle,
		uint layer2HasHeight,
		float layer2Scale,
		uint layer3AlbedoHandle,
		uint layer3NormalHandle,
		uint layer3MetallicRoughnessHandle,
		uint layer3OcclusionHandle,
		uint layer3HeightHandle,
		uint layer3HasHeight,
		float layer3Scale)
	{
		PreviousWorld = previousWorld;
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
		BaseColor = baseColor;
		MetallicRoughness = metallicRoughness;
		EmissiveFactorIntensity = emissiveFactorIntensity;
		Type = type;
		DrawHandle = drawHandle;
		InstanceHandle = instanceHandle;
		DrawKind = drawKind;
		MeshHandle = meshHandle;
		MaterialHandle = materialHandle;
		DrawFlags = drawFlags;
		VertexBufferHandle = vertexBufferHandle;
		IndexBufferHandle = indexBufferHandle;
		IndexCount = indexCount;
		IndexFormat = indexFormat;
		BaseVertex = baseVertex;
		AlbedoHandle = albedoHandle;
		MetallicRoughnessHandle = metallicRoughnessHandle;
		NormalHandle = normalHandle;
		OcclusionHandle = occlusionHandle;
		EmissiveHandle = emissiveHandle;
		SamplerHandle = samplerHandle;
		ControlMapHandle = controlMapHandle;
		HasControlMap = hasControlMap;
		LayerSamplerHandle = layerSamplerHandle;
		ControlSamplerHandle = controlSamplerHandle;
		LayerCount = layerCount;
		HeightBlendSharpness = heightBlendSharpness;
		_pad0 = 0;
		_pad1 = 0;
		Layer0AlbedoHandle = layer0AlbedoHandle;
		Layer0NormalHandle = layer0NormalHandle;
		Layer0MetallicRoughnessHandle = layer0MetallicRoughnessHandle;
		Layer0OcclusionHandle = layer0OcclusionHandle;
		Layer0HeightHandle = layer0HeightHandle;
		Layer0HasHeight = layer0HasHeight;
		Layer0Scale = layer0Scale;
		_layer0Pad = 0;
		Layer1AlbedoHandle = layer1AlbedoHandle;
		Layer1NormalHandle = layer1NormalHandle;
		Layer1MetallicRoughnessHandle = layer1MetallicRoughnessHandle;
		Layer1OcclusionHandle = layer1OcclusionHandle;
		Layer1HeightHandle = layer1HeightHandle;
		Layer1HasHeight = layer1HasHeight;
		Layer1Scale = layer1Scale;
		_layer1Pad = 0;
		Layer2AlbedoHandle = layer2AlbedoHandle;
		Layer2NormalHandle = layer2NormalHandle;
		Layer2MetallicRoughnessHandle = layer2MetallicRoughnessHandle;
		Layer2OcclusionHandle = layer2OcclusionHandle;
		Layer2HeightHandle = layer2HeightHandle;
		Layer2HasHeight = layer2HasHeight;
		Layer2Scale = layer2Scale;
		_layer2Pad = 0;
		Layer3AlbedoHandle = layer3AlbedoHandle;
		Layer3NormalHandle = layer3NormalHandle;
		Layer3MetallicRoughnessHandle = layer3MetallicRoughnessHandle;
		Layer3OcclusionHandle = layer3OcclusionHandle;
		Layer3HeightHandle = layer3HeightHandle;
		Layer3HasHeight = layer3HasHeight;
		Layer3Scale = layer3Scale;
		_layer3Pad = 0;
		_pad2 = 0;
		_pad3 = 0;
	}

	public readonly Matrix4x4 PreviousWorld;
	public readonly Matrix4x4 World;
	public readonly Vector4 BoundsCenterRadius;
	public readonly ColorRGBA BaseColor;
	public readonly Vector4 MetallicRoughness;
	public readonly Vector4 EmissiveFactorIntensity;
	public readonly uint Type;
	public readonly uint DrawHandle;
	public readonly uint InstanceHandle;
	public readonly uint DrawKind;
	public readonly uint MeshHandle;
	public readonly uint MaterialHandle;
	public readonly uint DrawFlags;
	public readonly uint VertexBufferHandle;
	public readonly uint IndexBufferHandle;
	public readonly uint IndexCount;
	public readonly uint IndexFormat;
	public readonly int BaseVertex;
	public readonly uint AlbedoHandle;
	public readonly uint MetallicRoughnessHandle;
	public readonly uint NormalHandle;
	public readonly uint OcclusionHandle;
	public readonly uint EmissiveHandle;
	public readonly uint SamplerHandle;
	public readonly uint ControlMapHandle;
	public readonly uint HasControlMap;
	public readonly uint LayerSamplerHandle;
	public readonly uint ControlSamplerHandle;
	public readonly uint LayerCount;
	public readonly float HeightBlendSharpness;
	private readonly uint _pad0;
	private readonly uint _pad1;
	public readonly uint Layer0AlbedoHandle;
	public readonly uint Layer0NormalHandle;
	public readonly uint Layer0MetallicRoughnessHandle;
	public readonly uint Layer0OcclusionHandle;
	public readonly uint Layer0HeightHandle;
	public readonly uint Layer0HasHeight;
	public readonly float Layer0Scale;
	private readonly uint _layer0Pad;
	public readonly uint Layer1AlbedoHandle;
	public readonly uint Layer1NormalHandle;
	public readonly uint Layer1MetallicRoughnessHandle;
	public readonly uint Layer1OcclusionHandle;
	public readonly uint Layer1HeightHandle;
	public readonly uint Layer1HasHeight;
	public readonly float Layer1Scale;
	private readonly uint _layer1Pad;
	public readonly uint Layer2AlbedoHandle;
	public readonly uint Layer2NormalHandle;
	public readonly uint Layer2MetallicRoughnessHandle;
	public readonly uint Layer2OcclusionHandle;
	public readonly uint Layer2HeightHandle;
	public readonly uint Layer2HasHeight;
	public readonly float Layer2Scale;
	private readonly uint _layer2Pad;
	public readonly uint Layer3AlbedoHandle;
	public readonly uint Layer3NormalHandle;
	public readonly uint Layer3MetallicRoughnessHandle;
	public readonly uint Layer3OcclusionHandle;
	public readonly uint Layer3HeightHandle;
	public readonly uint Layer3HasHeight;
	public readonly float Layer3Scale;
	private readonly uint _layer3Pad;
	private readonly uint _pad2;
	private readonly uint _pad3;
}
