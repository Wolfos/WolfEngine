#nullable enable

using System;
using System.Collections.Generic;

namespace WolfEngine.Rendering.Abstraction;

/// <summary>
/// Selects the execution queue that a render graph pass should target.
/// </summary>
public enum PassKind
{
	Graphics,
	Compute
}

/// <summary>
/// Identifies shader stages when declaring resource access or pipeline metadata.
/// </summary>
[Flags]
public enum ShaderStage
{
	None = 0,
	Vertex = 1 << 0,
	Pixel = 1 << 1,
	Compute = 1 << 2,
	AllGraphics = Vertex | Pixel,
	All = Vertex | Pixel | Compute
}

/// <summary>
/// Generic viewport definition used when beginning a graphics pass.
/// </summary>
public readonly struct Viewport
{
	public Viewport(float x, float y, float width, float height, float minDepth = 0.0f, float maxDepth = 1.0f)
	{
		X = x;
		Y = y;
		Width = width;
		Height = height;
		MinDepth = minDepth;
		MaxDepth = maxDepth;
	}

	public float X { get; }

	public float Y { get; }

	public float Width { get; }

	public float Height { get; }

	public float MinDepth { get; }

	public float MaxDepth { get; }
}

/// <summary>
/// Integer rectangle used for scissor definitions.
/// </summary>
public readonly struct RectInt
{
	public RectInt(int x, int y, int width, int height)
	{
		X = x;
		Y = y;
		Width = width;
		Height = height;
	}

	public int X { get; }

	public int Y { get; }

	public int Width { get; }

	public int Height { get; }
}

/// <summary>
/// Associates a texture with a specific render target slot.
/// </summary>
public readonly struct ColorTargetBinding
{
	public ColorTargetBinding(IGfxTexture texture, uint mipLevel = 0, uint arraySlice = 0)
	{
		Texture = texture ?? throw new ArgumentNullException(nameof(texture));
		MipLevel = mipLevel;
		ArraySlice = arraySlice;
	}

	public IGfxTexture Texture { get; }

	public uint MipLevel { get; }

	public uint ArraySlice { get; }
}

/// <summary>
/// Binds a depth-stencil texture to the current pass.
/// </summary>
public readonly struct DepthTargetBinding
{
	public DepthTargetBinding(IGfxTexture texture, bool readOnlyDepth = false, bool readOnlyStencil = false)
	{
		Texture = texture ?? throw new ArgumentNullException(nameof(texture));
		ReadOnlyDepth = readOnlyDepth;
		ReadOnlyStencil = readOnlyStencil;
	}

	public IGfxTexture Texture { get; }

	/// <remarks>
	/// Not implemented by any backend. D3D12 always builds a writable DSV (<c>DsvFlags.None</c>) and Metal
	/// has no read-only depth attachment at all, so a pass that needs to sample the depth buffer it is
	/// rendering with must read it as a texture and leave the depth attachment unbound.
	/// </remarks>
	public bool ReadOnlyDepth { get; }

	/// <inheritdoc cref="ReadOnlyDepth"/>
	public bool ReadOnlyStencil { get; }
}

/// <summary>
/// Collects the framebuffer attachments for a graphics pass.
/// </summary>
public readonly struct PassTargets
{
	public PassTargets(IReadOnlyList<ColorTargetBinding> colorAttachments, DepthTargetBinding? depthAttachment = null)
	{
		ColorAttachments = colorAttachments ?? throw new ArgumentNullException(nameof(colorAttachments));
		DepthAttachment = depthAttachment;
	}

	public IReadOnlyList<ColorTargetBinding> ColorAttachments { get; }

	public DepthTargetBinding? DepthAttachment { get; }
}

/// <summary>
/// Describes a vertex buffer slot binding for draw calls.
/// </summary>
public readonly struct VertexBufferView
{
	public VertexBufferView(IGfxBuffer buffer, uint stride, uint offset = 0)
	{
		Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
		Stride = stride;
		Offset = offset;
	}

	public IGfxBuffer Buffer { get; }

	public uint Stride { get; }

	public uint Offset { get; }
}

/// <summary>
/// Describes the index buffer bound to the input assembler.
/// </summary>
public readonly struct IndexBufferView
{
	public IndexBufferView(IGfxBuffer buffer, IndexFormat format, uint offset = 0)
	{
		Buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
		Format = format;
		Offset = offset;
	}

	public IGfxBuffer Buffer { get; }

	public IndexFormat Format { get; }

	public uint Offset { get; }
}

/// <summary>
/// Identifies the numeric representation of indices in an index buffer.
/// </summary>
public enum IndexFormat
{
	UInt16,
	UInt32
}

/// <summary>
/// Standard draw arguments for indexed draws.
/// </summary>
public readonly struct DrawArguments
{
	public DrawArguments(uint indexCount, uint instanceCount = 1, uint startIndex = 0, int baseVertex = 0, uint startInstance = 0)
	{
		IndexCount = indexCount;
		InstanceCount = instanceCount;
		StartIndex = startIndex;
		BaseVertex = baseVertex;
		StartInstance = startInstance;
	}

	public uint IndexCount { get; }

	public uint InstanceCount { get; }

	public uint StartIndex { get; }

	public int BaseVertex { get; }

	public uint StartInstance { get; }
}

/// <summary>
/// Describes a backend-agnostic indirect command buffer allocation.
/// </summary>
public readonly struct IndirectCommandBufferDescriptor
{
	public IndirectCommandBufferDescriptor(PassKind passKind, uint maxCommandCount, bool supportsIndexedExecution = false)
	{
		if (maxCommandCount == 0)
		{
			throw new ArgumentOutOfRangeException(nameof(maxCommandCount), "Command count must be greater than zero.");
		}

		PassKind = passKind;
		MaxCommandCount = maxCommandCount;
		SupportsIndexedExecution = supportsIndexedExecution;
	}

	public PassKind PassKind { get; }

	public uint MaxCommandCount { get; }

	public bool SupportsIndexedExecution { get; }
}

/// <summary>
/// High-level resource state transitions requested by the render graph.
/// </summary>
public readonly struct ResourceBarrierDescription
{
	public ResourceBarrierDescription(IGfxResource resource, ResourceState before, ResourceState after, ShaderStage stages = ShaderStage.All)
	{
		Resource = resource ?? throw new ArgumentNullException(nameof(resource));
		Before = before;
		After = after;
		Stages = stages;
	}

	public IGfxResource Resource { get; }

	public ResourceState Before { get; }

	public ResourceState After { get; }

	public ShaderStage Stages { get; }
}

/// <summary>
/// Generic resource state enumeration used for barriers.
/// </summary>
[Flags]
public enum ResourceState
{
	None = 0,
	Common = 1 << 0,
	RenderTarget = 1 << 1,
	DepthWrite = 1 << 2,
	ShaderResource = 1 << 3,
	UnorderedAccess = 1 << 4,
	CopySource = 1 << 5,
	CopyDestination = 1 << 6,
	IndirectArgument = 1 << 7,
	Present = 1 << 8
}

/// <summary>
/// Describes a sampler that can be allocated in the global descriptor table.
/// </summary>
public readonly struct SamplerDescriptor
{
	public SamplerDescriptor(FilterMode filter, AddressMode addressU, AddressMode addressV, AddressMode addressW, float mipLodBias = 0.0f, float maxAnisotropy = 1.0f)
	{
		Filter = filter;
		AddressU = addressU;
		AddressV = addressV;
		AddressW = addressW;
		MipLodBias = mipLodBias;
		MaxAnisotropy = maxAnisotropy;
	}

	public FilterMode Filter { get; }

	public AddressMode AddressU { get; }

	public AddressMode AddressV { get; }

	public AddressMode AddressW { get; }

	public float MipLodBias { get; }

	public float MaxAnisotropy { get; }
}

public enum FilterMode
{
	Point,
	Bilinear,
	Trilinear,
	Anisotropic
}

public enum AddressMode
{
	Clamp,
	Wrap,
	Mirror,
	Border
}

/// <summary>
/// Key used to request or cache pipeline state objects from a backend.
/// </summary>
public readonly struct PipelineKey : IEquatable<PipelineKey>
{
	public PipelineKey(
		PassKind passKind,
		string? vertexEntryPoint,
		string? pixelEntryPoint,
		string? computeEntryPoint,
		RenderTargetFormats renderTargets,
		DepthStencilFormat depthStencil,
		RenderStateDescriptor renderState,
		GraphicsLayoutKind layout = GraphicsLayoutKind.Default,
		string? shaderVariant = null)
	{
		PassKind = passKind;
		VertexEntryPoint = vertexEntryPoint;
		PixelEntryPoint = pixelEntryPoint;
		ComputeEntryPoint = computeEntryPoint;
		RenderTargets = renderTargets;
		DepthStencil = depthStencil;
		RenderState = renderState;
		Layout = layout;
		ShaderVariant = shaderVariant;
	}

	public PassKind PassKind { get; }

	public string? VertexEntryPoint { get; }

	public string? PixelEntryPoint { get; }

	public string? ComputeEntryPoint { get; }

	public RenderTargetFormats RenderTargets { get; }

	public DepthStencilFormat DepthStencil { get; }

	public RenderStateDescriptor RenderState { get; }

	public GraphicsLayoutKind Layout { get; }

	public string? ShaderVariant { get; }

	public bool Equals(PipelineKey other)
	{
		return PassKind == other.PassKind
		       && VertexEntryPoint == other.VertexEntryPoint
		       && PixelEntryPoint == other.PixelEntryPoint
		       && ComputeEntryPoint == other.ComputeEntryPoint
		       && RenderTargets.Equals(other.RenderTargets)
		       && DepthStencil.Equals(other.DepthStencil)
		       && Layout == other.Layout
		       && ShaderVariant == other.ShaderVariant
		       && RenderState.Equals(other.RenderState);
	}

	public override bool Equals(object? obj) => obj is PipelineKey other && Equals(other);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		hash.Add(PassKind);
		hash.Add(VertexEntryPoint);
		hash.Add(PixelEntryPoint);
		hash.Add(ComputeEntryPoint);
		hash.Add(RenderTargets);
		hash.Add(DepthStencil);
		hash.Add(RenderState);
		hash.Add(Layout);
		hash.Add(ShaderVariant);
		return hash.ToHashCode();
	}
}

/// <summary>
/// Describes the color target formats a pipeline renders into.
/// </summary>
public readonly struct RenderTargetFormats : IEquatable<RenderTargetFormats>
{
	public RenderTargetFormats(ReadOnlyMemory<TextureFormat> formats)
	{
		Formats = formats;
	}

	public ReadOnlyMemory<TextureFormat> Formats { get; }

	public bool Equals(RenderTargetFormats other)
	{
		return Formats.Span.SequenceEqual(other.Formats.Span);
	}

	public override bool Equals(object? obj) => obj is RenderTargetFormats other && Equals(other);

	public override int GetHashCode()
	{
		var hash = new HashCode();
		foreach (var format in Formats.Span)
		{
			hash.Add(format);
		}

		return hash.ToHashCode();
	}
}

/// <summary>
/// Captures depth-stencil format requirements for a pipeline.
/// </summary>
public readonly struct DepthStencilFormat : IEquatable<DepthStencilFormat>
{
	public DepthStencilFormat(TextureFormat format, bool readOnlyDepth = false, bool readOnlyStencil = false)
	{
		Format = format;
		ReadOnlyDepth = readOnlyDepth;
		ReadOnlyStencil = readOnlyStencil;
	}

	public TextureFormat Format { get; }

	/// <inheritdoc cref="DepthTargetBinding.ReadOnlyDepth"/>
	public bool ReadOnlyDepth { get; }

	/// <inheritdoc cref="DepthTargetBinding.ReadOnlyDepth"/>
	public bool ReadOnlyStencil { get; }

	public bool Equals(DepthStencilFormat other)
	{
		return Format == other.Format
		       && ReadOnlyDepth == other.ReadOnlyDepth
		       && ReadOnlyStencil == other.ReadOnlyStencil;
	}

	public override bool Equals(object? obj) => obj is DepthStencilFormat other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(Format, ReadOnlyDepth, ReadOnlyStencil);
}

/// <summary>
/// Combined render state configuration included in pipeline keys.
/// </summary>
public readonly struct RenderStateDescriptor : IEquatable<RenderStateDescriptor>
{
	public RenderStateDescriptor(
		FillMode fillMode,
		CullMode cullMode,
		bool depthTestEnabled,
		bool depthWriteEnabled,
		BlendMode blendMode)
	{
		FillMode = fillMode;
		CullMode = cullMode;
		DepthTestEnabled = depthTestEnabled;
		DepthWriteEnabled = depthWriteEnabled;
		BlendMode = blendMode;
	}

	public FillMode FillMode { get; }

	public CullMode CullMode { get; }

	public bool DepthTestEnabled { get; }

	public bool DepthWriteEnabled { get; }

	public BlendMode BlendMode { get; }

	public bool Equals(RenderStateDescriptor other)
	{
		return FillMode == other.FillMode
		       && CullMode == other.CullMode
		       && DepthTestEnabled == other.DepthTestEnabled
		       && DepthWriteEnabled == other.DepthWriteEnabled
		       && BlendMode == other.BlendMode;
	}

	public override bool Equals(object? obj) => obj is RenderStateDescriptor other && Equals(other);

	public override int GetHashCode() => HashCode.Combine(FillMode, CullMode, DepthTestEnabled, DepthWriteEnabled, BlendMode);
}

public enum FillMode
{
	Solid,
	Wireframe
}

public enum CullMode
{
	None,
	Front,
	Back
}

public enum BlendMode
{
	Opaque,
	Additive,
	AlphaBlend
}

public enum GraphicsLayoutKind
{
	Default = 0,
	Material = 1,
	Skybox = 2,
	ImGui = 3
}
