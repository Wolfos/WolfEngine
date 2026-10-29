namespace WolfEngine.Animation;

/// <summary>
/// Timeline data authored alongside motion. Imported takes store this in their source metadata;
/// authored clips store it in the clip source. Both compile it into the runtime clip.
/// </summary>
public sealed class AnimationClipMetadata
{
	public List<AnimationCurve> Curves { get; set; } = [];
	public List<AnimationMarker> Markers { get; set; } = [];

	public AnimationClipMetadata Clone() => new()
	{
		Curves = Curves.Select(curve => curve.Clone()).ToList(),
		Markers = Markers.Select(marker => marker.Clone()).ToList()
	};

	public void Validate(float duration)
	{
		if (Curves is null || Markers is null)
		{
			throw new InvalidOperationException("Animation curves and markers cannot be null.");
		}

		var names = new HashSet<string>(StringComparer.Ordinal);
		foreach (var curve in Curves)
		{
			if (curve is null ||
				string.IsNullOrWhiteSpace(curve.Name) ||
				curve.Times is null ||
				curve.Values is null ||
				curve.Times.Length != curve.Values.Length ||
				Enum.IsDefined(curve.Interpolation) == false)
			{
				throw new InvalidOperationException("Malformed named animation curve.");
			}

			if (names.Add(curve.Name) == false)
			{
				throw new InvalidOperationException($"Duplicate animation curve '{curve.Name}'.");
			}

			for (var i = 0; i < curve.Times.Length; i++)
			{
				if (float.IsFinite(curve.Times[i]) == false ||
					float.IsFinite(curve.Values[i]) == false ||
					curve.Times[i] < 0 ||
					curve.Times[i] > duration ||
					(i > 0 && curve.Times[i] <= curve.Times[i - 1]))
				{
					throw new InvalidOperationException($"Invalid keys for animation curve '{curve.Name}'.");
				}
			}

			if (curve.Interpolation == CurveInterpolation.CubicHermite &&
				(curve.InTangents?.Length != curve.Times.Length ||
				curve.OutTangents?.Length != curve.Times.Length ||
				curve.InTangents.Any(value => float.IsFinite(value) == false) ||
				curve.OutTangents.Any(value => float.IsFinite(value) == false)))
			{
				throw new InvalidOperationException($"Animation curve '{curve.Name}' requires finite tangents at every key.");
			}
		}

		foreach (var marker in Markers)
		{
			if (marker is null ||
				string.IsNullOrWhiteSpace(marker.Name) ||
				float.IsFinite(marker.Time) == false ||
				marker.Time < 0 ||
				marker.Time > duration)
			{
				throw new InvalidOperationException("Invalid animation marker.");
			}
		}
	}
}
