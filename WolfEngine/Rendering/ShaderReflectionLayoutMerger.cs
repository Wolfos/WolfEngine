#nullable enable

using System;
using System.Collections.Generic;

namespace WolfEngine.Rendering;

internal static class ShaderReflectionLayoutMerger
{
	public static ShaderReflectionLayout Merge(params ShaderReflectionLayout[] layouts)
	{
		ArgumentNullException.ThrowIfNull(layouts);
		if (layouts.Length == 0)
		{
			throw new ArgumentException("At least one reflection layout is required to merge.", nameof(layouts));
		}

		var mergedByName = new Dictionary<string, ShaderConstantBufferLayout>(StringComparer.Ordinal);
		var mergedByRegister = new Dictionary<uint, ShaderConstantBufferLayout>();
		var mergedResourcesByName = new Dictionary<string, ShaderResourceBindingLayout>(StringComparer.Ordinal);

		for (var i = 0; i < layouts.Length; i++)
		{
			var layout = layouts[i] ?? throw new InvalidOperationException($"Reflection layout at index {i} was null.");
			foreach (var candidate in layout.ConstantBuffersByName.Values)
			{
				if (mergedByName.TryGetValue(candidate.Name, out var existingByName))
				{
					EnsureCompatible(existingByName, candidate);
					continue;
				}

				if (mergedByRegister.TryGetValue(candidate.RegisterIndex, out var existingByRegister))
				{
					if (string.Equals(existingByRegister.Name, candidate.Name, StringComparison.Ordinal) == false)
					{
						throw new InvalidOperationException(
							$"Reflected constant-buffer register collision at b{candidate.RegisterIndex}: " +
							$"'{existingByRegister.Name}' vs '{candidate.Name}'.");
					}

					EnsureCompatible(existingByRegister, candidate);
					continue;
				}

				mergedByName.Add(candidate.Name, candidate);
				mergedByRegister.Add(candidate.RegisterIndex, candidate);
			}

			foreach (var candidate in layout.ResourcesByName.Values)
			{
				if (mergedResourcesByName.TryGetValue(candidate.Name, out var existing))
				{
					EnsureCompatible(existing, candidate);
					continue;
				}

				mergedResourcesByName.Add(candidate.Name, candidate);
			}
		}

		return new ShaderReflectionLayout(mergedByName.Values, mergedResourcesByName.Values);
	}

	private static void EnsureCompatible(ShaderConstantBufferLayout expected, ShaderConstantBufferLayout actual)
	{
		if (expected.RegisterIndex != actual.RegisterIndex)
		{
			throw new InvalidOperationException(
				$"Reflected constant buffer '{expected.Name}' register mismatch: b{expected.RegisterIndex} vs b{actual.RegisterIndex}.");
		}

		if (expected.SizeInBytes != actual.SizeInBytes)
		{
			throw new InvalidOperationException(
				$"Reflected constant buffer '{expected.Name}' size mismatch: {expected.SizeInBytes} vs {actual.SizeInBytes} bytes.");
		}

		if (expected.Fields.Count != actual.Fields.Count)
		{
			throw new InvalidOperationException(
				$"Reflected constant buffer '{expected.Name}' field count mismatch: {expected.Fields.Count} vs {actual.Fields.Count}.");
		}

		foreach (var (path, expectedField) in expected.Fields)
		{
			if (actual.TryGetField(path, out var actualField) == false)
			{
				throw new InvalidOperationException(
					$"Reflected constant buffer '{expected.Name}' is missing field '{path}' in merged stage layout.");
			}

			if (expectedField.Offset != actualField.Offset ||
			    expectedField.ByteSize != actualField.ByteSize ||
			    expectedField.ValueKind != actualField.ValueKind)
			{
				throw new InvalidOperationException(
					$"Reflected constant buffer '{expected.Name}' field '{path}' mismatch between merged stages.");
			}
		}
	}

	private static void EnsureCompatible(ShaderResourceBindingLayout expected, ShaderResourceBindingLayout actual)
	{
		if (expected.RegisterIndex != actual.RegisterIndex)
		{
			throw new InvalidOperationException(
				$"Reflected shader resource '{expected.Name}' register mismatch: {expected.RegisterIndex} vs {actual.RegisterIndex}.");
		}
	}
}
