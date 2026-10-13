using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;
using System.Text.Json.Nodes;
using WolfEngine.ECS;
using WolfEngine.Mathematics;
using WolfEngine.Serialization;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class BinarySerializationTests
{
	[OneTimeSetUp]
	public void RegisterSerializers()
	{
		SerializedTypeRegistry.EnsureRegistered(typeof(World).Assembly);
		SerializedTypeRegistry.EnsureRegistered(typeof(Camera).Assembly);
		SerializedTypeRegistry.EnsureRegistered(typeof(BinarySerializationTests).Assembly);
	}

	[Test]
	public void EverySupportedMemberKind_RoundTrips()
	{
		var target = new Entity(7, 3);
		var original = CreateFullyPopulatedComponent(target);
		var entities = new TestEntityReferences();
		entities.Map(target, Guid.NewGuid());

		var copy = RoundTrip(original, entities);

		// The editor's own JSON options describe every member it keeps, so equal JSON means nothing was lost.
		Assert.That(ToJson(copy), Is.EqualTo(ToJson(original)));
		Assert.Multiple(() =>
		{
			Assert.That(copy.Target, Is.EqualTo(target), "Entity references resolve through the decoder.");
			Assert.That(copy.NullText, Is.Null);
			Assert.That(copy.NullChain, Is.Null);
			Assert.That(copy.Chain!.Next!.Next, Is.Null, "A self-referencing class keeps its depth.");
			Assert.That(copy.Jagged![1], Is.EqualTo(new[] { 3, 4, 5 }));
			Assert.That(copy.Skipped, Is.Zero, "[NotSerialized] members are not cooked.");
			Assert.That(copy.Ignored, Is.Zero, "[JsonIgnore] members are not cooked.");
		});
	}

	[Test]
	public void EntityReferences_WithoutADecoder_ReadAsInvalid()
	{
		var original = new SerializationTestComponent
		{
			Target = new Entity(1, 1)
		};
		var entities = new TestEntityReferences();
		entities.Map(original.Target, Guid.NewGuid());
		var type = SerializedTypeRegistry.GetComponent(typeof(SerializationTestComponent));
		var writer = new BinaryAssetWriter
		{
			Entities = entities
		};
		type.WriteBoxed(writer, original);

		var reader = new BinaryAssetReader(writer.WrittenSpan);
		var copy = (SerializationTestComponent)type.ReadBoxed(ref reader);

		Assert.That(copy.Target.IsValid, Is.False);
	}

	[Test]
	public void NonPublicJsonIncludeAndInitOnlyMembers_RoundTripThroughGeneratedAccessors()
	{
		var original = SerializationTestPrivateMembers.Create(secret: 11, guarded: 22, initOnly: 33);

		var copy = RoundTrip(original, null);

		Assert.That(copy.SecretValue, Is.EqualTo(11));
		Assert.That(copy.GuardedProperty, Is.EqualTo(22));
		Assert.That(copy.InitOnly, Is.EqualTo(33));
	}

	[Test]
	public void DeserializedCallback_RunsOnTheReadValue()
	{
		var copy = RoundTrip(new SerializationTestCallbackComponent { Input = 1.5f }, null);

		Assert.That(copy.Derived, Is.EqualTo(15.0f), "The callback must mutate the struct itself, not a boxed copy.");
	}

	[Test]
	public void DataAsset_RoundTripsInheritedRequiredAndCollectionMembers()
	{
		var original = new SerializationTestDataAsset
		{
			Shared = "base",
			Required = 5,
			Values = [1, 2, 3]
		};

		var bytes = CookedDataAsset.Write(original);
		var copy = (SerializationTestDataAsset)CookedDataAsset.Read(bytes, typeof(SerializationTestDataAsset));

		Assert.Multiple(() =>
		{
			Assert.That(copy.Shared, Is.EqualTo("base"));
			Assert.That(copy.Required, Is.EqualTo(5));
			Assert.That(copy.Values, Is.EqualTo(new[] { 1, 2, 3 }));
			Assert.That(copy.Deserialized, Is.True);
		});
		Assert.Throws<InvalidDataException>(() => CookedDataAsset.Read(bytes, typeof(Rendering.Passes.RenderConfig)));
	}

	[Test]
	public void DataAsset_CookedWithADifferentLayout_FailsWithRebuildMessage()
	{
		var bytes = CookedDataAsset.Write(new SerializationTestDataAsset { Required = 1 });
		var key = SerializedTypeRegistry.GetDataAsset(typeof(SerializationTestDataAsset)).Key;

		// The layout hash directly follows the length-prefixed key.
		var keyBytes = System.Text.Encoding.UTF8.GetBytes(key);
		var hashOffset = bytes.AsSpan().IndexOf(keyBytes) + keyBytes.Length;
		bytes[hashOffset] ^= 0xFF;

		var exception = Assert.Throws<InvalidDataException>(() =>
			CookedDataAsset.Read(bytes, typeof(SerializationTestDataAsset)));
		Assert.That(exception!.Message, Does.Contain("Rebuild the game content"));
	}

	[Test]
	public void TruncatedData_ThrowsInvalidData()
	{
		var bytes = CookedDataAsset.Write(new SerializationTestDataAsset { Required = 1, Values = [1, 2, 3, 4] });

		Assert.Throws<InvalidDataException>(() =>
			CookedDataAsset.Read(bytes.AsSpan(0, bytes.Length - 3), typeof(SerializationTestDataAsset)));
	}

	/// <summary>
	/// Cooking writes what the editor's JSON options read, and a game reads it back through the generated reader.
	/// Both must agree on every member of every component, which comparing their JSON shows for default values.
	/// </summary>
	[Test]
	public void EveryEngineComponent_MatchesTheEditorsJsonRoundTrip()
	{
		var assemblies = new[] { typeof(World).Assembly, typeof(Camera).Assembly };
		var componentTypes = assemblies
			.SelectMany(assembly => assembly.GetTypes())
			.Where(type => type.IsValueType && typeof(IEntityComponent).IsAssignableFrom(type))
			.Where(type => Attribute.IsDefined(type, typeof(NotSerializedAttribute)) == false)
			.ToArray();
		Assert.That(componentTypes, Is.Not.Empty);

		foreach (var componentType in componentTypes)
		{
			foreach (var changeValues in new[] { false, true })
			{
				AssertCookedMatchesEditor(componentType, changeValues);
			}
		}
	}

	/// <summary>
	/// Reads a component the way the editor does, from its JSON, then cooks and reads it back. With
	/// <paramref name="changeValues"/>, every number and bool in the JSON is first moved off its default, so a member
	/// the cooked format drops cannot hide behind a value that happens to equal the default.
	/// </summary>
	private static void AssertCookedMatchesEditor(Type componentType, bool changeValues)
	{
		{
			var options = AssetJson.GetSerializerOptions(componentType);
			var json = JsonSerializer.SerializeToNode(Activator.CreateInstance(componentType), componentType, options)!;
			if (changeValues)
			{
				ChangeEveryValue(json);
			}

			var editorValue = json.Deserialize(componentType, options)!;
			var type = SerializedTypeRegistry.GetComponent(componentType);
			var writer = new BinaryAssetWriter();
			type.WriteBoxed(writer, editorValue);
			var reader = new BinaryAssetReader(writer.WrittenSpan);
			var cookedValue = type.ReadBoxed(ref reader);

			Assert.That(reader.IsAtEnd, Is.True, componentType.FullName);
			Assert.That(
				JsonSerializer.Serialize(cookedValue, componentType, options),
				Is.EqualTo(JsonSerializer.Serialize(editorValue, componentType, options)),
				$"{componentType.FullName} (values changed: {changeValues})");
		}
	}

	private static void ChangeEveryValue(JsonNode node)
	{
		if (node is JsonObject obj)
		{
			foreach (var property in obj.ToArray())
			{
				obj[property.Key] = ChangedValue(property.Value);
			}
		}
		else if (node is JsonArray array)
		{
			for (var i = 0; i < array.Count; i++)
			{
				array[i] = ChangedValue(array[i]);
			}
		}
	}

	private static JsonNode? ChangedValue(JsonNode? value)
	{
		if (value is JsonObject or JsonArray)
		{
			ChangeEveryValue(value);
			return value;
		}

		if (value is not JsonValue leaf)
		{
			return value;
		}

		if (leaf.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
		{
			return JsonValue.Create(leaf.GetValue<bool>() == false);
		}

		// Strings may be enum names, ids or other formats, so only numbers and bools are changed.
		if (leaf.GetValueKind() != JsonValueKind.Number)
		{
			return value;
		}

		// Far enough from zero and one that clamping callbacks, such as a camera forcing a zero resolution to
		// one by one, cannot turn a dropped member back into the expected value.
		var number = leaf.GetValue<double>();
		return JsonValue.Create(number == Math.Floor(number) ? number + 3 : number + 2.5);
	}

	[Test]
	public void SceneCells_ResolveReferencesAcrossCellsAndRestoreHierarchy()
	{
		var parentId = Guid.NewGuid();
		var childId = Guid.NewGuid();
		var otherCellId = Guid.NewGuid();
		var cooker = new ContentCooker((typeId, _) => typeId == "test:aim" ? typeof(PrefabTestAim) : null);
		var firstCell = cooker.CookSceneCell(Guid.NewGuid(), new SceneCellDocument
		{
			Entities =
			[
				new EntityDocument
				{
					EntityId = parentId,
					HasName = true,
					Name = "Parent",
					LocalTransform = Matrix4x4.CreateTranslation(1, 2, 3)
				},
				new EntityDocument
				{
					EntityId = childId,
					ParentEntityId = parentId,
					Enabled = false,
					Components =
					[
						new ComponentDocument
						{
							TypeId = "test:aim",
							Data = JsonSerializer.SerializeToElement(new Dictionary<string, object>
							{
								["Barrel"] = new Dictionary<string, string> { [Prefab.EntityReferenceIdPropertyName] = otherCellId.ToString("D") },
								["Owner"] = new Dictionary<string, string> { [Prefab.EntityReferenceIdPropertyName] = parentId.ToString("D") },
								["Range"] = 4.0f
							})
						}
					]
				}
			]
		});
		var secondCell = cooker.CookSceneCell(Guid.NewGuid(), new SceneCellDocument
		{
			Entities = [new EntityDocument { EntityId = otherCellId, HasName = true, Name = "Elsewhere" }]
		});

		var world = new World(WorldTag.Game);
		CookedSceneCell.Instantiate(world, [CookedSceneCell.Read(firstCell), CookedSceneCell.Read(secondCell)]);

		var parent = FindByName(world, "Parent");
		var elsewhere = FindByName(world, "Elsewhere");
		var child = world.GetComponent<Children>(parent).First;
		var aim = world.GetComponent<PrefabTestAim>(child);
		Assert.Multiple(() =>
		{
			Assert.That(world.GetComponent<LocalTransform>(parent).LocalPosition, Is.EqualTo(new Vector3(1, 2, 3)));
			Assert.That(world.IsEnabled(child), Is.False);
			Assert.That(aim.Owner, Is.EqualTo(parent));
			Assert.That(aim.Barrel, Is.EqualTo(elsewhere), "References resolve into other cells of the scene.");
			Assert.That(aim.Range, Is.EqualTo(4.0f));
		});
	}

	[Test]
	public void Cooking_DoesNotRunDeserializationCallbacks_ButLoadingDoes()
	{
		var cooker = new ContentCooker((typeId, _) => typeId == "test:callback" ? typeof(CookingCallbackProbe) : null);
		CookingCallbackProbe.Calls = 0;

		var cell = cooker.CookSceneCell(Guid.NewGuid(), new SceneCellDocument
		{
			Entities =
			[
				new EntityDocument
				{
					EntityId = Guid.NewGuid(),
					Components =
					[
						new ComponentDocument
						{
							TypeId = "test:callback",
							Data = JsonSerializer.SerializeToElement(new Dictionary<string, int> { ["Value"] = 3 })
						}
					]
				}
			]
		});
		var callsWhileCooking = CookingCallbackProbe.Calls;
		CookedSceneCell.Instantiate(new World(WorldTag.Game), [CookedSceneCell.Read(cell)]);

		Assert.That(callsWhileCooking, Is.Zero, "Callbacks build runtime state, such as loading meshes, which cooking must not do.");
		Assert.That(CookingCallbackProbe.Calls, Is.EqualTo(1));
	}

	private static Entity FindByName(World world, string name)
	{
		foreach (var entry in world.View<NameComponent>())
		{
			if (entry.First.Name == name)
			{
				return entry.Entity;
			}
		}

		throw new AssertionException($"No entity is named '{name}'.");
	}

	private static T RoundTrip<T>(T value, TestEntityReferences? entities)
		where T : struct, IEntityComponent
	{
		var type = (SerializedComponentType<T>)SerializedTypeRegistry.GetComponent(typeof(T));
		var writer = new BinaryAssetWriter
		{
			Entities = entities
		};
		type.Write(writer, in value);

		var reader = new BinaryAssetReader(writer.WrittenSpan, entities);
		var copy = type.Read(ref reader);
		Assert.That(reader.IsAtEnd, Is.True, "The reader consumes exactly what the writer wrote.");

		return copy;
	}

	private static string ToJson<T>(T value)
	{
		return JsonSerializer.Serialize(value, AssetJson.SerializerOptions);
	}

	private static SerializationTestComponent CreateFullyPopulatedComponent(Entity target)
	{
		return new SerializationTestComponent
		{
			Flag = true,
			Byte = 200,
			SByte = -100,
			Short = -30000,
			UShort = 60000,
			Char = 'ß',
			Int = -2_000_000_000,
			UInt = 4_000_000_000,
			Long = long.MinValue + 1,
			ULong = ulong.MaxValue - 1,
			Float = 3.25f,
			Double = Math.PI,
			Decimal = 12345.6789m,
			Text = "héllo, wörld",
			NullText = null,
			Id = Guid.Parse("d5a3c1a2-90b7-4e7e-8c1d-3b4a5f6e7d8c"),
			When = new DateTime(2026, 10, 13, 12, 30, 0, DateTimeKind.Utc),
			WhenWithOffset = new DateTimeOffset(2026, 10, 13, 12, 30, 0, TimeSpan.FromHours(2)),
			Duration = TimeSpan.FromMilliseconds(1234),
			V2 = new Vector2(1, 2),
			V3 = new Vector3(1, 2, 3),
			V4 = new Vector4(1, 2, 3, 4),
			Rotation = Quaternion.CreateFromYawPitchRoll(0.1f, 0.2f, 0.3f),
			Plane = new Plane(0, 1, 0, -5),
			M3 = Matrix3x2.CreateRotation(0.5f),
			M4 = Matrix4x4.CreateFromYawPitchRoll(0.1f, 0.2f, 0.3f) * Matrix4x4.CreateTranslation(4, 5, 6),
			Mode = SerializationTestMode.On,
			MaybeNumber = 42,
			NoNumber = null,
			MaybeVector = Vector3.UnitZ,
			Numbers = [1, 2, 3],
			Jagged = [[1, 2], [3, 4, 5], []],
			Names = ["a", null!, "c"],
			ReadOnlyItems = [new SerializationTestNested { Weight = 1.5f, Label = "x" }],
			Set = [5, 6],
			Lookup = new Dictionary<string, int> { ["one"] = 1, ["two"] = 2 },
			Nested = new SerializationTestNested { Weight = 9.5f, Label = "nested" },
			Chain = new SerializationTestNode { Value = 1, Next = new SerializationTestNode { Value = 2 } },
			NullChain = null,
			PrefabRef = new AssetRef<Prefab> { NodeId = Guid.Parse("11111111-2222-3333-4444-555555555555") },
			Target = target,
			Pair = (8, "eight"),
			Size = new Int2(1192, 712),
			ReadOnlyPair = new SerializationTestReadOnlyPair(3, "three"),
			Immutable = new SerializationTestImmutableNode("node", 4) { Extra = 5 },
			Property = 77,
			Skipped = 99,
			Ignored = 98
		};
	}

	private sealed class TestEntityReferences : IEntityReferenceEncoder, IEntityReferenceDecoder
	{
		private readonly Dictionary<Entity, Guid> _ids = new();
		private readonly Dictionary<Guid, Entity> _entities = new();

		public void Map(Entity entity, Guid id)
		{
			_ids[entity] = id;
			_entities[id] = entity;
		}

		public Guid Encode(Entity entity)
		{
			return _ids.GetValueOrDefault(entity);
		}

		public Entity Decode(Guid id)
		{
			return _entities.GetValueOrDefault(id);
		}
	}
}

public enum SerializationTestMode : short
{
	Off = 0,
	On = 7,
}

public struct SerializationTestNested
{
	public float Weight;
	public string? Label;
}

/// <summary>Get-only members filled through a [JsonConstructor], as System.Text.Json reads readonly structs.</summary>
public readonly struct SerializationTestReadOnlyPair
{
	[JsonConstructor]
	public SerializationTestReadOnlyPair(int number, string? name)
	{
		Number = number;
		Name = name;
	}

	public int Number { get; }

	public string? Name { get; }
}

/// <summary>A class read through its only public constructor, with one more member set afterwards.</summary>
public sealed class SerializationTestImmutableNode
{
	public SerializationTestImmutableNode(string name, int count)
	{
		Name = name;
		Count = count;
	}

	public string Name { get; }

	public int Count { get; }

	public int Extra { get; set; }
}

public sealed class SerializationTestNode
{
	public int Value { get; set; }
	public SerializationTestNode? Next { get; set; }
}

public struct SerializationTestComponent : IEntityComponent
{
	public SerializationTestComponent()
	{
	}

	public bool Flag;
	public byte Byte;
	public sbyte SByte;
	public short Short;
	public ushort UShort;
	public char Char;
	public int Int;
	public uint UInt;
	public long Long;
	public ulong ULong;
	public float Float;
	public double Double;
	public decimal Decimal;
	public string? Text = "default";
	public string? NullText;
	public Guid Id;
	public DateTime When;
	public DateTimeOffset WhenWithOffset;
	public TimeSpan Duration;
	public Vector2 V2;
	public Vector3 V3;
	public Vector4 V4;
	public Quaternion Rotation = Quaternion.Identity;
	public Plane Plane;
	public Matrix3x2 M3 = Matrix3x2.Identity;
	public Matrix4x4 M4 = Matrix4x4.Identity;
	public SerializationTestMode Mode;
	public int? MaybeNumber;
	public int? NoNumber;
	public Vector3? MaybeVector;
	public int[]? Numbers;
	public int[][]? Jagged;
	public List<string>? Names;
	public IReadOnlyList<SerializationTestNested>? ReadOnlyItems;
	public HashSet<int>? Set;
	public Dictionary<string, int>? Lookup;
	public SerializationTestNested Nested;
	public SerializationTestNode? Chain;
	public SerializationTestNode? NullChain;
	public AssetRef<Prefab> PrefabRef;
	public Entity Target;
	public (int Number, string Name) Pair;
	public Int2 Size;
	public SerializationTestReadOnlyPair ReadOnlyPair;
	public SerializationTestImmutableNode? Immutable;

	public int Property { get; set; }

	[NotSerialized]
	public int Skipped;

	[JsonIgnore]
	public int Ignored;

	public readonly int Computed => Int / 2;
}

public struct SerializationTestPrivateMembers : IEntityComponent
{
	[JsonInclude]
	private int _secret;

	[JsonInclude]
	public int GuardedProperty { get; private set; }

	public int InitOnly { get; init; }

	public readonly int SecretValue => _secret;

	public static SerializationTestPrivateMembers Create(int secret, int guarded, int initOnly)
	{
		return new SerializationTestPrivateMembers
		{
			_secret = secret,
			GuardedProperty = guarded,
			InitOnly = initOnly
		};
	}
}

public struct SerializationTestCallbackComponent : IEntityComponent, IJsonOnDeserialized
{
	public float Input;

	[JsonIgnore]
	public float Derived;

	void IJsonOnDeserialized.OnDeserialized()
	{
		Derived = Input * 10;
	}
}

public struct CookingCallbackProbe : IEntityComponent, IJsonOnDeserialized
{
	public static int Calls;

	public int Value;

	public void OnDeserialized()
	{
		Calls++;
	}
}

public class SerializationTestBaseAsset
{
	public string Shared { get; set; } = string.Empty;
}

public sealed class SerializationTestDataAsset : SerializationTestBaseAsset, IDataAsset, IJsonOnDeserialized
{
	public required int Required { get; set; }

	public List<int> Values { get; set; } = [];

	[JsonIgnore]
	public bool Deserialized;

	public void OnDeserialized()
	{
		Deserialized = true;
	}
}
