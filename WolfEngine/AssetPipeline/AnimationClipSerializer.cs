using WolfEngine.Animation;

namespace WolfEngine.AssetPipeline;

public static class AnimationClipSerializer
{
	private static ReadOnlySpan<byte> Magic => "WEAN"u8;
	public const int CurrentVersion = 1;

	public static void Write(string path, ImportedAnimationClipAssetFile clip)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		ArgumentNullException.ThrowIfNull(clip);

		AssetBinaryPrimitives.WriteFileAtomically(path, writer =>
		{
			writer.Write(Magic);
			writer.Write(CurrentVersion);
			writer.Write(clip.Name ?? string.Empty);
			writer.Write(clip.Duration);
			writer.Write(clip.FramesPerSecond);
			writer.Write(clip.Loop);
			writer.Write(clip.SourceSkeletonName ?? string.Empty);
			SkeletonSerializer.WriteBoneTransformArray(writer, clip.SourceBindPoseLocal);

			var transformTracks = clip.TransformTracks ?? [];
			writer.Write(transformTracks.Length);
			for (var i = 0; i < transformTracks.Length; i++)
			{
				WriteTransformTrack(writer, transformTracks[i]);
			}

			var propertyTracks = clip.PropertyTracks ?? [];
			writer.Write(propertyTracks.Length);
			for (var i = 0; i < propertyTracks.Length; i++)
			{
				WriteBinding(writer, propertyTracks[i].Binding);
				WriteFloatCurve(writer, propertyTracks[i].Curve);
			}
		});
	}

	public static ImportedAnimationClipAssetFile Read(string path)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		if (File.Exists(path) == false)
		{
			throw new FileNotFoundException($"Imported animation artifact '{path}' was not found.", path);
		}

		using var stream = File.OpenRead(path);
		return Read(stream);
	}

	public static ImportedAnimationClipAssetFile Read(Stream stream)
	{
		ArgumentNullException.ThrowIfNull(stream);
		using var reader = new BinaryReader(stream);
		var magic = reader.ReadBytes(Magic.Length);
		if (magic.AsSpan().SequenceEqual(Magic) == false)
		{
			throw new InvalidOperationException("Imported animation artifact has an invalid header.");
		}

		var version = reader.ReadInt32();
		if (version != CurrentVersion)
		{
			throw new InvalidOperationException(
				$"Unsupported imported animation artifact version {version}. Expected {CurrentVersion}.");
		}

		var clip = new ImportedAnimationClipAssetFile
		{
			Version = version,
			Name = reader.ReadString(),
			Duration = reader.ReadSingle(),
			FramesPerSecond = reader.ReadSingle(),
			Loop = reader.ReadBoolean(),
			SourceSkeletonName = reader.ReadString(),
			SourceBindPoseLocal = SkeletonSerializer.ReadBoneTransformArray(reader)
		};

		var transformTrackCount = reader.ReadInt32();
		var transformTracks = new TransformTrack[transformTrackCount];
		for (var i = 0; i < transformTrackCount; i++)
		{
			transformTracks[i] = ReadTransformTrack(reader);
		}

		clip.TransformTracks = transformTracks;

		var propertyTrackCount = reader.ReadInt32();
		var propertyTracks = new PropertyTrack[propertyTrackCount];
		for (var i = 0; i < propertyTrackCount; i++)
		{
			var binding = ReadBinding(reader);
			propertyTracks[i] = new PropertyTrack(binding, ReadFloatCurve(reader));
		}

		clip.PropertyTracks = propertyTracks;
		return clip;
	}

	private static void WriteTransformTrack(BinaryWriter writer, TransformTrack track)
	{
		WriteBinding(writer, track.Binding);
		WriteVector3Curve(writer, track.Position);
		WriteQuaternionCurve(writer, track.Rotation);
		WriteVector3Curve(writer, track.Scale);
	}

	private static TransformTrack ReadTransformTrack(BinaryReader reader)
	{
		var binding = ReadBinding(reader);
		return new TransformTrack(
			binding,
			ReadVector3Curve(reader),
			ReadQuaternionCurve(reader),
			ReadVector3Curve(reader));
	}

	private static void WriteBinding(BinaryWriter writer, in AnimationBinding binding)
	{
		writer.Write((int)binding.Kind);
		writer.Write(binding.Path ?? string.Empty);
		writer.Write(binding.Property ?? string.Empty);
	}

	private static AnimationBinding ReadBinding(BinaryReader reader) =>
		new((AnimationBindingKind)reader.ReadInt32(), reader.ReadString(), reader.ReadString());

	private static void WriteFloatCurve(BinaryWriter writer, FloatCurve curve)
	{
		curve ??= FloatCurve.Empty;
		writer.Write((int)curve.Interpolation);
		AssetBinaryPrimitives.WriteSingleArray(writer, curve.Times);
		AssetBinaryPrimitives.WriteSingleArray(writer, curve.Values);
		WriteOptional(writer, curve.InTangents, AssetBinaryPrimitives.WriteSingleArray);
		WriteOptional(writer, curve.OutTangents, AssetBinaryPrimitives.WriteSingleArray);
	}

	private static FloatCurve ReadFloatCurve(BinaryReader reader)
	{
		var interpolation = (CurveInterpolation)reader.ReadInt32();
		var times = AssetBinaryPrimitives.ReadSingleArray(reader);
		var values = AssetBinaryPrimitives.ReadSingleArray(reader);
		var inTangents = ReadOptional(reader, AssetBinaryPrimitives.ReadSingleArray);
		var outTangents = ReadOptional(reader, AssetBinaryPrimitives.ReadSingleArray);
		return new FloatCurve(times, values, interpolation, inTangents, outTangents);
	}

	private static void WriteVector3Curve(BinaryWriter writer, Vector3Curve curve)
	{
		curve ??= Vector3Curve.Empty;
		writer.Write((int)curve.Interpolation);
		AssetBinaryPrimitives.WriteSingleArray(writer, curve.Times);
		AssetBinaryPrimitives.WriteVector3Array(writer, curve.Values);
		WriteOptional(writer, curve.InTangents, AssetBinaryPrimitives.WriteVector3Array);
		WriteOptional(writer, curve.OutTangents, AssetBinaryPrimitives.WriteVector3Array);
	}

	private static Vector3Curve ReadVector3Curve(BinaryReader reader)
	{
		var interpolation = (CurveInterpolation)reader.ReadInt32();
		var times = AssetBinaryPrimitives.ReadSingleArray(reader);
		var values = AssetBinaryPrimitives.ReadVector3Array(reader);
		var inTangents = ReadOptional(reader, AssetBinaryPrimitives.ReadVector3Array);
		var outTangents = ReadOptional(reader, AssetBinaryPrimitives.ReadVector3Array);
		return new Vector3Curve(times, values, interpolation, inTangents, outTangents);
	}

	private static void WriteQuaternionCurve(BinaryWriter writer, QuaternionCurve curve)
	{
		curve ??= QuaternionCurve.Empty;
		writer.Write((int)curve.Interpolation);
		AssetBinaryPrimitives.WriteSingleArray(writer, curve.Times);
		AssetBinaryPrimitives.WriteQuaternionArray(writer, curve.Values);
	}

	private static QuaternionCurve ReadQuaternionCurve(BinaryReader reader)
	{
		var interpolation = (CurveInterpolation)reader.ReadInt32();
		var times = AssetBinaryPrimitives.ReadSingleArray(reader);
		var values = AssetBinaryPrimitives.ReadQuaternionArray(reader);
		return new QuaternionCurve(times, values, interpolation);
	}

	private static void WriteOptional<T>(BinaryWriter writer, T[]? values, Action<BinaryWriter, T[]> write)
	{
		writer.Write(values is not null);
		if (values is not null)
		{
			write(writer, values);
		}
	}

	private static T[]? ReadOptional<T>(BinaryReader reader, Func<BinaryReader, T[]> read) =>
		reader.ReadBoolean() ? read(reader) : null;
}
