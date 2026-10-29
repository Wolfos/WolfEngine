using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using WolfEngine.AssetPipeline;

namespace WolfEngine.Animation;

/// <summary>
/// Source format for clips authored in the editor (<c>*.anim.json</c>). Keys sit on integer frames
/// so the dopesheet, selection and key moves are never ambiguous; seconds only appear once the
/// file is compiled into the same runtime <see cref="AnimationClip"/> that imported clips use,
/// which is what lets sets and graphs reference authored clips unchanged.
/// </summary>
public sealed class AuthoredAnimationClipFile
{
	public const string Extension = ".anim.json";
	public const int CurrentVersion = 1;
	public const int DefaultFramesPerSecond = 30;
	public const int MaxFramesPerSecond = 1000;

	public int Version { get; set; } = CurrentVersion;

	/// <summary>The skeleton whose bones the editor shows. Tracks still bind by bone name at runtime.</summary>
	public AssetRef<Skeleton> Skeleton { get; set; }

	/// <summary>
	/// Editor-only: the model or prefab the clip editor previews on. A plain id rather than an
	/// <see cref="AssetRef{T}"/> so it never becomes an import dependency of the clip.
	/// </summary>
	public Guid PreviewModelId { get; set; }

	public int FramesPerSecond { get; set; } = DefaultFramesPerSecond;

	/// <summary>Clip length in frames. Keys may sit on any frame from 0 up to and including this value.</summary>
	public int LengthFrames { get; set; } = DefaultFramesPerSecond;

	public bool Loop { get; set; } = true;
	public List<AuthoredTransformTrack> TransformTracks { get; set; } = [];

	/// <summary>Kept and round-tripped unchanged; the clip editor does not edit these yet.</summary>
	public List<AuthoredPropertyTrack> PropertyTracks { get; set; } = [];
	public List<AnimationCurve> Curves { get; set; } = [];
	public List<AnimationMarker> Markers { get; set; } = [];

	/// <summary>
	/// Converts a runtime clip, typically an imported one, into an editable clip at the given frame
	/// rate. A track whose keys all sit on frames and that interpolates linearly or in steps is copied
	/// as it is; any other track is resampled to a linear key on every frame, which preserves its
	/// motion at the cost of density. Property tracks are carried over unchanged.
	/// </summary>
	public static AuthoredAnimationClipFile FromClip(AnimationClip clip, int framesPerSecond)
	{
		ArgumentNullException.ThrowIfNull(clip);

		framesPerSecond = Math.Clamp(framesPerSecond, 1, MaxFramesPerSecond);
		var lengthFrames = Math.Max(0, (int)MathF.Ceiling(clip.Duration * framesPerSecond));
		var file = new AuthoredAnimationClipFile
		{
			FramesPerSecond = framesPerSecond,
			LengthFrames = lengthFrames,
			Loop = clip.Loop,
			Curves = clip.Curves.Select(curve => curve.Clone()).ToList(),
			Markers = clip.Markers.Select(marker => marker.Clone()).ToList()
		};

		var seen = new HashSet<AnimationBinding>();
		foreach (var track in clip.TransformTracks)
		{
			// The editable format allows one track per target; a duplicate in an import would never play anyway.
			if (seen.Add(track.Binding) == false)
			{
				continue;
			}

			var authored = new AuthoredTransformTrack
			{
				Kind = track.Binding.Kind,
				Path = track.Binding.Path,
				Property = track.Binding.Property
			};

			var aligned =
				IsFrameAligned(track.Position.Times, track.Position.Interpolation, framesPerSecond, lengthFrames) &&
				IsFrameAligned(track.Rotation.Times, track.Rotation.Interpolation, framesPerSecond, lengthFrames) &&
				IsFrameAligned(track.Scale.Times, track.Scale.Interpolation, framesPerSecond, lengthFrames);
			if (aligned)
			{
				authored.Position = CopyKeys(track.Position.Times, track.Position.Values, track.Position.Interpolation, framesPerSecond);
				authored.Rotation = CopyKeys(track.Rotation.Times, track.Rotation.Values, track.Rotation.Interpolation, framesPerSecond);
				authored.Scale = CopyKeys(track.Scale.Times, track.Scale.Values, track.Scale.Interpolation, framesPerSecond);
			}
			else
			{
				authored.Position = ResampleVector(track.Position, framesPerSecond, lengthFrames);
				authored.Rotation = ResampleRotation(track.Rotation, framesPerSecond, lengthFrames);
				authored.Scale = ResampleVector(track.Scale, framesPerSecond, lengthFrames);
			}

			if (authored.HasKeys())
			{
				file.TransformTracks.Add(authored);
			}
		}

		foreach (var track in clip.PropertyTracks)
		{
			file.PropertyTracks.Add(AuthoredPropertyTrack.FromRuntime(track));
		}

		return file;
	}

	private const float FrameAlignmentTolerance = 1e-3f;

	private static bool IsFrameAligned(float[] times, CurveInterpolation interpolation, int framesPerSecond, int lengthFrames)
	{
		if (times.Length == 0)
		{
			return true;
		}

		if (interpolation == CurveInterpolation.CubicHermite)
		{
			return false;
		}

		foreach (var time in times)
		{
			var exact = time * framesPerSecond;
			var frame = MathF.Round(exact);
			if (MathF.Abs(exact - frame) > FrameAlignmentTolerance || frame < 0 || frame > lengthFrames)
			{
				return false;
			}
		}

		return true;
	}

	private static AuthoredKeyCurve<T> CopyKeys<T>(float[] times, T[] values, CurveInterpolation interpolation, int framesPerSecond)
		where T : struct
	{
		var curve = new AuthoredKeyCurve<T>
		{
			Interpolation = interpolation
		};

		for (var i = 0; i < times.Length; i++)
		{
			curve.Set((int)MathF.Round(times[i] * framesPerSecond), values[i]);
		}

		return curve;
	}

	private static AuthoredKeyCurve<Vector3> ResampleVector(Vector3Curve source, int framesPerSecond, int lengthFrames)
	{
		var curve = new AuthoredKeyCurve<Vector3>();
		if (source.IsEmpty)
		{
			return curve;
		}

		var cursor = 0;
		for (var frame = 0; frame <= lengthFrames; frame++)
		{
			curve.Frames.Add(frame);
			curve.Values.Add(source.Evaluate((float)frame / framesPerSecond, ref cursor, Vector3.Zero));
		}

		return curve;
	}

	private static AuthoredKeyCurve<Quaternion> ResampleRotation(QuaternionCurve source, int framesPerSecond, int lengthFrames)
	{
		var curve = new AuthoredKeyCurve<Quaternion>();
		if (source.IsEmpty)
		{
			return curve;
		}

		var cursor = 0;
		for (var frame = 0; frame <= lengthFrames; frame++)
		{
			curve.Frames.Add(frame);
			curve.Values.Add(source.Evaluate((float)frame / framesPerSecond, ref cursor, Quaternion.Identity));
		}

		return curve;
	}

	public static string GetClipName(string path)
	{
		var fileName = System.IO.Path.GetFileName(path);
		if (fileName.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
		{
			return fileName[..^Extension.Length];
		}

		return System.IO.Path.GetFileNameWithoutExtension(fileName);
	}

	public float GetDurationSeconds() => FrameToSeconds(LengthFrames);

	public float FrameToSeconds(int frame) => (float)frame / FramesPerSecond;

	public AuthoredTransformTrack? FindTransformTrack(AnimationBinding binding)
	{
		for (var i = 0; i < TransformTracks.Count; i++)
		{
			if (TransformTracks[i].GetBinding() == binding)
			{
				return TransformTracks[i];
			}
		}

		return null;
	}

	public AuthoredTransformTrack? FindBoneTrack(string boneName) =>
		FindTransformTrack(AnimationBinding.ForBone(boneName));

	/// <summary>Throws <see cref="InvalidDataException"/> describing the first problem found.</summary>
	public void Validate()
	{
		if (Version < 1 || Version > CurrentVersion)
		{
			throw new InvalidDataException($"Unsupported authored clip version {Version}.");
		}

		if (FramesPerSecond < 1 || FramesPerSecond > MaxFramesPerSecond)
		{
			throw new InvalidDataException($"Frames per second must be between 1 and {MaxFramesPerSecond}, but was {FramesPerSecond}.");
		}

		if (LengthFrames < 0)
		{
			throw new InvalidDataException($"Clip length cannot be negative, but was {LengthFrames} frames.");
		}

		new AnimationClipMetadata { Curves = Curves, Markers = Markers }.Validate(GetDurationSeconds());

		var bindings = new HashSet<AnimationBinding>();
		for (var i = 0; i < TransformTracks.Count; i++)
		{
			var track = TransformTracks[i]
				?? throw new InvalidDataException($"Transform track {i} is null.");
			if (bindings.Add(track.GetBinding()) == false)
			{
				throw new InvalidDataException($"Transform track '{track.Path}' appears more than once.");
			}

			track.Position.Validate($"'{track.Path}' position", LengthFrames);
			track.Rotation.Validate($"'{track.Path}' rotation", LengthFrames);
			track.Scale.Validate($"'{track.Path}' scale", LengthFrames);
		}

		for (var i = 0; i < PropertyTracks.Count; i++)
		{
			var track = PropertyTracks[i]
				?? throw new InvalidDataException($"Property track {i} is null.");
			track.Validate();
		}
	}

	/// <summary>Produces the runtime artifact. Call <see cref="Validate"/> first.</summary>
	public ImportedAnimationClipAssetFile Compile(string name, string sourceSkeletonName, BoneTransform[] sourceBindPoseLocal)
	{
		var transformTracks = new TransformTrack[TransformTracks.Count];
		for (var i = 0; i < TransformTracks.Count; i++)
		{
			var track = TransformTracks[i];
			transformTracks[i] = new TransformTrack(
				track.GetBinding(),
				CompileVector3Curve(track.Position),
				CompileQuaternionCurve(track.Rotation),
				CompileVector3Curve(track.Scale));
		}

		var propertyTracks = new PropertyTrack[PropertyTracks.Count];
		for (var i = 0; i < PropertyTracks.Count; i++)
		{
			propertyTracks[i] = PropertyTracks[i].Compile();
		}

		return new ImportedAnimationClipAssetFile
		{
			Name = name ?? string.Empty,
			Duration = GetDurationSeconds(),
			FramesPerSecond = FramesPerSecond,
			Loop = Loop,
			TransformTracks = transformTracks,
			PropertyTracks = propertyTracks,
			Curves = Curves.Select(curve => curve.Clone()).ToArray(),
			Markers = Markers.Select(marker => marker.Clone()).ToArray(),
			SourceSkeletonName = sourceSkeletonName ?? string.Empty,
			SourceBindPoseLocal = sourceBindPoseLocal ?? []
		};
	}

	public AuthoredAnimationClipFile Clone()
	{
		var clone = new AuthoredAnimationClipFile
		{
			Version = Version,
			Skeleton = Skeleton,
			PreviewModelId = PreviewModelId,
			FramesPerSecond = FramesPerSecond,
			LengthFrames = LengthFrames,
			Loop = Loop,
			Curves = Curves.Select(curve => curve.Clone()).ToList(),
			Markers = Markers.Select(marker => marker.Clone()).ToList(),
			TransformTracks = new List<AuthoredTransformTrack>(TransformTracks.Count),
			PropertyTracks = new List<AuthoredPropertyTrack>(PropertyTracks.Count)
		};

		for (var i = 0; i < TransformTracks.Count; i++)
		{
			clone.TransformTracks.Add(TransformTracks[i].Clone());
		}

		for (var i = 0; i < PropertyTracks.Count; i++)
		{
			clone.PropertyTracks.Add(PropertyTracks[i].Clone());
		}

		return clone;
	}

	private Vector3Curve CompileVector3Curve(AuthoredKeyCurve<Vector3> keys)
	{
		if (keys.Frames.Count == 0)
		{
			return Vector3Curve.Empty;
		}

		return new Vector3Curve(CompileTimes(keys.Frames), keys.Values.ToArray(), keys.Interpolation);
	}

	private QuaternionCurve CompileQuaternionCurve(AuthoredKeyCurve<Quaternion> keys)
	{
		if (keys.Frames.Count == 0)
		{
			return QuaternionCurve.Empty;
		}

		return new QuaternionCurve(CompileTimes(keys.Frames), keys.Values.ToArray(), keys.Interpolation);
	}

	private float[] CompileTimes(List<int> frames)
	{
		var times = new float[frames.Count];
		for (var i = 0; i < frames.Count; i++)
		{
			times[i] = FrameToSeconds(frames[i]);
		}

		return times;
	}
}

/// <summary>
/// One transform track. Bone tracks are what the clip editor shows; tracks that bind to other
/// entities are carried along so an imported clip duplicated into this format loses nothing.
/// </summary>
public sealed class AuthoredTransformTrack
{
	public AnimationBindingKind Kind { get; set; } = AnimationBindingKind.Bone;

	/// <summary>Bone name for bone tracks, otherwise an entity-relative node path.</summary>
	public string Path { get; set; } = string.Empty;

	public string Property { get; set; } = string.Empty;
	public AuthoredKeyCurve<Vector3> Position { get; set; } = new();
	public AuthoredKeyCurve<Quaternion> Rotation { get; set; } = new();
	public AuthoredKeyCurve<Vector3> Scale { get; set; } = new();

	public static AuthoredTransformTrack ForBone(string boneName) =>
		new()
		{
			Kind = AnimationBindingKind.Bone,
			Path = boneName ?? string.Empty
		};

	public AnimationBinding GetBinding() => new(Kind, Path ?? string.Empty, Property ?? string.Empty);

	public bool HasKeys() =>
		Position.Frames.Count > 0 ||
		Rotation.Frames.Count > 0 ||
		Scale.Frames.Count > 0;

	public AuthoredTransformTrack Clone() =>
		new()
		{
			Kind = Kind,
			Path = Path,
			Property = Property,
			Position = Position.Clone(),
			Rotation = Rotation.Clone(),
			Scale = Scale.Clone()
		};
}

/// <summary>Keys on integer frames, sorted ascending and unique.</summary>
public sealed class AuthoredKeyCurve<T> where T : struct
{
	public CurveInterpolation Interpolation { get; set; } = CurveInterpolation.Linear;
	public List<int> Frames { get; set; } = [];
	public List<T> Values { get; set; } = [];

	public int IndexOf(int frame)
	{
		var index = Frames.BinarySearch(frame);
		return index >= 0 ? index : -1;
	}

	public bool TryGetValue(int frame, out T value)
	{
		var index = IndexOf(frame);
		if (index < 0)
		{
			value = default;
			return false;
		}

		value = Values[index];
		return true;
	}

	/// <summary>Inserts a key, or overwrites the key already on that frame.</summary>
	public void Set(int frame, T value)
	{
		var index = Frames.BinarySearch(frame);
		if (index >= 0)
		{
			Values[index] = value;
			return;
		}

		var insertAt = ~index;
		Frames.Insert(insertAt, frame);
		Values.Insert(insertAt, value);
	}

	public bool Remove(int frame)
	{
		var index = IndexOf(frame);
		if (index < 0)
		{
			return false;
		}

		Frames.RemoveAt(index);
		Values.RemoveAt(index);
		return true;
	}

	public void Clear()
	{
		Frames.Clear();
		Values.Clear();
	}

	public AuthoredKeyCurve<T> Clone() =>
		new()
		{
			Interpolation = Interpolation,
			Frames = new List<int>(Frames),
			Values = new List<T>(Values)
		};

	internal void Validate(string label, int lengthFrames)
	{
		if (Frames is null || Values is null)
		{
			throw new InvalidDataException($"Keys for {label} are missing their frame or value list.");
		}

		if (Frames.Count != Values.Count)
		{
			throw new InvalidDataException($"Keys for {label} have {Frames.Count} frames but {Values.Count} values.");
		}

		// Tangent editing does not exist yet, so a Hermite curve here could never be edited correctly.
		if (Interpolation == CurveInterpolation.CubicHermite)
		{
			throw new InvalidDataException($"Keys for {label} use CubicHermite, which authored clips do not support.");
		}

		for (var i = 0; i < Frames.Count; i++)
		{
			if (Frames[i] < 0 || Frames[i] > lengthFrames)
			{
				throw new InvalidDataException($"Key for {label} at frame {Frames[i]} is outside the clip (0..{lengthFrames}).");
			}

			if (i > 0 && Frames[i] <= Frames[i - 1])
			{
				throw new InvalidDataException($"Keys for {label} are not in strictly ascending frame order at frame {Frames[i]}.");
			}
		}
	}
}

/// <summary>
/// A scalar property track in seconds, mirroring <see cref="FloatCurve"/> so imported data
/// survives a round trip exactly, tangents included.
/// </summary>
public sealed class AuthoredPropertyTrack
{
	public string Path { get; set; } = string.Empty;
	public string Property { get; set; } = string.Empty;
	public CurveInterpolation Interpolation { get; set; } = CurveInterpolation.Linear;
	public List<float> Times { get; set; } = [];
	public List<float> Values { get; set; } = [];
	public List<float>? InTangents { get; set; }
	public List<float>? OutTangents { get; set; }

	public static AuthoredPropertyTrack FromRuntime(PropertyTrack track)
	{
		ArgumentNullException.ThrowIfNull(track);

		return new AuthoredPropertyTrack
		{
			Path = track.Binding.Path,
			Property = track.Binding.Property,
			Interpolation = track.Curve.Interpolation,
			Times = [.. track.Curve.Times],
			Values = [.. track.Curve.Values],
			InTangents = track.Curve.InTangents is null ? null : [.. track.Curve.InTangents],
			OutTangents = track.Curve.OutTangents is null ? null : [.. track.Curve.OutTangents]
		};
	}

	public PropertyTrack Compile() =>
		new(
			AnimationBinding.ForProperty(Path, Property),
			new FloatCurve(
				Times.ToArray(),
				Values.ToArray(),
				Interpolation,
				InTangents?.ToArray(),
				OutTangents?.ToArray()));

	public AuthoredPropertyTrack Clone() =>
		new()
		{
			Path = Path,
			Property = Property,
			Interpolation = Interpolation,
			Times = new List<float>(Times),
			Values = new List<float>(Values),
			InTangents = InTangents is null ? null : new List<float>(InTangents),
			OutTangents = OutTangents is null ? null : new List<float>(OutTangents)
		};

	internal void Validate()
	{
		if (Times is null || Values is null || Times.Count != Values.Count)
		{
			throw new InvalidDataException($"Property track '{Path}.{Property}' has mismatched time and value counts.");
		}

		if ((InTangents is not null && InTangents.Count != Times.Count) ||
		    (OutTangents is not null && OutTangents.Count != Times.Count))
		{
			throw new InvalidDataException($"Property track '{Path}.{Property}' has mismatched tangent counts.");
		}
	}
}

/// <summary>
/// Reads and writes <see cref="AuthoredAnimationClipFile"/>. Vectors and quaternions are written as
/// short number arrays so a dense clip stays readable and diffs line by line per key.
/// </summary>
public static class AuthoredAnimationClipJson
{
	public static readonly JsonSerializerOptions Options = new()
	{
		WriteIndented = true,
		Converters =
		{
			new JsonStringEnumConverter(),
			new Vector3ArrayJsonConverter(),
			new QuaternionArrayJsonConverter()
		}
	};

	public static bool IsAuthoredClipPath(string path) =>
		path.EndsWith(AuthoredAnimationClipFile.Extension, StringComparison.OrdinalIgnoreCase);

	/// <summary>Reads and validates a clip.</summary>
	public static AuthoredAnimationClipFile Read(string path)
	{
		var clip = Deserialize(File.ReadAllText(path));
		try
		{
			clip.Validate();
		}
		catch (InvalidDataException exception)
		{
			throw new InvalidDataException($"Authored clip '{path}' is invalid: {exception.Message}", exception);
		}

		return clip;
	}

	public static AuthoredAnimationClipFile Deserialize(string json) =>
		JsonSerializer.Deserialize<AuthoredAnimationClipFile>(json, Options)
		?? throw new InvalidDataException("Authored clip is empty.");

	public static string Serialize(AuthoredAnimationClipFile clip) =>
		JsonSerializer.Serialize(clip, Options);

	/// <summary>Writes through a temporary file so a crash mid-save never leaves a truncated clip.</summary>
	public static void Write(string path, AuthoredAnimationClipFile clip)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(clip);

		Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
		var temporary = path + ".tmp";
		File.WriteAllText(temporary, Serialize(clip));
		File.Move(temporary, path, true);
	}

	private sealed class Vector3ArrayJsonConverter : JsonConverter<Vector3>
	{
		public override Vector3 Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			Span<float> components = stackalloc float[3];
			ReadComponents(ref reader, components);
			return new Vector3(components[0], components[1], components[2]);
		}

		public override void Write(Utf8JsonWriter writer, Vector3 value, JsonSerializerOptions options)
		{
			writer.WriteStartArray();
			writer.WriteNumberValue(value.X);
			writer.WriteNumberValue(value.Y);
			writer.WriteNumberValue(value.Z);
			writer.WriteEndArray();
		}
	}

	private sealed class QuaternionArrayJsonConverter : JsonConverter<Quaternion>
	{
		public override Quaternion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
		{
			Span<float> components = stackalloc float[4];
			ReadComponents(ref reader, components);
			return new Quaternion(components[0], components[1], components[2], components[3]);
		}

		public override void Write(Utf8JsonWriter writer, Quaternion value, JsonSerializerOptions options)
		{
			writer.WriteStartArray();
			writer.WriteNumberValue(value.X);
			writer.WriteNumberValue(value.Y);
			writer.WriteNumberValue(value.Z);
			writer.WriteNumberValue(value.W);
			writer.WriteEndArray();
		}
	}

	private static void ReadComponents(ref Utf8JsonReader reader, scoped Span<float> components)
	{
		if (reader.TokenType != JsonTokenType.StartArray)
		{
			throw new JsonException($"Expected an array of {components.Length} numbers.");
		}

		for (var i = 0; i < components.Length; i++)
		{
			if (reader.Read() == false || reader.TokenType != JsonTokenType.Number)
			{
				throw new JsonException($"Expected an array of {components.Length} numbers.");
			}

			components[i] = reader.GetSingle();
		}

		if (reader.Read() == false || reader.TokenType != JsonTokenType.EndArray)
		{
			throw new JsonException($"Expected an array of {components.Length} numbers.");
		}
	}
}
