using System.Diagnostics.CodeAnalysis;
using WolfEngine.Rendering.Abstraction;

namespace WolfEngine.Rendering.Shaders;

/// <summary>
/// Shader variants a pass builds on demand, one per key, such as one per draw lane or shadow cascade. Each variant is
/// whatever the pass derives from a compiled shader: its pipeline, and the bindings it reflects. A shader reload drops
/// every variant, so each is built again from the new shader the next time it is asked for.
/// </summary>
/// <remarks>
/// <see cref="Generation"/> increases whenever a variant is added or the set is dropped by a reload, which is when
/// a draw record that captured a variant's pipeline or bindings becomes stale. A pass's
/// <see cref="ILaneBindingProvider.Generation"/> returns it instead of counting replacements by hand.
/// </remarks>
internal sealed class ShaderVariantSet<TKey, TVariant>
	where TKey : notnull
	where TVariant : class
{
	private readonly ShaderSource _source;
	private readonly Func<TKey, IGfxDevice, TVariant> _build;
	private readonly Dictionary<TKey, TVariant> _variants;
	private long _builtRevision;
	private ulong _generation;

	/// <param name="source">The shaders the variants are built from.</param>
	/// <param name="build">Builds the variant for a key. It runs again for every key after a shader reload.</param>
	/// <param name="comparer">Compares keys, or null for the default comparer.</param>
	public ShaderVariantSet(
		ShaderSource source,
		Func<TKey, IGfxDevice, TVariant> build,
		IEqualityComparer<TKey>? comparer = null)
	{
		_source = source ?? throw new ArgumentNullException(nameof(source));
		_build = build ?? throw new ArgumentNullException(nameof(build));
		_variants = new Dictionary<TKey, TVariant>(comparer);
		_builtRevision = source.AppliedRevision;
	}

	/// <summary>Increases each time a variant is added or a shader reload drops the set.</summary>
	public ulong Generation
	{
		get
		{
			DropIfStale();
			return _generation;
		}
	}

	/// <summary>The number of variants built for the current applied shader revision.</summary>
	public int Count
	{
		get
		{
			DropIfStale();
			return _variants.Count;
		}
	}

	/// <summary>Returns the variant for <paramref name="key"/>, building it first if it was not built for the current shaders.</summary>
	public TVariant Get(TKey key, IGfxDevice device)
	{
		ArgumentNullException.ThrowIfNull(device);
		DropIfStale();

		if (_variants.TryGetValue(key, out var variant))
		{
			return variant;
		}

		variant = _build(key, device) ?? throw new InvalidOperationException($"Shader variant build returned null for '{key}'.");
		_variants[key] = variant;
		_generation++;

		return variant;
	}

	/// <summary>Returns the variant for <paramref name="key"/> only if it was already built for the current shaders.</summary>
	public bool TryGet(TKey key, [NotNullWhen(true)] out TVariant? variant)
	{
		DropIfStale();

		return _variants.TryGetValue(key, out variant);
	}

	private void DropIfStale()
	{
		var appliedRevision = _source.AppliedRevision;
		if (_builtRevision == appliedRevision) return;

		_variants.Clear();
		_builtRevision = appliedRevision;
		_generation++;
	}
}
