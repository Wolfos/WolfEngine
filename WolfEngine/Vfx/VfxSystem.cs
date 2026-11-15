using System.Numerics;
using WolfEngine.ECS;

namespace WolfEngine.Vfx;

/// <summary>
/// Advances the effects of every world it runs for, and plays or stops <see cref="VisualEffect"/> components.
/// Register it after <see cref="TransformSystem"/>, so attached effects follow this frame's transforms; in the editor
/// it belongs to the gameplay group, so effects pause with Play mode.
/// </summary>
public sealed class VfxSystem : IPreRender
{
	private readonly VfxService _vfx;
	private readonly WorldTag _tag;
	private readonly List<Entity> _removed = new();
	private long _syncFrame;

	public VfxSystem(VfxService vfx, WorldTag tag = WorldTag.Game)
	{
		_vfx = vfx ?? throw new ArgumentNullException(nameof(vfx));
		_tag = tag;
	}

	public WorldTag GetTag() => _tag;

	public void PreRender(float deltaTime, World world)
	{
		ArgumentNullException.ThrowIfNull(world);
		using var profile = Profiling.FrameProfiler.Instance.Measure("VFX update");

		var runtime = _vfx.GetRuntime(world);
		SyncComponents(world, runtime);
		runtime.Update(world, deltaTime);
	}

	private void SyncComponents(World world, VfxWorldRuntime runtime)
	{
		var effects = runtime.ComponentEffects;
		var frame = ++_syncFrame;
		foreach (var entry in world.View<VisualEffect>())
		{
			ref var component = ref entry.First;
			var wanted = component.Playing &&
			             component.Effect.IsValid &&
			             world.IsEnabled(entry.Entity);
			effects.TryGetValue(entry.Entity, out var current);

			if (current.Requested && (wanted == false || current.AssetId != component.Effect.NodeId))
			{
				runtime.Stop(current.Handle, immediate: false);
				current = default;
			}

			if (wanted && current.Requested == false)
			{
				var handle = runtime.Play(
					component.Effect,
					null,
					VfxPlacement.AttachedTo(entry.Entity, Matrix4x4.Identity));
				current = new VfxComponentEffect(handle, component.Effect.NodeId, Requested: true, SeenFrame: frame);
			}

			effects[entry.Entity] = current with
			{
				SeenFrame = frame
			};
		}

		_removed.Clear();
		foreach (var (entity, effect) in effects)
		{
			if (effect.SeenFrame == frame)
			{
				continue;
			}

			// The component or its entity is gone.
			runtime.Stop(effect.Handle, immediate: false);
			_removed.Add(entity);
		}

		for (var i = 0; i < _removed.Count; i++)
		{
			effects.Remove(_removed[i]);
		}
	}
}

/// <summary>What <see cref="VfxSystem"/> started for one <see cref="VisualEffect"/> component.</summary>
internal readonly record struct VfxComponentEffect(VfxHandle Handle, Guid AssetId, bool Requested, long SeenFrame);
