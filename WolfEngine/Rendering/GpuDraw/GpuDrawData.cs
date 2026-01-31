#nullable enable

using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine.Rendering;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuInstanceData
{
	public readonly Matrix4x4 World;
	public readonly Vector4 BoundsCenterRadius;
	public readonly uint MaterialId;
	public readonly uint MeshId;
	public readonly uint Flags;
	private readonly uint _pad0;
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuMaterialData
{
	public readonly Vector4 BaseColor;
	public readonly Vector2 MetallicRoughness;
	private readonly Vector2 _padding;
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
	public readonly uint InstanceId;
	public readonly uint DrawId;
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
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly struct GpuDrawUpdateData
{
	public GpuDrawUpdateData(
		Matrix4x4 world,
		Vector4 boundsCenterRadius,
		Vector4 baseColor,
		Vector4 metallicRoughness,
		uint type,
		uint drawId,
		uint instanceId,
		uint meshId,
		uint materialId,
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
		World = world;
		BoundsCenterRadius = boundsCenterRadius;
		BaseColor = baseColor;
		MetallicRoughness = metallicRoughness;
		Type = type;
		DrawId = drawId;
		InstanceId = instanceId;
		MeshId = meshId;
		MaterialId = materialId;
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
	}

	public readonly Matrix4x4 World;
	public readonly Vector4 BoundsCenterRadius;
	public readonly Vector4 BaseColor;
	public readonly Vector4 MetallicRoughness;
	public readonly uint Type;
	public readonly uint DrawId;
	public readonly uint InstanceId;
	public readonly uint MeshId;
	public readonly uint MaterialId;
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
}
