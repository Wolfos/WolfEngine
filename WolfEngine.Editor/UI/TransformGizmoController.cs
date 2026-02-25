using System.Numerics;
using ImGuiNET;
using WolfEngine;
using WolfEngine.ECS;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

public sealed class TransformGizmoController
{
	private const float MinHandleLength = 0.35f;
	private const float HandleLengthDistanceScale = 0.15f;
	private const float AxisHitRadiusPixels = 10.0f;
	private const float RingHitRadiusPixels = 10.0f;
	private const int RingSegments = 64;
	private const float MinScaleComponent = 0.05f;

	private readonly EditorViewportStateBus _viewportStateBus;
	private readonly EditorCameraContext _cameraContext;
	private readonly GizmoDragState _dragState = new();
	private GizmoAxis _hoveredAxis;

	public TransformGizmoController(EditorViewportStateBus viewportStateBus, EditorCameraContext cameraContext)
	{
		_viewportStateBus = viewportStateBus ?? throw new ArgumentNullException(nameof(viewportStateBus));
		_cameraContext = cameraContext ?? throw new ArgumentNullException(nameof(cameraContext));
	}

	public void DrawAndHandle(
		World world,
		Entity entity,
		bool hasSelectedEntity,
		TransformGizmoMode mode,
		TransformSpace space,
		TransformPivotMode pivotMode)
	{
		var viewportState = _viewportStateBus.GetUiState();
		if (IsViewportValid(viewportState) == false ||
		    hasSelectedEntity == false ||
		    entity.IsValid == false ||
		    world.HasComponent<LocalTransform>(entity) == false ||
		    world.HasComponent<WorldTransform>(entity) == false ||
		    _cameraContext.TryGet(out var camera, out var cameraWorldTransform) == false ||
		    camera.ScreenResolution.X <= 0 ||
		    camera.ScreenResolution.Y <= 0)
		{
			EndDrag();
			return;
		}

		ref var worldTransform = ref world.GetComponent<WorldTransform>(entity);
		ref var localTransform = ref world.GetComponent<LocalTransform>(entity);
		var entityWorldPosition = worldTransform.LocalToWorld.Translation;
		var gizmoPivotWorld = ResolveGizmoPivotWorld(world, entity, worldTransform, pivotMode);
		Quaternion objectWorldRotation;
		if (Matrix4x4.Decompose(worldTransform.LocalToWorld, out _, out objectWorldRotation, out _) == false)
		{
			objectWorldRotation = localTransform.LocalRotation;
		}

		objectWorldRotation = NormalizeOrIdentity(objectWorldRotation);
		if (Matrix4x4.Invert(cameraWorldTransform.LocalToWorld, out var view) == false)
		{
			EndDrag();
			return;
		}

		var viewProjection = view * camera.Perspective;
		if (Matrix4x4.Invert(viewProjection, out var inverseViewProjection) == false)
		{
			EndDrag();
			return;
		}

		var axisRotation = mode == TransformGizmoMode.Scale || space == TransformSpace.Local
			? objectWorldRotation
			: Quaternion.Identity;
		var axisX = SafeNormalize(Vector3.Transform(Vector3.UnitX, axisRotation), Vector3.UnitX);
		var axisY = SafeNormalize(Vector3.Transform(Vector3.UnitY, axisRotation), Vector3.UnitY);
		var axisZ = SafeNormalize(Vector3.Transform(Vector3.UnitZ, axisRotation), Vector3.UnitZ);

		var cameraPosition = cameraWorldTransform.LocalToWorld.Translation;
		var distanceToCamera = Vector3.Distance(cameraPosition, gizmoPivotWorld);
		var handleLength = MathF.Max(MinHandleLength, distanceToCamera * HandleLengthDistanceScale);
		var ringRadius = handleLength * 0.8f;

		if (TryProjectToScreen(gizmoPivotWorld, viewProjection, viewportState.ImageMin, viewportState.ImageMax, out var pivotScreen) ==
		    false)
		{
			EndDrag();
			return;
		}

		var io = ImGui.GetIO();
		var mousePosition = io.MousePos;
		var mouseInViewport = IsPointInsideRect(mousePosition, viewportState.ImageMin, viewportState.ImageMax);
		var leftDown = ImGui.IsMouseDown(ImGuiMouseButton.Left);
		var leftClicked = ImGui.IsMouseClicked(ImGuiMouseButton.Left);
		var leftReleased = ImGui.IsMouseReleased(ImGuiMouseButton.Left);

		if (_dragState.Active && (_dragState.Entity != entity || leftDown == false || leftReleased))
		{
			EndDrag();
		}

		if (_dragState.Active == false)
		{
			_hoveredAxis = ResolveHoveredAxis(
				mode,
				mousePosition,
				viewProjection,
				viewportState.ImageMin,
				viewportState.ImageMax,
				gizmoPivotWorld,
				axisX,
				axisY,
				axisZ,
				handleLength,
				ringRadius);

			if (mouseInViewport && leftClicked && _hoveredAxis != GizmoAxis.None)
			{
				TryBeginDrag(
					world,
					entity,
					mode,
					space,
					_hoveredAxis,
					mousePosition,
					inverseViewProjection,
					cameraPosition,
					gizmoPivotWorld,
					entityWorldPosition,
					objectWorldRotation,
					localTransform.LocalScale,
					axisX,
					axisY,
					axisZ,
					handleLength);
			}
		}

		if (_dragState.Active)
		{
			UpdateDrag(world, mousePosition, inverseViewProjection);
			_hoveredAxis = _dragState.Axis;
		}

		DrawGizmo(
			mode,
			_hoveredAxis,
			_dragState.Active ? _dragState.Axis : GizmoAxis.None,
			viewProjection,
			viewportState.ImageMin,
			viewportState.ImageMax,
			gizmoPivotWorld,
			pivotScreen,
			axisX,
			axisY,
			axisZ,
			handleLength,
			ringRadius);

		_viewportStateBus.PublishGizmoDragging(_dragState.Active);
	}

	private void TryBeginDrag(
		World world,
		Entity entity,
		TransformGizmoMode mode,
		TransformSpace space,
		GizmoAxis axis,
		Vector2 mousePosition,
		Matrix4x4 inverseViewProjection,
		Vector3 cameraPosition,
		Vector3 gizmoPivotWorld,
		Vector3 entityWorldPosition,
		Quaternion objectWorldRotation,
		Vector3 objectLocalScale,
		Vector3 axisX,
		Vector3 axisY,
		Vector3 axisZ,
		float handleLength)
	{
		if (TryBuildMouseRay(mousePosition, inverseViewProjection, out var ray) == false)
		{
			return;
		}

		var axisWorld = SelectAxis(axis, axisX, axisY, axisZ);
		if (axisWorld == Vector3.Zero)
		{
			return;
		}

		_dragState.Active = true;
		_dragState.Entity = entity;
		_dragState.Mode = mode;
		_dragState.Space = space;
		_dragState.Axis = axis;
		_dragState.PivotWorld = gizmoPivotWorld;
		_dragState.AxisWorld = axisWorld;
		_dragState.StartEntityWorldPosition = entityWorldPosition;
		_dragState.StartWorldRotation = objectWorldRotation;
		_dragState.StartLocalScale = objectLocalScale;
		_dragState.HandleLength = MathF.Max(handleLength, MinHandleLength);

		if (mode == TransformGizmoMode.Rotate)
		{
			if (TryIntersectRayPlane(ray, gizmoPivotWorld, axisWorld, out var hitPoint) == false)
			{
				EndDrag();
				return;
			}

			var startVector = hitPoint - gizmoPivotWorld;
			if (startVector.LengthSquared() < 1e-8f)
			{
				EndDrag();
				return;
			}

			_dragState.StartPlaneVector = Vector3.Normalize(startVector);
			return;
		}

		var dragPlaneNormal = BuildAxisDragPlaneNormal(axisWorld, cameraPosition, gizmoPivotWorld);
		if (TryIntersectRayPlane(ray, gizmoPivotWorld, dragPlaneNormal, out var planeHitPoint) == false)
		{
			EndDrag();
			return;
		}

		_dragState.DragPlaneNormal = dragPlaneNormal;
		_dragState.StartAxisParameter = Vector3.Dot(planeHitPoint - gizmoPivotWorld, axisWorld);
	}

	private void UpdateDrag(World world, Vector2 mousePosition, Matrix4x4 inverseViewProjection)
	{
		if (_dragState.Active == false || TryBuildMouseRay(mousePosition, inverseViewProjection, out var ray) == false)
		{
			return;
		}

		switch (_dragState.Mode)
		{
			case TransformGizmoMode.Translate:
			{
				if (TryIntersectRayPlane(ray, _dragState.PivotWorld, _dragState.DragPlaneNormal, out var hitPoint) == false)
				{
					return;
				}

				var currentParameter = Vector3.Dot(hitPoint - _dragState.PivotWorld, _dragState.AxisWorld);
				var delta = currentParameter - _dragState.StartAxisParameter;
				var worldPosition = _dragState.StartEntityWorldPosition + (_dragState.AxisWorld * delta);
				world.SetWorldPosition(_dragState.Entity, worldPosition);
				break;
			}
			case TransformGizmoMode.Rotate:
			{
				if (TryIntersectRayPlane(ray, _dragState.PivotWorld, _dragState.AxisWorld, out var hitPoint) == false)
				{
					return;
				}

				var currentVector = hitPoint - _dragState.PivotWorld;
				if (currentVector.LengthSquared() < 1e-8f)
				{
					return;
				}

				currentVector = Vector3.Normalize(currentVector);
				var angle = SignedAngle(_dragState.StartPlaneVector, currentVector, _dragState.AxisWorld);
				var deltaRotation = Quaternion.CreateFromAxisAngle(_dragState.AxisWorld, angle);
				var targetWorldRotation = NormalizeOrIdentity(deltaRotation * _dragState.StartWorldRotation);
				world.SetWorldRotation(_dragState.Entity, targetWorldRotation);
				break;
			}
			case TransformGizmoMode.Scale:
			{
				if (TryIntersectRayPlane(ray, _dragState.PivotWorld, _dragState.DragPlaneNormal, out var hitPoint) == false)
				{
					return;
				}

				var currentParameter = Vector3.Dot(hitPoint - _dragState.PivotWorld, _dragState.AxisWorld);
				var delta = currentParameter - _dragState.StartAxisParameter;
				var factor = MathF.Max(MinScaleComponent, 1.0f + (delta / _dragState.HandleLength));
				var localScale = _dragState.StartLocalScale;
				switch (_dragState.Axis)
				{
					case GizmoAxis.X:
						localScale.X = MathF.Max(MinScaleComponent, localScale.X * factor);
						break;
					case GizmoAxis.Y:
						localScale.Y = MathF.Max(MinScaleComponent, localScale.Y * factor);
						break;
					case GizmoAxis.Z:
						localScale.Z = MathF.Max(MinScaleComponent, localScale.Z * factor);
						break;
				}

				world.SetLocalScale(_dragState.Entity, localScale);
				break;
			}
		}
	}

	private GizmoAxis ResolveHoveredAxis(
		TransformGizmoMode mode,
		Vector2 mousePosition,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		Vector3 pivotWorld,
		Vector3 axisX,
		Vector3 axisY,
		Vector3 axisZ,
		float handleLength,
		float ringRadius)
	{
		return mode == TransformGizmoMode.Rotate
			? ResolveRotateHover(mousePosition, viewProjection, viewportMin, viewportMax, pivotWorld, axisX, axisY, axisZ, ringRadius)
			: ResolveAxisHover(mousePosition, viewProjection, viewportMin, viewportMax, pivotWorld, axisX, axisY, axisZ, handleLength);
	}

	private static GizmoAxis ResolveAxisHover(
		Vector2 mousePosition,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		Vector3 pivotWorld,
		Vector3 axisX,
		Vector3 axisY,
		Vector3 axisZ,
		float handleLength)
	{
		if (TryProjectToScreen(pivotWorld, viewProjection, viewportMin, viewportMax, out var pivotScreen) == false)
		{
			return GizmoAxis.None;
		}

		var bestAxis = GizmoAxis.None;
		var bestDistanceSquared = float.MaxValue;
		TestAxisHover(
			mousePosition,
			pivotScreen,
			pivotWorld + axisX * handleLength,
			GizmoAxis.X,
			viewProjection,
			viewportMin,
			viewportMax,
			ref bestAxis,
			ref bestDistanceSquared);
		TestAxisHover(
			mousePosition,
			pivotScreen,
			pivotWorld + axisY * handleLength,
			GizmoAxis.Y,
			viewProjection,
			viewportMin,
			viewportMax,
			ref bestAxis,
			ref bestDistanceSquared);
		TestAxisHover(
			mousePosition,
			pivotScreen,
			pivotWorld + axisZ * handleLength,
			GizmoAxis.Z,
			viewProjection,
			viewportMin,
			viewportMax,
			ref bestAxis,
			ref bestDistanceSquared);
		return bestAxis;
	}

	private static void TestAxisHover(
		Vector2 mousePosition,
		Vector2 pivotScreen,
		Vector3 endWorld,
		GizmoAxis axis,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		ref GizmoAxis bestAxis,
		ref float bestDistanceSquared)
	{
		if (TryProjectToScreen(endWorld, viewProjection, viewportMin, viewportMax, out var endScreen) == false)
		{
			return;
		}

		var distanceSquared = DistanceToSegmentSquared(mousePosition, pivotScreen, endScreen);
		if (distanceSquared <= AxisHitRadiusPixels * AxisHitRadiusPixels && distanceSquared < bestDistanceSquared)
		{
			bestDistanceSquared = distanceSquared;
			bestAxis = axis;
		}
	}

	private static GizmoAxis ResolveRotateHover(
		Vector2 mousePosition,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		Vector3 pivotWorld,
		Vector3 axisX,
		Vector3 axisY,
		Vector3 axisZ,
		float ringRadius)
	{
		var bestAxis = GizmoAxis.None;
		var bestDistanceSquared = float.MaxValue;
		TestRingHover(
			mousePosition,
			pivotWorld,
			axisX,
			ringRadius,
			GizmoAxis.X,
			viewProjection,
			viewportMin,
			viewportMax,
			ref bestAxis,
			ref bestDistanceSquared);
		TestRingHover(
			mousePosition,
			pivotWorld,
			axisY,
			ringRadius,
			GizmoAxis.Y,
			viewProjection,
			viewportMin,
			viewportMax,
			ref bestAxis,
			ref bestDistanceSquared);
		TestRingHover(
			mousePosition,
			pivotWorld,
			axisZ,
			ringRadius,
			GizmoAxis.Z,
			viewProjection,
			viewportMin,
			viewportMax,
			ref bestAxis,
			ref bestDistanceSquared);
		return bestAxis;
	}

	private static void TestRingHover(
		Vector2 mousePosition,
		Vector3 pivotWorld,
		Vector3 axisWorld,
		float ringRadius,
		GizmoAxis axis,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		ref GizmoAxis bestAxis,
		ref float bestDistanceSquared)
	{
		Span<Vector2> ringPoints = stackalloc Vector2[RingSegments + 1];
		if (TryBuildRingScreenPoints(
			    pivotWorld,
			    axisWorld,
			    ringRadius,
			    viewProjection,
			    viewportMin,
			    viewportMax,
			    ringPoints) == false)
		{
			return;
		}

		var distanceSquared = DistanceToPolylineSquared(mousePosition, ringPoints);
		if (distanceSquared <= RingHitRadiusPixels * RingHitRadiusPixels && distanceSquared < bestDistanceSquared)
		{
			bestDistanceSquared = distanceSquared;
			bestAxis = axis;
		}
	}

	private static bool TryBuildRingScreenPoints(
		Vector3 pivotWorld,
		Vector3 axisWorld,
		float ringRadius,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		Span<Vector2> destination)
	{
		if (destination.Length < RingSegments + 1)
		{
			return false;
		}

		BuildPlaneBasis(axisWorld, out var tangent, out var bitangent);
		for (var i = 0; i <= RingSegments; i++)
		{
			var t = i / (float)RingSegments;
			var angle = t * MathF.Tau;
			var offset = (tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)) * ringRadius;
			var point = pivotWorld + offset;
			if (TryProjectToScreen(point, viewProjection, viewportMin, viewportMax, out var screenPoint) == false)
			{
				return false;
			}

			destination[i] = screenPoint;
		}

		return true;
	}

	private static void DrawGizmo(
		TransformGizmoMode mode,
		GizmoAxis hoveredAxis,
		GizmoAxis activeAxis,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		Vector3 pivotWorld,
		Vector2 pivotScreen,
		Vector3 axisX,
		Vector3 axisY,
		Vector3 axisZ,
		float handleLength,
		float ringRadius)
	{
		var drawList = ImGui.GetWindowDrawList();
		if (mode == TransformGizmoMode.Rotate)
		{
			DrawRing(drawList, pivotWorld, axisX, ringRadius, viewProjection, viewportMin, viewportMax, AxisColor(GizmoAxis.X, hoveredAxis, activeAxis));
			DrawRing(drawList, pivotWorld, axisY, ringRadius, viewProjection, viewportMin, viewportMax, AxisColor(GizmoAxis.Y, hoveredAxis, activeAxis));
			DrawRing(drawList, pivotWorld, axisZ, ringRadius, viewProjection, viewportMin, viewportMax, AxisColor(GizmoAxis.Z, hoveredAxis, activeAxis));
			return;
		}

		DrawAxisLine(drawList, pivotScreen, pivotWorld + axisX * handleLength, viewProjection, viewportMin, viewportMax, AxisColor(GizmoAxis.X, hoveredAxis, activeAxis), mode);
		DrawAxisLine(drawList, pivotScreen, pivotWorld + axisY * handleLength, viewProjection, viewportMin, viewportMax, AxisColor(GizmoAxis.Y, hoveredAxis, activeAxis), mode);
		DrawAxisLine(drawList, pivotScreen, pivotWorld + axisZ * handleLength, viewProjection, viewportMin, viewportMax, AxisColor(GizmoAxis.Z, hoveredAxis, activeAxis), mode);
		drawList.AddCircleFilled(pivotScreen, 4.0f, ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.95f, 0.95f, 1.0f)));
	}

	private static void DrawAxisLine(
		ImDrawListPtr drawList,
		Vector2 pivotScreen,
		Vector3 endWorld,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		Vector4 color,
		TransformGizmoMode mode)
	{
		if (TryProjectToScreen(endWorld, viewProjection, viewportMin, viewportMax, out var endScreen) == false)
		{
			return;
		}

		var packedColor = ImGui.ColorConvertFloat4ToU32(color);
		drawList.AddLine(pivotScreen, endScreen, packedColor, 3.0f);
		if (mode == TransformGizmoMode.Scale)
		{
			drawList.AddCircleFilled(endScreen, 5.0f, packedColor);
			return;
		}

		drawList.AddCircle(endScreen, 4.0f, packedColor, 10, 3.0f);
	}

	private static void DrawRing(
		ImDrawListPtr drawList,
		Vector3 pivotWorld,
		Vector3 axisWorld,
		float ringRadius,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		Vector4 color)
	{
		Span<Vector2> ringPoints = stackalloc Vector2[RingSegments + 1];
		if (TryBuildRingScreenPoints(
			    pivotWorld,
			    axisWorld,
			    ringRadius,
			    viewProjection,
			    viewportMin,
			    viewportMax,
			    ringPoints) == false)
		{
			return;
		}

		var packedColor = ImGui.ColorConvertFloat4ToU32(color);
		for (var i = 0; i < RingSegments; i++)
		{
			drawList.AddLine(ringPoints[i], ringPoints[i + 1], packedColor, 2.0f);
		}
	}

	private static Vector4 AxisColor(GizmoAxis axis, GizmoAxis hoveredAxis, GizmoAxis activeAxis)
	{
		var baseColor = axis switch
		{
			GizmoAxis.X => new Vector4(0.93f, 0.25f, 0.25f, 1.0f),
			GizmoAxis.Y => new Vector4(0.25f, 0.85f, 0.35f, 1.0f),
			GizmoAxis.Z => new Vector4(0.28f, 0.52f, 0.96f, 1.0f),
			_ => new Vector4(0.95f, 0.95f, 0.95f, 1.0f)
		};

		if (axis == activeAxis)
		{
			return new Vector4(1.0f, 0.92f, 0.42f, 1.0f);
		}

		if (axis == hoveredAxis)
		{
			return Vector4.Lerp(baseColor, Vector4.One, 0.25f);
		}

		return baseColor;
	}

	private bool TryBuildMouseRay(Vector2 mousePosition, Matrix4x4 inverseViewProjection, out Ray ray)
	{
		ray = default;
		var viewportState = _viewportStateBus.GetUiState();
		if (IsPointInsideRect(mousePosition, viewportState.ImageMin, viewportState.ImageMax) == false)
		{
			return false;
		}

		var width = viewportState.ImageMax.X - viewportState.ImageMin.X;
		var height = viewportState.ImageMax.Y - viewportState.ImageMin.Y;
		if (width <= 1e-5f || height <= 1e-5f)
		{
			return false;
		}

		var u = (mousePosition.X - viewportState.ImageMin.X) / width;
		var v = (mousePosition.Y - viewportState.ImageMin.Y) / height;
		var ndcX = (u * 2.0f) - 1.0f;
		var ndcY = 1.0f - (v * 2.0f);
		var nearPoint = UnprojectPoint(new Vector3(ndcX, ndcY, 0.0f), inverseViewProjection);
		var farPoint = UnprojectPoint(new Vector3(ndcX, ndcY, 1.0f), inverseViewProjection);
		if (nearPoint.HasValue == false || farPoint.HasValue == false)
		{
			return false;
		}

		var direction = farPoint.Value - nearPoint.Value;
		if (direction.LengthSquared() <= 1e-8f)
		{
			return false;
		}

		ray = new Ray(nearPoint.Value, Vector3.Normalize(direction));
		return true;
	}

	private static Vector3? UnprojectPoint(Vector3 ndc, Matrix4x4 inverseViewProjection)
	{
		var clip = new Vector4(ndc, 1.0f);
		var world = Vector4.Transform(clip, inverseViewProjection);
		if (MathF.Abs(world.W) <= 1e-6f)
		{
			return null;
		}

		return new Vector3(world.X / world.W, world.Y / world.W, world.Z / world.W);
	}

	private static bool TryProjectToScreen(
		Vector3 worldPoint,
		Matrix4x4 viewProjection,
		Vector2 viewportMin,
		Vector2 viewportMax,
		out Vector2 screenPoint)
	{
		screenPoint = Vector2.Zero;
		var clip = Vector4.Transform(new Vector4(worldPoint, 1.0f), viewProjection);
		// Reject points at/behind the camera to avoid mirrored projection artifacts.
		if (clip.W <= 1e-6f)
		{
			return false;
		}

		var ndc = new Vector3(clip.X, clip.Y, clip.Z) / clip.W;
		// Left-handed perspective in this engine maps visible depth to [0, 1].
		// Ignore points outside clip depth so gizmos do not appear when behind camera.
		if (ndc.Z < 0.0f || ndc.Z > 1.0f)
		{
			return false;
		}

		var width = viewportMax.X - viewportMin.X;
		var height = viewportMax.Y - viewportMin.Y;
		if (width <= 1e-5f || height <= 1e-5f)
		{
			return false;
		}

		var sx = viewportMin.X + ((ndc.X * 0.5f + 0.5f) * width);
		var sy = viewportMin.Y + ((1.0f - (ndc.Y * 0.5f + 0.5f)) * height);
		screenPoint = new Vector2(sx, sy);
		return float.IsFinite(sx) && float.IsFinite(sy);
	}

	private static Vector3 BuildAxisDragPlaneNormal(Vector3 axis, Vector3 cameraPosition, Vector3 pivot)
	{
		var toCamera = SafeNormalize(cameraPosition - pivot, Vector3.UnitZ);
		var side = Vector3.Cross(axis, toCamera);
		if (side.LengthSquared() <= 1e-8f)
		{
			side = Vector3.Cross(axis, Vector3.UnitY);
			if (side.LengthSquared() <= 1e-8f)
			{
				side = Vector3.Cross(axis, Vector3.UnitX);
			}
		}

		var normal = Vector3.Cross(axis, side);
		return SafeNormalize(normal, Vector3.UnitY);
	}

	private static bool TryIntersectRayPlane(Ray ray, Vector3 planePoint, Vector3 planeNormal, out Vector3 hitPoint)
	{
		hitPoint = Vector3.Zero;
		var denom = Vector3.Dot(planeNormal, ray.Direction);
		if (MathF.Abs(denom) <= 1e-6f)
		{
			return false;
		}

		var distance = Vector3.Dot(planePoint - ray.Origin, planeNormal) / denom;
		if (distance < 0.0f)
		{
			return false;
		}

		hitPoint = ray.Origin + ray.Direction * distance;
		return true;
	}

	private static float SignedAngle(Vector3 from, Vector3 to, Vector3 axis)
	{
		var cross = Vector3.Cross(from, to);
		var sin = Vector3.Dot(axis, cross);
		var cos = Math.Clamp(Vector3.Dot(from, to), -1.0f, 1.0f);
		return MathF.Atan2(sin, cos);
	}

	private static void BuildPlaneBasis(Vector3 normal, out Vector3 tangent, out Vector3 bitangent)
	{
		var reference = MathF.Abs(Vector3.Dot(normal, Vector3.UnitY)) > 0.99f ? Vector3.UnitX : Vector3.UnitY;
		tangent = SafeNormalize(Vector3.Cross(reference, normal), Vector3.UnitX);
		bitangent = SafeNormalize(Vector3.Cross(normal, tangent), Vector3.UnitZ);
	}

	private static Vector3 SelectAxis(GizmoAxis axis, Vector3 x, Vector3 y, Vector3 z)
	{
		return axis switch
		{
			GizmoAxis.X => x,
			GizmoAxis.Y => y,
			GizmoAxis.Z => z,
			_ => Vector3.Zero
		};
	}

	private static Vector3 ResolveGizmoPivotWorld(
		World world,
		Entity entity,
		in WorldTransform worldTransform,
		TransformPivotMode pivotMode)
	{
		if (pivotMode == TransformPivotMode.TransformPivot)
		{
			return worldTransform.LocalToWorld.Translation;
		}

		if (TryGetVisualCenterWorld(world, entity, out var visualCenter))
		{
			return visualCenter;
		}

		return worldTransform.LocalToWorld.Translation;
	}

	private static bool TryGetVisualCenterWorld(World world, Entity entity, out Vector3 visualCenter)
	{
		visualCenter = Vector3.Zero;
		if (TryGetEntityMeshCenter(world, entity, out visualCenter))
		{
			return true;
		}

		if (world.HasComponent<Children>(entity) == false)
		{
			return false;
		}

		var sum = Vector3.Zero;
		var count = 0;
		var child = world.GetComponent<Children>(entity).First;
		while (child.IsValid)
		{
			AccumulateDescendantMeshCenters(world, child, ref sum, ref count);
			if (world.HasComponent<Sibling>(child) == false)
			{
				break;
			}

			child = world.GetComponent<Sibling>(child).Next;
		}

		if (count == 0)
		{
			return false;
		}

		visualCenter = sum / count;
		return true;
	}

	private static void AccumulateDescendantMeshCenters(World world, Entity entity, ref Vector3 sum, ref int count)
	{
		if (TryGetEntityMeshCenter(world, entity, out var center))
		{
			sum += center;
			count++;
		}

		if (world.HasComponent<Children>(entity) == false)
		{
			return;
		}

		var child = world.GetComponent<Children>(entity).First;
		while (child.IsValid)
		{
			AccumulateDescendantMeshCenters(world, child, ref sum, ref count);
			if (world.HasComponent<Sibling>(child) == false)
			{
				break;
			}

			child = world.GetComponent<Sibling>(child).Next;
		}
	}

	private static bool TryGetEntityMeshCenter(World world, Entity entity, out Vector3 centerWorld)
	{
		centerWorld = Vector3.Zero;
		if (world.HasComponent<MeshRenderer>(entity) == false || world.HasComponent<WorldTransform>(entity) == false)
		{
			return false;
		}

		ref var meshRenderer = ref world.GetComponent<MeshRenderer>(entity);
		if (meshRenderer.Mesh is null)
		{
			return false;
		}

		ref var entityWorldTransform = ref world.GetComponent<WorldTransform>(entity);
		var centerLocal = meshRenderer.Mesh.BoundingSphere.Center;
		centerWorld = Vector3.Transform(centerLocal, entityWorldTransform.LocalToWorld);
		return true;
	}

	private static bool IsViewportValid(in SceneViewportUiState state)
	{
		return state.Visible &&
		       state.ContentSizePixels.X > 0 &&
		       state.ContentSizePixels.Y > 0 &&
		       state.ImageMax.X > state.ImageMin.X &&
		       state.ImageMax.Y > state.ImageMin.Y;
	}

	private static bool IsPointInsideRect(Vector2 point, Vector2 min, Vector2 max)
	{
		return point.X >= min.X &&
		       point.X <= max.X &&
		       point.Y >= min.Y &&
		       point.Y <= max.Y;
	}

	private static float DistanceToPolylineSquared(Vector2 point, ReadOnlySpan<Vector2> polyline)
	{
		var minDistance = float.MaxValue;
		for (var i = 0; i < polyline.Length - 1; i++)
		{
			var distance = DistanceToSegmentSquared(point, polyline[i], polyline[i + 1]);
			if (distance < minDistance)
			{
				minDistance = distance;
			}
		}

		return minDistance;
	}

	private static float DistanceToSegmentSquared(Vector2 point, Vector2 a, Vector2 b)
	{
		var ab = b - a;
		var abLengthSquared = ab.LengthSquared();
		if (abLengthSquared <= 1e-8f)
		{
			return (point - a).LengthSquared();
		}

		var t = Vector2.Dot(point - a, ab) / abLengthSquared;
		t = Math.Clamp(t, 0.0f, 1.0f);
		var projection = a + (ab * t);
		return (point - projection).LengthSquared();
	}

	private static Quaternion NormalizeOrIdentity(Quaternion rotation)
	{
		return rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
	}

	private static Vector3 SafeNormalize(Vector3 vector, Vector3 fallback)
	{
		return vector.LengthSquared() > 1e-8f ? Vector3.Normalize(vector) : fallback;
	}

	private void EndDrag()
	{
		_dragState.Active = false;
		_dragState.Entity = default;
		_dragState.Mode = TransformGizmoMode.Translate;
		_dragState.Axis = GizmoAxis.None;
		_dragState.Space = TransformSpace.Local;
		_dragState.PivotWorld = Vector3.Zero;
		_dragState.AxisWorld = Vector3.Zero;
		_dragState.DragPlaneNormal = Vector3.Zero;
		_dragState.StartPlaneVector = Vector3.Zero;
		_dragState.StartEntityWorldPosition = Vector3.Zero;
		_dragState.StartWorldRotation = Quaternion.Identity;
		_dragState.StartLocalScale = Vector3.One;
		_dragState.StartAxisParameter = 0.0f;
		_dragState.HandleLength = 1.0f;
		_hoveredAxis = GizmoAxis.None;
		_viewportStateBus.PublishGizmoDragging(false);
	}

	private enum GizmoAxis
	{
		None = 0,
		X,
		Y,
		Z
	}

	private readonly struct Ray
	{
		public Ray(Vector3 origin, Vector3 direction)
		{
			Origin = origin;
			Direction = direction;
		}

		public Vector3 Origin { get; }
		public Vector3 Direction { get; }
	}

	private sealed class GizmoDragState
	{
		public bool Active;
		public Entity Entity;
		public TransformGizmoMode Mode;
		public TransformSpace Space;
		public GizmoAxis Axis;
		public Vector3 PivotWorld;
		public Vector3 AxisWorld;
		public Vector3 DragPlaneNormal;
		public Vector3 StartPlaneVector;
		public Vector3 StartEntityWorldPosition;
		public Quaternion StartWorldRotation = Quaternion.Identity;
		public Vector3 StartLocalScale = Vector3.One;
		public float StartAxisParameter;
		public float HandleLength = 1.0f;
	}
}
