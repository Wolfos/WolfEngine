using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Mathematics;

namespace WolfEngine.Tests;

[TestFixture]
public sealed class CameraViewportRayTests
{
	[Test]
	public void ViewportPointToRay_UsesCurrentProjectionFieldsAndCameraTransform()
	{
		var camera = CreateCamera(new Int2(1600, 600), 45.0f);
		camera.Fov = 90.0f; // The projection fields changed after the cached Perspective was built.
		var transform = CreateTransform(new Vector3(10.0f, 2.0f, -3.0f));

		Assert.That(camera.TryViewportPointToRay(new Vector2(0.75f, 0.5f), transform, out var rightRay), Is.True);
		Assert.That(camera.TryViewportPointToRay(new Vector2(0.5f, 0.5f), transform, out var centerRay), Is.True);
		Assert.That(rightRay.IsValid, Is.True);
		Assert.That(centerRay.Origin.X, Is.EqualTo(10.0f).Within(1e-4f));
		Assert.That(centerRay.Origin.Y, Is.EqualTo(2.0f).Within(1e-4f));
		Assert.That(centerRay.Origin.Z, Is.EqualTo(-3.0f + camera.NearPlane).Within(1e-4f));
		Assert.That(Vector3.Distance(centerRay.Direction, Vector3.UnitZ), Is.LessThan(1e-5f));
		Assert.That(rightRay.Direction.X / rightRay.Direction.Z, Is.EqualTo(4.0f / 3.0f).Within(1e-4f));
	}

	[Test]
	public void ViewportPointToRay_UsesCameraAspectAndAllowsExtrapolation()
	{
		var wideCamera = CreateCamera(new Int2(1600, 600), 90.0f);
		var tallCamera = CreateCamera(new Int2(600, 1600), 90.0f);
		var transform = CreateTransform(Vector3.Zero);

		Assert.That(wideCamera.TryViewportPointToRay(new Vector2(0.75f, 0.5f), transform, out var wideRay), Is.True);
		Assert.That(tallCamera.TryViewportPointToRay(new Vector2(0.75f, 0.5f), transform, out var tallRay), Is.True);
		Assert.That(wideCamera.TryViewportPointToRay(new Vector2(1.25f, 0.5f), transform, out var outsideRay), Is.True);
		Assert.That(wideRay.Direction.X, Is.GreaterThan(tallRay.Direction.X));
		Assert.That(outsideRay.Direction.X, Is.GreaterThan(wideRay.Direction.X));
	}

	[Test]
	public void ViewportPointToRay_InvalidCameraOrTransformReturnsInvalidRay()
	{
		var camera = default(Camera);
		var invalidTransform = default(WorldTransform);

		Assert.That(camera.TryViewportPointToRay(Vector2.Zero, invalidTransform, out _), Is.False);
		Assert.That(camera.ViewportPointToRay(Vector2.Zero, invalidTransform).IsValid, Is.False);

		camera = CreateCamera(new Int2(800, 600), 70.0f);
		camera.Fov = float.NaN;
		Assert.That(camera.TryViewportPointToRay(Vector2.Zero, CreateTransform(Vector3.Zero), out _), Is.False);
		camera.Fov = 180.0f;
		Assert.That(camera.TryViewportPointToRay(Vector2.Zero, CreateTransform(Vector3.Zero), out _), Is.False);
		camera.Fov = 70.0f;
		camera.NearPlane = Camera.DefaultFarPlane + 1.0f;
		camera.FarPlane = 0.0f;
		Assert.That(camera.TryViewportPointToRay(Vector2.Zero, CreateTransform(Vector3.Zero), out _), Is.False);
		camera.NearPlane = 0.25f;
		camera.FarPlane = 100.0f;
		Assert.That(camera.TryViewportPointToRay(new Vector2(float.NaN, 0.5f), CreateTransform(Vector3.Zero), out _), Is.False);
	}

	private static Camera CreateCamera(Int2 resolution, float fov)
	{
		var camera = new Camera
		{
			ScreenResolution = resolution,
			NearPlane = 0.25f,
			FarPlane = 100.0f
		};
		camera.SetPerspective(fov);
		return camera;
	}

	private static WorldTransform CreateTransform(Vector3 position)
	{
		var localToWorld = Matrix4x4.CreateTranslation(position);
		Matrix4x4.Invert(localToWorld, out var worldToLocal);
		return new WorldTransform { LocalToWorld = localToWorld, WorldToLocal = worldToLocal };
	}
}
