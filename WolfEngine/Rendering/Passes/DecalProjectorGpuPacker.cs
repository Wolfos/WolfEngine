#nullable enable

using System;
using System.Numerics;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

internal static class DecalProjectorGpuPacker
{
	public static GpuDecalProjectorData CreateGpuData(
		in DecalProjectorPacket packet,
		Vector3 cameraOrigin,
		in DecalProjectorResolvedHandles handles)
	{
		var localToWorld = Matrix4x4.CreateScale(packet.Projector.Size) * packet.Transform;
		localToWorld.Translation -= cameraOrigin;
		if (Matrix4x4.Invert(localToWorld, out var worldToLocal) == false)
		{
			worldToLocal = Matrix4x4.Identity;
		}

		return new GpuDecalProjectorData(
			localToWorld,
			worldToLocal,
			packet.Projector.UvScaleOffset,
			packet.Projector.Tint,
			new Vector4(
				Math.Clamp(packet.Projector.AlbedoOpacity, 0.0f, 1.0f),
				Math.Clamp(packet.Projector.NormalOpacity, 0.0f, 1.0f),
				Math.Clamp(packet.Projector.MaterialOpacity, 0.0f, 1.0f),
				Math.Clamp(packet.Projector.EmissiveOpacity, 0.0f, 1.0f)),
			new Vector4(
				packet.Projector.MaterialFactors,
				Math.Max(packet.Projector.EmissiveIntensity, 0.0f)),
			(uint)packet.Projector.ChannelMask,
			handles.AlbedoHandle.Value,
			handles.NormalHandle.Value,
			handles.MaterialHandle.Value,
			handles.EmissiveHandle.Value,
			handles.SamplerHandle.Value);
	}
}

internal readonly record struct DecalProjectorResolvedHandles(
	DescriptorHandle AlbedoHandle,
	DescriptorHandle NormalHandle,
	DescriptorHandle MaterialHandle,
	DescriptorHandle EmissiveHandle,
	DescriptorHandle SamplerHandle);
