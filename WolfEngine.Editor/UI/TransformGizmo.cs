using System.Numerics;
using ImGuiNET;
using WolfEngine.Mathematics;
using WolfEngine.Rendering;
using WolfEngine.Rendering.UI;

namespace WolfEngine.Editor.UI;

/// <summary>The image a gizmo is drawn over and the projection that produced it.</summary>
public readonly record struct TransformGizmoView(SceneViewportUiState Viewport, ViewProjection ViewProjection);

/// <summary>Pointer state for one frame, in the same screen units as the view's image rectangle.</summary>
public readonly record struct TransformGizmoPointer(
	Vector2 Position,
	bool CanBeginDrag,
	bool LeftDown,
	bool LeftClicked,
	bool LeftReleased)
{
	public static TransformGizmoPointer FromImGui(in SceneViewportUiState viewport)
	{
		return new TransformGizmoPointer(
			ImGui.GetIO().MousePos,
			viewport.PointerAvailable,
			viewport.PointerCaptured && ImGui.IsMouseDown(ImGuiMouseButton.Left),
			ImGui.IsMouseClicked(ImGuiMouseButton.Left),
			ImGui.IsMouseReleased(ImGuiMouseButton.Left));
	}
}

/// <param name="RotateAroundPivot">When rotating, also orbit each target's position around the pivot.</param>
public readonly record struct TransformGizmoOptions(
	TransformGizmoMode Mode,
	TransformSpace Space,
	Vector3 PivotWorld,
	bool RotateAroundPivot);

/// <summary>
/// Translate, rotate and scale handles over any rendered view, moving any
/// <see cref="ITransformGizmoTarget"/>. Holds hover and drag state between frames, so each view
/// that shows a gizmo owns its own instance.
/// </summary>
public sealed class TransformGizmo
{
	private const float MinHandleLength = 0.35f;
	private const float HandleLengthDistanceScale = 0.15f;
	private const float AxisHitRadiusPixels = 10.0f;
	private const float RingHitRadiusPixels = 10.0f;
	private const int RingSegments = 64;
	private const float MinScaleComponent = 0.05f;

	private readonly GizmoDragState _dragState = new();
	private GizmoAxis _hoveredAxis;
	private GizmoFrame _frame;

	public bool IsDragging => _dragState.Active;

	/// <summary>Runs hover, drag start, drag update and release for one frame. Call <see cref="Draw"/> afterwards.</summary>
	public void Update(
		ITransformGizmoTarget? target,
		in TransformGizmoView view,
		in TransformGizmoPointer pointer,
		in TransformGizmoOptions options)
	{
		_frame = default;

		if (target is null || target.Count == 0 || view.ViewProjection.IsValid == false)
		{
			Cancel();
			return;
		}

		var primary = target.GetState(0);
		var objectWorldRotation = NormalizeOrIdentity(primary.WorldRotation);
		var viewProjection = view.ViewProjection.WorldToClip;
		var viewportMin = view.Viewport.ImageMin;
		var viewportMax = view.Viewport.ImageMax;
		var pivotWorld = options.PivotWorld;

		var axisRotation = options.Mode == TransformGizmoMode.Scale || options.Space == TransformSpace.Local
			? objectWorldRotation
			: Quaternion.Identity;
		var axisX = SafeNormalize(Vector3.Transform(Vector3.UnitX, axisRotation), Vector3.UnitX);
		var axisY = SafeNormalize(Vector3.Transform(Vector3.UnitY, axisRotation), Vector3.UnitY);
		var axisZ = SafeNormalize(Vector3.Transform(Vector3.UnitZ, axisRotation), Vector3.UnitZ);

		var cameraPosition = view.ViewProjection.CameraOrigin;
		var distanceToCamera = Vector3.Distance(cameraPosition, pivotWorld);
		var handleLength = MathF.Max(MinHandleLength, distanceToCamera * HandleLengthDistanceScale);
		var ringRadius = handleLength * 0.8f;

		if (TryProjectToScreen(pivotWorld, viewProjection, viewportMin, viewportMax, out var pivotScreen) == false)
		{
			Cancel();
			return;
		}

		if (_dragState.Active &&
		    (pointer.LeftDown == false || pointer.LeftReleased))
		{
			EndDrag(commit: true);
		}

		if (_dragState.Active == false)
		{
			_hoveredAxis = GizmoAxis.None;
			if (pointer.CanBeginDrag)
			{
				_hoveredAxis = ResolveHoveredAxis(
					options.Mode,
					pointer.Position,
					viewProjection,
					viewportMin,
					viewportMax,
					pivotWorld,
					axisX,
					axisY,
					axisZ,
					handleLength,
					ringRadius);
			}

			if (pointer.LeftClicked && _hoveredAxis != GizmoAxis.None)
			{
				TryBeginDrag(
					target,
					view,
					options,
					_hoveredAxis,
					pointer.Position,
					cameraPosition,
					axisX,
					axisY,
					axisZ,
					handleLength);
			}
		}

		if (_dragState.Active)
		{
			UpdateDrag(view, pointer.Position);
			_hoveredAxis = _dragState.Axis;
		}

		_frame = new GizmoFrame(
			true,
			options.Mode,
			viewProjection,
			viewportMin,
			viewportMax,
			pivotWorld,
			pivotScreen,
			axisX,
			axisY,
			axisZ,
			handleLength,
			ringRadius);
	}

	/// <summary>Draws the handles computed by the last <see cref="Update"/>, if they were visible.</summary>
	public void Draw(ImDrawListPtr drawList)
	{
		if (_frame.Visible == false)
		{
			return;
		}

		var activeAxis = _dragState.Active ? _dragState.Axis : GizmoAxis.None;
		if (_frame.Mode == TransformGizmoMode.Rotate)
		{
			DrawRing(drawList, _frame, _frame.AxisX, AxisColor(GizmoAxis.X, _hoveredAxis, activeAxis));
			DrawRing(drawList, _frame, _frame.AxisY, AxisColor(GizmoAxis.Y, _hoveredAxis, activeAxis));
			DrawRing(drawList, _frame, _frame.AxisZ, AxisColor(GizmoAxis.Z, _hoveredAxis, activeAxis));
			return;
		}

		DrawAxisLine(drawList, _frame, _frame.AxisX, AxisColor(GizmoAxis.X, _hoveredAxis, activeAxis));
		DrawAxisLine(drawList, _frame, _frame.AxisY, AxisColor(GizmoAxis.Y, _hoveredAxis, activeAxis));
		DrawAxisLine(drawList, _frame, _frame.AxisZ, AxisColor(GizmoAxis.Z, _hoveredAxis, activeAxis));
		drawList.AddCircleFilled(_frame.PivotScreen, 4.0f, ImGui.ColorConvertFloat4ToU32(new ColorRGBA(0.95f, 0.95f, 0.95f, 1.0f)));
	}

	/// <summary>Abandons any drag without committing it and hides the gizmo until the next update.</summary>
	public void Cancel()
	{
		EndDrag(commit: false);
		_frame = default;
	}

	private void TryBeginDrag(
		ITransformGizmoTarget target,
		in TransformGizmoView view,
		in TransformGizmoOptions options,
		GizmoAxis axis,
		Vector2 mousePosition,
		Vector3 cameraPosition,
		Vector3 axisX,
		Vector3 axisY,
		Vector3 axisZ,
		float handleLength)
	{
		if (TryBuildMouseRay(view, mousePosition, out var ray) == false)
		{
			return;
		}

		var axisWorld = SelectAxis(axis, axisX, axisY, axisZ);
		if (axisWorld == Vector3.Zero)
		{
			return;
		}

		var pivotWorld = options.PivotWorld;
		Vector3 startPlaneVector = default;
		Vector3 dragPlaneNormal = default;
		var startAxisParameter = 0.0f;

		if (options.Mode == TransformGizmoMode.Rotate)
		{
			if (TryIntersectRayPlane(ray, pivotWorld, axisWorld, out var hitPoint) == false)
			{
				return;
			}

			var startVector = hitPoint - pivotWorld;
			if (startVector.LengthSquared() < 1e-8f)
			{
				return;
			}

			startPlaneVector = Vector3.Normalize(startVector);
		}
		else
		{
			dragPlaneNormal = BuildAxisDragPlaneNormal(axisWorld, cameraPosition, pivotWorld);
			if (TryIntersectRayPlane(ray, pivotWorld, dragPlaneNormal, out var planeHitPoint) == false)
			{
				return;
			}

			startAxisParameter = Vector3.Dot(planeHitPoint - pivotWorld, axisWorld);
		}

		_dragState.StartStates.Clear();
		for (var i = 0; i < target.Count; i++)
		{
			var state = target.GetState(i);
			_dragState.StartStates.Add(state with
			{
				WorldRotation = NormalizeOrIdentity(state.WorldRotation)
			});
		}

		_dragState.Active = true;
		_dragState.Target = target;
		_dragState.Mode = options.Mode;
		_dragState.Axis = axis;
		_dragState.PivotWorld = pivotWorld;
		_dragState.AxisWorld = axisWorld;
		_dragState.DragPlaneNormal = dragPlaneNormal;
		_dragState.StartPlaneVector = startPlaneVector;
		_dragState.StartAxisParameter = startAxisParameter;
		_dragState.RotateAroundPivot = options.RotateAroundPivot;
		_dragState.HandleLength = MathF.Max(handleLength, MinHandleLength);

		target.BeginEdit();
	}

	private void UpdateDrag(in TransformGizmoView view, Vector2 mousePosition)
	{
		if (_dragState.Active == false ||
		    TryBuildMouseRay(view, mousePosition, out var ray) == false)
		{
			return;
		}

		var target = _dragState.Target!;
		var startStates = _dragState.StartStates;

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
				for (var i = 0; i < startStates.Count; i++)
				{
					target.SetWorldPosition(i, startStates[i].WorldPosition + (_dragState.AxisWorld * delta));
				}

				target.OnTransformsChanged();
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
				for (var i = 0; i < startStates.Count; i++)
				{
					if (_dragState.RotateAroundPivot)
					{
						var relativePosition = startStates[i].WorldPosition - _dragState.PivotWorld;
						target.SetWorldPosition(i, _dragState.PivotWorld + Vector3.Transform(relativePosition, deltaRotation));
					}

					target.SetWorldRotation(i, NormalizeOrIdentity(deltaRotation * startStates[i].WorldRotation));
				}

				target.OnTransformsChanged();
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
				for (var i = 0; i < startStates.Count; i++)
				{
					var localScale = ScaleAxis(startStates[i].LocalScale, _dragState.Axis, factor);
					var relativePosition = startStates[i].WorldPosition - _dragState.PivotWorld;
					var scaledPosition = _dragState.PivotWorld +
					                     relativePosition +
					                     (_dragState.AxisWorld * Vector3.Dot(relativePosition, _dragState.AxisWorld) * (factor - 1.0f));
					target.SetWorldPosition(i, scaledPosition);
					target.SetLocalScale(i, localScale);
				}

				target.OnTransformsChanged();
				break;
			}
		}
	}

	private static Vector3 ScaleAxis(Vector3 localScale, GizmoAxis axis, float factor)
	{
		switch (axis)
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

		return localScale;
	}

	private void EndDrag(bool commit)
	{
		var target = _dragState.Target;
		var wasActive = _dragState.Active;

		_dragState.Reset();
		_hoveredAxis = GizmoAxis.None;

		if (wasActive)
		{
			target?.EndEdit(commit);
		}
	}

	private static GizmoAxis ResolveHoveredAxis(
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

	private static void DrawAxisLine(ImDrawListPtr drawList, in GizmoFrame frame, Vector3 axisWorld, ColorRGBA color)
	{
		var endWorld = frame.PivotWorld + axisWorld * frame.HandleLength;
		if (TryProjectToScreen(endWorld, frame.ViewProjection, frame.ViewportMin, frame.ViewportMax, out var endScreen) == false)
		{
			return;
		}

		var packedColor = ImGui.ColorConvertFloat4ToU32(color);
		drawList.AddLine(frame.PivotScreen, endScreen, packedColor, 3.0f);
		if (frame.Mode == TransformGizmoMode.Scale)
		{
			drawList.AddCircleFilled(endScreen, 5.0f, packedColor);
			return;
		}

		drawList.AddCircle(endScreen, 4.0f, packedColor, 10, 3.0f);
	}

	private static void DrawRing(ImDrawListPtr drawList, in GizmoFrame frame, Vector3 axisWorld, ColorRGBA color)
	{
		Span<Vector2> ringPoints = stackalloc Vector2[RingSegments + 1];
		if (TryBuildRingScreenPoints(
			    frame.PivotWorld,
			    axisWorld,
			    frame.RingRadius,
			    frame.ViewProjection,
			    frame.ViewportMin,
			    frame.ViewportMax,
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

	private static ColorRGBA AxisColor(GizmoAxis axis, GizmoAxis hoveredAxis, GizmoAxis activeAxis)
	{
		var baseColor = axis switch
		{
			GizmoAxis.X => new ColorRGBA(0.93f, 0.25f, 0.25f, 1.0f),
			GizmoAxis.Y => new ColorRGBA(0.25f, 0.85f, 0.35f, 1.0f),
			GizmoAxis.Z => new ColorRGBA(0.28f, 0.52f, 0.96f, 1.0f),
			_ => new ColorRGBA(0.95f, 0.95f, 0.95f, 1.0f)
		};

		if (axis == activeAxis)
		{
			return new ColorRGBA(1.0f, 0.92f, 0.42f, 1.0f);
		}

		if (axis == hoveredAxis)
		{
			return Vector4.Lerp(baseColor, Vector4.One, 0.25f);
		}

		return baseColor;
	}

	private static bool TryBuildMouseRay(in TransformGizmoView view, Vector2 mousePosition, out Ray ray)
	{
		return SceneViewportRayUtility.TryBuildWorldRay(view.Viewport, mousePosition, view.ViewProjection, out ray);
	}

	internal static bool TryProjectToScreen(
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

	private static bool TryIntersectRayPlane(in Ray ray, Vector3 planePoint, Vector3 planeNormal, out Vector3 hitPoint)
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

	internal static Quaternion NormalizeOrIdentity(Quaternion rotation)
	{
		return rotation.LengthSquared() > 0.0f ? Quaternion.Normalize(rotation) : Quaternion.Identity;
	}

	private static Vector3 SafeNormalize(Vector3 vector, Vector3 fallback)
	{
		return vector.LengthSquared() > 1e-8f ? Vector3.Normalize(vector) : fallback;
	}

	private enum GizmoAxis
	{
		None = 0,
		X,
		Y,
		Z
	}

	/// <summary>Everything <see cref="Draw"/> needs, computed by the last update.</summary>
	private readonly record struct GizmoFrame(
		bool Visible,
		TransformGizmoMode Mode,
		Matrix4x4 ViewProjection,
		Vector2 ViewportMin,
		Vector2 ViewportMax,
		Vector3 PivotWorld,
		Vector2 PivotScreen,
		Vector3 AxisX,
		Vector3 AxisY,
		Vector3 AxisZ,
		float HandleLength,
		float RingRadius);

	private sealed class GizmoDragState
	{
		public bool Active;
		public ITransformGizmoTarget? Target;
		public readonly List<TransformGizmoTargetState> StartStates = new();
		public TransformGizmoMode Mode;
		public GizmoAxis Axis;
		public Vector3 PivotWorld;
		public Vector3 AxisWorld;
		public Vector3 DragPlaneNormal;
		public Vector3 StartPlaneVector;
		public bool RotateAroundPivot;
		public float StartAxisParameter;
		public float HandleLength = 1.0f;

		public void Reset()
		{
			Active = false;
			Target = null;
			StartStates.Clear();
			Mode = TransformGizmoMode.Translate;
			Axis = GizmoAxis.None;
			PivotWorld = Vector3.Zero;
			AxisWorld = Vector3.Zero;
			DragPlaneNormal = Vector3.Zero;
			StartPlaneVector = Vector3.Zero;
			RotateAroundPivot = false;
			StartAxisParameter = 0.0f;
			HandleLength = 1.0f;
		}
	}
}
