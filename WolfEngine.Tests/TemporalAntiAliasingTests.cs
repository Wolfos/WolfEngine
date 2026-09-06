using System.Numerics;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;
using WolfEngine.Mathematics;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class TemporalAntiAliasingTests
{
	[Test]
	public void DefaultsSelectTaaWithCasAndRetainFsr3HostControls()
	{
		var settings = new AntiAliasingConfig();

		Assert.Multiple(() =>
		{
			Assert.That(settings.Enabled, Is.True);
			Assert.That(settings.Mode, Is.EqualTo(AntiAliasingMode.Taa));
			Assert.That(settings.UsesCasSharpening, Is.True);
			Assert.That(settings.Taa.CasSharpness, Is.EqualTo(0.35f));
			Assert.That(settings.EnableSharpening, Is.False);
			Assert.That(settings.Sharpness, Is.EqualTo(0.2f));
			Assert.That(settings.AlphaTestReactiveScale, Is.EqualTo(1.0f));
			Assert.That(settings.TransparencyAndCompositionMaskScale, Is.EqualTo(1.0f));
		});
	}

	[Test]
	public void LegacyJsonKeyPreservesFsr3ControlsWithTaaAsDefault()
	{
		const string json =
			"""
			{
			  "TemporalAntiAliasing": {
			    "Enabled": false,
			    "PhaseCount": 8,
			    "EnableCasSharpen": true,
			    "CasSharpness": 1.0,
			    "StaticHistoryWeight": 0.97
			  }
			}
			""";

		var config = JsonSerializer.Deserialize<RenderConfig>(
			json,
			AssetJson.SerializerOptions)!;

		Assert.Multiple(() =>
		{
			Assert.That(config.AntiAliasing.Enabled, Is.False);
			// FSR3 controls remain independent of TAA/CAS tuning.
			Assert.That(config.AntiAliasing.EnableSharpening, Is.False);
			Assert.That(config.AntiAliasing.Sharpness, Is.EqualTo(0.2f));
			Assert.That(config.AntiAliasing.AlphaTestReactiveScale, Is.EqualTo(1.0f));
			Assert.That(config.AntiAliasing.TransparencyAndCompositionMaskScale, Is.EqualTo(1.0f));
		});
	}

	[TestCase(AntiAliasingMode.Taa)]
	[TestCase(AntiAliasingMode.Fsr3)]
	public void ConfigAndSnapshotPreserveBothMethods(AntiAliasingMode mode)
	{
		var config = new RenderConfig
		{
			AntiAliasing = new AntiAliasingConfig
			{
				Mode = mode,
				EnableSharpening = true,
				Sharpness = 0.6f,
				Taa = new TemporalAntiAliasingConfig { PhaseCount = 16, CasSharpness = 0.8f, StaticHistoryWeight = 0.97f }
			}
		};
		var json = JsonSerializer.Serialize(config, AssetJson.SerializerOptions);
		var restored = JsonSerializer.Deserialize<RenderConfig>(json, AssetJson.SerializerOptions)!;
		var snapshot = new FrameSnapshot();
		snapshot.SetConfig(restored);
		Assert.Multiple(() =>
		{
			Assert.That(snapshot.Config.AntiAliasing.Mode, Is.EqualTo(mode));
			Assert.That(snapshot.Config.AntiAliasing.Taa.PhaseCount, Is.EqualTo(16));
			Assert.That(snapshot.Config.AntiAliasing.Taa.StaticHistoryWeight, Is.EqualTo(0.97f));
			Assert.That(snapshot.Config.AntiAliasing.Taa.CasSharpness, Is.EqualTo(0.8f));
			Assert.That(snapshot.Config.AntiAliasing.Sharpness, Is.EqualTo(0.6f));
			Assert.That(snapshot.Config.AntiAliasing.EnableSharpening, Is.True);
			Assert.That(snapshot.Config.AntiAliasing.UsesCasSharpening, Is.EqualTo(mode == AntiAliasingMode.Taa));
		});
	}

	[Test]
	public void EightPhaseJitterRepeatsExactly()
	{
		for (ulong frameIndex = 0; frameIndex < 8; frameIndex++)
		{
			Assert.That(
				TemporalJitter.GetHaltonJitterPixels(frameIndex, 8),
				Is.EqualTo(TemporalJitter.GetHaltonJitterPixels(frameIndex + 8, 8)));
		}
	}

	[Test]
	public void ProjectionChangeUsesConfiguredEpsilon()
	{
		var projection = Matrix4x4.CreatePerspectiveFieldOfView(
			MathF.PI / 3.0f,
			16.0f / 9.0f,
			0.1f,
			1000.0f);
		var withinTolerance = projection;
		withinTolerance.M11 += 0.5e-5f;
		var changed = projection;
		changed.M11 += 2.0e-5f;

		Assert.That(
			TemporalJitter.HasProjectionChanged(projection, withinTolerance),
			Is.False);
		Assert.That(
			TemporalJitter.HasProjectionChanged(projection, changed),
			Is.True);
	}

	[TestCase(1280, 720, 640, 360)]
	[TestCase(1279, 719, 639, 359)]
	[TestCase(1, 1, 1, 1)]
	public void Fsr3ShadingChangeTargetUsesHalfRenderResolution(
		int renderWidth,
		int renderHeight,
		int expectedWidth,
		int expectedHeight)
	{
		var size = RenderGraphFrameBuilder.GetFsr3ShadingChangeSize(
			new Int2(renderWidth, renderHeight));

		Assert.That(size, Is.EqualTo(new Int2(expectedWidth, expectedHeight)));
	}

	[TestCase(GraphicsBackendKind.D3D12)]
	[TestCase(GraphicsBackendKind.Metal)]
	public void ResolveShaderCompilesWithFocusedBindings(GraphicsBackendKind backend)
	{
		if (backend == GraphicsBackendKind.D3D12 && !OperatingSystem.IsWindows() ||
		    backend == GraphicsBackendKind.Metal && !OperatingSystem.IsMacOS())
		{
			Assert.Ignore("Shader validation requires the matching platform.");
		}

		var compiled = new ShaderCompiler().GetComputeShaderWithReflection(
			ShaderPath("Taa/taa_resolve.compute.slang"),
			"TaaResolveCS",
			backend);

		Assert.That(compiled.Bytecode.IsEmpty, Is.False);
		Assert.That(compiled.ThreadGroupSize.X, Is.EqualTo(8));
		Assert.That(compiled.ThreadGroupSize.Y, Is.EqualTo(8));
		Assert.That(
			compiled.ReflectionLayout.GetConstantBuffer("BindlessHandles")
				.GetFieldOrThrow("normalHandle").ValueKind,
			Is.EqualTo(ShaderConstantFieldValueKind.UInt));
		Assert.That(
			compiled.ReflectionLayout.GetConstantBuffer("TaaSettings")
				.GetFieldOrThrow("staticHistoryWeight").ValueKind,
			Is.EqualTo(ShaderConstantFieldValueKind.Float));
		Assert.That(
			compiled.ReflectionLayout.GetConstantBuffer("TaaSettings")
				.GetFieldOrThrow("inverseUnjitteredViewProjection").ValueKind,
			Is.EqualTo(ShaderConstantFieldValueKind.Matrix4x4));
	}

	private static string ShaderPath(string relativePath) => Path.GetFullPath(Path.Combine(
		TestContext.CurrentContext.TestDirectory,
		"..",
		"..",
		"..",
		"..",
		"WolfEngine",
		"Shaders",
		relativePath));
}
