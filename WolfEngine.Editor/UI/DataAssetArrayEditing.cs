namespace WolfEngine.Editor.UI;

internal static class DataAssetArrayEditing
{
	public static bool CanCreateElement(Type elementType)
	{
		return elementType == typeof(string) ||
			elementType.IsSZArray ||
			elementType.IsValueType ||
			(elementType.IsAbstract == false &&
			 elementType.IsInterface == false &&
			 elementType.GetConstructor(Type.EmptyTypes) is not null);
	}

	public static object? CreateElement(Type elementType)
	{
		if (elementType == typeof(string))
		{
			return string.Empty;
		}

		if (elementType.IsSZArray)
		{
			return Array.CreateInstance(elementType.GetElementType()!, 0);
		}

		return Activator.CreateInstance(elementType);
	}

	public static Array AddElement(Array? source, Type elementType)
	{
		var count = source?.Length ?? 0;
		var result = Array.CreateInstance(elementType, count + 1);
		if (source is not null)
		{
			Array.Copy(source, result, count);
		}

		result.SetValue(CreateElement(elementType), count);

		return result;
	}

	public static Array RemoveElement(Array source, int index)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(index);
		ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, source.Length);

		var result = Array.CreateInstance(source.GetType().GetElementType()!, source.Length - 1);
		Array.Copy(source, 0, result, 0, index);
		Array.Copy(source, index + 1, result, index, source.Length - index - 1);

		return result;
	}
}
