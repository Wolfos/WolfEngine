#nullable enable

using System;
using System.Collections.Generic;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Passes;

public enum GraphicsPassBindingKind
{
	ConstantBuffer,
	StructuredBuffer
}

public enum GraphicsPassBindingVisibility
{
	Vertex,
	Fragment,
	All
}

/// <summary>A reflected, pass-scoped graphics buffer binding.</summary>
public readonly struct GraphicsPassBinding
{
	public GraphicsPassBinding(uint registerIndex, GraphicsPassBindingKind kind, IGfxBuffer resource,
		GraphicsPassBindingVisibility visibility = GraphicsPassBindingVisibility.All, string? debugName = null)
	{
		Resource = resource ?? throw new ArgumentNullException(nameof(resource));
		RegisterIndex = registerIndex;
		Kind = kind;
		Visibility = visibility;
		DebugName = debugName;
	}

	public uint RegisterIndex { get; }
	public GraphicsPassBindingKind Kind { get; }
	public IGfxBuffer Resource { get; }
	public GraphicsPassBindingVisibility Visibility { get; }
	public string? DebugName { get; }
}

/// <summary>
/// Immutable collection of pass-scoped buffers derived from graphics-shader reflection.
/// Per-draw tables are deliberately excluded and encoded with each indirect command.
/// </summary>
public sealed class GraphicsPassBindingSet
{
	private readonly GraphicsPassBinding[] _bindings;

	public GraphicsPassBindingSet(IEnumerable<GraphicsPassBinding> bindings)
	{
		ArgumentNullException.ThrowIfNull(bindings);
		var bySlot = new Dictionary<(GraphicsPassBindingKind Kind, uint Register), GraphicsPassBinding>();
		foreach (var binding in bindings)
		{
			if (bySlot.TryAdd((binding.Kind, binding.RegisterIndex), binding) == false)
			{
				throw new InvalidOperationException($"Duplicate graphics pass binding at {(binding.Kind == GraphicsPassBindingKind.ConstantBuffer ? 'b' : 't')}{binding.RegisterIndex}.");
			}
		}

		_bindings = new GraphicsPassBinding[bySlot.Count];
		bySlot.Values.CopyTo(_bindings, 0);
		Array.Sort(_bindings, static (left, right) =>
		{
			var kind = left.Kind.CompareTo(right.Kind);
			return kind != 0 ? kind : left.RegisterIndex.CompareTo(right.RegisterIndex);
		});
	}

	public ReadOnlySpan<GraphicsPassBinding> Bindings => _bindings;

	public static GraphicsPassBindingSet FromReflection(
		ShaderReflectionLayout reflection,
		IReadOnlyDictionary<string, IGfxBuffer?> runtimeResources,
		IReadOnlySet<string> perDrawResourceNames)
	{
		ArgumentNullException.ThrowIfNull(reflection);
		ArgumentNullException.ThrowIfNull(runtimeResources);
		ArgumentNullException.ThrowIfNull(perDrawResourceNames);
		var bindings = new List<GraphicsPassBinding>();

		foreach (var constantBuffer in reflection.ConstantBuffersByName.Values)
		{
			if (IsBindlessResource(constantBuffer.Name, constantBuffer.RegisterIndex) ||
			    IsIndirectCommandConstant(constantBuffer.Name) ||
			    perDrawResourceNames.Contains(constantBuffer.Name))
				continue;
			bindings.Add(new GraphicsPassBinding(constantBuffer.RegisterIndex, GraphicsPassBindingKind.ConstantBuffer,
				ResolveRequiredResource(runtimeResources, constantBuffer.Name), ToPassVisibility(constantBuffer.Visibility), constantBuffer.Name));
		}

		foreach (var resource in reflection.ResourcesByName.Values)
		{
			if (IsBindlessResource(resource.Name, resource.RegisterIndex) || perDrawResourceNames.Contains(resource.Name))
				continue;
			bindings.Add(new GraphicsPassBinding(resource.RegisterIndex, GraphicsPassBindingKind.StructuredBuffer,
				ResolveRequiredResource(runtimeResources, resource.Name), ToPassVisibility(resource.Visibility), resource.Name));
		}

		return new GraphicsPassBindingSet(bindings);
	}

	private static IGfxBuffer ResolveRequiredResource(IReadOnlyDictionary<string, IGfxBuffer?> runtimeResources, string name)
	{
		if (runtimeResources.TryGetValue(name, out var resource) && resource is not null)
			return resource;
		throw new InvalidOperationException($"Reflected pass resource '{name}' has no supplied graphics-pass binding.");
	}

	/// <summary>
	/// The draw index reaches a shared-draw shader as a root constant written per command by
	/// ExecuteIndirect, so it has no pass-level buffer to resolve and must not be demanded as one.
	/// </summary>
	public const string IndirectCommandConstantBufferName = "DrawIndexParams";

	private static bool IsIndirectCommandConstant(string name) =>
		name.Equals(IndirectCommandConstantBufferName, StringComparison.Ordinal);

	private static bool IsBindlessResource(string name, uint registerIndex) =>
		registerIndex == 27 || name.Equals("BindlessCounts", StringComparison.Ordinal) ||
		name.Contains("Heap", StringComparison.Ordinal) || name.Equals("g_Textures", StringComparison.Ordinal) ||
		name.StartsWith("g_RWTextures", StringComparison.Ordinal) || name.Equals("g_Samplers", StringComparison.Ordinal);

	private static GraphicsPassBindingVisibility ToPassVisibility(ShaderStage visibility) =>
		visibility switch
		{
			ShaderStage.Vertex => GraphicsPassBindingVisibility.Vertex,
			ShaderStage.Pixel => GraphicsPassBindingVisibility.Fragment,
			_ => GraphicsPassBindingVisibility.All
		};
}
