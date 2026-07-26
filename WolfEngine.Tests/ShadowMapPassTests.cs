using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class ShadowMapPassTests
{
	[TestCase(1)]
	[TestCase(2)]
	[TestCase(3)]
	public void PrepareFrame_UsesConfiguredCascadeCount(int cascadeCount)
	{
		var pass = new ShadowMapPass(new ShaderCompiler());
		var sceneData = CreateSceneData(hasDirectionalLight: true);
		var config = new ShadowMapConfig { CascadeCount = cascadeCount };

		pass.PrepareFrame(sceneData, config);

		var frameData = pass.GetCurrentFrameData();
		Assert.That(frameData.Enabled, Is.True);
		Assert.That(frameData.CascadeCount, Is.EqualTo(cascadeCount));
		for (var cascadeIndex = 0; cascadeIndex < cascadeCount; cascadeIndex++)
		{
			Assert.That(
				frameData.GetCascadeViewProjection(cascadeIndex),
				Is.Not.EqualTo(default(Matrix4x4)));
		}
	}

	[Test]
	public void PrepareFrame_WithoutDirectionalLight_DisablesShadowRendering()
	{
		var pass = new ShadowMapPass(new ShaderCompiler());

		pass.PrepareFrame(CreateSceneData(hasDirectionalLight: false), new ShadowMapConfig());

		Assert.That(pass.GetCurrentFrameData().Enabled, Is.False);
	}

	private static SceneDrawData CreateSceneData(bool hasDirectionalLight)
	{
		var lights = hasDirectionalLight
			? new[]
			{
				new LightPacket(
					new Light { Type = LightType.Directional, Intensity = 1.0f },
					Matrix4x4.Identity)
			}
			: Array.Empty<LightPacket>();

		return new SceneDrawData(
			Matrix4x4.Identity,
			Matrix4x4.Identity,
			Matrix4x4.Identity,
			Matrix4x4.Identity,
			Matrix4x4.Identity,
			Matrix4x4.Identity,
			Matrix4x4.Identity,
			Matrix4x4.Identity,
			Vector3.Zero,
			Vector3.Zero,
			new Int2(1920, 1080),
			0.1f,
			1000.0f,
			Vector2.Zero,
			Vector2.Zero,
			Vector2.Zero,
			resetHistory: false,
			lights,
			Array.Empty<DecalProjectorPacket>());
	}
}
