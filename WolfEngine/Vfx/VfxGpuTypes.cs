using System.Numerics;
using System.Runtime.InteropServices;

namespace WolfEngine.Vfx;

/// <summary>Fixed budgets of the GPU particle system. The layouts below are mirrored in <c>Shaders/Vfx/vfx_common.slang</c>.</summary>
public static class VfxLimits
{
	/// <summary>Particle slots per world. 32768 particles take 2 MiB.</summary>
	public const int ParticlePoolCapacity = 32768;

	/// <summary>Emitter instances, sprite and ribbon, that can play at once in one world.</summary>
	public const int MaxEmitterSlots = 1024;

	/// <summary>Ribbon points uploaded per world per frame, after subdivision.</summary>
	public const int MaxRibbonPoints = 8192;

	/// <summary>Particles a single emitter instance may reserve.</summary>
	public const int MaxParticlesPerEmitter = 4096;
}

[Flags]
internal enum VfxEmitterFlags : uint
{
	None = 0,
	LocalSpace = 1u << 0,
	EmitFromEdge = 1u << 1,
	AlignGround = 1u << 2,
	AlignVelocity = 1u << 3,
	SpriteSheetRandomStart = 1u << 4,
	Ribbon = 1u << 5
}

/// <summary>
/// Everything the GPU needs to know about one playing emitter, indexed by emitter slot. Only four-component fields:
/// a float3 is 16-byte aligned on Metal but not in D3D12 structured buffers, so it would shift every later field.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct GpuVfxEmitter
{
	/// <summary>Current transform, with its translation relative to <see cref="Origin"/>.</summary>
	public Matrix4x4 LocalToOrigin;

	/// <summary>
	/// XYZ: world position particle positions are stored relative to. It is fixed for an emitter instance's life, so a
	/// world far from zero costs precision once per emitter rather than once per particle.
	/// </summary>
	public Vector4 Origin;

	/// <summary>XYZ: shape radius or half extents.</summary>
	public Vector4 ShapeSize;

	/// <summary>XYZ: shape centre in emitter space.</summary>
	public Vector4 ShapeOffset;

	/// <summary>XYZ: constant velocity. W: gravity.</summary>
	public Vector4 VelocityGravity;

	/// <summary>Lifetime min and max, speed min and max.</summary>
	public Vector4 LifetimeSpeed;

	/// <summary>Size min and max, rotation min and max in radians.</summary>
	public Vector4 SizeRotation;

	/// <summary>Angular velocity min and max in radians per second, drag, velocity inheritance.</summary>
	public Vector4 AngularDrag;

	/// <summary>Velocity stretch, intensity, soft particle distance, ribbon taper pivot.</summary>
	public Vector4 Render;

	public Vector4 StartColor;
	public Vector4 StartColorVariant;

	/// <summary>Sprite sheet columns, rows and frame rate.</summary>
	public Vector4 SpriteSheet;

	public uint Flags;
	public uint Shape;
	public uint Generation;
	public uint Padding0;

	public uint TextureHandle;
	public uint SamplerHandle;
	public uint Padding1;
	public uint Padding2;

	public fixed float SizeCurve[VfxCurve.BakedSampleCount];
	public fixed float ColorCurve[VfxCurve.BakedSampleCount * 4];
}

/// <summary>
/// A run of particles one emitter spawned since the last published frame. A Vector3 followed by a float packs into
/// one float4 on both backends, which is how the shader declares these pairs.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuVfxSpawnBatch
{
	public uint EmitterSlot;
	/// <summary>Index of this batch's first particle among all particles spawned this frame.</summary>
	public uint FirstSpawn;
	public uint Count;
	public uint Seed;

	public uint RingBase;
	public uint RingCapacity;
	/// <summary>Ring position of the first particle; the rest follow it, wrapping.</summary>
	public uint RingStart;
	public uint Padding0;

	/// <summary>Emitter position when the first particle spawned, relative to its origin.</summary>
	public Vector3 PositionStart;
	/// <summary>Seconds the first particle has already lived by the end of the frame.</summary>
	public float AgeStart;

	public Vector3 PositionEnd;
	public float AgeEnd;

	public Vector3 EmitterVelocity;
	public float Padding1;

	public Quaternion Rotation;
}

/// <summary>One sample of a ribbon's edge pair, relative to its emitter's origin.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuVfxRibbonPoint
{
	public Vector3 Root;
	/// <summary>0 at the head of the trail, 1 at the tail.</summary>
	public float Along;

	public Vector3 Tip;
	public float Padding0;
}

/// <summary>Particle state on the GPU. Only the GPU reads or writes it; the size is all the CPU needs.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct GpuVfxParticle
{
	public Vector3 Position;
	public float Age;
	public Vector3 Velocity;
	public float Lifetime;
	public float Size;
	public float Rotation;
	public float AngularVelocity;
	public uint EmitterSlotAndGeneration;
	public uint Color;
	public uint Seed;
	public uint Padding0;
	public uint Padding1;
}
