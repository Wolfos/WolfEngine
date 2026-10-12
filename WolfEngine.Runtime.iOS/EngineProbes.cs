using System.Numerics;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

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
		passed &= Run("Component JSON by runtime type", ProbeComponentJson);

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

	private static void ProbeComponentJson()
	{
		// The runtime scene loader deserializes components by System.Type with AssetJson's reflection-based
		// options. That is the path NativeAOT trimming is expected to break.
		var componentType = typeof(Camera);
		var json = JsonSerializer.Serialize(
			new Camera { Fov = 75, NearPlane = 0.2f, FarPlane = 500 },
			componentType,
			AssetJson.SerializerOptions);
		var roundTripped = (Camera?)JsonSerializer.Deserialize(json, componentType, AssetJson.SerializerOptions);
		if (roundTripped is not { Fov: 75, FarPlane: 500 })
		{
			throw new InvalidOperationException($"Camera did not round-trip through JSON: {json}");
		}
	}
}
