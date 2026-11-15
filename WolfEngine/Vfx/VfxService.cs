using System.Numerics;
using System.Runtime.CompilerServices;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Logging;
using WolfEngine.Rendering;

namespace WolfEngine.Vfx;

/// <summary>
/// Plays visual effects. Get it from the service provider. Effects are fire-and-forget: they clean themselves up
/// when they finish, and the returned handle is only needed to stop or move a long-running one.
/// </summary>
public interface IVfxService
{
	/// <summary>Plays an effect at a fixed position, for impacts and other one-shots.</summary>
	VfxHandle Play(World world, AssetRef<VfxAsset> effect, Vector3 position, Quaternion rotation);

	/// <summary>
	/// Plays an effect that follows an entity. To follow a bone, attach to the bone's socket entity (see
	/// <c>ExposedBone</c>). The effect stops emitting if the entity is destroyed.
	/// </summary>
	VfxHandle Play(World world, AssetRef<VfxAsset> effect, Entity attachTo);

	VfxHandle Play(World world, AssetRef<VfxAsset> effect, in VfxPlacement placement);

	/// <summary>Plays an asset object directly, such as an unsaved copy in an editor. It is never re-resolved.</summary>
	VfxHandle Play(World world, VfxAsset effect, in VfxPlacement placement);

	/// <summary>
	/// Stops emitting. Particles already alive finish their lives unless <paramref name="immediate"/> is set.
	/// Stale or invalid handles are ignored.
	/// </summary>
	void Stop(VfxHandle handle, bool immediate = false);

	/// <summary>Whether the effect is still emitting or has particles alive.</summary>
	bool IsPlaying(VfxHandle handle);

	/// <summary>Moves an effect that was not attached to an entity.</summary>
	void SetTransform(VfxHandle handle, Vector3 position, Quaternion rotation);

	void StopAll(World world, bool immediate = false);

	/// <summary>
	/// Advances a world's effects. <see cref="VfxSystem"/> does this for game worlds; private worlds, such as editor
	/// previews, call it themselves after their transforms are up to date.
	/// </summary>
	void Update(World world, float deltaTime);

	VfxStatistics GetStatistics(World world);
}

public readonly record struct VfxStatistics(int ActiveEffects, int ReservedParticles, int ParticleCapacity);

public sealed class VfxService : IVfxService
{
	private readonly ConditionalWeakTable<World, VfxWorldRuntime> _runtimes = new();
	private readonly ILogService? _log;

	public VfxService()
	{
	}

	public VfxService(ILogService log)
	{
		_log = log;
	}

	public VfxHandle Play(World world, AssetRef<VfxAsset> effect, Vector3 position, Quaternion rotation) =>
		Play(world, effect, VfxPlacement.At(position, rotation));

	public VfxHandle Play(World world, AssetRef<VfxAsset> effect, Entity attachTo) =>
		Play(world, effect, VfxPlacement.AttachedTo(attachTo, Matrix4x4.Identity));

	public VfxHandle Play(World world, AssetRef<VfxAsset> effect, in VfxPlacement placement)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (effect.IsValid == false) return VfxHandle.Invalid;

		return GetRuntime(world).Play(effect, null, placement);
	}

	public VfxHandle Play(World world, VfxAsset effect, in VfxPlacement placement)
	{
		ArgumentNullException.ThrowIfNull(world);
		ArgumentNullException.ThrowIfNull(effect);

		return GetRuntime(world).Play(default, effect, placement);
	}

	public void Stop(VfxHandle handle, bool immediate = false) => handle.Runtime?.Stop(handle, immediate);

	public bool IsPlaying(VfxHandle handle) => handle.Runtime?.IsPlaying(handle) ?? false;

	public void SetTransform(VfxHandle handle, Vector3 position, Quaternion rotation) =>
		handle.Runtime?.SetTransform(handle, Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position));

	public void StopAll(World world, bool immediate = false)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (_runtimes.TryGetValue(world, out var runtime))
		{
			runtime.StopAll(immediate);
		}
	}

	public void Update(World world, float deltaTime)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (_runtimes.TryGetValue(world, out var runtime))
		{
			runtime.Update(world, deltaTime);
		}
	}

	public VfxStatistics GetStatistics(World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		if (_runtimes.TryGetValue(world, out var runtime) == false)
		{
			return new VfxStatistics(0, 0, VfxLimits.ParticlePoolCapacity);
		}

		return new VfxStatistics(runtime.ActiveEffectCount, runtime.ReservedParticleCount, VfxLimits.ParticlePoolCapacity);
	}

	internal VfxWorldRuntime GetRuntime(World world) =>
		_runtimes.GetValue(world, _ => new VfxWorldRuntime(message => _log?.Warning(message)));

	/// <summary>Game thread: fills <paramref name="frame"/> with the world's effects, or leaves it empty.</summary>
	internal void Extract(World world, VfxWorldFrame frame, IRenderResourceScheduler? resources)
	{
		frame.Reset(world);
		if (_runtimes.TryGetValue(world, out var runtime))
		{
			runtime.Extract(frame, resources);
		}
	}
}
