#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine.Rendering;

public enum GpuDrawKind : uint
{
	Mesh = 0,
	DebugPrimitive = 1
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
		uint samplerHandle)
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
		_pad0 = 0;
		_pad1 = 0;
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
	private readonly uint _pad0;
	private readonly uint _pad1;
}
