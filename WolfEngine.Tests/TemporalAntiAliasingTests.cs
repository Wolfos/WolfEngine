using System.Numerics;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class TemporalAntiAliasingTests
{
	[Test]
	public void DefaultsMatchBalancedNativeResolutionPreset()
	{
		var settings = new TemporalAntiAliasingConfig();

		Assert.Multiple(() =>
		{
			Assert.That(settings.Enabled, Is.True);
			Assert.That(settings.PhaseCount, Is.EqualTo(8));
			Assert.That(settings.StaticHistoryWeight, Is.EqualTo(0.95f));
			Assert.That(settings.MovingHistoryWeight, Is.EqualTo(0.65f));
			Assert.That(settings.MotionResponsePixels, Is.EqualTo(8.0f));
			Assert.That(settings.DepthRejectionAbsolute, Is.EqualTo(0.02f));
			Assert.That(settings.DepthRejectionRelative, Is.EqualTo(0.01f));
			Assert.That(settings.VarianceClipGamma, Is.EqualTo(1.0f));
			Assert.That(settings.AlphaTestHistoryScale, Is.EqualTo(0.75f));
			Assert.That(settings.EnableCasSharpen, Is.True);
			Assert.That(settings.CasSharpness, Is.EqualTo(0.35f));
		});
	}

	[Test]
	public void LegacyJsonPropertiesAreIgnoredWithoutLosingFocusedSettings()
	{
		const string json =
			"""
			{
			  "TemporalAntiAliasing": {
			    "Enabled": true,
			    "PhaseCount": 8,
			    "OpaqueDepthThreshold": 0.004,
			    "LowMotionOpaqueHistoryWeight": 0.975,
			    "StaticHistoryWeight": 0.9
			  }
			}
			""";

		var config = JsonSerializer.Deserialize<RenderConfig>(
			json,
			AssetJson.SerializerOptions)!;

		Assert.That(config.TemporalAntiAliasing.PhaseCount, Is.EqualTo(8));
		Assert.That(config.TemporalAntiAliasing.StaticHistoryWeight, Is.EqualTo(0.9f));
		Assert.That(config.TemporalAntiAliasing.MovingHistoryWeight, Is.EqualTo(0.65f));
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

	[Test]
	public void ResolveShaderCompilesWithFocusedBindingsForD3D12()
	{
		if (OperatingSystem.IsWindows() == false)
		{
			Assert.Ignore("DirectX shader validation only runs on Windows.");
		}

		var compiled = new ShaderCompiler().GetComputeShaderWithReflection(
			ShaderPath("taa_resolve.compute.slang"),
			"TaaResolveCS",
			GraphicsBackendKind.D3D12);

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
