using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine;

public enum FogVolumeShape
{
	Box,
	Ellipsoid
}

/// <summary>Additive, transformable participating-media region.</summary>
public struct FogVolume : IEntityComponent
{
	public FogVolume()
	{
	}

	public bool Enabled = true;
	public FogVolumeShape Shape = FogVolumeShape.Box;
	public Vector3 Size = Vector3.One;
	public float Extinction = 0.05f;
	public Vector3 Albedo = Vector3.One;
	public float Anisotropy;
	public float BlendDistance = 0.1f;

	public readonly bool IsValid => Enabled &&
		Size.X > 0.0f && Size.Y > 0.0f && Size.Z > 0.0f && Extinction > 0.0f;
}
