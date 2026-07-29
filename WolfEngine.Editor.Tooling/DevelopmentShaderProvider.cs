// Source compilation and hot reload are editor/build tooling concerns.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WolfEngine.AssetPipeline;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

public sealed class EngineShaderOptions
{
	public required string EngineContentRoot { get; init; }
}

public sealed class DevelopmentShaderProvider : IShaderProvider
{
	private const string CompilerIdentity = "Slangc.NET/2026.7.0";
	private readonly object _sync = new();
	private readonly EngineShaderCatalog _catalog;
	private readonly string _shaderSourceRoot;
	private readonly Dictionary<ShaderRequest, CompiledShaderArtifact> _artifacts = new();
	private readonly HashSet<ShaderRequest> _observedRequests = new();
	private string? _projectRootPath;
	private ShaderSourceIndex? _sourceIndex;
	private long _revision;

	public DevelopmentShaderProvider(EngineShaderOptions options, EngineShaderCatalog catalog)
	{
		ArgumentNullException.ThrowIfNull(options);
		_catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
		var engineContentRoot = Path.GetFullPath(options.EngineContentRoot);
		_shaderSourceRoot = Path.Combine(engineContentRoot, "Shaders");
		if (Directory.Exists(_shaderSourceRoot) == false)
			throw new DirectoryNotFoundException($"Engine shader source root '{_shaderSourceRoot}' does not exist.");
		_catalog.ValidateSourceTree(_shaderSourceRoot);
	}

	public long Revision { get { lock (_sync) return _revision; } }
	public event Action<long>? RevisionChanged;

	public void SetProjectRoot(string? projectRootPath)
	{
		lock (_sync)
		{
			_projectRootPath = string.IsNullOrWhiteSpace(projectRootPath) ? null : Path.GetFullPath(projectRootPath);
			if (_projectRootPath is not null)
			{
				foreach (var artifact in _artifacts.Values) WriteArtifactFile(artifact);
				WriteManifest();
			}
		}
	}

	public CompiledShaderArtifact GetArtifact(ShaderRequest request)
	{
		lock (_sync)
		{
			_catalog.ValidateRequest(request);
			_observedRequests.Add(request);
			if (_artifacts.TryGetValue(request, out var cached)) return cached;
			var key = BuildContentKey(request, GetProgramFingerprint(request.ProgramId));
			if (TryReadArtifact(request, key, out cached))
			{
				_artifacts[request] = cached;
				return cached;
			}

			var compiled = Compile(request, key);
			_artifacts[request] = compiled;
			WriteArtifactFile(compiled);
			WriteManifest();
			return compiled;
		}
	}

	public ShaderReloadResult Reload(GraphicsBackendKind backendKind)
	{
		List<ShaderReloadFailure> failures = [];
		Dictionary<ShaderRequest, CompiledShaderArtifact> staged = new();
		long revision;
		lock (_sync)
		{
			_sourceIndex = null;
			foreach (var request in _observedRequests.Where(request => request.BackendKind == backendKind).ToArray())
			{
				_artifacts.TryGetValue(request, out var previous);
				var contentKey = BuildContentKey(request, GetProgramFingerprint(request.ProgramId));
				// Nothing this program compiles has changed, so the loaded artifact is still the right one.
				if (previous is not null && previous.ContentKey == contentKey) continue;
				try
				{
					if (TryReadArtifact(request, contentKey, out var compiled) == false)
						compiled = Compile(request, contentKey);
					if (previous is not null && ReflectionCompatible(previous.ReflectionLayout, compiled.ReflectionLayout) == false)
					{
						failures.Add(new ShaderReloadFailure(request, "The reflected shader interface changed; the previous pipeline was retained."));
						continue;
					}
					staged[request] = compiled;
				}
				catch (Exception ex)
				{
					failures.Add(new ShaderReloadFailure(request, ex.Message));
				}
			}

			foreach (var entry in staged)
			{
				_artifacts[entry.Key] = entry.Value;
				WriteArtifactFile(entry.Value);
			}
			if (staged.Count == 0) return new ShaderReloadResult(0, failures);
			WriteManifest();
			revision = ++_revision;
		}

		RevisionChanged?.Invoke(revision);
		return new ShaderReloadResult(staged.Count, failures);
	}

	private CompiledShaderArtifact Compile(ShaderRequest request, string contentKey)
	{
		var descriptor = _catalog.Get(request.ProgramId);
		var sourcePath = Path.Combine(_shaderSourceRoot, descriptor.RelativeSourcePath);
		var compiler = new ShaderCompiler();
		if (request.Kind == ShaderRequestKind.Compute)
		{
			var compiled = compiler.GetComputeShaderWithReflection(sourcePath, request.ComputeEntryPoint!, request.BackendKind, request.GetDefines());
			return new CompiledShaderArtifact(request, contentKey,
				new ShaderBytecodeSet(compute: compiled.Bytecode, computeThreadGroupSize: compiled.ThreadGroupSize),
				compiled.ReflectionLayout, compiled.ThreadGroupSize);
		}

		var graphics = compiler.GetGraphicsShaderWithReflection(sourcePath, request.VertexEntryPoint!, request.PixelEntryPoint!,
			request.BackendKind, request.GetDefines());
		return new CompiledShaderArtifact(request, contentKey, graphics.Bytecode, graphics.ReflectionLayout);
	}

	/// <summary>
	/// Fingerprints the program's own source plus everything it imports, so an edit only invalidates the
	/// programs that actually compile the edited file.
	/// </summary>
	private string GetProgramFingerprint(ShaderProgramId programId)
	{
		_sourceIndex ??= ShaderSourceIndex.Build(_shaderSourceRoot);
		return _sourceIndex.GetFingerprint(_catalog.Get(programId).RelativeSourcePath);
	}

	private static string BuildContentKey(ShaderRequest request, string sourceFingerprint)
	{
		var input = string.Join('|', CompiledShaderArtifact.CurrentFormatVersion, EngineShaderCatalog.Version,
			CompilerIdentity, sourceFingerprint, request.ProgramId.Value, request.Kind, request.BackendKind,
			request.VertexEntryPoint, request.PixelEntryPoint, request.ComputeEntryPoint, request.Defines);
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
	}

	private bool TryReadArtifact(ShaderRequest request, string contentKey, out CompiledShaderArtifact artifact)
	{
		artifact = null!;
		var path = GetArtifactPath(request.BackendKind, contentKey);
		if (path is null || File.Exists(path) == false) return false;
		try
		{
			using var stream = File.OpenRead(path);
			artifact = ShaderArtifactSerializer.Read(stream);
			return artifact.Request == request && artifact.ContentKey == contentKey;
		}
		catch (Exception)
		{
			return false;
		}
	}

	private void WriteArtifactFile(CompiledShaderArtifact artifact)
	{
		var path = GetArtifactPath(artifact.Request.BackendKind, artifact.ContentKey);
		if (path is null) return;
		Directory.CreateDirectory(Path.GetDirectoryName(path)!);
		var tempPath = path + ".tmp";
		using (var stream = File.Create(tempPath)) ShaderArtifactSerializer.Write(stream, artifact);
		File.Move(tempPath, path, true);
	}

	private void WriteManifest()
	{
		if (_projectRootPath is null) return;
		var root = Path.Combine(AssetPipelinePaths.GetLibraryPath(_projectRootPath), "ShaderCache");
		Directory.CreateDirectory(root);
		var entries = _artifacts.Values.OrderBy(value => value.Request.ProgramId.Value, StringComparer.Ordinal)
			.ThenBy(value => value.ContentKey, StringComparer.Ordinal)
			.Select(value => new { ProgramId = value.Request.ProgramId.Value, value.Request.BackendKind, value.Request.Kind,
				value.Request.VertexEntryPoint, value.Request.PixelEntryPoint, value.Request.ComputeEntryPoint,
				value.Request.Defines, value.ContentKey }).ToArray();
		var path = Path.Combine(root, "manifest.json");
		var tempPath = path + ".tmp";
		File.WriteAllText(tempPath, JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true }));
		File.Move(tempPath, path, true);
	}

	private string? GetArtifactPath(GraphicsBackendKind backendKind, string contentKey) => _projectRootPath is null
		? null
		: Path.Combine(AssetPipelinePaths.GetLibraryPath(_projectRootPath), "ShaderCache", backendKind.ToString(), contentKey + ".wolfshader");

	private static bool ReflectionCompatible(ShaderReflectionLayout previous, ShaderReflectionLayout current)
	{
		static string Signature(ShaderReflectionLayout layout) => string.Join('|',
			layout.ConstantBuffersByName.Values.OrderBy(value => value.Name, StringComparer.Ordinal)
				.Select(value => $"b:{value.Name}:{value.RegisterIndex}:{value.SizeInBytes}:" +
				                 string.Join(',', value.Fields.Values.OrderBy(field => field.Path, StringComparer.Ordinal)
					                 .Select(field => $"{field.Path}:{field.Offset}:{field.ByteSize}:{field.ValueKind}")))
				.Concat(layout.ResourcesByName.Values.OrderBy(value => value.Name, StringComparer.Ordinal)
					.Select(value => $"r:{value.Name}:{value.RegisterIndex}")));
		return string.Equals(Signature(previous), Signature(current), StringComparison.Ordinal);
	}
}
