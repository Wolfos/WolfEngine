// Slang reflection is editor tooling; runtime consumes serialized layouts only.

using Slangc.NET;

namespace WolfEngine.Rendering;

internal static class ShaderReflectionLayoutBuilder
{
	public static ShaderReflectionLayout Build(SlangReflection reflection)
	{
		ArgumentNullException.ThrowIfNull(reflection);

		var parameters = reflection.Parameters ?? [];
		var constantBuffers = new List<ShaderConstantBufferLayout>(parameters.Length);
		var resources = new List<ShaderResourceBindingLayout>(parameters.Length);
		for (var i = 0; i < parameters.Length; i++)
		{
			var parameter = parameters[i];
			// Graphics stages are reflected independently.  Slang still exposes
			// declarations from the other stage, but marks their bindings unused.
			// Do not promote those declarations into the stage layout: doing so can
			// make a fragment-only b0 overwrite Metal's vertex buffer slot zero.
			if (HasUsedBinding(parameter) == false)
			{
				continue;
			}

			if (IsConstantBufferType(parameter.Type))
			{
				constantBuffers.Add(BuildConstantBuffer(parameter));
				continue;
			}

			if (TryBuildResource(parameter, out var resource))
			{
				resources.Add(resource);
			}
		}

		if (constantBuffers.Count == 0)
		{
			throw new InvalidOperationException("Slang reflection did not expose any constant buffers.");
		}

		return new ShaderReflectionLayout(constantBuffers, resources);
	}

	private static bool HasUsedBinding(SlangParameter parameter)
	{
		var bindings = parameter.Bindings;
		if (bindings is not { Length: > 0 })
		{
			return false;
		}

		for (var i = 0; i < bindings.Length; i++)
		{
			if (bindings[i].Used)
			{
				return true;
			}
		}

		return false;
	}

	private static bool TryBuildResource(SlangParameter parameter, out ShaderResourceBindingLayout layout)
	{
		layout = null!;
		if (string.IsNullOrWhiteSpace(parameter.Name))
		{
			return false;
		}

		var bindings = parameter.Bindings;
		if (bindings is not { Length: > 0 })
		{
			return false;
		}

		var registerIndex = ResolveRegisterIndex(parameter);
		layout = new ShaderResourceBindingLayout(parameter.Name, registerIndex);
		return true;
	}

	private static ShaderConstantBufferLayout BuildConstantBuffer(SlangParameter parameter)
	{
		var bufferName = string.IsNullOrWhiteSpace(parameter.Name)
			? throw new InvalidOperationException("Encountered a reflected constant buffer with no name.")
			: parameter.Name;

		var registerIndex = ResolveRegisterIndex(parameter);
		var (elementType, elementVarLayout, containerSize) = ResolveContainerTypeLayout(parameter.Type, bufferName);
		if (elementVarLayout.Binding is null)
		{
			throw new InvalidOperationException(
				$"Reflected constant container '{bufferName}' has no element binding layout.");
		}

		var fields = new Dictionary<string, ShaderConstantFieldLayout>(StringComparer.Ordinal);
		var rootOffset = checked((int)elementVarLayout.Binding.Offset);
		CollectFields(
			elementType,
			pathPrefix: string.Empty,
			baseOffset: rootOffset,
			fields,
			bufferName);

		var elementSize = checked((int)elementVarLayout.Binding.Size);
		var sizeInBytes = ResolveBufferSize(containerSize, elementSize, fields.Values, bufferName);
		return new ShaderConstantBufferLayout(bufferName, registerIndex, sizeInBytes, fields);
	}

	private static uint ResolveRegisterIndex(SlangParameter parameter)
	{
		var bindings = parameter.Bindings;
		if (bindings is null || bindings.Length == 0)
		{
			throw new InvalidOperationException(
				$"Reflected parameter '{parameter.Name}' does not expose register bindings.");
		}

		var selected = bindings[0];
		var found = false;
		for (var i = 0; i < bindings.Length; i++)
		{
			var binding = bindings[i];
			if (binding.Used && IsConstantBufferBindingCategory(binding.Kind.ToString()))
			{
				selected = binding;
				found = true;
				break;
			}
		}

		if (found == false)
		{
			for (var i = 0; i < bindings.Length; i++)
			{
				if (bindings[i].Used == false)
				{
					continue;
				}

				selected = bindings[i];
				found = true;
				break;
			}
		}

		return checked((uint)selected.Index);
	}

	private static int ResolveBufferSize(
		int containerSize,
		int elementSize,
		IEnumerable<ShaderConstantFieldLayout> fields,
		string bufferName)
	{
		var maxFieldEnd = 0;
		foreach (var field in fields)
		{
			var fallbackByteSize = field.ByteSize > 0 ? field.ByteSize : 1;
			maxFieldEnd = Math.Max(maxFieldEnd, checked(field.Offset + fallbackByteSize));
		}

		var size = Math.Max(containerSize, Math.Max(elementSize, maxFieldEnd));
		if (size <= 0)
		{
			throw new InvalidOperationException(
				$"Failed to resolve byte size for constant buffer '{bufferName}' from Slang reflection.");
		}

		return size;
	}

	private static (SlangType ElementType, SlangVar ElementVarLayout, int ContainerSize) ResolveContainerTypeLayout(
		SlangType parameterType,
		string bufferName)
	{
		var kindName = parameterType.Kind.ToString();
		if (string.Equals(kindName, "ConstantBuffer", StringComparison.Ordinal))
		{
			var constantBufferType = parameterType.ConstantBuffer
				?? throw new InvalidOperationException($"Constant buffer '{bufferName}' has no constant buffer layout.");
			var elementType = constantBufferType.ElementType
				?? throw new InvalidOperationException($"Constant buffer '{bufferName}' has no element type.");
			var elementVarLayout = constantBufferType.ElementVarLayout
				?? throw new InvalidOperationException($"Constant buffer '{bufferName}' has no element variable layout.");
			var containerVarLayout = constantBufferType.ContainerVarLayout
				?? throw new InvalidOperationException($"Constant buffer '{bufferName}' has no container var layout.");

			return (elementType, elementVarLayout, checked((int)containerVarLayout.Size));
		}

		if (string.Equals(kindName, "ParameterBlock", StringComparison.Ordinal))
		{
			var parameterBlockType = parameterType.ParameterBlock
				?? throw new InvalidOperationException($"Parameter block '{bufferName}' has no parameter block layout.");
			var elementType = parameterBlockType.ElementType
				?? throw new InvalidOperationException($"Parameter block '{bufferName}' has no element type.");
			var elementVarLayout = parameterBlockType.ElementVarLayout
				?? throw new InvalidOperationException($"Parameter block '{bufferName}' has no element variable layout.");
			var containerVarLayout = parameterBlockType.ContainerVarLayout
				?? throw new InvalidOperationException($"Parameter block '{bufferName}' has no container var layout.");

			return (elementType, elementVarLayout, checked((int)containerVarLayout.Size));
		}

		throw new InvalidOperationException(
			$"Reflected parameter '{bufferName}' is neither ConstantBuffer nor ParameterBlock (kind={kindName}).");
	}

	private static void CollectFields(
		SlangType type,
		string pathPrefix,
		int baseOffset,
		Dictionary<string, ShaderConstantFieldLayout> fields,
		string bufferName)
	{
		var typeKind = type.Kind.ToString();
		if (string.Equals(typeKind, "Struct", StringComparison.Ordinal))
		{
			var structType = type.Struct
				?? throw new InvalidOperationException($"Struct type in '{bufferName}' has no struct layout.");
			var structFields = structType.Fields ?? [];
			for (var i = 0; i < structFields.Length; i++)
			{
				var field = structFields[i];
				if (string.IsNullOrWhiteSpace(field.Name))
				{
					throw new InvalidOperationException(
						$"Constant buffer '{bufferName}' contains a struct field with no name.");
				}
				if (field.Binding is null)
				{
					throw new InvalidOperationException(
						$"Constant buffer '{bufferName}' field '{field.Name}' has no binding layout.");
				}

				var nextPath = string.IsNullOrEmpty(pathPrefix)
					? field.Name
					: $"{pathPrefix}.{field.Name}";
				var nextOffset = checked(baseOffset + checked((int)field.Binding.Offset));
				CollectFields(field.Type, nextPath, nextOffset, fields, bufferName);
			}

			return;
		}

		if (string.Equals(typeKind, "Array", StringComparison.Ordinal))
		{
			var array = type.Array
				?? throw new InvalidOperationException($"Array type in '{bufferName}' has no array layout.");
			var elementType = array.ElementType
				?? throw new InvalidOperationException($"Array type in '{bufferName}' is missing an element type.");
			var elementCount = checked((int)array.ElementCount);
			if (elementCount < 0)
			{
				throw new InvalidOperationException($"Array in '{bufferName}' has a negative element count.");
			}

			var stride = checked((int)array.UniformStride);
			if (stride <= 0)
			{
				stride = Math.Max(GetFallbackByteSize(elementType), 1);
			}

			for (var i = 0; i < elementCount; i++)
			{
				var elementPath = string.IsNullOrEmpty(pathPrefix)
					? $"[{i}]"
					: $"{pathPrefix}[{i}]";
				var elementOffset = checked(baseOffset + checked(i * stride));
				CollectFields(elementType, elementPath, elementOffset, fields, bufferName);
			}

			return;
		}

		var valueKind = ResolveValueKind(type);
		var byteSize = GetFallbackByteSize(type);
		if (string.IsNullOrWhiteSpace(pathPrefix))
		{
			throw new InvalidOperationException(
				$"Encountered a leaf field with no path while parsing constant buffer '{bufferName}'.");
		}

		if (fields.TryAdd(pathPrefix, new ShaderConstantFieldLayout(pathPrefix, baseOffset, byteSize, valueKind)) == false)
		{
			throw new InvalidOperationException(
				$"Duplicate reflected shader field path '{pathPrefix}' in constant buffer '{bufferName}'.");
		}
	}

	private static ShaderConstantFieldValueKind ResolveValueKind(SlangType type)
	{
		var typeKind = type.Kind.ToString();
		if (string.Equals(typeKind, "Scalar", StringComparison.Ordinal) && type.Scalar is { } scalar)
		{
			var scalarTypeName = scalar.ScalarType.ToString();
			if (IsUIntScalar(scalarTypeName))
			{
				return ShaderConstantFieldValueKind.UInt;
			}

			if (IsIntScalar(scalarTypeName))
			{
				return ShaderConstantFieldValueKind.Int;
			}

			return IsFloatScalar(scalarTypeName)
				? ShaderConstantFieldValueKind.Float
				: ShaderConstantFieldValueKind.Unsupported;
		}

		if (string.Equals(typeKind, "Vector", StringComparison.Ordinal) && type.Vector is { } vector)
		{
			if (vector.ElementType?.Scalar is not { } elementScalar)
			{
				return ShaderConstantFieldValueKind.Unsupported;
			}

			var scalarTypeName = elementScalar.ScalarType.ToString();
			var elementCount = checked((int)vector.ElementCount);
			if (IsFloatScalar(scalarTypeName))
			{
				return elementCount switch
				{
					2 => ShaderConstantFieldValueKind.Vector2,
					3 => ShaderConstantFieldValueKind.Vector3,
					4 => ShaderConstantFieldValueKind.Vector4,
					_ => ShaderConstantFieldValueKind.Unsupported
				};
			}

			return ShaderConstantFieldValueKind.Unsupported;
		}

		if (string.Equals(typeKind, "Matrix", StringComparison.Ordinal) && type.Matrix is { } matrix)
		{
			if (matrix.ElementType?.Scalar is not { } elementScalar)
			{
				return ShaderConstantFieldValueKind.Unsupported;
			}

			var scalarTypeName = elementScalar.ScalarType.ToString();
			var rowCount = checked((int)matrix.RowCount);
			var columnCount = checked((int)matrix.ColumnCount);
			if (rowCount == 4 && columnCount == 4 &&
			    IsFloatScalar(scalarTypeName))
			{
				return ShaderConstantFieldValueKind.Matrix4x4;
			}
		}

		return ShaderConstantFieldValueKind.Unsupported;
	}

	private static int GetFallbackByteSize(SlangType type)
	{
		var kind = type.Kind.ToString();
		if (string.Equals(kind, "Scalar", StringComparison.Ordinal))
		{
			return 4;
		}

		if (string.Equals(kind, "Vector", StringComparison.Ordinal) && type.Vector is { } vector)
		{
			return checked((int)vector.ElementCount * 4);
		}

		if (string.Equals(kind, "Matrix", StringComparison.Ordinal) && type.Matrix is { } matrix)
		{
			return checked((int)(matrix.RowCount * matrix.ColumnCount * 4));
		}

		if (string.Equals(kind, "Array", StringComparison.Ordinal) && type.Array is { } array)
		{
			var elementCount = checked((int)array.ElementCount);
			var stride = checked((int)array.UniformStride);
			var elementType = array.ElementType;
			if (elementCount <= 0)
			{
				return 0;
			}

			if (stride > 0)
			{
				return checked(elementCount * stride);
			}

			return elementType is null
				? 0
				: checked(elementCount * GetFallbackByteSize(elementType));
		}

		return 0;
	}

	private static bool IsConstantBufferType(SlangType type)
	{
		var kindName = type.Kind.ToString();
		return string.Equals(kindName, "ConstantBuffer", StringComparison.Ordinal) ||
		       string.Equals(kindName, "ParameterBlock", StringComparison.Ordinal);
	}

	private static bool IsConstantBufferBindingCategory(string categoryName)
	{
		return categoryName.Contains("ConstantBuffer", StringComparison.OrdinalIgnoreCase) ||
		       categoryName.Contains("Uniform", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsUIntScalar(string scalarTypeName)
	{
		return scalarTypeName.Contains("UInt", StringComparison.OrdinalIgnoreCase);
	}

	private static bool IsIntScalar(string scalarTypeName)
	{
		return scalarTypeName.Contains("Int", StringComparison.OrdinalIgnoreCase) &&
		       IsUIntScalar(scalarTypeName) == false;
	}

	private static bool IsFloatScalar(string scalarTypeName)
	{
		return scalarTypeName.Contains("Float", StringComparison.OrdinalIgnoreCase);
	}
}
