using System.Numerics;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;

namespace WolfEngine.Vfx;

/// <summary>
/// A complete visual effect: one or more emitters that start together and run on their own timings. Describes what
/// the effect looks like only; how particles are stored, simulated and drawn is the renderer's business.
/// </summary>
[RuntimeAsset(AssetType.DataAsset, typeof(VfxAsset), typeof(IDataAssetRuntimeResolver))]
public sealed class VfxAsset : IDataAsset
{
	[JsonInclude]
	public List<VfxEmitterDefinition> Emitters { get; set; } =
	[
		new()
	];
}

public enum VfxEmitterKind
{
	/// <summary>GPU-simulated particles drawn as textured quads.</summary>
	Sprite,

	/// <summary>A connected strip built from the attachment's recent positions, such as a sword trail.</summary>
	Ribbon
}

public enum VfxBlendMode
{
	/// <summary>Adds light. Needs no sorting, so it suits sparks, flashes and glows.</summary>
	Additive,

	/// <summary>Conventional transparency. Emitters are sorted back to front; see the VFX manual for the limits.</summary>
	AlphaBlend
}

public enum VfxSpawnShape
{
	Point,
	Sphere,
	Box,

	/// <summary>A disc in the emitter's XZ plane, so a ring of dust or sparks lies flat on the ground.</summary>
	Circle
}

public enum VfxSimulationSpace
{
	/// <summary>Particles stay where they were spawned when the emitter moves. Dust and sparks.</summary>
	World,

	/// <summary>Particles move with the emitter. Auras and effects glued to a character.</summary>
	Local
}

public enum VfxSpriteAlignment
{
	/// <summary>Always faces the camera.</summary>
	Camera,

	/// <summary>Lies flat in the emitter's XZ plane, facing up. Shockwave rings and ground puffs.</summary>
	Ground,

	/// <summary>Stretched along the particle's motion. Sparks and streaks.</summary>
	Velocity
}

public sealed class VfxEmitterDefinition
{
	public string Name { get; set; } = "Emitter";
	public bool Enabled { get; set; } = true;
	public VfxEmitterKind Kind { get; set; } = VfxEmitterKind.Sprite;

	/// <summary>Seconds after the effect starts before this emitter begins.</summary>
	public float StartDelay { get; set; }

	/// <summary>Seconds the emitter emits or samples for. Zero or less means until the effect is stopped.</summary>
	public float Duration { get; set; } = 1.0f;

	/// <summary>Restart after <see cref="Duration"/>, replaying bursts. Ignored when the duration is unlimited.</summary>
	public bool Looping { get; set; }

	/// <summary>Seeds the randomness so every play looks the same. Zero picks a new seed for each play.</summary>
	public int Seed { get; set; }

	public AssetRef<Texture> Texture { get; set; }
	public VfxBlendMode Blend { get; set; } = VfxBlendMode.AlphaBlend;

	/// <summary>Multiplies the colour, so values above one push the effect into bloom.</summary>
	public float Intensity { get; set; } = 1.0f;

	/// <summary>Colour and opacity over a particle's life, or along a ribbon from head to tail.</summary>
	public VfxGradient ColorOverLifetime { get; set; } = new();

	/// <summary>
	/// Fades the effect out where it meets opaque geometry over this many metres, hiding hard intersections. Zero
	/// disables it.
	/// </summary>
	public float SoftParticleDistance { get; set; } = 0.2f;

	public VfxSpriteSettings Sprite { get; set; } = new();
	public VfxRibbonSettings Ribbon { get; set; } = new();

	/// <summary>Seconds from the effect's start until this emitter can no longer produce anything visible.</summary>
	[JsonIgnore]
	public float VisibleEnd
	{
		get
		{
			if (Duration <= 0.0f || Looping)
			{
				return float.PositiveInfinity;
			}

			var trail = Kind == VfxEmitterKind.Sprite
				? Sprite.Lifetime.Largest
				: Ribbon.Lifetime;

			return MathF.Max(StartDelay, 0.0f) + Duration + MathF.Max(trail, 0.0f);
		}
	}
}

public struct VfxBurst
{
	public VfxBurst(float time, int count)
	{
		Time = time;
		Count = count;
	}

	/// <summary>Seconds after the emitter starts.</summary>
	public float Time { get; set; }

	public int Count { get; set; }
}

public sealed class VfxSpriteSettings
{
	/// <summary>
	/// Most particles this emitter keeps alive at once. It reserves that many slots for every playing instance;
	/// once they are all in use, a new particle replaces the oldest.
	/// </summary>
	public int MaxParticles { get; set; } = 64;

	/// <summary>Particles per second.</summary>
	public float Rate { get; set; } = 10.0f;

	/// <summary>Particles per metre the emitter travels, for footstep dust and other movement effects.</summary>
	public float RatePerMetre { get; set; }

	[JsonInclude]
	public List<VfxBurst> Bursts { get; set; } = new();

	public VfxSpawnShape Shape { get; set; } = VfxSpawnShape.Point;

	/// <summary>Radius in X for spheres and circles; half extents for boxes.</summary>
	public Vector3 ShapeSize { get; set; } = new(0.25f, 0.25f, 0.25f);

	/// <summary>Moves the shape's centre in the emitter's space, such as up from a character's feet.</summary>
	public Vector3 Offset { get; set; }

	/// <summary>Spawn on the sphere's surface or the circle's edge instead of throughout.</summary>
	public bool EmitFromEdge { get; set; }

	/// <summary>Seconds each particle lives.</summary>
	public VfxFloatRange Lifetime { get; set; } = new(0.6f, 1.0f);

	/// <summary>Speed away from the shape's centre, in metres per second. Bursts of this make starbursts.</summary>
	public VfxFloatRange Speed { get; set; } = new(0.5f, 1.0f);

	/// <summary>Velocity added to every particle, in the emitter's space.</summary>
	public Vector3 Velocity { get; set; }

	/// <summary>Share of the emitter's own velocity a particle keeps when spawned.</summary>
	public float InheritVelocity { get; set; }

	/// <summary>Downward acceleration in metres per second squared. Negative values make particles rise.</summary>
	public float Gravity { get; set; }

	/// <summary>How quickly particles lose speed, per second.</summary>
	public float Drag { get; set; }

	public VfxSimulationSpace SimulationSpace { get; set; } = VfxSimulationSpace.World;

	/// <summary>Size in metres.</summary>
	public VfxFloatRange Size { get; set; } = new(0.25f, 0.4f);

	public VfxCurve SizeOverLifetime { get; set; } = new();

	/// <summary>Starting rotation in degrees.</summary>
	public VfxFloatRange Rotation { get; set; } = new(0.0f, 360.0f);

	/// <summary>Spin in degrees per second.</summary>
	public VfxFloatRange AngularVelocity { get; set; }

	/// <summary>Each particle's colour is picked between this and <see cref="StartColorVariant"/>.</summary>
	public ColorRGBA StartColor { get; set; } = ColorRGBA.White;

	public ColorRGBA StartColorVariant { get; set; } = ColorRGBA.White;

	public VfxSpriteAlignment Alignment { get; set; } = VfxSpriteAlignment.Camera;

	/// <summary>Extra length per metre per second of speed for velocity-aligned sprites.</summary>
	public float VelocityStretch { get; set; } = 0.05f;

	/// <summary>Columns and rows of a sprite sheet texture. One by one is a plain texture.</summary>
	public int SpriteSheetColumns { get; set; } = 1;

	public int SpriteSheetRows { get; set; } = 1;

	/// <summary>Frames per second. Zero plays the sheet once over each particle's life.</summary>
	public float SpriteSheetFrameRate { get; set; }

	public bool SpriteSheetRandomStart { get; set; }
}

public sealed class VfxRibbonSettings
{
	/// <summary>One edge of the ribbon in the attachment's space, such as the blade's root.</summary>
	public Vector3 RootOffset { get; set; }

	/// <summary>The other edge, such as the blade's tip.</summary>
	public Vector3 TipOffset { get; set; } = new(0.0f, 1.0f, 0.0f);

	/// <summary>Seconds a sample stays in the trail.</summary>
	public float Lifetime { get; set; } = 0.2f;

	/// <summary>Most samples the trail remembers. Old samples drop off first.</summary>
	public int MaxSamples { get; set; } = 32;

	/// <summary>Smoothed points inserted between frames so fast swings stay round instead of faceted.</summary>
	public int Subdivisions { get; set; } = 3;

	/// <summary>Width over the trail's length from head to tail, scaling the root-to-tip edge about the pivot.</summary>
	public VfxCurve WidthOverLength { get; set; } = new(new VfxCurveKey(0.0f, 1.0f), new VfxCurveKey(1.0f, 0.0f));

	/// <summary>Where along the root-to-tip edge width scaling pivots: 0 is the root, 1 the tip.</summary>
	public float TaperPivot { get; set; } = 1.0f;

	/// <summary>
	/// A jump longer than this many metres between frames breaks the trail rather than drawing a streak across the
	/// screen, for teleports and animation resets.
	/// </summary>
	public float MaxSegmentLength { get; set; } = 3.0f;
}
