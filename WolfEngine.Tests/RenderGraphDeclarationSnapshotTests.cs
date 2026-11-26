using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;
using Moq;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

/// <summary>
/// Temporary safety net while passes move their graph-node declarations out of the frame builder. Each case records
/// one frame and compares every graph node's name, kind, view and declared reads and writes with a stored snapshot,
/// so a migration step that changes what the graph schedules fails here instead of as a barrier bug. The GpuDraw,
/// GBuffer, Shadow Map and Transparent Forward nodes are still declared by the frame builder. Delete this test and
/// its snapshots once they have moved too: it pins the frame's shape, not any behaviour.
/// Set WOLF_UPDATE_GRAPH_SNAPSHOTS=1 to rewrite the snapshots.
/// </summary>
[TestFixture]
public sealed class RenderGraphDeclarationSnapshotTests
{
	public static IEnumerable<TestCaseData> Cases()
	{
		yield return new TestCaseData("taa-cas-ssr-fog-decals", (Func<RenderConfig>)CreateTaaConfig, true, false);
		yield return new TestCaseData("fsr3-raytraced-ddgi-clouds", (Func<RenderConfig>)CreateFsr3Config, false, false);
		yield return new TestCaseData("minimal", (Func<RenderConfig>)CreateMinimalConfig, false, false);
		yield return new TestCaseData("two-views", (Func<RenderConfig>)CreateTaaConfig, true, true);
	}

	[TestCaseSource(nameof(Cases))]
	public void GraphMatchesSnapshot(string name, Func<RenderConfig> createConfig, bool hasActiveDecals, bool twoViews)
	{
		var registry = new RenderGraphResourceRegistry();
		var (graph, builder) = CreateFixture(registry);

		RecordFrame(graph, builder, createConfig(), hasActiveDecals, twoViews);
		var actual = Describe(graph, registry);

		var path = Path.Combine(SnapshotDirectory(), name + ".txt");
		if (Environment.GetEnvironmentVariable("WOLF_UPDATE_GRAPH_SNAPSHOTS") == "1")
		{
			Directory.CreateDirectory(SnapshotDirectory());
			File.WriteAllText(path, actual);
			Assert.Pass($"Wrote {path}");
		}

		Assert.That(File.Exists(path), Is.True, $"No snapshot at {path}; run with WOLF_UPDATE_GRAPH_SNAPSHOTS=1.");
		Assert.That(actual, Is.EqualTo(File.ReadAllText(path)));
	}

	private static void RecordFrame(
		RenderGraph graph,
		RenderGraphFrameBuilder builder,
		RenderConfig config,
		bool hasActiveDecals,
		bool twoViews)
	{
		// The production order: every view is set up before anything is recorded, because the shared draw update
		// records first and has to know which views draw this frame.
		var size = new Int2(64, 32);
		var second = RenderViewId.FromIndex(1);
		builder.BeginSharedFrame(size);
		builder.BeginViewFrame(
			RenderViewId.Primary,
			size,
			size,
			default,
			true,
			hasActiveDecals,
			config,
			Vector3.Zero,
			Vector3.UnitY,
			Vector3.One);
		if (twoViews)
		{
			graph.ViewRegistry.GetOrCreate(second).Output = RenderViewOutput.Texture;
			builder.BeginViewFrame(
				second,
				new Int2(32, 16),
				new Int2(32, 16),
				default,
				true,
				false,
				config,
				new Vector3(4.0f, 2.0f, 0.0f),
				Vector3.UnitY,
				Vector3.One);
		}

		builder.RecordSharedPreparation(graph);
		builder.BindView(RenderViewId.Primary);
		builder.RecordBoundView(graph);
		if (twoViews)
		{
			builder.BindView(second);
			builder.RecordBoundView(graph);
		}

		builder.BindView(RenderViewId.Primary);
		builder.RecordSharedPresentation(graph);
	}

	/// <summary>
	/// Names every resource by where it first appears in node order, not by its handle id, so a migration that
	/// creates handles in a different order but declares the same graph still matches.
	/// </summary>
	private static string Describe(RenderGraph graph, RenderGraphResourceRegistry registry)
	{
		var names = new Dictionary<int, string>();
		var text = new StringBuilder();
		foreach (var node in graph.Passes)
		{
			text.Append(node.Name)
				.Append(" [")
				.Append(node.Kind)
				.Append("] view=")
				.Append(node.View.IsValid ? node.View.Index.ToString() : "shared")
				.Append('\n');

			foreach (var usage in node.ResourceUsages)
			{
				if (names.TryGetValue(usage.Handle.Id, out var resourceName) == false)
				{
					resourceName = $"R{names.Count} ({registry.Describe(usage.Handle)})";
					names.Add(usage.Handle.Id, resourceName);
				}

				text.Append("  ")
					.Append(usage.AccessMode == ResourceAccessMode.Read ? "read  " : "write ")
					.Append(resourceName)
					.Append(' ')
					.Append(usage.State)
					.Append('\n');
			}
		}

		return text.ToString();
	}

	private static RenderConfig CreateTaaConfig()
	{
		return new RenderConfig
		{
			AntiAliasing = new AntiAliasingConfig
			{
				Mode = AntiAliasingMode.Taa,
				Enabled = true,
				Taa = new TemporalAntiAliasingConfig { EnableCasSharpen = true }
			},
			AmbientOcclusion = new AmbientOcclusionConfig { Enabled = true, Resolution = AmbientOcclusionResolution.Full },
			Reflections = new ReflectionConfig { Enabled = true, Mode = ReflectionMode.ScreenSpace },
			Bloom = new BloomConfig { Enabled = true },
			VolumetricFog = new VolumetricFogConfig { Enabled = true },
			Decals = new DecalConfig { Enabled = true },
			Sky = new SkyConfig { Mode = SkyConfigMode.Procedural }
		};
	}

	private static RenderConfig CreateFsr3Config()
	{
		return new RenderConfig
		{
			AntiAliasing = new AntiAliasingConfig { Mode = AntiAliasingMode.Fsr3, Enabled = true },
			AmbientOcclusion = new AmbientOcclusionConfig
			{
				Enabled = true,
				Mode = AmbientOcclusionMode.RayTraced,
				Resolution = AmbientOcclusionResolution.Half
			},
			Reflections = new ReflectionConfig { Enabled = true, Mode = ReflectionMode.RayTraced },
			DiffuseGlobalIllumination = new DiffuseGlobalIlluminationConfig
			{
				Enabled = true,
				ProbeCounts = new DdgiProbeCounts { X = 4, Y = 2, Z = 4 },
				FinalGather = new DdgiFinalGatherSettings { Enabled = true, Denoise = true }
			},
			Bloom = new BloomConfig { Enabled = true },
			VolumetricFog = new VolumetricFogConfig { Enabled = true },
			Clouds = new CloudConfig { Enabled = true },
			Sky = new SkyConfig { Mode = SkyConfigMode.Procedural }
		};
	}

	private static RenderConfig CreateMinimalConfig()
	{
		return new RenderConfig
		{
			AntiAliasing = new AntiAliasingConfig { Enabled = false },
			AmbientOcclusion = new AmbientOcclusionConfig { Enabled = false },
			Reflections = new ReflectionConfig { Enabled = false },
			Bloom = new BloomConfig { Enabled = false },
			ShadowMaps = new ShadowMapConfig { Enabled = false },
			Sky = new SkyConfig { Mode = SkyConfigMode.Gradient }
		};
	}

	/// <summary>The scheduling fixture, with a device that reports ray tracing so the ray-traced passes are recorded.</summary>
	private static (RenderGraph Graph, RenderGraphFrameBuilder Builder) CreateFixture(RenderGraphResourceRegistry registry)
	{
		return ScreenSpaceDecalPassTests.CreateSchedulingFixture(registry, supportsRayTracing: true);
	}

	private static string SnapshotDirectory([CallerFilePath] string sourcePath = "")
	{
		return Path.Combine(Path.GetDirectoryName(sourcePath)!, "RenderGraphSnapshots");
	}
}
