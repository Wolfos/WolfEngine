using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Vfx;

/// <summary>One emitter instance to draw this frame.</summary>
internal readonly struct VfxDrawPacket
{
	public VfxDrawPacket(
		VfxEmitterKind kind,
		VfxBlendMode blend,
		uint emitterSlot,
		uint first,
		uint capacity,
		uint start,
		uint count,
		Vector3 sortPosition)
	{
		Kind = kind;
		Blend = blend;
		EmitterSlot = emitterSlot;
		First = first;
		Capacity = capacity;
		Start = start;
		Count = count;
		SortPosition = sortPosition;
	}

	public VfxEmitterKind Kind { get; }
	public VfxBlendMode Blend { get; }
	public uint EmitterSlot { get; }

	/// <summary>Sprites: the emitter's first particle slot. Ribbons: its first point this frame.</summary>
	public uint First { get; }

	/// <summary>Sprites: the emitter's ring size. Unused for ribbons.</summary>
	public uint Capacity { get; }

	/// <summary>Sprites: the ring position of the oldest particle, so particles draw oldest first.</summary>
	public uint Start { get; }

	/// <summary>Sprites: particles that may be alive. Ribbons: points.</summary>
	public uint Count { get; }

	/// <summary>World position used to sort alpha-blended emitters back to front.</summary>
	public Vector3 SortPosition { get; }
}

/// <summary>
/// What one world's effects need on the render thread for one published frame. A world shown in several views is
/// extracted once per frame, so its particles are spawned and simulated once however many views draw them.
/// </summary>
internal sealed class VfxWorldFrame
{
	public World? World { get; private set; }

	/// <summary>Increases by one for every extraction, so the render thread simulates each frame exactly once.</summary>
	public long Sequence { get; set; }

	/// <summary>Simulation time that passed since the previous extraction.</summary>
	public float DeltaTime { get; set; }

	/// <summary>Emitter table indexed by slot, up to the highest slot in use.</summary>
	public GpuVfxEmitter[] Emitters { get; private set; } = new GpuVfxEmitter[64];

	/// <summary>Each slot's texture, resolved on the game thread; the render thread turns it into a handle.</summary>
	public Texture?[] EmitterTextures { get; private set; } = new Texture?[64];

	public int EmitterCount { get; set; }

	public List<GpuVfxSpawnBatch> Spawns { get; } = new();

	public int SpawnedParticleCount { get; set; }

	public List<VfxDrawPacket> Draws { get; } = new();

	public List<GpuVfxRibbonPoint> RibbonPoints { get; } = new();

	/// <summary>Clears the frame for reuse. Pass null when the frame is pooled, so it does not keep a world alive.</summary>
	public void Reset(World? world)
	{
		World = world;
		Sequence = 0;
		DeltaTime = 0.0f;
		Array.Clear(Emitters, 0, EmitterCount);
		Array.Clear(EmitterTextures, 0, EmitterCount);
		EmitterCount = 0;
		Spawns.Clear();
		SpawnedParticleCount = 0;
		Draws.Clear();
		RibbonPoints.Clear();
	}

	public void EnsureEmitterCapacity(int count)
	{
		if (Emitters.Length >= count)
		{
			return;
		}

		var capacity = Emitters.Length;
		while (capacity < count)
		{
			capacity *= 2;
		}

		var emitters = Emitters;
		var textures = EmitterTextures;
		Array.Resize(ref emitters, capacity);
		Array.Resize(ref textures, capacity);
		Emitters = emitters;
		EmitterTextures = textures;
	}
}
