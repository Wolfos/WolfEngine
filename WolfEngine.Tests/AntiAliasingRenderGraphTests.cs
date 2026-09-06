using System.Numerics;
using System.Reflection;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class AntiAliasingRenderGraphTests
{
	[TestCase(AntiAliasingMode.Taa, true, true)]
	[TestCase(AntiAliasingMode.Taa, true, false)]
	[TestCase(AntiAliasingMode.Fsr3, true, true)]
	[TestCase(AntiAliasingMode.Taa, false, true)]
	[TestCase(AntiAliasingMode.Fsr3, false, true)]
	public void SchedulesOnlySelectedMethodAndRoutesFinalColor(AntiAliasingMode mode, bool enabled, bool cas)
	{
		var registry = new RenderGraphResourceRegistry();
		var (graph, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(registry);
		BeginFrame(builder, mode, enabled, cas);
		builder.Build(graph);
		var names = graph.Passes.Select(pass => pass.Name).ToArray();
		var usesTaa = enabled && mode == AntiAliasingMode.Taa;
		var usesFsr3 = enabled && mode == AntiAliasingMode.Fsr3;
		Assert.Multiple(() =>
		{
			Assert.That(names.Contains("TAA Resolve"), Is.EqualTo(usesTaa));
			Assert.That(names.Contains("TAA History Store"), Is.EqualTo(usesTaa));
			Assert.That(names.Any(name => name.StartsWith("FSR3")), Is.EqualTo(usesFsr3));
			Assert.That(names.Contains("CAS Sharpen"), Is.EqualTo(usesTaa && cas));
			Assert.That(Resources(builder).Fsr3.InternalHistoryWrite.IsValid, Is.EqualTo(usesFsr3));
			Assert.That(Resources(builder).HistoryColorWrite.IsValid, Is.EqualTo(enabled));
			Assert.That(GetField<Array>(builder, "_fsr3CurrentLumaTextures").GetValue(0) is not null, Is.EqualTo(usesFsr3));
		});
		var producer = graph.Passes.Single(pass => pass.Name == (usesTaa && cas ? "CAS Sharpen" : "Tonemapping"));
		Assert.That(graph.Passes.Single(pass => pass.Name == "Copy To Final").Reads, Does.Contain(producer.Writes.Single()));
		if (usesTaa)
		{
			Assert.That(Array.IndexOf(names, "TAA Resolve"), Is.LessThan(Array.IndexOf(names, "TAA History Store")));
			Assert.That(Array.IndexOf(names, "TAA History Store"), Is.LessThan(Array.IndexOf(names, "Tonemapping")));
		}
		// Includes state tracking: TAA must never query unallocated FSR3 handles.
		Assert.DoesNotThrow(builder.CompleteFrame);
	}

	[Test]
	public void SwitchingMethodsAndDisablingInvalidateHistoryAndReleaseFsr3Resources()
	{
		var (_, builder) = ScreenSpaceDecalPassTests.CreateSchedulingFixture(new RenderGraphResourceRegistry());
		foreach (var mode in new[] { AntiAliasingMode.Taa, AntiAliasingMode.Fsr3, AntiAliasingMode.Taa })
		{
			BeginFrame(builder, mode);
			Assert.That(GetField<bool>(builder, "_resetTaaHistoryThisFrame"), Is.True);
			Assert.That(GetField<bool>(builder, "_historyValid"), Is.False);
			builder.CompleteFrame();
			BeginFrame(builder, mode);
			Assert.That(GetField<bool>(builder, "_resetTaaHistoryThisFrame"), Is.False);
			Assert.That(GetField<bool>(builder, "_historyValid"), Is.True);
			builder.CompleteFrame();
		}
		BeginFrame(builder, AntiAliasingMode.Taa, enabled: false);
		Assert.That(GetField<bool>(builder, "_historyValid"), Is.False);
		Assert.That(GetField<Array>(builder, "_historyColorTextures").GetValue(0), Is.Null);
		Assert.That(GetField<Array>(builder, "_fsr3CurrentLumaTextures").GetValue(0), Is.Null);
		BeginFrame(builder, AntiAliasingMode.Taa);
		Assert.That(GetField<bool>(builder, "_resetTaaHistoryThisFrame"), Is.True);
	}

	private static void BeginFrame(RenderGraphFrameBuilder builder, AntiAliasingMode mode, bool enabled = true, bool cas = true)
	{
		var config = new RenderConfig
		{
			AntiAliasing = new AntiAliasingConfig { Mode = mode, Enabled = enabled, Taa = new TemporalAntiAliasingConfig { EnableCasSharpen = cas } },
			AmbientOcclusion = new AmbientOcclusionConfig { Enabled = false },
			Reflections = new ReflectionConfig { Enabled = false },
			Bloom = new BloomConfig { Enabled = false }
		};
		builder.BeginFrame(new Int2(16, 16), new Int2(16, 16), default, true, false, Vector3.UnitY, 1.0f, config, Vector3.Zero);
	}

	private static RenderGraphFrameResources Resources(RenderGraphFrameBuilder builder) => GetField<RenderGraphFrameResources>(builder, "_frameResources");
	private static T GetField<T>(object instance, string name) => (T)instance.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(instance)!;
}
