using System.Numerics;
using ImGuiNET;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering;
using WolfEngine.Vfx;

namespace WolfEngine.Editor.UI;

/// <summary>The selected emitter's properties, grouped the way an artist thinks about an effect.</summary>
public sealed partial class VfxAssetEditor
{
	private const float CurveHeight = 64.0f;

	private bool DrawEmitter(VfxEmitterDefinition emitter)
	{
		var changed = false;

		if (EditorUIUtility.CollapsingHeader("Emitter", isOpenByDefault: true))
		{
			EditorUIUtility.BeginIndentedGroup();
			try
			{
				changed |= DrawGeneral(emitter);
			}
			finally
			{
				EditorUIUtility.EndIndentedGroup();
			}
		}

		if (EditorUIUtility.CollapsingHeader("Appearance", isOpenByDefault: true))
		{
			EditorUIUtility.BeginIndentedGroup();
			try
			{
				changed |= DrawAppearance(emitter);
			}
			finally
			{
				EditorUIUtility.EndIndentedGroup();
			}
		}

		if (emitter.Kind == VfxEmitterKind.Ribbon)
		{
			if (EditorUIUtility.CollapsingHeader("Ribbon", isOpenByDefault: true))
			{
				EditorUIUtility.BeginIndentedGroup();
				try
				{
					changed |= DrawRibbon(emitter.Ribbon);
				}
				finally
				{
					EditorUIUtility.EndIndentedGroup();
				}
			}

			return changed;
		}

		var sprite = emitter.Sprite;
		if (EditorUIUtility.CollapsingHeader("Emission", isOpenByDefault: true))
		{
			EditorUIUtility.BeginIndentedGroup();
			try
			{
				changed |= DrawEmission(sprite);
			}
			finally
			{
				EditorUIUtility.EndIndentedGroup();
			}
		}

		if (EditorUIUtility.CollapsingHeader("Spawn", isOpenByDefault: true))
		{
			EditorUIUtility.BeginIndentedGroup();
			try
			{
				changed |= DrawSpawn(sprite);
			}
			finally
			{
				EditorUIUtility.EndIndentedGroup();
			}
		}

		if (EditorUIUtility.CollapsingHeader("Motion", isOpenByDefault: true))
		{
			EditorUIUtility.BeginIndentedGroup();
			try
			{
				changed |= DrawMotion(sprite);
			}
			finally
			{
				EditorUIUtility.EndIndentedGroup();
			}
		}

		if (EditorUIUtility.CollapsingHeader("Size and rotation", isOpenByDefault: true))
		{
			EditorUIUtility.BeginIndentedGroup();
			try
			{
				changed |= DrawSizeAndRotation(sprite);
			}
			finally
			{
				EditorUIUtility.EndIndentedGroup();
			}
		}

		if (EditorUIUtility.CollapsingHeader("Sprite rendering"))
		{
			EditorUIUtility.BeginIndentedGroup();
			try
			{
				changed |= DrawSpriteRendering(sprite);
			}
			finally
			{
				EditorUIUtility.EndIndentedGroup();
			}
		}

		return changed;
	}

	private static bool DrawGeneral(VfxEmitterDefinition emitter)
	{
		var changed = false;

		var name = emitter.Name;
		if (EditorUIUtility.InputText("Name", ref name, 64))
		{
			emitter.Name = name;
			changed = true;
		}

		changed |= DrawEnum("Kind", emitter.Kind, value => emitter.Kind = value);

		var delay = emitter.StartDelay;
		if (EditorUIUtility.InputFloat("Start delay", ref delay))
		{
			emitter.StartDelay = MathF.Max(delay, 0.0f);
			changed = true;
		}

		var duration = emitter.Duration;
		if (EditorUIUtility.InputFloat("Duration", ref duration))
		{
			emitter.Duration = MathF.Max(duration, 0.0f);
			changed = true;
		}

		if (emitter.Duration <= 0.0f)
		{
			ImGui.TextDisabled("Zero: runs until the effect is stopped.");
		}
		else
		{
			var looping = emitter.Looping;
			if (EditorUIUtility.Checkbox("Looping", ref looping))
			{
				emitter.Looping = looping;
				changed = true;
			}
		}

		var seed = emitter.Seed;
		if (EditorUIUtility.InputInt("Seed", ref seed))
		{
			emitter.Seed = seed;
			changed = true;
		}

		return changed;
	}

	private bool DrawAppearance(VfxEmitterDefinition emitter)
	{
		var changed = false;

		var texture = emitter.Texture;
		if (_propertyDrawers.DrawAssetRef("Texture", ref texture, _linkButton))
		{
			emitter.Texture = texture;
			changed = true;
		}

		if (texture.IsValid == false)
		{
			ImGui.TextDisabled(emitter.Kind == VfxEmitterKind.Ribbon
				? "No texture: a solid strip with soft edges."
				: "No texture: a soft round puff.");
		}

		changed |= DrawEnum("Blend", emitter.Blend, value => emitter.Blend = value);

		var intensity = emitter.Intensity;
		if (EditorUIUtility.InputFloat("Intensity", ref intensity))
		{
			emitter.Intensity = MathF.Max(intensity, 0.0f);
			changed = true;
		}

		var soft = emitter.SoftParticleDistance;
		if (EditorUIUtility.InputFloat("Soft distance", ref soft))
		{
			emitter.SoftParticleDistance = MathF.Max(soft, 0.0f);
			changed = true;
		}

		var gradientLabel = emitter.Kind == VfxEmitterKind.Ribbon
			? "Colour along trail"
			: "Colour over lifetime";
		changed |= DrawGradient(gradientLabel, emitter.ColorOverLifetime);

		return changed;
	}

	private static bool DrawEmission(VfxSpriteSettings sprite)
	{
		var changed = false;

		var maxParticles = sprite.MaxParticles;
		if (EditorUIUtility.InputInt("Max particles", ref maxParticles))
		{
			sprite.MaxParticles = Math.Clamp(maxParticles, 1, VfxLimits.MaxParticlesPerEmitter);
			changed = true;
		}

		var rate = sprite.Rate;
		if (EditorUIUtility.InputFloat("Rate per second", ref rate))
		{
			sprite.Rate = MathF.Max(rate, 0.0f);
			changed = true;
		}

		var ratePerMetre = sprite.RatePerMetre;
		if (EditorUIUtility.InputFloat("Rate per metre", ref ratePerMetre))
		{
			sprite.RatePerMetre = MathF.Max(ratePerMetre, 0.0f);
			changed = true;
		}

		ImGui.TextDisabled("Bursts");
		var removeIndex = -1;
		for (var i = 0; i < sprite.Bursts.Count; i++)
		{
			ImGui.PushID(i);
			try
			{
				var burst = sprite.Bursts[i];
				var time = burst.Time;
				var count = burst.Count;
				ImGui.SetNextItemWidth(90.0f);
				if (ImGui.DragFloat("##time", ref time, 0.01f, 0.0f, 60.0f, "at %.2f s"))
				{
					sprite.Bursts[i] = burst with
					{
						Time = MathF.Max(time, 0.0f)
					};
					changed = true;
				}

				ImGui.SameLine();
				ImGui.SetNextItemWidth(90.0f);
				if (ImGui.DragInt("##count", ref count, 0.25f, 0, VfxLimits.MaxParticlesPerEmitter, "%d particles"))
				{
					sprite.Bursts[i] = sprite.Bursts[i] with
					{
						Count = Math.Max(count, 0)
					};
					changed = true;
				}

				ImGui.SameLine();
				if (ImGui.SmallButton("Remove"))
				{
					removeIndex = i;
				}
			}
			finally
			{
				ImGui.PopID();
			}
		}

		if (removeIndex >= 0)
		{
			sprite.Bursts.RemoveAt(removeIndex);
			changed = true;
		}

		if (ImGui.SmallButton("Add burst"))
		{
			sprite.Bursts.Add(new VfxBurst(0.0f, 10));
			changed = true;
		}

		return changed;
	}

	private static bool DrawSpawn(VfxSpriteSettings sprite)
	{
		var changed = DrawEnum("Shape", sprite.Shape, value => sprite.Shape = value);
		if (sprite.Shape != VfxSpawnShape.Point)
		{
			var size = sprite.ShapeSize;
			if (sprite.Shape == VfxSpawnShape.Box)
			{
				if (EditorUIUtility.InputVector3("Half extents", ref size))
				{
					sprite.ShapeSize = Vector3.Max(size, Vector3.Zero);
					changed = true;
				}
			}
			else
			{
				var radius = size.X;
				if (EditorUIUtility.InputFloat("Radius", ref radius))
				{
					sprite.ShapeSize = new Vector3(MathF.Max(radius, 0.0f), size.Y, size.Z);
					changed = true;
				}

				var edge = sprite.EmitFromEdge;
				if (EditorUIUtility.Checkbox(sprite.Shape == VfxSpawnShape.Circle ? "Emit from rim" : "Emit from surface", ref edge))
				{
					sprite.EmitFromEdge = edge;
					changed = true;
				}
			}
		}

		var offset = sprite.Offset;
		if (EditorUIUtility.InputVector3("Offset", ref offset))
		{
			sprite.Offset = offset;
			changed = true;
		}

		var color = sprite.StartColor.ToVector4();
		if (EditorUIUtility.ColorEdit4("Start colour", ref color))
		{
			sprite.StartColor = color;
			changed = true;
		}

		var variant = sprite.StartColorVariant.ToVector4();
		if (EditorUIUtility.ColorEdit4("Start colour variant", ref variant))
		{
			sprite.StartColorVariant = variant;
			changed = true;
		}

		return changed;
	}

	private static bool DrawMotion(VfxSpriteSettings sprite)
	{
		var changed = false;
		changed |= DrawRange("Lifetime", sprite.Lifetime, value => sprite.Lifetime = value, 0.01f);
		changed |= DrawRange("Speed", sprite.Speed, value => sprite.Speed = value);

		var velocity = sprite.Velocity;
		if (EditorUIUtility.InputVector3("Velocity", ref velocity))
		{
			sprite.Velocity = velocity;
			changed = true;
		}

		var inherit = sprite.InheritVelocity;
		if (ImGui.SliderFloat("Inherit velocity", ref inherit, 0.0f, 1.0f))
		{
			sprite.InheritVelocity = inherit;
			changed = true;
		}

		var gravity = sprite.Gravity;
		if (EditorUIUtility.InputFloat("Gravity", ref gravity))
		{
			sprite.Gravity = gravity;
			changed = true;
		}

		var drag = sprite.Drag;
		if (EditorUIUtility.InputFloat("Drag", ref drag))
		{
			sprite.Drag = MathF.Max(drag, 0.0f);
			changed = true;
		}

		changed |= DrawEnum("Simulation space", sprite.SimulationSpace, value => sprite.SimulationSpace = value);

		return changed;
	}

	private static bool DrawSizeAndRotation(VfxSpriteSettings sprite)
	{
		var changed = false;
		changed |= DrawRange("Size", sprite.Size, value => sprite.Size = value, 0.0f);
		changed |= DrawCurve("Size over lifetime", sprite.SizeOverLifetime);
		changed |= DrawRange("Rotation", sprite.Rotation, value => sprite.Rotation = value);
		changed |= DrawRange("Spin", sprite.AngularVelocity, value => sprite.AngularVelocity = value);

		return changed;
	}

	private static bool DrawSpriteRendering(VfxSpriteSettings sprite)
	{
		var changed = DrawEnum("Alignment", sprite.Alignment, value => sprite.Alignment = value);
		if (sprite.Alignment == VfxSpriteAlignment.Velocity)
		{
			var stretch = sprite.VelocityStretch;
			if (EditorUIUtility.InputFloat("Velocity stretch", ref stretch))
			{
				sprite.VelocityStretch = MathF.Max(stretch, 0.0f);
				changed = true;
			}
		}

		var columns = sprite.SpriteSheetColumns;
		if (EditorUIUtility.InputInt("Sheet columns", ref columns))
		{
			sprite.SpriteSheetColumns = Math.Clamp(columns, 1, 64);
			changed = true;
		}

		var rows = sprite.SpriteSheetRows;
		if (EditorUIUtility.InputInt("Sheet rows", ref rows))
		{
			sprite.SpriteSheetRows = Math.Clamp(rows, 1, 64);
			changed = true;
		}

		if (sprite.SpriteSheetColumns * sprite.SpriteSheetRows > 1)
		{
			var frameRate = sprite.SpriteSheetFrameRate;
			if (EditorUIUtility.InputFloat("Frames per second", ref frameRate))
			{
				sprite.SpriteSheetFrameRate = MathF.Max(frameRate, 0.0f);
				changed = true;
			}

			var randomStart = sprite.SpriteSheetRandomStart;
			if (EditorUIUtility.Checkbox("Random start frame", ref randomStart))
			{
				sprite.SpriteSheetRandomStart = randomStart;
				changed = true;
			}
		}

		return changed;
	}

	private static bool DrawRibbon(VfxRibbonSettings ribbon)
	{
		var changed = false;

		var root = ribbon.RootOffset;
		if (EditorUIUtility.InputVector3("Root offset", ref root))
		{
			ribbon.RootOffset = root;
			changed = true;
		}

		var tip = ribbon.TipOffset;
		if (EditorUIUtility.InputVector3("Tip offset", ref tip))
		{
			ribbon.TipOffset = tip;
			changed = true;
		}

		var lifetime = ribbon.Lifetime;
		if (EditorUIUtility.InputFloat("Trail length (s)", ref lifetime))
		{
			ribbon.Lifetime = MathF.Max(lifetime, 0.01f);
			changed = true;
		}

		var maxSamples = ribbon.MaxSamples;
		if (EditorUIUtility.InputInt("Max samples", ref maxSamples))
		{
			ribbon.MaxSamples = Math.Clamp(maxSamples, 2, 512);
			changed = true;
		}

		var subdivisions = ribbon.Subdivisions;
		if (ImGui.SliderInt("Subdivisions", ref subdivisions, 0, 8))
		{
			ribbon.Subdivisions = subdivisions;
			changed = true;
		}

		changed |= DrawCurve("Width along trail", ribbon.WidthOverLength);

		var pivot = ribbon.TaperPivot;
		if (ImGui.SliderFloat("Taper pivot", ref pivot, 0.0f, 1.0f, pivot < 0.01f ? "root" : pivot > 0.99f ? "tip" : "%.2f"))
		{
			ribbon.TaperPivot = pivot;
			changed = true;
		}

		var maxSegment = ribbon.MaxSegmentLength;
		if (EditorUIUtility.InputFloat("Break above (m)", ref maxSegment))
		{
			ribbon.MaxSegmentLength = MathF.Max(maxSegment, 0.0f);
			changed = true;
		}

		return changed;
	}

	private static bool DrawEnum<T>(string label, T value, Action<T> set) where T : struct, Enum
	{
		var changed = false;
		EditorUIUtility.Combo(label, value.ToString(), () =>
		{
			foreach (var candidate in Enum.GetValues<T>())
			{
				if (ImGui.Selectable(candidate.ToString(), EqualityComparer<T>.Default.Equals(candidate, value)))
				{
					set(candidate);
					changed = true;
				}
			}
		});

		return changed;
	}

	private static bool DrawRange(string label, VfxFloatRange range, Action<VfxFloatRange> set, float minimum = float.MinValue)
	{
		var values = range.ToVector2();
		var changed = EditorUIUtility.DrawLabeledField(label, () => ImGui.DragFloat2("##range", ref values, 0.01f));
		if (changed)
		{
			set(new VfxFloatRange(MathF.Max(values.X, minimum), MathF.Max(values.Y, minimum)));
		}

		return changed;
	}

	/// <summary>A small plot of the curve with its keys listed underneath. Keys are edited as numbers, not dragged.</summary>
	private static bool DrawCurve(string label, VfxCurve curve)
	{
		var changed = false;
		ImGui.PushID(label);
		try
		{
			ImGui.TextUnformatted(label);
			var minimum = curve.Keys.Count > 0 ? MathF.Min(curve.Keys.Min(key => key.Value), 0.0f) : 0.0f;
			var maximum = curve.Keys.Count > 0 ? MathF.Max(curve.Keys.Max(key => key.Value), 1.0f) : 1.0f;
			var width = ImGui.GetContentRegionAvail().X;
			var origin = ImGui.GetCursorScreenPos();
			var drawList = ImGui.GetWindowDrawList();
			drawList.AddRectFilled(origin, origin + new Vector2(width, CurveHeight), ImGui.GetColorU32(ImGuiCol.FrameBg));
			var previous = Vector2.Zero;
			for (var i = 0; i <= 48; i++)
			{
				var t = i / 48.0f;
				var value = (curve.Evaluate(t) - minimum) / MathF.Max(maximum - minimum, 1e-4f);
				var point = origin + new Vector2(t * width, (1.0f - value) * CurveHeight);
				if (i > 0)
				{
					drawList.AddLine(previous, point, ImGui.GetColorU32(ImGuiCol.PlotLines), 2.0f);
				}

				previous = point;
			}

			ImGui.Dummy(new Vector2(width, CurveHeight));

			var removeIndex = -1;
			for (var i = 0; i < curve.Keys.Count; i++)
			{
				ImGui.PushID(i);
				try
				{
					var key = curve.Keys[i];
					var pair = new Vector2(key.Time, key.Value);
					ImGui.SetNextItemWidth(MathF.Max(width - 70.0f, 80.0f));
					if (ImGui.DragFloat2("##key", ref pair, 0.01f))
					{
						curve.Keys[i] = new VfxCurveKey(Math.Clamp(pair.X, 0.0f, 1.0f), pair.Y);
						changed = true;
					}

					ImGui.SameLine();
					ImGui.BeginDisabled(curve.Keys.Count <= 1);
					if (ImGui.SmallButton("Remove"))
					{
						removeIndex = i;
					}

					ImGui.EndDisabled();
				}
				finally
				{
					ImGui.PopID();
				}
			}

			if (removeIndex >= 0)
			{
				curve.Keys.RemoveAt(removeIndex);
				changed = true;
			}

			if (ImGui.SmallButton("Add key"))
			{
				curve.Keys.Add(new VfxCurveKey(0.5f, curve.Evaluate(0.5f)));
				changed = true;
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("Constant"))
			{
				SetKeys(curve, (0.0f, 1.0f), (1.0f, 1.0f));
				changed = true;
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("Grow"))
			{
				SetKeys(curve, (0.0f, 0.2f), (1.0f, 1.0f));
				changed = true;
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("Shrink"))
			{
				SetKeys(curve, (0.0f, 1.0f), (1.0f, 0.0f));
				changed = true;
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("Pop"))
			{
				SetKeys(curve, (0.0f, 0.0f), (0.15f, 1.0f), (1.0f, 0.6f));
				changed = true;
			}

			if (changed)
			{
				curve.Sort();
			}
		}
		finally
		{
			ImGui.PopID();
		}

		return changed;
	}

	private static void SetKeys(VfxCurve curve, params (float Time, float Value)[] keys)
	{
		curve.Keys.Clear();
		foreach (var (time, value) in keys)
		{
			curve.Keys.Add(new VfxCurveKey(time, value));
		}
	}

	/// <summary>The gradient as a bar over a checkerboard, so opacity reads at a glance, with its keys underneath.</summary>
	private static bool DrawGradient(string label, VfxGradient gradient)
	{
		var changed = false;
		ImGui.PushID(label);
		try
		{
			ImGui.TextUnformatted(label);
			var width = ImGui.GetContentRegionAvail().X;
			const float height = 22.0f;
			var origin = ImGui.GetCursorScreenPos();
			var drawList = ImGui.GetWindowDrawList();
			const float checker = 6.0f;
			for (var x = 0; x * checker < width; x++)
			{
				for (var y = 0; y * checker < height; y++)
				{
					var light = (x + y) % 2 == 0;
					var cellMinimum = origin + new Vector2(x * checker, y * checker);
					var cellMaximum = Vector2.Min(cellMinimum + new Vector2(checker), origin + new Vector2(width, height));
					drawList.AddRectFilled(cellMinimum, cellMaximum, light ? 0xFF9A9A9Au : 0xFF666666u);
				}
			}

			const int segments = 32;
			for (var i = 0; i < segments; i++)
			{
				var left = gradient.Evaluate(i / (float)segments);
				var right = gradient.Evaluate((i + 1) / (float)segments);
				var minimum = origin + new Vector2(width * i / segments, 0.0f);
				var maximum = origin + new Vector2(width * (i + 1) / segments, height);
				var leftColor = ImGui.GetColorU32(Vector4.Clamp(left, Vector4.Zero, Vector4.One));
				var rightColor = ImGui.GetColorU32(Vector4.Clamp(right, Vector4.Zero, Vector4.One));
				drawList.AddRectFilledMultiColor(minimum, maximum, leftColor, rightColor, rightColor, leftColor);
			}

			ImGui.Dummy(new Vector2(width, height));

			var removeIndex = -1;
			for (var i = 0; i < gradient.Keys.Count; i++)
			{
				ImGui.PushID(i);
				try
				{
					var key = gradient.Keys[i];
					var time = key.Time;
					ImGui.SetNextItemWidth(70.0f);
					if (ImGui.DragFloat("##time", ref time, 0.005f, 0.0f, 1.0f, "%.2f"))
					{
						gradient.Keys[i] = key with
						{
							Time = Math.Clamp(time, 0.0f, 1.0f)
						};
						changed = true;
					}

					ImGui.SameLine();
					var color = gradient.Keys[i].Color.ToVector4();
					ImGui.SetNextItemWidth(MathF.Max(width - 150.0f, 80.0f));
					if (ImGui.ColorEdit4("##color", ref color, ImGuiColorEditFlags.Float | ImGuiColorEditFlags.HDR))
					{
						gradient.Keys[i] = gradient.Keys[i] with
						{
							Color = color
						};
						changed = true;
					}

					ImGui.SameLine();
					ImGui.BeginDisabled(gradient.Keys.Count <= 1);
					if (ImGui.SmallButton("Remove"))
					{
						removeIndex = i;
					}

					ImGui.EndDisabled();
				}
				finally
				{
					ImGui.PopID();
				}
			}

			if (removeIndex >= 0)
			{
				gradient.Keys.RemoveAt(removeIndex);
				changed = true;
			}

			if (ImGui.SmallButton("Add key"))
			{
				gradient.Keys.Add(new VfxGradientKey(0.5f, gradient.Evaluate(0.5f)));
				changed = true;
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("Fade out"))
			{
				var start = gradient.Evaluate(0.0f);
				gradient.Keys.Clear();
				gradient.Keys.Add(new VfxGradientKey(0.0f, start with
				{
					W = 1.0f
				}));
				gradient.Keys.Add(new VfxGradientKey(1.0f, start with
				{
					W = 0.0f
				}));
				changed = true;
			}

			ImGui.SameLine();
			if (ImGui.SmallButton("Fade in and out"))
			{
				var start = gradient.Evaluate(0.0f);
				gradient.Keys.Clear();
				gradient.Keys.Add(new VfxGradientKey(0.0f, start with
				{
					W = 0.0f
				}));
				gradient.Keys.Add(new VfxGradientKey(0.15f, start with
				{
					W = 1.0f
				}));
				gradient.Keys.Add(new VfxGradientKey(1.0f, start with
				{
					W = 0.0f
				}));
				changed = true;
			}

			if (changed)
			{
				gradient.Sort();
			}
		}
		finally
		{
			ImGui.PopID();
		}

		return changed;
	}
}
