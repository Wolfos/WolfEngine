namespace WolfEngine.Rendering.Shaders;

/// <summary>
/// The cooked form of a compute shader asset: every kernel it declares, compiled for one backend, each stored as a
/// regular shader artifact.
/// </summary>
public static class ComputeShaderArtifactSerializer
{
	public const string ArtifactKind = "RuntimeComputeShader";
	public const string FileName = "kernels.wolfcompute";
	private const int CurrentVersion = 1;
	private static ReadOnlySpan<byte> Magic => "WOLFCSHD"u8;

	public static void Write(string path, IReadOnlyList<CompiledShaderArtifact> kernels)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(path);
		Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
		var tempPath = path + ".tmp";
		using (var stream = File.Create(tempPath))
		{
			Write(stream, kernels);
		}

		File.Move(tempPath, path, overwrite: true);
	}

	public static void Write(Stream destination, IReadOnlyList<CompiledShaderArtifact> kernels)
	{
		ArgumentNullException.ThrowIfNull(destination);
		ArgumentNullException.ThrowIfNull(kernels);
		using (var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true))
		{
			writer.Write(Magic);
			writer.Write(CurrentVersion);
			writer.Write(kernels.Count);
		}

		foreach (var kernel in kernels)
		{
			ShaderArtifactSerializer.Write(destination, kernel);
		}
	}

	public static IReadOnlyList<CompiledShaderArtifact> Read(string path)
	{
		using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
		return Read(stream);
	}

	public static IReadOnlyList<CompiledShaderArtifact> Read(Stream source)
	{
		ArgumentNullException.ThrowIfNull(source);
		int count;
		using (var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true))
		{
			if (reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) == false)
				throw new InvalidDataException("The file is not a WolfEngine compute shader artifact.");
			var version = reader.ReadInt32();
			if (version != CurrentVersion)
				throw new InvalidDataException($"Unsupported compute shader artifact version {version}.");
			count = reader.ReadInt32();
			if (count is <= 0 or > 1024)
				throw new InvalidDataException("Compute shader artifact kernel count is invalid.");
		}

		var kernels = new CompiledShaderArtifact[count];
		for (var i = 0; i < count; i++)
		{
			kernels[i] = ShaderArtifactSerializer.Read(source);
		}

		return kernels;
	}
}
