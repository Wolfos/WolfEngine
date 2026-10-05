using System.Collections;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using WolfEngine.Animation;
using WolfEngine.AssetPipeline;
using WolfEngine.ECS;
using WolfEngine.Editor.Projects;

namespace WolfEngine.Editor.Tests;

[TestFixture]
public sealed class AssetDependencyCollectorTests
{
	[Test]
	public void Collect_FindsNestedReferencesAcrossCollectionsAndNullableValues()
	{
		var ids = Enumerable.Range(0, 7).Select(_ => Guid.NewGuid()).ToArray();
		var asset = new TestAsset
		{
			Field = Reference(ids[0]),
			Items = [new() { Reference = Reference(ids[1]) }, null],
			List = [Reference(ids[2]), default, Reference(ids[0])],
			Dictionary = new Dictionary<string, NestedReferences>
			{
				["nested"] = new() { Reference = Reference(ids[3]) }
			},
			ReadOnlyDictionary = new SortedDictionary<string, AssetRef<Material>>
			{
				["material"] = Reference(ids[4])
			},
			UntypedDictionary = new Hashtable { ["material"] = Reference(ids[5]) },
			Optional = Reference(ids[6]),
			InternalId = Guid.NewGuid()
		};

		Assert.That(AssetDependencyCollector.Collect(asset), Is.EqualTo(ids.Order()));
	}

	[Test]
	public void Collect_RespectsSerializationExclusionsAndIncludedMembers()
	{
		var asset = new ExcludedReferences();

		Assert.That(AssetDependencyCollector.Collect(asset), Is.EquivalentTo(new[]
		{
			asset.Included.NodeId,
			asset.AlwaysIncluded.NodeId
		}));
	}

	[Test]
	public void Collect_HandlesCyclesAndDeepGraphs()
	{
		var id = Guid.NewGuid();
		var root = new NestedReferences();
		var current = root;
		for (var i = 0; i < 2048; i++)
		{
			current.Next = new NestedReferences();
			current = current.Next;
		}

		current.Reference = Reference(id);
		current.Next = root;

		Assert.That(AssetDependencyCollector.Collect(root), Is.EqualTo(new[] { id }));
	}

	[Test]
	public void Collect_UsesDeclaredContractsAndConfiguredPolymorphism()
	{
		var baseId = Guid.NewGuid();
		var derivedId = Guid.NewGuid();
		var shared = new DerivedReferences
		{
			Reference = Reference(baseId),
			Extra = Reference(derivedId)
		};
		var asset = new ContractReferences { Base = shared, Derived = shared };
		var polymorphicId = Guid.NewGuid();
		asset.Polymorphic = new PolymorphicDerivedReferences { Reference = Reference(polymorphicId) };

		Assert.That(AssetDependencyCollector.Collect(asset), Is.EquivalentTo(new[] { baseId, derivedId, polymorphicId }));
		Assert.That(AssetDependencyCollector.Collect(new BaseOnlyReferences { Value = shared }),
			Is.EqualTo(new[] { baseId }));
	}

	[Test]
	public void Collect_PreservesAnimationGuidReferencesWithoutIncludingGraphNodeIds()
	{
		var skeletonId = Guid.NewGuid();
		var sequenceId = Guid.NewGuid();
		var clipId = Guid.NewGuid();
		var maskId = Guid.NewGuid();
		var set = new AnimationSet { SkeletonId = skeletonId, Clips = new() { ["run"] = sequenceId } };
		var sequence = new AnimationSequence { ClipId = clipId };
		var mask = new BoneMask { SkeletonId = skeletonId };
		var nodeId = Guid.NewGuid();
		var graph = new AnimationGraph
		{
			Output = nodeId,
			Nodes = [new() { Id = nodeId, Inputs = [Guid.NewGuid()], MaskId = maskId }, new()],
			Layout = [new() { NodeId = nodeId }]
		};

		Assert.Multiple(() =>
		{
			Assert.That(AssetDependencyCollector.Collect(set), Is.EquivalentTo(new[] { skeletonId, sequenceId }));
			Assert.That(AssetDependencyCollector.Collect(sequence), Is.EqualTo(new[] { clipId }));
			Assert.That(AssetDependencyCollector.Collect(mask), Is.EqualTo(new[] { skeletonId }));
			Assert.That(AssetDependencyCollector.Collect(graph), Is.EqualTo(new[] { maskId }));
			Assert.That(AssetDependencyCollector.Collect(new AnimationSequence()), Is.Empty);
		});
	}

	[Test]
	public void Collect_DoesNotRetainCollectibleAssemblyMetadata()
	{
		var assemblyReference = CollectFromTemporaryAssembly();
		for (var i = 0; i < 30 && assemblyReference.IsAlive; i++)
		{
			// System.Text.Json's member-accessor cache expires entries after one second,
			// and evicts them when another metadata request touches the cache.
			new DefaultJsonTypeInfoResolver().GetTypeInfo(typeof(ExcludedReferences), new JsonSerializerOptions());
			GC.Collect();
			GC.WaitForPendingFinalizers();
			GC.Collect();
			Thread.Sleep(100);
		}

		Assert.That(assemblyReference.IsAlive, Is.False);
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static WeakReference CollectFromTemporaryAssembly()
	{
		var assemblyPath = typeof(AssetDependencyCollectorTests).Assembly.Location;
		var context = new GameplayAssemblyLoadContext(assemblyPath);
		var assembly = context.LoadManagedAssembly(assemblyPath);
		var type = assembly.GetType(typeof(TestAsset).FullName!)!;
		var value = Activator.CreateInstance(type)!;
		var id = Guid.NewGuid();
		type.GetField(nameof(TestAsset.Field))!.SetValue(value, Reference(id));

		Assert.That(AssetDependencyCollector.Collect(new List<object> { value }), Is.EqualTo(new[] { id }));
		context.Unload();

		return new WeakReference(context);
	}

	private static AssetRef<Material> Reference(Guid id) => new() { NodeId = id };

	public sealed class TestAsset : IDataAsset
	{
		public AssetRef<Material> Field;
		public NestedReferences?[] Items { get; set; } = [];
		public List<AssetRef<Material>> List { get; set; } = [];
		public Dictionary<string, NestedReferences> Dictionary { get; set; } = [];
		public IReadOnlyDictionary<string, AssetRef<Material>> ReadOnlyDictionary { get; set; } = new Dictionary<string, AssetRef<Material>>();
		public IDictionary UntypedDictionary { get; set; } = new Hashtable();
		public AssetRef<Material>? Optional { get; set; }
		public Guid InternalId { get; set; }
	}

	public sealed class NestedReferences
	{
		public AssetRef<Material> Reference { get; set; }
		public NestedReferences? Next { get; set; }
	}

	public sealed class ExcludedReferences
	{
		[JsonIgnore]
		public AssetRef<Material> Ignored => throw new InvalidOperationException("Ignored getters must not run.");

		[NotSerialized]
		public AssetRef<Material> Transient => throw new InvalidOperationException("Transient getters must not run.");

		[JsonIgnore]
		public AssetRef<Material> IgnoredField = Reference(Guid.NewGuid());

		[NotSerialized]
		public AssetRef<Material> TransientField = Reference(Guid.NewGuid());

		[JsonInclude]
		internal AssetRef<Material> Included { get; set; } = Reference(Guid.NewGuid());

		[JsonIgnore(Condition = JsonIgnoreCondition.Never)]
		public AssetRef<Material> AlwaysIncluded { get; set; } = Reference(Guid.NewGuid());

		private AssetRef<Material> PrivateReference { get; set; } = Reference(Guid.NewGuid());
	}

	public class BaseReferences
	{
		public AssetRef<Material> Reference { get; set; }
	}

	public sealed class DerivedReferences : BaseReferences
	{
		public AssetRef<Material> Extra { get; set; }
	}

	public sealed class ContractReferences
	{
		public BaseReferences? Base { get; set; }
		public DerivedReferences? Derived { get; set; }
		public PolymorphicReferences? Polymorphic { get; set; }
	}

	public sealed class BaseOnlyReferences
	{
		public BaseReferences? Value { get; set; }
	}

	[JsonDerivedType(typeof(PolymorphicDerivedReferences), "derived")]
	public class PolymorphicReferences;

	public sealed class PolymorphicDerivedReferences : PolymorphicReferences
	{
		public AssetRef<Material> Reference { get; set; }
	}
}
