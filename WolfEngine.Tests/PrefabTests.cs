using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;

namespace WolfEngine.Tests;

public sealed class PrefabTests
{
	private static readonly Guid PrefabId = Guid.Parse("7a1e0000-0000-0000-0000-000000000001");
	private static readonly Guid RootId = Guid.Parse("7a1e0000-0000-0000-0000-0000000000a0");
	private static readonly Guid TurretId = Guid.Parse("7a1e0000-0000-0000-0000-0000000000a1");
	private static readonly Guid BarrelId = Guid.Parse("7a1e0000-0000-0000-0000-0000000000a2");
	private static readonly Guid BannerId = Guid.Parse("7a1e0000-0000-0000-0000-0000000000a3");
	private static readonly Guid OutsideId = Guid.Parse("7a1e0000-0000-0000-0000-0000000000ff");

	private static readonly Dictionary<string, Type> ComponentTypes = new(StringComparer.Ordinal)
	{
		["test:health"] = typeof(PrefabTestHealth),
		["test:aim"] = typeof(PrefabTestAim),
		["test:label"] = typeof(PrefabTestLabel),
		["test:inventory"] = typeof(PrefabTestInventory),
		["test:name"] = typeof(NameComponent)
	};

	[Test]
	public void Instantiate_CreatesHierarchyWithNamesTransformsAndEnabledState()
	{
		var prefab = CreateUnitPrefab();
		var world = new World(WorldTag.Game);

		var root = prefab.Instantiate(world);

		var turret = world.GetComponent<Children>(root).First;
		var barrel = world.GetComponent<Children>(turret).First;
		var banner = world.GetComponent<Sibling>(turret).Next;
		Assert.Multiple(() =>
		{
			Assert.That(prefab.EntityCount, Is.EqualTo(4));
			Assert.That(world.GetComponent<NameComponent>(root).Name, Is.EqualTo("Unit"));
			Assert.That(world.GetComponent<NameComponent>(turret).Name, Is.EqualTo("Turret"));
			Assert.That(world.GetComponent<NameComponent>(barrel).Name, Is.EqualTo("Barrel"));
			Assert.That(world.HasComponent<NameComponent>(banner), Is.False, "An unnamed entity stays unnamed.");
			Assert.That(world.GetComponent<Parent>(barrel).Value, Is.EqualTo(turret));
			Assert.That(world.GetComponent<Parent>(banner).Value, Is.EqualTo(root));
			Assert.That(world.IsEnabled(banner), Is.False);
			Assert.That(world.GetComponent<LocalTransform>(root).LocalPosition, Is.EqualTo(new Vector3(1, 2, 3)));
			Assert.That(world.GetComponent<LocalTransform>(root).LocalScale.X, Is.EqualTo(2).Within(1e-5f));
			Assert.That(world.GetComponent<LocalTransform>(turret).LocalPosition, Is.EqualTo(new Vector3(0, 1, 0)));
			Assert.That(world.HasComponent<LocalTransform>(banner), Is.False);
			Assert.That(world.GetComponent<PrefabTestHealth>(root).Value, Is.EqualTo(100));
		});
	}

	[Test]
	public void Instantiate_WithPose_ReplacesRootPositionAndRotationAndKeepsAuthoredScale()
	{
		var prefab = CreateUnitPrefab();
		var world = new World(WorldTag.Game);
		var rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2);

		var root = prefab.Instantiate(world, new Vector3(10, 0, -5), rotation);

		var transform = world.GetComponent<LocalTransform>(root);
		Assert.That(transform.LocalPosition, Is.EqualTo(new Vector3(10, 0, -5)));
		Assert.That(transform.LocalRotation, Is.EqualTo(rotation));
		Assert.That(transform.LocalScale.X, Is.EqualTo(2).Within(1e-5f));
	}

	[Test]
	public void Instantiate_WithParent_AttachesTheRoot()
	{
		var prefab = CreateUnitPrefab();
		var world = new World(WorldTag.Game);
		var factory = world.CreateEntity("Factory");
		world.AddTransform(factory, Matrix4x4.Identity);

		var root = prefab.Instantiate(world, Vector3.UnitX, Quaternion.Identity, factory);

		Assert.That(world.GetComponent<Parent>(root).Value, Is.EqualTo(factory));
		Assert.Throws<ArgumentException>(() => prefab.Instantiate(world, Vector3.Zero, Quaternion.Identity, new Entity(4096, 1)));
	}

	[Test]
	public void EntityReferences_PointIntoTheirOwnInstance()
	{
		var prefab = CreateUnitPrefab();
		var world = new World(WorldTag.Game);

		var first = prefab.Instantiate(world);
		var second = prefab.Instantiate(world);

		AssertReferencesStayInInstance(world, first);
		AssertReferencesStayInInstance(world, second);
	}

	[Test]
	public void EntityReferences_OutsideThePrefab_BecomeInvalid()
	{
		var prefab = CreateUnitPrefab();
		var world = new World(WorldTag.Game);

		var root = prefab.Instantiate(world);

		var turret = world.GetComponent<Children>(root).First;
		Assert.That(world.GetComponent<PrefabTestAim>(turret).Fallback.IsValid, Is.False);
	}

	[Test]
	public void MutableComponentState_IsNotSharedBetweenInstances()
	{
		var prefab = CreateUnitPrefab();
		var world = new World(WorldTag.Game);

		var first = prefab.Instantiate(world);
		var second = prefab.Instantiate(world);
		world.GetComponent<PrefabTestInventory>(first).Items.Add(99);

		Assert.That(world.GetComponent<PrefabTestInventory>(first).Items, Is.EqualTo(new[] { 1, 2, 99 }));
		Assert.That(world.GetComponent<PrefabTestInventory>(second).Items, Is.EqualTo(new[] { 1, 2 }));
		Assert.That(
			world.GetComponent<PrefabTestInventory>(second).Items,
			Is.Not.SameAs(world.GetComponent<PrefabTestInventory>(first).Items));
	}

	[Test]
	public void Create_SkipsComponentsTheResolverDeclinesAndTheSerializedName()
	{
		var document = CreateUnitDocument();
		EntityOf(document, RootId).Components.Add(Component("test:name", new JsonObject { ["Name"] = "Stale" }));
		EntityOf(document, RootId).Components.Add(Component("test:unknown", new JsonObject()));
		var prefab = Prefab.Create(PrefabId, document, ResolveTestComponent);
		var world = new World(WorldTag.Game);

		var root = prefab.Instantiate(world);

		Assert.That(world.GetComponent<NameComponent>(root).Name, Is.EqualTo("Unit"));
	}

	[Test]
	public void Create_RejectsDocumentsWithoutTheirRootOrWithAnotherVersion()
	{
		var missingRoot = CreateUnitDocument();
		missingRoot.RootEntityId = Guid.NewGuid();
		var futureVersion = CreateUnitDocument();
		futureVersion.Version = PrefabDocument.CurrentVersion + 1;

		Assert.Throws<InvalidDataException>(() => Prefab.Create(PrefabId, missingRoot, ResolveTestComponent));
		Assert.Throws<InvalidDataException>(() => Prefab.Create(PrefabId, futureVersion, ResolveTestComponent));
	}

	[Test]
	public void Create_RejectsUnreadableComponentData()
	{
		var document = CreateUnitDocument();
		EntityOf(document, TurretId).Components.Add(Component("test:inventory", new JsonObject { ["Items"] = "not a list" }));

		var exception = Assert.Throws<InvalidDataException>(() => Prefab.Create(PrefabId, document, ResolveTestComponent));
		Assert.That(exception!.InnerException, Is.InstanceOf<JsonException>());
	}

	[Test]
	public void Instantiate_AfterWarmUp_DoesNotAllocate()
	{
		// Covers the shared-copy and patched-reference paths, which are the ones meant for hot spawning.
		var document = CreateUnitDocument();
		document.Entities.RemoveAll(entity => entity.EntityId == BannerId);
		foreach (var entity in document.Entities)
		{
			entity.Components.RemoveAll(component => component.TypeId is "test:inventory" or "test:label");
		}

		var prefab = Prefab.Create(PrefabId, document, ResolveTestComponent);
		var world = new World(WorldTag.Game);
		var spawned = new Entity[256];
		for (var round = 0; round < 4; round++)
		{
			for (var i = 0; i < spawned.Length; i++)
			{
				spawned[i] = prefab.Instantiate(world, new Vector3(i, 0, 0), Quaternion.Identity);
			}

			for (var i = 0; i < spawned.Length; i++)
			{
				world.DestroyEntity(spawned[i]);
			}
		}

		var before = GC.GetAllocatedBytesForCurrentThread();
		for (var i = 0; i < spawned.Length; i++)
		{
			spawned[i] = prefab.Instantiate(world, new Vector3(i, 0, 0), Quaternion.Identity);
		}

		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.That(allocated, Is.Zero, $"Spawning {spawned.Length} instances allocated {allocated} bytes.");
		AssertReferencesStayInInstance(world, spawned[^1], expectBanner: false);
	}

	[Test]
	public void Load_ReadsAPrefabFromACookedPack()
	{
		var root = Path.Combine(Path.GetTempPath(), "PrefabTests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(root);
		try
		{
			// The build cooks the editor's prefab file verbatim, authoring-only members included.
			var json = JsonSerializer.SerializeToNode(CreateUnitDocument(), AssetJson.SerializerOptions)!.AsObject();
			foreach (var entity in json["Entities"]!.AsArray())
			{
				entity!["Icon"] = "unit";
				entity["PrefabSourcePath"] = new JsonArray();
				entity["PrefabOverrides"] = new JsonObject { ["Name"] = false };
			}

			var packPath = Path.Combine(root, "scenes-prefabs.wolfpack");
			WolfPackFile.Write(packPath,
				[new WolfPackSource(PrefabId, nameof(AssetType.Prefab), JsonSerializer.SerializeToUtf8Bytes(json), [])]);
			var packBytes = File.ReadAllBytes(packPath);
			var manifestPath = Path.Combine(root, "bootstrap.wolfmanifest");
			File.WriteAllBytes(manifestPath, JsonSerializer.SerializeToUtf8Bytes(new WolfBootstrapManifest
			{
				Target = "test",
				Packs =
				[
					new WolfManifestPack
					{
						Name = "scenes-prefabs",
						FileName = "scenes-prefabs.wolfpack",
						ByteSize = packBytes.Length,
						Sha256 = Convert.ToHexString(SHA256.HashData(packBytes))
					}
				]
			}, AssetJson.SerializerOptions));

			using var catalog = new WolfPackCatalog(manifestPath);
			var prefab = Prefab.Load(PrefabId, catalog.Read(PrefabId), ResolveTestComponent);
			var world = new World(WorldTag.Game);
			var spawned = prefab.Instantiate(world);

			Assert.That(catalog.GetEntry(PrefabId).Kind, Is.EqualTo(nameof(AssetType.Prefab)));
			Assert.That(prefab.AssetId, Is.EqualTo(PrefabId));
			Assert.That(prefab.EntityCount, Is.EqualTo(4));
			AssertReferencesStayInInstance(world, spawned);
		}
		finally
		{
			Directory.Delete(root, true);
		}
	}

	[Test]
	public void WorldInstantiate_ResolvesTheAssetReference()
	{
		var prefab = CreateUnitPrefab();
		var registry = new SinglePrefabRegistry(prefab);
		AssetDatabase.SetInstanceRegistry(registry);
		try
		{
			var world = new World(WorldTag.Game);

			var root = world.Instantiate(new AssetRef<Prefab> { NodeId = PrefabId }, new Vector3(4, 5, 6), Quaternion.Identity);

			Assert.That(world.GetComponent<LocalTransform>(root).LocalPosition, Is.EqualTo(new Vector3(4, 5, 6)));
			Assert.That(world.GetComponent<NameComponent>(world.Instantiate(new AssetRef<Prefab> { NodeId = PrefabId })).Name,
				Is.EqualTo("Unit"));
			Assert.Throws<ArgumentException>(() => world.Instantiate(default(AssetRef<Prefab>)));
			Assert.Throws<InvalidOperationException>(() => world.Instantiate(new AssetRef<Prefab> { NodeId = Guid.NewGuid() }));
		}
		finally
		{
			AssetDatabase.ClearInstanceRegistry();
		}
	}

	private static void AssertReferencesStayInInstance(World world, Entity root, bool expectBanner = true)
	{
		var turret = world.GetComponent<Children>(root).First;
		var barrel = world.GetComponent<Children>(turret).First;
		var aim = world.GetComponent<PrefabTestAim>(turret);
		Assert.Multiple(() =>
		{
			Assert.That(aim.Barrel, Is.EqualTo(barrel), "Patched reference to a child.");
			Assert.That(aim.Owner, Is.EqualTo(root), "Patched reference to the root.");
			Assert.That(aim.Range, Is.EqualTo(25.5f));
			if (expectBanner)
			{
				var banner = world.GetComponent<Sibling>(turret).Next;
				var label = world.GetComponent<PrefabTestLabel>(banner);
				Assert.That(label.Text, Is.EqualTo("Infantry"));
				Assert.That(label.Owner, Is.EqualTo(root), "Reference inside a deserialized-per-instance component.");
			}
		});
	}

	private static Prefab CreateUnitPrefab() => Prefab.Create(PrefabId, CreateUnitDocument(), ResolveTestComponent);

	/// <summary>
	/// Unit (root) ─┬─ Turret ── Barrel
	///              └─ Banner (disabled, unnamed)
	/// The entities are listed out of hierarchy order to prove the template orders them itself.
	/// </summary>
	private static PrefabDocument CreateUnitDocument()
	{
		var rootTransform = Matrix4x4.CreateScale(2) * Matrix4x4.CreateTranslation(1, 2, 3);
		return new PrefabDocument
		{
			RootEntityId = RootId,
			Entities =
			[
				new PrefabDocumentEntity
				{
					EntityId = BarrelId, ParentEntityId = TurretId, HasName = true, Name = "Barrel",
					LocalTransform = Matrix4x4.Identity
				},
				new PrefabDocumentEntity
				{
					EntityId = RootId, HasName = true, Name = "Unit", LocalTransform = rootTransform,
					Components =
					[
						Component("test:health", new JsonObject { ["Value"] = 100 }),
						Component("test:inventory", new JsonObject { ["Items"] = new JsonArray(1, 2) })
					]
				},
				new PrefabDocumentEntity
				{
					EntityId = TurretId, ParentEntityId = RootId, HasName = true, Name = "Turret",
					LocalTransform = Matrix4x4.CreateTranslation(0, 1, 0),
					Components =
					[
						Component("test:aim", new JsonObject
						{
							["Barrel"] = Reference(BarrelId),
							["Range"] = 25.5f,
							["Owner"] = Reference(RootId),
							["Fallback"] = Reference(OutsideId)
						})
					]
				},
				new PrefabDocumentEntity
				{
					EntityId = BannerId, ParentEntityId = RootId, Enabled = false,
					Components = [Component("test:label", new JsonObject { ["Text"] = "Infantry", ["Owner"] = Reference(RootId) })]
				}
			]
		};
	}

	private static PrefabDocumentEntity EntityOf(PrefabDocument document, Guid entityId) =>
		document.Entities.Single(entity => entity.EntityId == entityId);

	private static PrefabDocumentComponent Component(string typeId, JsonObject data) => new()
	{
		TypeId = typeId,
		Type = ComponentTypes.TryGetValue(typeId, out var type) ? type.AssemblyQualifiedName! : typeId,
		Data = JsonSerializer.SerializeToElement(data)
	};

	private static JsonObject Reference(Guid entityId) =>
		new() { [Prefab.EntityReferenceIdPropertyName] = entityId.ToString("D") };

	private static Type? ResolveTestComponent(PrefabDocumentComponent component) =>
		ComponentTypes.GetValueOrDefault(component.TypeId);

	private sealed class SinglePrefabRegistry(Prefab prefab) : IAssetInstanceRegistry
	{
		public object? GetInstance(Guid assetId, Type expectedType) =>
			assetId == prefab.AssetId && expectedType == typeof(Prefab) ? prefab : null;

		public void RefreshProject(string projectRootPath, AssetDatabase database) { }
		public void InvalidateAssets(IEnumerable<Guid> assetIds) { }
		public void ClearCachedInstances() { }
		public void Clear() { }
	}
}

public struct PrefabTestHealth : IEntityComponent
{
	public int Value;
}

/// <summary>Unmanaged with entity references: copied and patched per instance.</summary>
public struct PrefabTestAim : IEntityComponent
{
	public Entity Barrel;
	public float Range;
	public Entity Owner;
	public Entity Fallback;
}

/// <summary>Managed with an entity reference: deserialized again per instance.</summary>
public struct PrefabTestLabel : IEntityComponent
{
	public string Text;
	public Entity Owner;
}

/// <summary>Owns mutable state: deserialized again per instance.</summary>
public struct PrefabTestInventory : IEntityComponent
{
	public List<int> Items;
}
