using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Gameplay;
using WolfEngine.Runtime;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class RuntimeSceneTransitionTests
{
	[Test]
	public void CookedScenes_LoadAndReplaceInOneSessionWithoutKeepingOldWorldRegistered()
	{
		var root = Path.Combine(Path.GetTempPath(), "RuntimeSceneTransitionTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			var firstId = Guid.NewGuid();
			var secondId = Guid.NewGuid();
			var sources = new List<WolfPackSource>();
			void Scene(Guid id, string name)
			{
				var cellId = Guid.NewGuid();
				sources.Add(new WolfPackSource(id, nameof(AssetType.Scene), JsonSerializer.SerializeToUtf8Bytes(
					new CookedSceneManifest { Version = 1, GlobalCellId = cellId }, AssetJson.SerializerOptions), [cellId]));
				sources.Add(new WolfPackSource(cellId, nameof(AssetType.SceneCell), JsonSerializer.SerializeToUtf8Bytes(
					new CookedCell { Version = 1, Entities = [new CookedEntity { EntityId = Guid.NewGuid(), HasName = true, Name = name }] },
					AssetJson.SerializerOptions), []));
			}
			Scene(firstId, "First");
			Scene(secondId, "Second");
			var packPath = Path.Combine(root, "scenes.wolfpack");
			WolfPackFile.Write(packPath, sources);
			var bytes = File.ReadAllBytes(packPath);
			var manifest = new WolfBootstrapManifest
			{
				Target = "test", InitialSceneId = firstId,
				Packs = [new WolfManifestPack { Name = "scenes", FileName = "scenes.wolfpack", ByteSize = bytes.Length,
					Sha256 = Convert.ToHexString(SHA256.HashData(bytes)) }]
			};
			var manifestPath = Path.Combine(root, "bootstrap.wolfmanifest");
			File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest, AssetJson.SerializerOptions));
			using var catalog = new WolfPackCatalog(manifestPath);
			var loader = new RuntimeSceneLoader(catalog);
			var manager = new WorldManager();
			var module = new Module();
			using var services = new ServiceCollection().BuildServiceProvider();
			var session = new GameplayWorldSession(manager, module, services, _ => { }, () => { });
			var first = loader.Load(firstId);
			session.Replace(first);
			var second = loader.Load(secondId);
			session.Replace(second);
			Assert.That(Name(first), Is.EqualTo("First"));
			Assert.That(Name(second), Is.EqualTo("Second"));
			Assert.That(module.Unloaded, Is.EqualTo(new[] { first }));
			Assert.That(manager.RemoveWorld(first), Is.False);
			Assert.Throws<InvalidDataException>(() => loader.Load(sources[1].Id));
			Assert.Throws<KeyNotFoundException>(() => loader.Load(Guid.NewGuid()));
			Assert.That(session.World, Is.SameAs(second));
			session.Stop();
			Assert.That(module.Unloaded, Is.EqualTo(new[] { first, second }));
			Assert.That(manager.RemoveWorld(second), Is.False);
		}
		finally { Directory.Delete(root, true); }
	}

	private static string? Name(World world)
	{
		foreach (var entry in world.View<NameComponent>()) return entry.First.Name;
		return null;
	}

	private sealed class Module : IGameplayModule
	{
		public List<World> Unloaded { get; } = [];
		public void OnLoaded(World world) { }
		public void OnUnloading(World world) => Unloaded.Add(world);
		public void Update(float deltaTime, World world) { }
		public void PhysicsUpdate(float fixedDeltaTime, World world) { }
	}
}
