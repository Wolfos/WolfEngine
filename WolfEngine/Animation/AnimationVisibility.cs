using System.Numerics;
using WolfEngine.ECS;
using WolfEngine.Rendering.Passes;

namespace WolfEngine.Animation;

/// <summary>Conservative primary-view visibility for skeletal pose sampling.</summary>
internal sealed class AnimationVisibility
{
	private readonly HashSet<Entity> _hasMesh = [];
	private readonly HashSet<Entity> _visible = [];
	private readonly Vector4[] _planes = new Vector4[6];
	private bool _hasCamera;

	public void Update(World world)
	{
		_hasMesh.Clear();
		_visible.Clear();
		_hasCamera = false;
		foreach (var entry in world.View<Camera, WorldTransform>())
		{
			if (world.IsEnabled(entry.Entity) == false ||
				world.TryGetCurrentWorldMatrix(entry.Entity, out var cameraWorld) == false ||
				Matrix4x4.Invert(cameraWorld, out var view) == false)
			{
				continue;
			}

			var projection = entry.First.GetPerspective(entry.First.ScreenResolution);
			FrustumCulling.ExtractPlanes(view * projection, _planes);
			_hasCamera = true;
			break;
		}

		if (_hasCamera == false)
		{
			return;
		}

		foreach (var entry in world.View<SkinnedMeshRenderer>())
		{
			if (world.IsEnabled(entry.Entity) == false)
			{
				continue;
			}

			ref var renderer = ref entry.First;
			var animatorEntity = renderer.AnimatorEntity.IsValid ? renderer.AnimatorEntity : entry.Entity;
			if (world.HasComponent<Animator>(animatorEntity) == false)
			{
				continue;
			}

			_hasMesh.Add(animatorEntity);
			if (_visible.Contains(animatorEntity))
			{
				continue;
			}

			var mesh = renderer.Mesh ?? (renderer.MeshAsset.IsValid ? renderer.MeshAsset.Asset : null);
			if (mesh is null || world.TryGetCurrentWorldMatrix(entry.Entity, out var transform) == false ||
				Outside(mesh.BoundingSphere.Center, mesh.BoundingSphere.Radius * MathF.Max(2, renderer.BoundsExpansion), transform) == false)
			{
				_visible.Add(animatorEntity);
			}
		}
	}

	public bool IsCulled(Entity animator) => _hasCamera && _hasMesh.Contains(animator) && _visible.Contains(animator) == false;

	private bool Outside(Vector3 localCenter, float localRadius, in Matrix4x4 transform)
	{
		if (float.IsFinite(localRadius) == false || localRadius <= 0)
		{
			return false;
		}

		var center = Vector3.Transform(localCenter, transform);
		var sx = new Vector3(transform.M11, transform.M12, transform.M13).Length();
		var sy = new Vector3(transform.M21, transform.M22, transform.M23).Length();
		var sz = new Vector3(transform.M31, transform.M32, transform.M33).Length();
		var radius = localRadius * MathF.Max(sx, MathF.Max(sy, sz));
		if (float.IsFinite(center.X) == false || float.IsFinite(center.Y) == false ||
			float.IsFinite(center.Z) == false || float.IsFinite(radius) == false)
		{
			return false;
		}

		foreach (var plane in _planes)
		{
			var distance = plane.X * center.X + plane.Y * center.Y + plane.Z * center.Z + plane.W;
			if (float.IsFinite(distance) && distance < -radius)
			{
				return true;
			}
		}

		return false;
	}
}
