using System.Numerics;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;
using WolfEngine.Serialization;

namespace WolfEngine.Runtime.iOS;

/// <summary>
/// Spike checks for engine code paths that NativeAOT and trimming are most likely to break.
/// Each probe mirrors how the runtime uses that path rather than testing the library in isolation.
/// </summary>
internal static class EngineProbes
{
	public static bool RunAll()
	{
		var passed = true;
		passed &= Run("ECS world and view", ProbeEcs);
		passed &= Run("Serializers registered at startup", ProbeRegistration);
		passed &= Run("Component binary round trip", ProbeComponentRoundTrip);
		passed &= Run("Data asset binary round trip", ProbeDataAssetRoundTrip);
		passed &= Run("Cooked scene cell into a world", ProbeCookedSceneCell);

		return passed;
	}

	private static bool Run(string name, Action probe)
	{
		try
		{
			probe();
			Console.WriteLine($"[probe] PASS {name}");

			return true;
		}
		catch (Exception exception)
		{
			Console.WriteLine($"[probe] FAIL {name}: {exception}");

			return false;
		}
	}

	private static void ProbeEcs()
	{
		var world = new World(WorldTag.Game);
		var entity = world.CreateEntity("Probe", new Vector3(1, 2, 3), Quaternion.Identity, Vector3.One);
		world.AddComponent(entity, new Camera { Fov = 60, NearPlane = 0.1f, FarPlane = 1000 });

		var found = 0;
		foreach (var entry in world.View<Camera, LocalTransform>())
		{
			if (entry.Second.LocalPosition != new Vector3(1, 2, 3))
			{
				throw new InvalidOperationException($"Unexpected position {entry.Second.LocalPosition}.");
			}

			found++;
		}

		if (found != 1)
		{
			throw new InvalidOperationException($"Expected one camera entity, found {found}.");
		}
	}

	/// <summary>
	/// NativeAOT runs every module initializer at startup, so the generated registrations must already be in place
	/// before anything asks for them. On CoreCLR the runtime host registers them explicitly instead.
	/// </summary>
	private static void ProbeRegistration()
	{
		var types = new[] { typeof(Camera), typeof(Light), typeof(NameComponent), typeof(RenderConfig) };
		foreach (var type in types)
		{
			if (SerializedTypeRegistry.TryGet(type, out _) == false)
			{
				throw new InvalidOperationException($"'{type.FullName}' had no registered serializer at startup.");
			}
		}
	}

	private static void ProbeComponentRoundTrip()
	{
		var type = (SerializedComponentType<Camera>)SerializedTypeRegistry.GetComponent(typeof(Camera));
		var original = new Camera
		{
			ScreenResolution = new Int2(2622, 1206),
			Fov = 75,
			NearPlane = 0.2f,
			FarPlane = 500
		};
		var writer = new BinaryAssetWriter();
		type.Write(writer, in original);

		var reader = new BinaryAssetReader(writer.WrittenSpan);
		var copy = type.Read(ref reader);
		if (copy.ScreenResolution != original.ScreenResolution || copy.Fov != 75 || copy.FarPlane != 500 || reader.IsAtEnd == false)
		{
			throw new InvalidOperationException(
				$"Camera did not round-trip: {copy.ScreenResolution}, fov {copy.Fov}, far {copy.FarPlane}.");
		}
	}

	private static void ProbeDataAssetRoundTrip()
	{
		var original = new RenderConfig();
		original.VolumetricFog = original.VolumetricFog with
		{
			Enabled = true,
			Extinction = 0.25f,
			SliceCount = 64
		};

		var bytes = CookedDataAsset.Write(original);
		var copy = (RenderConfig)CookedDataAsset.Read(bytes, typeof(RenderConfig));
		if (copy.VolumetricFog.Enabled == false || copy.VolumetricFog.Extinction != 0.25f || copy.VolumetricFog.SliceCount != 64)
		{
			throw new InvalidOperationException("RenderConfig did not round-trip through the cooked format.");
		}
	}

	/// <summary>Writes a cell as the build does and loads it as the runtime does, entity references included.</summary>
	private static void ProbeCookedSceneCell()
	{
		var parentId = Guid.NewGuid();
		var childId = Guid.NewGuid();
		var entities = new CellEntityIds();
		entities.Add(parentId);
		entities.Add(childId);

		var light = new Light
		{
			Type = LightType.Point,
			Intensity = 4,
			Range = 12
		};
		var parent = new ParentReference { Value = entities.Placeholder(parentId) };
		var cell = CookedSceneCell.Write(
		[
			new CookedEntity
			{
				EntityId = parentId,
				Name = "Lamp",
				LocalTransform = Matrix4x4.CreateTranslation(0, 3, 0),
				Components = [Cook(light, entities)]
			},
			new CookedEntity
			{
				EntityId = childId,
				ParentEntityId = parentId,
				Name = "Bulb",
				Enabled = false,
				Components = [Cook(parent, entities)]
			}
		]);

		var world = new World(WorldTag.Game);
		CookedSceneCell.Instantiate(world, [CookedSceneCell.Read(cell)]);

		var lamp = default(Entity);
		var bulb = default(Entity);
		foreach (var entry in world.View<NameComponent>())
		{
			if (entry.First.Name == "Lamp")
			{
				lamp = entry.Entity;
			}
			else if (entry.First.Name == "Bulb")
			{
				bulb = entry.Entity;
			}
		}

		if (lamp.IsValid == false || bulb.IsValid == false)
		{
			throw new InvalidOperationException("The cooked cell's entities were not created.");
		}

		if (world.GetComponent<Light>(lamp).Intensity != 4 ||
		    world.GetComponent<Parent>(bulb).Value != lamp ||
		    world.GetComponent<ParentReference>(bulb).Value != lamp ||
		    world.IsEnabled(bulb))
		{
			throw new InvalidOperationException("The cooked cell loaded with the wrong components, hierarchy or references.");
		}
	}

	private static CookedComponent Cook<T>(T value, CellEntityIds entities)
		where T : struct, IEntityComponent
	{
		var type = (SerializedComponentType<T>)SerializedTypeRegistry.GetComponent(typeof(T));
		var writer = new BinaryAssetWriter
		{
			Entities = entities
		};
		type.Write(writer, in value);

		return new CookedComponent(type, writer.ToArray());
	}

	/// <summary>Stands in for the cooker's entity ids, which map placeholder entities back to authored ids.</summary>
	private sealed class CellEntityIds : IEntityReferenceEncoder
	{
		private readonly List<Guid> _ids = [];

		public void Add(Guid id)
		{
			_ids.Add(id);
		}

		public Entity Placeholder(Guid id)
		{
			return new Entity(_ids.IndexOf(id) + 1, 1);
		}

		public Guid Encode(Entity entity)
		{
			return _ids[entity.Index - 1];
		}
	}
}

/// <summary>A component holding an entity reference, to prove references survive cooking on device.</summary>
public struct ParentReference : IEntityComponent
{
	public Entity Value;
}
