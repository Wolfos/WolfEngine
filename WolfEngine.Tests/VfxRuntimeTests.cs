using System.Numerics;
using System.Runtime.InteropServices;
using WolfEngine.ECS;
using WolfEngine.Rendering;
using WolfEngine.Vfx;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class VfxRuntimeTests
{
	private const float Frame = 1.0f / 60.0f;

	[Test]
	public void Burst_SpawnsOnce_ThenTheEffectFinishesWhenItsParticlesHaveDied()
	{
		var (vfx, world) = Create();
		var asset = Asset(Sprite(burst: 12, duration: 0.1f, lifetime: 0.5f));
		var handle = vfx.Play(world, asset, VfxPlacement.At(Vector3.Zero, Quaternion.Identity));

		var spawned = 0;
		for (var frame = 0; frame < 20; frame++)
		{
			vfx.Update(world, Frame);
			spawned += Extract(vfx, world).SpawnedParticleCount;
		}

		Assert.That(spawned, Is.EqualTo(12));
		Assert.That(vfx.IsPlaying(handle), Is.True, "particles are still alive");

		Run(vfx, world, 0.6f);

		Assert.Multiple(() =>
		{
			Assert.That(vfx.IsPlaying(handle), Is.False);
			Assert.That(vfx.GetStatistics(world).ReservedParticles, Is.Zero);
		});
	}

	[Test]
	public void ContinuousRate_SpawnsRateTimesElapsedTime()
	{
		var (vfx, world) = Create();
		vfx.Play(world, Asset(Sprite(rate: 30.0f, duration: 0.0f)), VfxPlacement.At(Vector3.Zero, Quaternion.Identity));

		var spawned = 0;
		for (var frame = 0; frame < 60; frame++)
		{
			vfx.Update(world, Frame);
			spawned += Extract(vfx, world).SpawnedParticleCount;
		}

		Assert.That(spawned, Is.InRange(29, 31));
	}

	[Test]
	public void DistanceEmission_SpawnsPerMetreTravelled_SpreadAlongThePath()
	{
		var (vfx, world) = Create();
		var asset = Asset(Sprite(rate: 0.0f, ratePerMetre: 2.0f, duration: 0.0f, maxParticles: 64));
		var handle = vfx.Play(world, asset, VfxPlacement.At(Vector3.Zero, Quaternion.Identity));

		var batches = new List<GpuVfxSpawnBatch>();
		for (var frame = 1; frame <= 60; frame++)
		{
			vfx.SetTransform(handle, new Vector3(frame * 0.05f, 0.0f, 0.0f), Quaternion.Identity);
			vfx.Update(world, Frame);
			batches.AddRange(Extract(vfx, world).Spawns);
		}

		var spawned = batches.Sum(batch => (int)batch.Count);
		var positions = batches.Select(batch => batch.PositionStart.X).ToArray();

		Assert.Multiple(() =>
		{
			Assert.That(spawned, Is.InRange(5, 6), "3 metres at 2 per metre");
			Assert.That(positions.Max() - positions.Min(), Is.GreaterThan(2.0f), "puffs are laid along the path, not in one place");
		});
	}

	[Test]
	public void DistanceEmission_StandingStill_SpawnsNothing()
	{
		var (vfx, world) = Create();
		vfx.Play(world, Asset(Sprite(rate: 0.0f, ratePerMetre: 4.0f, duration: 0.0f)), VfxPlacement.At(Vector3.One, Quaternion.Identity));

		var spawned = 0;
		for (var frame = 0; frame < 60; frame++)
		{
			vfx.Update(world, Frame);
			spawned += Extract(vfx, world).SpawnedParticleCount;
		}

		Assert.That(spawned, Is.Zero);
	}

	[Test]
	public void FullRing_DrawsEveryParticle_StartingFromTheOldest()
	{
		var (vfx, world) = Create();
		vfx.Play(world, Asset(Sprite(rate: 60.0f, duration: 0.0f, maxParticles: 8, lifetime: 10.0f)), VfxPlacement.At(Vector3.Zero, Quaternion.Identity));

		VfxWorldFrame frame = null!;
		var spawned = 0;
		for (var i = 0; i < 12; i++)
		{
			vfx.Update(world, Frame);
			frame = Extract(vfx, world);
			spawned += frame.SpawnedParticleCount;
		}

		Assume.That(spawned, Is.GreaterThan(8));
		var draw = frame.Draws.Single();
		Assert.Multiple(() =>
		{
			Assert.That(draw.Capacity, Is.EqualTo(8u));
			Assert.That(draw.Count, Is.EqualTo(8u), "a full ring draws all of its slots");
			Assert.That(draw.Start, Is.EqualTo((uint)(spawned % 8)), "the next slot to be overwritten holds the oldest particle");
		});
	}

	[Test]
	public void ExhaustedPool_RefusesNewEmitters_AndWarnsOnce()
	{
		var warnings = new List<string>();
		var runtime = new VfxWorldRuntime(warnings.Add);
		var world = new World(WorldTag.Game);
		var greedy = Asset(Sprite(burst: 1, duration: 0.1f, maxParticles: VfxLimits.MaxParticlesPerEmitter, lifetime: 10.0f));
		var effects = VfxLimits.ParticlePoolCapacity / VfxLimits.MaxParticlesPerEmitter;
		for (var i = 0; i <= effects + 1; i++)
		{
			runtime.Play(default, greedy, VfxPlacement.At(Vector3.Zero, Quaternion.Identity));
		}

		runtime.Update(world, Frame);

		Assert.Multiple(() =>
		{
			Assert.That(runtime.ReservedParticleCount, Is.EqualTo(VfxLimits.ParticlePoolCapacity));
			Assert.That(warnings, Has.Count.EqualTo(1));
		});
	}

	[Test]
	public void StopImmediately_ReleasesItsParticlesAndSlots()
	{
		var (vfx, world) = Create();
		var handle = vfx.Play(world, Asset(Sprite(rate: 30.0f, duration: 0.0f)), VfxPlacement.At(Vector3.Zero, Quaternion.Identity));
		Run(vfx, world, 0.2f);
		Assume.That(vfx.GetStatistics(world).ReservedParticles, Is.GreaterThan(0));

		vfx.Stop(handle, immediate: true);
		var frame = Extract(vfx, world);

		Assert.Multiple(() =>
		{
			Assert.That(vfx.IsPlaying(handle), Is.False);
			Assert.That(vfx.GetStatistics(world).ReservedParticles, Is.Zero);
			Assert.That(frame.Draws, Is.Empty);
			Assert.That(frame.EmitterCount, Is.Zero);
		});
	}

	[Test]
	public void StopGracefully_StopsEmitting_ButKeepsDrawingUntilParticlesDie()
	{
		var (vfx, world) = Create();
		var handle = vfx.Play(world, Asset(Sprite(rate: 30.0f, duration: 0.0f, lifetime: 0.5f)), VfxPlacement.At(Vector3.Zero, Quaternion.Identity));
		Run(vfx, world, 0.3f);

		vfx.Stop(handle);
		vfx.Update(world, Frame);
		var frame = Extract(vfx, world);

		Assert.Multiple(() =>
		{
			Assert.That(frame.SpawnedParticleCount, Is.Zero);
			Assert.That(frame.Draws, Has.Count.EqualTo(1));
			Assert.That(vfx.IsPlaying(handle), Is.True);
		});

		Run(vfx, world, 0.6f);

		Assert.That(vfx.IsPlaying(handle), Is.False);
	}

	[Test]
	public void AttachedEffect_FollowsItsEntity_AndStopsWhenTheEntityIsDestroyed()
	{
		var (vfx, world) = Create();
		var entity = world.CreateEntity("Holder", Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f));
		world.GetComponent<WorldTransform>(entity).LocalToWorld = Matrix4x4.CreateTranslation(3.0f, 0.0f, 0.0f);
		var handle = vfx.Play(world, Asset(Sprite(rate: 60.0f, duration: 0.0f, lifetime: 0.2f)), VfxPlacement.AttachedTo(entity, Matrix4x4.Identity));

		vfx.Update(world, Frame);
		var frame = Extract(vfx, world);
		Assert.That(frame.Draws.Single().SortPosition.X, Is.EqualTo(3.0f).Within(1e-4f));

		world.DestroyEntity(entity);
		Run(vfx, world, 0.5f);

		Assert.That(vfx.IsPlaying(handle), Is.False);
	}

	[Test]
	public void LiveEdits_ToAPlayingAsset_ApplyWithoutRestartingTheEffect()
	{
		var (vfx, world) = Create();
		var emitter = Sprite(rate: 10.0f, duration: 0.0f, maxParticles: 16);
		var asset = Asset(emitter);
		var handle = vfx.Play(world, asset, VfxPlacement.At(Vector3.Zero, Quaternion.Identity));
		Run(vfx, world, 0.2f);

		emitter.Sprite.MaxParticles = 32;
		asset.Emitters.Add(Sprite(rate: 10.0f, duration: 0.0f, maxParticles: 8));
		Run(vfx, world, 0.2f);

		Assert.Multiple(() =>
		{
			Assert.That(vfx.IsPlaying(handle), Is.True);
			Assert.That(vfx.GetStatistics(world).ReservedParticles, Is.EqualTo(40));
		});
	}

	[Test]
	public void Ribbon_SamplesEveryFrame_AndBreaksOnATeleport()
	{
		var (vfx, world) = Create();
		var ribbon = new VfxEmitterDefinition
		{
			Kind = VfxEmitterKind.Ribbon,
			Duration = 0.0f,
			Ribbon = new VfxRibbonSettings
			{
				Lifetime = 1.0f,
				MaxSamples = 64,
				Subdivisions = 0,
				MaxSegmentLength = 1.0f
			}
		};
		var handle = vfx.Play(world, Asset(ribbon), VfxPlacement.At(Vector3.Zero, Quaternion.Identity));

		VfxWorldFrame frame = null!;
		for (var i = 1; i <= 10; i++)
		{
			vfx.SetTransform(handle, new Vector3(i * 0.1f, 0.0f, 0.0f), Quaternion.Identity);
			vfx.Update(world, Frame);
			frame = Extract(vfx, world);
		}

		Assert.That(frame.Draws.Single().Count, Is.EqualTo(10u));

		vfx.SetTransform(handle, new Vector3(50.0f, 0.0f, 0.0f), Quaternion.Identity);
		vfx.Update(world, Frame);
		frame = Extract(vfx, world);

		Assert.That(frame.Draws, Is.Empty, "a lone sample after a teleport has no strip to draw");
	}

	[Test]
	public void Ribbon_SubdivisionAddsPointsBetweenFrames_HeadFirst()
	{
		var (vfx, world) = Create();
		var ribbon = new VfxEmitterDefinition
		{
			Kind = VfxEmitterKind.Ribbon,
			Duration = 0.0f,
			Ribbon = new VfxRibbonSettings
			{
				Lifetime = 1.0f,
				Subdivisions = 3
			}
		};
		var handle = vfx.Play(world, Asset(ribbon), VfxPlacement.At(Vector3.Zero, Quaternion.Identity));

		VfxWorldFrame frame = null!;
		for (var i = 1; i <= 4; i++)
		{
			vfx.SetTransform(handle, new Vector3(i * 0.1f, 0.0f, 0.0f), Quaternion.Identity);
			vfx.Update(world, Frame);
			frame = Extract(vfx, world);
		}

		var points = frame.RibbonPoints;
		Assert.Multiple(() =>
		{
			Assert.That(points, Has.Count.EqualTo(3 * 4 + 1), "three gaps of four points, then the tail");
			Assert.That(points[0].Along, Is.EqualTo(0.0f).Within(1e-4f), "the head is the newest sample");
			Assert.That(points[^1].Along, Is.GreaterThan(points[0].Along));
		});
	}

	[Test]
	public void ViewsOfTheSameWorld_ShareOneExtraction()
	{
		var (vfx, world) = Create();
		vfx.Play(world, Asset(Sprite(burst: 5, duration: 0.1f)), VfxPlacement.At(Vector3.Zero, Quaternion.Identity));
		vfx.Update(world, Frame);
		var snapshot = new FrameSnapshot();

		var first = snapshot.ExtractVfx(world, vfx, null);
		var second = snapshot.ExtractVfx(world, vfx, null);

		Assert.Multiple(() =>
		{
			Assert.That(second, Is.SameAs(first));
			Assert.That(first.SpawnedParticleCount, Is.EqualTo(5), "the spawns were extracted once, not split between views");
		});
	}

	[Test]
	public void VisualEffectComponent_PlaysWhileOn_AndStartsAgainWhenTurnedBackOn()
	{
		var vfx = new VfxService();
		var system = new VfxSystem(vfx);
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Effect", Matrix4x4.Identity);
		world.AddComponent(entity, new VisualEffect
		{
			Effect = new AssetPipeline.AssetRef<VfxAsset>
			{
				NodeId = Guid.NewGuid()
			}
		});

		system.PreRender(Frame, world);
		var first = vfx.GetRuntime(world).ComponentEffects[entity].Handle;
		system.PreRender(Frame, world);
		Assert.That(vfx.GetRuntime(world).ComponentEffects[entity].Handle, Is.EqualTo(first), "staying on keeps the effect");

		world.GetComponent<VisualEffect>(entity).Playing = false;
		system.PreRender(Frame, world);
		world.GetComponent<VisualEffect>(entity).Playing = true;
		system.PreRender(Frame, world);

		Assert.That(vfx.GetRuntime(world).ComponentEffects[entity].Handle, Is.Not.EqualTo(first));

		world.RemoveComponent<VisualEffect>(entity);
		system.PreRender(Frame, world);

		Assert.That(vfx.GetRuntime(world).ComponentEffects, Is.Empty);
	}

	[Test]
	public void RangeAllocator_MergesFreedNeighbours()
	{
		var allocator = new VfxRangeAllocator(100);
		Assume.That(allocator.TryAllocate(40, out var a), Is.True);
		Assume.That(allocator.TryAllocate(40, out var b), Is.True);
		Assume.That(allocator.TryAllocate(40, out _), Is.False);

		allocator.Free(a, 40);
		allocator.Free(b, 40);

		Assert.Multiple(() =>
		{
			Assert.That(allocator.TryAllocate(100, out var whole), Is.True);
			Assert.That(whole, Is.Zero);
		});
	}

	[Test]
	public void CurveAndGradient_InterpolateLinearlyBetweenKeys()
	{
		var curve = new VfxCurve(new VfxCurveKey(0.0f, 0.0f), new VfxCurveKey(0.5f, 1.0f), new VfxCurveKey(1.0f, 0.0f));
		var gradient = new VfxGradient(
			new VfxGradientKey(0.0f, new ColorRGBA(0.0f, 0.0f, 0.0f, 0.0f)),
			new VfxGradientKey(1.0f, new ColorRGBA(1.0f, 1.0f, 1.0f, 1.0f)));
		var baked = new float[VfxCurve.BakedSampleCount];
		curve.Bake(baked);

		Assert.Multiple(() =>
		{
			Assert.That(curve.Evaluate(0.25f), Is.EqualTo(0.5f).Within(1e-5f));
			Assert.That(curve.Evaluate(2.0f), Is.EqualTo(0.0f));
			Assert.That(gradient.Evaluate(0.5f).W, Is.EqualTo(0.5f).Within(1e-5f));
			Assert.That(baked[0], Is.EqualTo(0.0f));
			Assert.That(baked[^1], Is.EqualTo(0.0f).Within(1e-5f));
		});
	}

	[Test]
	public void GpuLayouts_MatchTheirShaderDeclarations()
	{
		// vfx_common.slang declares these with float4 and uint fields only; a size change here without the same change
		// there makes every later field read the wrong data on the GPU.
		Assert.Multiple(() =>
		{
			Assert.That(Marshal.SizeOf<GpuVfxEmitter>(), Is.EqualTo(64 + 11 * 16 + 8 * 4 + 16 * 4 + 64 * 4));
			Assert.That(Marshal.SizeOf<GpuVfxSpawnBatch>(), Is.EqualTo(96));
			Assert.That(Marshal.SizeOf<GpuVfxParticle>(), Is.EqualTo(64));
			Assert.That(Marshal.SizeOf<GpuVfxRibbonPoint>(), Is.EqualTo(32));
		});
	}

	private static (VfxService Vfx, World World) Create() => (new VfxService(), new World(WorldTag.Game));

	private static VfxAsset Asset(params VfxEmitterDefinition[] emitters) => new()
	{
		Emitters = emitters.ToList()
	};

	private static VfxEmitterDefinition Sprite(
		float rate = 0.0f,
		float ratePerMetre = 0.0f,
		int burst = 0,
		float duration = 1.0f,
		int maxParticles = 64,
		float lifetime = 0.5f)
	{
		var emitter = new VfxEmitterDefinition
		{
			Duration = duration,
			Sprite = new VfxSpriteSettings
			{
				Rate = rate,
				RatePerMetre = ratePerMetre,
				MaxParticles = maxParticles,
				Lifetime = new VfxFloatRange(lifetime)
			}
		};
		if (burst > 0)
		{
			emitter.Sprite.Bursts.Add(new VfxBurst(0.0f, burst));
		}

		return emitter;
	}

	private static VfxWorldFrame Extract(VfxService vfx, World world)
	{
		var frame = new VfxWorldFrame();
		vfx.Extract(world, frame, null);

		return frame;
	}

	private static void Run(VfxService vfx, World world, float seconds)
	{
		for (var elapsed = 0.0f; elapsed < seconds; elapsed += Frame)
		{
			vfx.Update(world, Frame);
			Extract(vfx, world);
		}
	}
}
