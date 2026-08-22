using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine;

[Flags]
public enum DecalChannelMask : uint
{
	None = 0,
	Albedo = 1 << 0,
	Normal = 1 << 1,
	Material = 1 << 2,
	Emissive = 1 << 3
}

public struct DecalProjector : IEntityComponent
{
	public DecalProjector()
	{
	}

	public bool Enabled = true;
	public Vector3 Size = Vector3.One;
	public Vector4 UvScaleOffset = new(1.0f, 1.0f, 0.0f, 0.0f);
	public ColorRGBA Tint = ColorRGBA.White;
	public Texture? AlbedoTexture;
	public Texture? NormalTexture;
	public Texture? MaterialTexture;
	public Texture? EmissiveTexture;
	public Vector3 MaterialFactors = new(1.0f, 1.0f, 0.0f);
	public float EmissiveIntensity = 1.0f;
	public float AlbedoOpacity = 1.0f;
	public float NormalOpacity = 1.0f;
	public float MaterialOpacity = 1.0f;
	public float EmissiveOpacity = 0.0f;
	public DecalChannelMask ChannelMask = DecalChannelMask.Albedo;

	public readonly bool IsEnabled => Enabled;

	public readonly bool IsValid
	{
		get
		{
			if (Enabled == false ||
			    Size.X <= 0.0f ||
			    Size.Y <= 0.0f ||
			    Size.Z <= 0.0f ||
			    ChannelMask == DecalChannelMask.None)
			{
				return false;
			}

			if ((ChannelMask & DecalChannelMask.Albedo) != 0 &&
			    (AlbedoTexture is null || AlbedoOpacity <= 0.0f))
			{
				return false;
			}

			if ((ChannelMask & DecalChannelMask.Normal) != 0 &&
			    (NormalTexture is null || NormalOpacity <= 0.0f))
			{
				return false;
			}

			if ((ChannelMask & DecalChannelMask.Material) != 0 &&
			    (MaterialTexture is null || MaterialOpacity <= 0.0f))
			{
				return false;
			}

			if ((ChannelMask & DecalChannelMask.Emissive) != 0 &&
			    (EmissiveTexture is null || EmissiveOpacity <= 0.0f))
			{
				return false;
			}

			return true;
		}
	}

	public readonly void EnsureTextureResources(IRenderResourceScheduler resourceScheduler)
	{
		ArgumentNullException.ThrowIfNull(resourceScheduler);

		if (AlbedoTexture is not null)
		{
			resourceScheduler.EnsureTextureResources(AlbedoTexture);
		}

		if (NormalTexture is not null)
		{
			resourceScheduler.EnsureTextureResources(NormalTexture);
		}

		if (MaterialTexture is not null)
		{
			resourceScheduler.EnsureTextureResources(MaterialTexture);
		}

		if (EmissiveTexture is not null)
		{
			resourceScheduler.EnsureTextureResources(EmissiveTexture);
		}
	}
}
