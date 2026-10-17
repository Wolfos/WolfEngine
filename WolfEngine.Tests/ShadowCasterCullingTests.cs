using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class ShadowCasterCullingTests
{
	[Test]
	public void VerticalLightRejectsIrrelevantCastersButKeepsOffscreenCastersAboveReceivers()
	{
		var planes = Build(CreateScene(), -Vector3.UnitY);
		Assert.That(Visible(planes.AsSpan(0, 6), new Vector3(25, 0, 5), 0.1f), Is.True);
		Assert.That(Visible(planes, new Vector3(25, 0, 5), 0.1f), Is.False);
		Assert.That(Visible(planes, new Vector3(0, 0, -5), 0.1f), Is.False);
		Assert.That(Visible(planes, new Vector3(0, 0, 40), 0.1f), Is.False);
		Assert.That(Visible(planes, new Vector3(0, 20, 5), 0.1f), Is.True);
	}

	[Test]
	public void LightPointingIntoViewKeepsCastersBehindCamera()
	{
		Assert.That(Visible(Build(CreateScene(), Vector3.UnitZ), new Vector3(0, 0, -20), 0.1f), Is.True);
	}

	[TestCase(false)]
	[TestCase(true)]
	public void ExtrudedReceiversSurviveForVariedCameraAndLightDirections(bool orthographic)
	{
		var random = new Random(312);
		for (var orientation = 0; orientation < 20; orientation++)
		{
			var rotation = Matrix4x4.CreateFromYawPitchRoll(orientation * 0.37f, orientation * 0.11f, 0.13f);
			Matrix4x4.Invert(rotation, out var view);
			var scene = CreateScene(view: view, orthographic: orthographic);
			var light = Vector3.Normalize(new Vector3((float)random.NextDouble() - 0.5f,
				(float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f));
			var planes = Build(scene, light);
			Assert.That(planes.Length, Is.InRange(7, FrustumCulling.MaxPlaneCount));
			for (var sample = 0; sample < 100; sample++)
			{
				var depth = 0.1f + (float)random.NextDouble() * 29.9f;
				var halfSize = orthographic ? 10 : depth;
				var receiver = Vector3.Transform(new Vector3(
					((float)random.NextDouble() * 2 - 1) * halfSize,
					((float)random.NextDouble() * 2 - 1) * halfSize, depth), rotation);
				var caster = receiver - light * ((float)random.NextDouble() * 80);
				// Existing shadow-map planes still limit reach; new receiver planes must
				// never reject a caster lying on a ray from an in-view receiver to the light.
				foreach (var plane in planes.AsSpan(6))
					Assert.That(Vector4.Dot(plane, new Vector4(caster, 1)), Is.GreaterThanOrEqualTo(-0.001f));
			}
		}
	}

	[Test]
	public void FilteringAndSphereExtentRemainConservativeAtReceiverBoundary()
	{
		var planes = Build(CreateScene(), -Vector3.UnitY);
		// At depth 5 the right frustum edge is x=5. Its inward plane has normal
		// (-1,0,1)/sqrt(2); allow padding and the sphere radius outside that edge.
		Assert.That(Visible(planes, new Vector3(5.2f, 15, 5), 0.1f), Is.True);
		Assert.That(Visible(planes, new Vector3(6, 15, 5), 0.1f), Is.False);
	}

	[Test]
	public void CameraOriginDoesNotEnterRelativeSpacePlaneEquations()
	{
		var original = Build(CreateScene(), new Vector3(1, -1, 0));
		var translated = Build(CreateScene(origin: new Vector3(100000, 20000, -40000)), new Vector3(1, -1, 0));
		Assert.That(translated, Is.EqualTo(original));
	}

	[Test]
	public void SingularCameraFallsBackToExistingShadowMapVolume()
	{
		var scene = CreateScene(view: default(Matrix4x4));
		var planes = Build(scene, -Vector3.UnitY);
		Assert.That(planes.Length, Is.EqualTo(6));
	}

	[Test]
	public void DisabledOptimizationUsesOriginalPlanesAndDoesNotChangeProjection()
	{
		var scene = CreateScene();
		var pass = new ShadowMapPass(new ShaderCompiler());
		pass.PrepareFrame(scene, new ShadowMapConfig());
		var matrix = pass.GetCurrentFrameData().CascadeViewProjection0;
		Span<Vector4> planes = stackalloc Vector4[FrustumCulling.MaxPlaneCount];
		Assert.That(pass.BuildCasterCullingPlanes(scene, 0, false, planes), Is.EqualTo(6));
		Span<Vector4> expected = stackalloc Vector4[6];
		FrustumCulling.ExtractPlanes(matrix, expected);
		Assert.That(planes[..6].ToArray(), Is.EqualTo(expected.ToArray()));
		Assert.That(pass.GetCurrentFrameData().CascadeViewProjection0, Is.EqualTo(matrix));
	}

	private static Vector4[] Build(SceneDrawData scene, Vector3 lightDirection)
	{
		var shadowView = Matrix4x4.CreateLookAtLeftHanded(-Vector3.Normalize(lightDirection) * 80,
			Vector3.Zero, MathF.Abs(Vector3.Dot(Vector3.Normalize(lightDirection), Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY);
		var shadowProjection = Matrix4x4.CreateOrthographicLeftHanded(60, 60, 0.1f, 180);
		Span<Vector4> planes = stackalloc Vector4[FrustumCulling.MaxPlaneCount];
		var count = FrustumCulling.BuildShadowCasterPlanes(scene, shadowView * shadowProjection,
			lightDirection, 30, 0.1f, planes);
		return planes[..count].ToArray();
	}

	private static bool Visible(ReadOnlySpan<Vector4> planes, Vector3 point, float radius)
	{
		foreach (var plane in planes)
			if (Vector4.Dot(plane, new Vector4(point, 1)) < -radius) return false;
		return true;
	}

	private static SceneDrawData CreateScene(Matrix4x4? view = null, Vector3 origin = default, bool orthographic = false)
	{
		var viewMatrix = view ?? Matrix4x4.Identity;
		var projection = orthographic ? Matrix4x4.CreateOrthographicLeftHanded(20, 20, 0.1f, 1000)
			: Matrix4x4.CreatePerspectiveFieldOfViewLeftHanded(MathF.PI / 2, 1, 0.1f, 1000);
		var vp = viewMatrix * projection;
		Matrix4x4.Invert(projection, out var inverseProjection);
		Matrix4x4.Invert(vp, out var inverseVp);
		return new SceneDrawData(viewMatrix, vp, projection, vp, projection, vp, inverseProjection, inverseVp,
			origin, origin, new Int2(1920, 1920), 0.1f, 1000, Vector2.Zero, Vector2.Zero, Vector2.Zero, false,
			[new LightPacket(new Light { Type = LightType.Directional }, Matrix4x4.Identity)], [], [], []);
	}
}
