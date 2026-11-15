using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Rendering;

namespace WolfEngine.Vfx;

/// <summary>
/// The effects playing in one world, on the game thread. It schedules emission, samples ribbons and decides where
/// every particle lives in the world's GPU pool; the particles themselves are only ever touched by the GPU.
/// </summary>
internal sealed class VfxWorldRuntime
{
	// The GPU integrates in short fixed steps, so any frame length gives the same arcs. Past this the particles have
	// long since died, so a longer gap, such as a hidden preview, is not worth the steps.
	private const float MaxSimulationStep = 1.0f;
	private const float DrainMargin = 0.05f;
	private const float DegreesToRadians = MathF.PI / 180.0f;

	private readonly List<EffectInstance> _instances = new();
	private readonly Stack<int> _freeInstances = new();
	private readonly VfxRangeAllocator _particleRanges = new(VfxLimits.ParticlePoolCapacity);
	private readonly ushort[] _emitterGenerations = new ushort[VfxLimits.MaxEmitterSlots];
	private readonly SortedSet<int> _freeEmitterSlots = new();
	private readonly List<EmitterState?> _emittersBySlot = new();
	private readonly List<PendingSpawn> _pendingSpawns = new();
	private readonly Action<string>? _warn;
	private readonly Random _seeds = new();
	private int _pendingSpawnCount;
	private double _time;
	private float _pendingDelta;
	private long _sequence;
	private bool _warnedPoolExhausted;
	private bool _warnedSlotsExhausted;

	public VfxWorldRuntime(Action<string>? warn = null)
	{
		_warn = warn;
		for (var slot = 0; slot < VfxLimits.MaxEmitterSlots; slot++)
		{
			_freeEmitterSlots.Add(slot);
		}
	}

	/// <summary><see cref="VisualEffect"/> components and the effect each one started, kept by <see cref="VfxSystem"/>.</summary>
	public Dictionary<Entity, VfxComponentEffect> ComponentEffects { get; } = new();

	/// <summary>Effects currently playing or fading out.</summary>
	public int ActiveEffectCount { get; private set; }

	/// <summary>Particle slots reserved by playing emitters, for budget diagnostics.</summary>
	public int ReservedParticleCount => _particleRanges.Capacity - _particleRanges.FreeCount;

	public VfxHandle Play(AssetRef<VfxAsset> effect, VfxAsset? asset, in VfxPlacement placement)
	{
		var slot = _freeInstances.Count > 0 ? _freeInstances.Pop() : AddInstanceSlot();
		var instance = _instances[slot];
		instance.Generation++;
		instance.Alive = true;
		instance.AssetId = effect.NodeId;
		instance.Asset = asset;
		instance.FixedAsset = asset is not null;
		instance.Attachment = placement.Attachment;
		instance.Offset = placement.Offset;
		instance.Transform = placement.Transform;
		instance.Elapsed = 0.0f;
		instance.Stopping = false;
		instance.Seed = (uint)_seeds.Next();
		ActiveEffectCount++;

		return new VfxHandle(this, slot, instance.Generation);
	}

	public bool IsPlaying(VfxHandle handle) => TryGetInstance(handle, out _);

	public void Stop(VfxHandle handle, bool immediate)
	{
		if (TryGetInstance(handle, out var instance) == false) return;

		if (immediate)
		{
			ReleaseInstance(handle.Slot);
			return;
		}

		instance.Stopping = true;
	}

	public void SetTransform(VfxHandle handle, Matrix4x4 transform)
	{
		if (TryGetInstance(handle, out var instance) == false) return;

		instance.Transform = transform;
	}

	public void StopAll(bool immediate)
	{
		for (var slot = 0; slot < _instances.Count; slot++)
		{
			if (_instances[slot].Alive == false)
			{
				continue;
			}

			if (immediate)
			{
				ReleaseInstance(slot);
			}
			else
			{
				_instances[slot].Stopping = true;
			}
		}
	}

	/// <summary>Advances every effect by <paramref name="deltaTime"/>. Runs after transforms are propagated.</summary>
	public void Update(World world, float deltaTime)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (float.IsFinite(deltaTime) == false || deltaTime < 0.0f)
		{
			deltaTime = 0.0f;
		}

		_time += deltaTime;
		_pendingDelta += deltaTime;

		for (var slot = 0; slot < _instances.Count; slot++)
		{
			var instance = _instances[slot];
			if (instance.Alive == false)
			{
				continue;
			}

			if (UpdateInstance(world, instance, deltaTime) == false)
			{
				ReleaseInstance(slot);
			}
		}
	}

	/// <summary>
	/// Hands everything that happened since the last extraction to the render thread. Called once per published
	/// frame, however many views show this world.
	/// </summary>
	public void Extract(VfxWorldFrame frame, IRenderResourceScheduler? resources)
	{
		_sequence++;
		frame.Sequence = _sequence;
		frame.DeltaTime = MathF.Min(_pendingDelta, MaxSimulationStep);
		_pendingDelta = 0.0f;

		ExtractEmitters(frame, resources);
		ExtractSpawns(frame);
		ExtractDraws(frame);
	}

	private int AddInstanceSlot()
	{
		_instances.Add(new EffectInstance());

		return _instances.Count - 1;
	}

	private bool TryGetInstance(VfxHandle handle, out EffectInstance instance)
	{
		instance = null!;
		if (ReferenceEquals(handle.Runtime, this) == false ||
		    handle.Slot < 0 ||
		    handle.Slot >= _instances.Count)
		{
			return false;
		}

		instance = _instances[handle.Slot];

		return instance.Alive && instance.Generation == handle.Generation;
	}

	private void ReleaseInstance(int slot)
	{
		var instance = _instances[slot];
		for (var i = 0; i < instance.Emitters.Count; i++)
		{
			ReleaseEmitter(instance.Emitters[i]);
		}

		instance.Emitters.Clear();
		instance.Alive = false;
		instance.Asset = null;
		_freeInstances.Push(slot);
		ActiveEffectCount--;
	}

	/// <summary>Returns false once the effect has finished and every particle it made has died.</summary>
	private bool UpdateInstance(World world, EffectInstance instance, float deltaTime)
	{
		var asset = ResolveAsset(instance);
		if (asset is null)
		{
			// An effect whose asset is missing or failed to load has nothing to show.
			return false;
		}

		if (instance.Attachment.IsValid)
		{
			if (world.IsAlive(instance.Attachment) &&
			    world.HasComponent<WorldTransform>(instance.Attachment))
			{
				instance.Transform = instance.Offset * world.GetComponent<WorldTransform>(instance.Attachment).LocalToWorld;
			}
			else
			{
				// The attachment is gone: stop emitting where it last was and let what exists fade out.
				instance.Attachment = default;
				instance.Stopping = true;
			}
		}

		SyncEmitterStates(instance, asset);
		instance.Elapsed += deltaTime;

		var anyAlive = false;
		for (var i = 0; i < instance.Emitters.Count; i++)
		{
			var state = instance.Emitters[i];
			var definition = asset.Emitters[i];
			state.Transform = instance.Transform;
			if (definition.Kind == VfxEmitterKind.Sprite)
			{
				UpdateSprite(instance, state, definition, i, deltaTime);
				anyAlive |= IsSpriteAlive(state, definition);
			}
			else
			{
				UpdateRibbon(instance, state, definition, deltaTime);
				anyAlive |= IsRibbonAlive(state);
			}
		}

		return anyAlive;
	}

	private static VfxAsset? ResolveAsset(EffectInstance instance)
	{
		if (instance.FixedAsset)
		{
			return instance.Asset;
		}

		// Resolved every frame, so a saved edit reaches effects that are already playing.
		VfxAsset? asset;
		try
		{
			asset = AssetDatabase.GetInstance<VfxAsset>(instance.AssetId);
		}
		catch (Exception)
		{
			asset = null;
		}

		if (ReferenceEquals(asset, instance.Asset) == false)
		{
			instance.Asset = asset;
		}

		return asset;
	}

	/// <summary>
	/// Matches emitter states to the asset's emitters. An emitter that was added, removed or swapped out, or whose
	/// budget changed, restarts; the editor relies on this to apply edits to a playing preview.
	/// </summary>
	private void SyncEmitterStates(EffectInstance instance, VfxAsset asset)
	{
		var emitters = asset.Emitters;
		for (var i = 0; i < emitters.Count; i++)
		{
			var definition = emitters[i];
			if (i < instance.Emitters.Count)
			{
				var existing = instance.Emitters[i];
				if (ReferenceEquals(existing.Definition, definition) &&
				    existing.Kind == definition.Kind &&
				    (existing.RangeLength == 0 || existing.RangeLength == GetParticleBudget(definition)))
				{
					continue;
				}

				ReleaseEmitter(existing);
				instance.Emitters[i] = CreateEmitterState(definition, instance.Transform);
				continue;
			}

			instance.Emitters.Add(CreateEmitterState(definition, instance.Transform));
		}

		while (instance.Emitters.Count > emitters.Count)
		{
			ReleaseEmitter(instance.Emitters[^1]);
			instance.Emitters.RemoveAt(instance.Emitters.Count - 1);
		}
	}

	private static EmitterState CreateEmitterState(VfxEmitterDefinition definition, Matrix4x4 transform)
	{
		return new EmitterState
		{
			Definition = definition,
			Kind = definition.Kind,
			Transform = transform,
			PreviousPosition = transform.Translation
		};
	}

	private static int GetParticleBudget(VfxEmitterDefinition definition) =>
		Math.Clamp(definition.Sprite.MaxParticles, 1, VfxLimits.MaxParticlesPerEmitter);

	/// <summary>Whether the emitter emits during the frame that ends at <paramref name="emitterTime"/>.</summary>
	private static bool IsEmitting(EffectInstance instance, VfxEmitterDefinition definition, float emitterTime, float deltaTime)
	{
		if (instance.Stopping || definition.Enabled == false || emitterTime <= 0.0f)
		{
			return false;
		}

		return definition.Duration <= 0.0f || definition.Looping || emitterTime - deltaTime < definition.Duration;
	}

	private static bool HasFinishedEmitting(EffectInstance instance, VfxEmitterDefinition definition, float emitterTime)
	{
		return instance.Stopping ||
		       definition.Enabled == false ||
		       (definition.Duration > 0.0f && definition.Looping == false && emitterTime >= definition.Duration);
	}

	private void UpdateSprite(
		EffectInstance instance,
		EmitterState state,
		VfxEmitterDefinition definition,
		int emitterIndex,
		float deltaTime)
	{
		var position = state.Transform.Translation;
		var previousPosition = state.HasPrevious ? state.PreviousPosition : position;
		state.PreviousPosition = position;
		state.HasPrevious = true;

		var settings = definition.Sprite;
		var end = instance.Elapsed - definition.StartDelay;
		var start = end - deltaTime;
		if (IsEmitting(instance, definition, end, deltaTime) == false)
		{
			state.EmissionFinished = HasFinishedEmitting(instance, definition, end);
			return;
		}

		state.EmissionFinished = false;
		var windowStart = MathF.Max(start, 0.0f);
		var windowEnd = definition.Duration > 0.0f && definition.Looping == false
			? MathF.Min(end, definition.Duration)
			: end;
		var window = MathF.Max(windowEnd - windowStart, 0.0f);

		var continuous = 0;
		state.RateAccumulator += MathF.Max(settings.Rate, 0.0f) * window;
		continuous += (int)state.RateAccumulator;
		state.RateAccumulator -= (int)state.RateAccumulator;

		if (settings.RatePerMetre > 0.0f)
		{
			state.DistanceAccumulator += Vector3.Distance(position, previousPosition) * settings.RatePerMetre;
			continuous += (int)state.DistanceAccumulator;
			state.DistanceAccumulator -= (int)state.DistanceAccumulator;
		}

		var burst = CountBursts(settings.Bursts, definition, windowStart, windowEnd);
		if (continuous == 0 && burst.Count == 0)
		{
			return;
		}

		if (EnsureSpriteAllocated(state, definition) == false)
		{
			return;
		}

		var velocity = deltaTime > 0.0f ? (position - previousPosition) / deltaTime : Vector3.Zero;
		var rotation = Quaternion.CreateFromRotationMatrix(state.Transform);
		var frameStart = _time - deltaTime;

		if (continuous > 0)
		{
			// Spread over the part of the frame the emitter was active, and along the path it travelled, so a fast
			// emitter leaves an even trail instead of one clump per frame.
			var firstFraction = deltaTime > 0.0f ? (windowStart - start) / deltaTime : 1.0f;
			var lastFraction = deltaTime > 0.0f ? (windowEnd - start) / deltaTime : 1.0f;
			QueueSpawn(
				instance,
				state,
				emitterIndex,
				continuous,
				frameStart + deltaTime * firstFraction,
				frameStart + deltaTime * lastFraction,
				Vector3.Lerp(previousPosition, position, firstFraction),
				Vector3.Lerp(previousPosition, position, lastFraction),
				velocity,
				rotation);
		}

		if (burst.Count > 0)
		{
			var fraction = deltaTime > 0.0f ? Math.Clamp((burst.Time - start) / deltaTime, 0.0f, 1.0f) : 1.0f;
			var burstTime = frameStart + deltaTime * fraction;
			var burstPosition = Vector3.Lerp(previousPosition, position, fraction);
			QueueSpawn(
				instance,
				state,
				emitterIndex,
				burst.Count,
				burstTime,
				burstTime,
				burstPosition,
				burstPosition,
				velocity,
				rotation);
		}
	}

	/// <summary>Bursts whose time falls in [start, end) of emitter time, including later loop cycles.</summary>
	private static (int Count, float Time) CountBursts(
		List<VfxBurst> bursts,
		VfxEmitterDefinition definition,
		float start,
		float end)
	{
		if (bursts.Count == 0 || end <= start)
		{
			return (0, 0.0f);
		}

		var count = 0;
		var firstTime = end;
		var cycleLength = definition.Looping && definition.Duration > 0.0f ? definition.Duration : float.PositiveInfinity;
		var cycle = float.IsPositiveInfinity(cycleLength) ? 0 : (int)MathF.Floor(start / cycleLength);
		while (true)
		{
			var cycleStart = float.IsPositiveInfinity(cycleLength) ? 0.0f : cycle * cycleLength;
			if (cycleStart >= end)
			{
				break;
			}

			for (var i = 0; i < bursts.Count; i++)
			{
				var time = cycleStart + bursts[i].Time;
				if (time >= start && time < end && bursts[i].Count > 0)
				{
					count += bursts[i].Count;
					firstTime = MathF.Min(firstTime, time);
				}
			}

			if (float.IsPositiveInfinity(cycleLength))
			{
				break;
			}

			cycle++;
		}

		return (count, firstTime);
	}

	private void QueueSpawn(
		EffectInstance instance,
		EmitterState state,
		int emitterIndex,
		int count,
		double timeStart,
		double timeEnd,
		Vector3 positionStart,
		Vector3 positionEnd,
		Vector3 velocity,
		Quaternion rotation)
	{
		count = Math.Min(count, state.RangeLength);
		if (_pendingSpawnCount + count > VfxLimits.ParticlePoolCapacity)
		{
			// Nothing has been published for a long time, such as a hidden preview. Spawning more than the pool
			// holds would only overwrite itself.
			return;
		}

		var seedBase = state.Definition.Seed != 0
			? (uint)state.Definition.Seed
			: instance.Seed;
		var batch = new GpuVfxSpawnBatch
		{
			EmitterSlot = (uint)state.Slot,
			Count = (uint)count,
			Seed = Hash(seedBase, (uint)emitterIndex, state.BatchCounter++),
			RingBase = (uint)state.RangeStart,
			RingCapacity = (uint)state.RangeLength,
			RingStart = (uint)state.RingCursor,
			PositionStart = positionStart - state.Origin,
			PositionEnd = positionEnd - state.Origin,
			EmitterVelocity = velocity,
			Rotation = rotation
		};
		_pendingSpawns.Add(new PendingSpawn(batch, timeStart, timeEnd, state.Slot, state.Generation));
		_pendingSpawnCount += count;

		state.RingCursor = (state.RingCursor + count) % state.RangeLength;
		state.TotalSpawned += count;
		state.LastSpawnTime = timeEnd;
	}

	private bool IsSpriteAlive(EmitterState state, VfxEmitterDefinition definition)
	{
		if (state.EmissionFinished == false)
		{
			return true;
		}

		return _time - state.LastSpawnTime < definition.Sprite.Lifetime.Largest + DrainMargin;
	}

	private void UpdateRibbon(EffectInstance instance, EmitterState state, VfxEmitterDefinition definition, float deltaTime)
	{
		var settings = definition.Ribbon;
		var end = instance.Elapsed - definition.StartDelay;
		var emitting = IsEmitting(instance, definition, end, deltaTime);
		state.EmissionFinished = emitting == false && HasFinishedEmitting(instance, definition, end);

		if (emitting && EnsureSlotAllocated(state))
		{
			var root = Vector3.Transform(settings.RootOffset, state.Transform);
			var tip = Vector3.Transform(settings.TipOffset, state.Transform);
			if (state.Samples.Count > 0)
			{
				var last = state.Samples[^1];
				var maxJump = MathF.Max(settings.MaxSegmentLength, 0.0f);
				if (maxJump > 0.0f &&
				    (Vector3.Distance(root, last.Root) > maxJump || Vector3.Distance(tip, last.Tip) > maxJump))
				{
					// A teleport or an animation snapping back to its first frame. Start a new trail here.
					state.Samples.Clear();
				}
			}

			if (state.Samples.Count == 0)
			{
				state.Origin = root;
			}

			state.Samples.Add(new RibbonSample(root, tip, _time));
		}

		var lifetime = MathF.Max(settings.Lifetime, 0.0f);
		var removeCount = 0;
		while (removeCount < state.Samples.Count && _time - state.Samples[removeCount].Time > lifetime)
		{
			removeCount++;
		}

		var maxSamples = Math.Max(settings.MaxSamples, 2);
		removeCount = Math.Max(removeCount, state.Samples.Count - maxSamples);
		if (removeCount > 0)
		{
			state.Samples.RemoveRange(0, removeCount);
		}
	}

	private static bool IsRibbonAlive(EmitterState state) => state.EmissionFinished == false || state.Samples.Count > 0;

	private bool EnsureSpriteAllocated(EmitterState state, VfxEmitterDefinition definition)
	{
		if (state.RangeStart >= 0)
		{
			return true;
		}

		var budget = GetParticleBudget(definition);
		if (_particleRanges.TryAllocate(budget, out var start) == false)
		{
			if (_warnedPoolExhausted == false)
			{
				_warnedPoolExhausted = true;
				_warn?.Invoke(
					$"VFX particle pool is full ({VfxLimits.ParticlePoolCapacity} particles); emitter '{definition.Name}' " +
					"needs " + budget + " and will not emit until playing effects finish. Lower Max Particles on busy emitters.");
			}

			return false;
		}

		if (EnsureSlotAllocated(state) == false)
		{
			_particleRanges.Free(start, budget);
			return false;
		}

		state.RangeStart = start;
		state.RangeLength = budget;
		state.RingCursor = 0;
		state.TotalSpawned = 0;
		state.Origin = state.Transform.Translation;

		return true;
	}

	private bool EnsureSlotAllocated(EmitterState state)
	{
		if (state.Slot >= 0)
		{
			return true;
		}

		if (_freeEmitterSlots.Count == 0)
		{
			if (_warnedSlotsExhausted == false)
			{
				_warnedSlotsExhausted = true;
				_warn?.Invoke($"More than {VfxLimits.MaxEmitterSlots} VFX emitters are playing in one world; new ones are skipped.");
			}

			return false;
		}

		var slot = _freeEmitterSlots.Min;
		_freeEmitterSlots.Remove(slot);

		// Zero is never a live generation, so a zeroed table entry matches no particle.
		var generation = (ushort)(_emitterGenerations[slot] + 1);
		if (generation == 0)
		{
			generation = 1;
		}

		_emitterGenerations[slot] = generation;
		while (_emittersBySlot.Count <= slot)
		{
			_emittersBySlot.Add(null);
		}

		_emittersBySlot[slot] = state;
		state.Slot = slot;
		state.Generation = generation;

		return true;
	}

	private void ReleaseEmitter(EmitterState state)
	{
		if (state.RangeStart >= 0)
		{
			_particleRanges.Free(state.RangeStart, state.RangeLength);
			state.RangeStart = -1;
			state.RangeLength = 0;
		}

		if (state.Slot >= 0)
		{
			_emittersBySlot[state.Slot] = null;
			_freeEmitterSlots.Add(state.Slot);
			state.Slot = -1;
		}

		state.Samples.Clear();
	}

	private void ExtractEmitters(VfxWorldFrame frame, IRenderResourceScheduler? resources)
	{
		var count = 0;
		for (var slot = _emittersBySlot.Count - 1; slot >= 0; slot--)
		{
			if (_emittersBySlot[slot] is not null)
			{
				count = slot + 1;
				break;
			}
		}

		frame.EnsureEmitterCapacity(count);
		frame.EmitterCount = count;
		for (var slot = 0; slot < count; slot++)
		{
			var state = _emittersBySlot[slot];
			if (state is null)
			{
				frame.Emitters[slot] = default;
				frame.EmitterTextures[slot] = null;
				continue;
			}

			var texture = ResolveTexture(state.Definition.Texture);
			if (texture is not null)
			{
				resources?.EnsureTextureResources(texture);
			}

			frame.EmitterTextures[slot] = texture;
			PackEmitter(ref frame.Emitters[slot], state);
		}
	}

	private static Texture? ResolveTexture(AssetRef<Texture> texture)
	{
		if (texture.IsValid == false)
		{
			return null;
		}

		try
		{
			return texture.Asset;
		}
		catch (Exception)
		{
			return null;
		}
	}

	private static unsafe void PackEmitter(ref GpuVfxEmitter target, EmitterState state)
	{
		var definition = state.Definition;
		var settings = definition.Sprite;
		var flags = VfxEmitterFlags.None;
		if (definition.Kind == VfxEmitterKind.Ribbon)
		{
			flags |= VfxEmitterFlags.Ribbon;
		}

		if (settings.SimulationSpace == VfxSimulationSpace.Local)
		{
			flags |= VfxEmitterFlags.LocalSpace;
		}

		if (settings.EmitFromEdge)
		{
			flags |= VfxEmitterFlags.EmitFromEdge;
		}

		if (settings.Alignment == VfxSpriteAlignment.Ground)
		{
			flags |= VfxEmitterFlags.AlignGround;
		}
		else if (settings.Alignment == VfxSpriteAlignment.Velocity)
		{
			flags |= VfxEmitterFlags.AlignVelocity;
		}

		if (settings.SpriteSheetRandomStart)
		{
			flags |= VfxEmitterFlags.SpriteSheetRandomStart;
		}

		var localToOrigin = state.Transform;
		localToOrigin.Translation -= state.Origin;

		target.LocalToOrigin = localToOrigin;
		target.Origin = new Vector4(state.Origin, 0.0f);
		target.ShapeSize = new Vector4(settings.ShapeSize, 0.0f);
		target.ShapeOffset = new Vector4(settings.Offset, 0.0f);
		target.VelocityGravity = new Vector4(settings.Velocity, settings.Gravity);
		target.LifetimeSpeed = new Vector4(
			MathF.Max(settings.Lifetime.Min, 0.01f),
			MathF.Max(settings.Lifetime.Max, 0.01f),
			settings.Speed.Min,
			settings.Speed.Max);
		target.SizeRotation = new Vector4(
			settings.Size.Min,
			settings.Size.Max,
			settings.Rotation.Min * DegreesToRadians,
			settings.Rotation.Max * DegreesToRadians);
		target.AngularDrag = new Vector4(
			settings.AngularVelocity.Min * DegreesToRadians,
			settings.AngularVelocity.Max * DegreesToRadians,
			MathF.Max(settings.Drag, 0.0f),
			settings.InheritVelocity);
		target.Render = new Vector4(
			MathF.Max(settings.VelocityStretch, 0.0f),
			MathF.Max(definition.Intensity, 0.0f),
			MathF.Max(definition.SoftParticleDistance, 0.0f),
			Math.Clamp(definition.Ribbon.TaperPivot, 0.0f, 1.0f));
		target.StartColor = settings.StartColor;
		target.StartColorVariant = settings.StartColorVariant;
		target.SpriteSheet = new Vector4(
			Math.Clamp(settings.SpriteSheetColumns, 1, 64),
			Math.Clamp(settings.SpriteSheetRows, 1, 64),
			MathF.Max(settings.SpriteSheetFrameRate, 0.0f),
			0.0f);
		target.Flags = (uint)flags;
		target.Shape = (uint)settings.Shape;
		target.Generation = state.Generation;

		// Handles are filled in on the render thread, which owns the bindless table.
		target.TextureHandle = 0;
		target.SamplerHandle = 0;

		var sizeCurve = definition.Kind == VfxEmitterKind.Ribbon
			? definition.Ribbon.WidthOverLength
			: settings.SizeOverLifetime;
		fixed (float* size = target.SizeCurve)
		{
			sizeCurve.Bake(new Span<float>(size, VfxCurve.BakedSampleCount));
		}

		fixed (float* color = target.ColorCurve)
		{
			definition.ColorOverLifetime.Bake(new Span<Vector4>(color, VfxCurve.BakedSampleCount));
		}
	}

	private void ExtractSpawns(VfxWorldFrame frame)
	{
		frame.Spawns.Clear();
		var spawned = 0u;
		for (var i = 0; i < _pendingSpawns.Count; i++)
		{
			var pending = _pendingSpawns[i];

			// The emitter may have been stopped immediately, or restarted by an edit, since this was queued.
			if (pending.Slot >= _emittersBySlot.Count ||
			    _emittersBySlot[pending.Slot] is not { } state ||
			    state.Generation != pending.Generation)
			{
				continue;
			}

			var batch = pending.Batch;
			batch.FirstSpawn = spawned;
			batch.AgeStart = (float)Math.Max(_time - pending.TimeStart, 0.0);
			batch.AgeEnd = (float)Math.Max(_time - pending.TimeEnd, 0.0);
			frame.Spawns.Add(batch);
			spawned += batch.Count;
		}

		frame.SpawnedParticleCount = (int)spawned;
		_pendingSpawns.Clear();
		_pendingSpawnCount = 0;
	}

	private void ExtractDraws(VfxWorldFrame frame)
	{
		frame.Draws.Clear();
		frame.RibbonPoints.Clear();
		for (var slot = 0; slot < _emittersBySlot.Count; slot++)
		{
			if (_emittersBySlot[slot] is not { } state)
			{
				continue;
			}

			if (state.Kind == VfxEmitterKind.Sprite)
			{
				if (state.TotalSpawned == 0 || state.RangeStart < 0)
				{
					continue;
				}

				var capacity = (uint)state.RangeLength;
				var full = state.TotalSpawned >= state.RangeLength;
				frame.Draws.Add(new VfxDrawPacket(
					VfxEmitterKind.Sprite,
					state.Definition.Blend,
					(uint)slot,
					(uint)state.RangeStart,
					capacity,
					full ? (uint)state.RingCursor : 0u,
					full ? capacity : (uint)state.TotalSpawned,
					state.Transform.Translation));
				continue;
			}

			var first = frame.RibbonPoints.Count;
			AppendRibbonPoints(frame.RibbonPoints, state);
			var pointCount = frame.RibbonPoints.Count - first;
			if (pointCount < 2)
			{
				frame.RibbonPoints.RemoveRange(first, pointCount);
				continue;
			}

			frame.Draws.Add(new VfxDrawPacket(
				VfxEmitterKind.Ribbon,
				state.Definition.Blend,
				(uint)slot,
				(uint)first,
				0u,
				0u,
				(uint)pointCount,
				state.Samples[^1].Root));
		}
	}

	/// <summary>
	/// Writes the trail head first, inserting Catmull-Rom points between frames. A fast swing covers a wide arc
	/// between two frames, and without them the trail shows that arc as straight facets.
	/// </summary>
	private void AppendRibbonPoints(List<GpuVfxRibbonPoint> points, EmitterState state)
	{
		var samples = state.Samples;
		if (samples.Count < 2)
		{
			return;
		}

		var settings = state.Definition.Ribbon;
		var lifetime = MathF.Max(settings.Lifetime, 1e-3f);
		var subdivisions = Math.Clamp(settings.Subdivisions, 0, 8);
		var remaining = VfxLimits.MaxRibbonPoints - points.Count;
		if (remaining < 2)
		{
			return;
		}

		for (var i = samples.Count - 1; i >= 1; i--)
		{
			var a = samples[Math.Min(i + 1, samples.Count - 1)];
			var b = samples[i];
			var c = samples[i - 1];
			var d = samples[Math.Max(i - 2, 0)];
			for (var step = 0; step <= subdivisions; step++)
			{
				var t = step / (float)(subdivisions + 1);
				var root = CatmullRom(a.Root, b.Root, c.Root, d.Root, t);
				var tip = CatmullRom(a.Tip, b.Tip, c.Tip, d.Tip, t);
				var time = b.Time + (c.Time - b.Time) * t;
				points.Add(CreatePoint(state, root, tip, time, lifetime));
				if (--remaining < 2)
				{
					break;
				}
			}

			if (remaining < 2)
			{
				break;
			}
		}

		var tail = samples[0];
		points.Add(CreatePoint(state, tail.Root, tail.Tip, tail.Time, lifetime));
	}

	private GpuVfxRibbonPoint CreatePoint(EmitterState state, Vector3 root, Vector3 tip, double time, float lifetime)
	{
		return new GpuVfxRibbonPoint
		{
			Root = root - state.Origin,
			Tip = tip - state.Origin,
			Along = Math.Clamp((float)((_time - time) / lifetime), 0.0f, 1.0f)
		};
	}

	private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
	{
		var t2 = t * t;
		var t3 = t2 * t;

		return 0.5f * (2.0f * p1 +
		               (p2 - p0) * t +
		               (2.0f * p0 - 5.0f * p1 + 4.0f * p2 - p3) * t2 +
		               (3.0f * p1 - p0 - 3.0f * p2 + p3) * t3);
	}

	private static uint Hash(uint a, uint b, uint c)
	{
		var hash = a * 0x9E3779B1u;
		hash ^= b + 0x7F4A7C15u + (hash << 6) + (hash >> 2);
		hash ^= c + 0x165667B1u + (hash << 6) + (hash >> 2);
		hash ^= hash >> 15;
		hash *= 0x2C1B3C6Du;
		hash ^= hash >> 12;

		return hash;
	}

	private sealed class EffectInstance
	{
		public int Generation;
		public bool Alive;
		public Guid AssetId;
		public VfxAsset? Asset;

		/// <summary>The asset was handed in directly, such as the editor's unsaved copy, and is never re-resolved.</summary>
		public bool FixedAsset;

		public Entity Attachment;
		public Matrix4x4 Offset = Matrix4x4.Identity;
		public Matrix4x4 Transform = Matrix4x4.Identity;
		public float Elapsed;
		public bool Stopping;
		public uint Seed;
		public readonly List<EmitterState> Emitters = new();
	}

	private sealed class EmitterState
	{
		public VfxEmitterDefinition Definition = null!;
		public VfxEmitterKind Kind;
		public int Slot = -1;
		public ushort Generation;
		public int RangeStart = -1;
		public int RangeLength;
		public int RingCursor;
		public long TotalSpawned;
		public uint BatchCounter;
		public float RateAccumulator;
		public float DistanceAccumulator;
		public Vector3 PreviousPosition;
		public bool HasPrevious;
		public Vector3 Origin;
		public Matrix4x4 Transform = Matrix4x4.Identity;
		public double LastSpawnTime = double.NegativeInfinity;
		public bool EmissionFinished;
		public readonly List<RibbonSample> Samples = new();
	}

	private readonly record struct RibbonSample(Vector3 Root, Vector3 Tip, double Time);

	private readonly record struct PendingSpawn(
		GpuVfxSpawnBatch Batch,
		double TimeStart,
		double TimeEnd,
		int Slot,
		ushort Generation);
}

/// <summary>Where an effect plays: a fixed transform, or an entity it follows.</summary>
public readonly struct VfxPlacement
{
	private VfxPlacement(Entity attachment, Matrix4x4 offset, Matrix4x4 transform)
	{
		Attachment = attachment;
		Offset = offset;
		Transform = transform;
	}

	public Entity Attachment { get; }

	/// <summary>Transform relative to <see cref="Attachment"/>.</summary>
	public Matrix4x4 Offset { get; }

	/// <summary>World transform when there is no attachment; the attachment's first transform otherwise.</summary>
	public Matrix4x4 Transform { get; }

	public static VfxPlacement At(Vector3 position, Quaternion rotation) =>
		new(default, Matrix4x4.Identity, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));

	public static VfxPlacement At(Matrix4x4 transform) => new(default, Matrix4x4.Identity, transform);

	public static VfxPlacement AttachedTo(Entity entity, Matrix4x4 offset) => new(entity, offset, Matrix4x4.Identity);
}
