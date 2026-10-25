using System.Numerics;
using WolfEngine.Mathematics;
using WolfEngine.Rendering.Abstraction;
using WolfEngine.Rendering.Shaders;

namespace WolfEngine.Rendering.Features;

/// <summary>
/// One kernel dispatch being recorded. Declare every texture the kernel touches with <see cref="Read"/> or
/// <see cref="ReadWrite"/>, set its constants by name, then call <see cref="Dispatch(Int2)"/>.
/// </summary>
/// <remarks>
/// Constants are the fields of the kernel's cbuffers, found by name through the shader's reflection; a field inside
/// a struct is named <c>member.field</c>. A texture is passed as the <c>uint</c> handle field that
/// <see cref="Read"/> or <see cref="ReadWrite"/> names, and opened in the shader with <c>GetTexture</c> or
/// <c>GetRWTexture</c>. The engine fills <c>WolfView</c> itself.
/// </remarks>
public sealed class RenderFeatureComputePass
{
	private const string ViewBufferName = "WolfView";
	private const string BindlessCountsBufferName = "BindlessCounts";

	private readonly RenderFeatureHost _host;
	private readonly List<ShaderPropertyWriter> _writers = [];
	private readonly Dictionary<ShaderConstantBufferLayout, ShaderPropertyWriter> _writerCache = new(ReferenceEqualityComparer.Instance);
	private readonly List<TextureBinding> _textures = [];
	private CompiledShaderArtifact? _kernel;
	private ShaderPropertyWriter? _viewWriter;
	private RenderGraphBuilder? _builder;
	private string _name = string.Empty;
	private Int2 _renderSize;
	private uint _threadsX;
	private uint _threadsY;
	private uint _threadsZ;
	private bool _dispatched;

	internal RenderFeatureComputePass(RenderFeatureHost host)
	{
		_host = host;
		Execute = ExecutePass;
	}

	internal Action<RenderGraphContext> Execute { get; }

	internal CompiledShaderArtifact Kernel => _kernel ?? throw new InvalidOperationException("The pass has no kernel.");

	internal void Begin(string name, CompiledShaderArtifact kernel, RenderGraphBuilder builder, Int2 renderSize)
	{
		_name = name;
		_kernel = kernel;
		_builder = builder;
		_renderSize = renderSize;
		_dispatched = false;
		_textures.Clear();
		_writers.Clear();
		_viewWriter = null;
		// Each reimport of a shader brings new layouts; keep the cache from growing with every edit.
		if (_writerCache.Count > 64)
		{
			_writerCache.Clear();
		}

		foreach (var buffer in kernel.ReflectionLayout.ConstantBuffersByName.Values)
		{
			if (buffer.Name == BindlessCountsBufferName)
			{
				continue;
			}

			if (_writerCache.TryGetValue(buffer, out var writer) == false)
			{
				_writerCache[buffer] = writer = new ShaderPropertyWriter(buffer);
			}

			writer.Clear();
			if (buffer.Name == ViewBufferName)
			{
				_viewWriter = writer;
			}
			else
			{
				_writers.Add(writer);
			}
		}
	}

	/// <summary>Declares that the kernel samples or loads <paramref name="texture"/> through the handle field <paramref name="handleField"/>.</summary>
	public RenderFeatureComputePass Read(string handleField, RenderFeatureTexture texture) =>
		Bind(handleField, texture, writable: false);

	/// <summary>Declares that the kernel reads and writes <paramref name="texture"/> through the handle field <paramref name="handleField"/>.</summary>
	public RenderFeatureComputePass ReadWrite(string handleField, RenderFeatureTexture texture)
	{
		ArgumentNullException.ThrowIfNull(texture);
		if (texture.IsDepth)
		{
			throw new InvalidOperationException($"Pass '{_name}' cannot write scene depth.");
		}

		if (texture.Owned is { } owned && (owned.Descriptor.Usage & TextureUsage.UnorderedAccess) == 0)
		{
			throw new InvalidOperationException(
				$"Pass '{_name}' cannot write a texture created without writable: true.");
		}

		return Bind(handleField, texture, writable: true);
	}

	/// <summary>Passes a sampler through the handle field <paramref name="handleField"/>; open it with <c>GetSampler</c>.</summary>
	public RenderFeatureComputePass Set(string handleField, in SamplerDescriptor sampler)
	{
		var (writer, field) = FindField(handleField);
		writer.SetUInt(field, _host.GetInitializedBindless().GetSamplerHandle(sampler).Value);
		return this;
	}

	public RenderFeatureComputePass Set(string field, uint value)
	{
		var (writer, layout) = FindField(field);
		writer.SetUInt(layout, value);
		return this;
	}

	public RenderFeatureComputePass Set(string field, int value)
	{
		var (writer, layout) = FindField(field);
		writer.SetInt(layout, value);
		return this;
	}

	public RenderFeatureComputePass Set(string field, float value)
	{
		var (writer, layout) = FindField(field);
		writer.SetFloat(layout, value);
		return this;
	}

	public RenderFeatureComputePass Set(string field, Vector2 value)
	{
		FindField(field).Writer.SetVector2(field, value);
		return this;
	}

	public RenderFeatureComputePass Set(string field, Vector3 value)
	{
		FindField(field).Writer.SetVector3(field, value);
		return this;
	}

	public RenderFeatureComputePass Set(string field, Vector4 value)
	{
		FindField(field).Writer.SetVector4(field, value);
		return this;
	}

	public RenderFeatureComputePass Set(string field, Matrix4x4 value)
	{
		FindField(field).Writer.SetMatrix4x4(field, value);
		return this;
	}

	/// <summary>Runs one thread per pixel of <paramref name="threads"/>, usually <see cref="RenderFeatureContext.RenderSize"/>.</summary>
	public void Dispatch(Int2 threads) => Dispatch(threads.X, threads.Y);

	/// <summary>Runs this many threads, rounded up to whole thread groups; the kernel must bounds-check.</summary>
	public void Dispatch(int threadsX, int threadsY = 1, int threadsZ = 1)
	{
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadsX);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadsY);
		ArgumentOutOfRangeException.ThrowIfNegativeOrZero(threadsZ);
		if (_dispatched)
		{
			throw new InvalidOperationException($"Pass '{_name}' was already dispatched.");
		}

		_threadsX = (uint)threadsX;
		_threadsY = (uint)threadsY;
		_threadsZ = (uint)threadsZ;
		_dispatched = true;
		_builder!.SetExecute(Execute);
	}

	internal void ThrowIfNotDispatched()
	{
		if (_dispatched == false)
		{
			throw new InvalidOperationException($"Pass '{_name}' was added but never dispatched.");
		}
	}

	private RenderFeatureComputePass Bind(string handleField, RenderFeatureTexture texture, bool writable)
	{
		ArgumentNullException.ThrowIfNull(texture);
		var (writer, field) = FindField(handleField);
		var handle = _host.GetHandle(texture);
		if (writable)
		{
			_builder!.WriteTexture(handle, ResourceState.UnorderedAccess);
		}
		else
		{
			_builder!.ReadTexture(handle, ResourceState.ShaderResource);
		}

		_textures.Add(new TextureBinding(writer, field, handle, texture.IsDepth, writable));
		return this;
	}

	private (ShaderPropertyWriter Writer, ShaderConstantFieldLayout Field) FindField(string name)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(name);
		if (_dispatched)
		{
			throw new InvalidOperationException($"Pass '{_name}' was already dispatched.");
		}

		foreach (var writer in _writers)
		{
			if (writer.Layout.Fields.TryGetValue(name, out var field))
			{
				return (writer, field);
			}
		}

		throw new KeyNotFoundException(
			$"Kernel '{Kernel.Request.ComputeEntryPoint}' has no constant '{name}'. Pass '{_name}' can set: " +
			string.Join(", ", _writers.SelectMany(writer => writer.Layout.Fields.Keys)) + ".");
	}

	private void ExecutePass(RenderGraphContext context)
	{
		var kernel = Kernel;
		var commandList = context.CommandList;
		var bindless = _host.GetInitializedBindless();
		foreach (var binding in _textures)
		{
			var texture = context.GetTexture(binding.Handle);
			var descriptor = binding.Writable
				? bindless.RegisterRwTexture(texture)
				: binding.IsDepth
					? bindless.RegisterDepthTexture(texture)
					: bindless.RegisterTexture(texture);
			binding.Writer.SetUInt(binding.Field, descriptor.Value);
		}

		commandList.BindPipeline(_host.GetPipeline(kernel));
		foreach (var writer in _writers)
		{
			commandList.SetComputeConstants(writer.RegisterIndex, writer.AsBytes());
		}

		if (_viewWriter is { } view)
		{
			var scene = context.SceneData;
			view.SetMatrix4x4("wolfView.invViewProjection", scene.InverseViewProjection);
			view.SetVector3("wolfView.cameraOrigin", scene.CameraOrigin);
			view.SetUInt("wolfView.renderSizeX", (uint)_renderSize.X);
			view.SetUInt("wolfView.renderSizeY", (uint)_renderSize.Y);
			commandList.SetComputeConstants(view.RegisterIndex, view.AsBytes());
		}

		var (groupsX, groupsY, groupsZ) = kernel.ThreadGroupSize!.Value.GetDispatchGroupCount(_threadsX, _threadsY, _threadsZ);
		commandList.Dispatch(groupsX, groupsY, groupsZ);
	}

	private readonly record struct TextureBinding(
		ShaderPropertyWriter Writer,
		ShaderConstantFieldLayout Field,
		RenderGraphResourceHandle Handle,
		bool IsDepth,
		bool Writable);
}
