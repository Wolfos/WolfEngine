using WolfEngine.ECS;

namespace WolfEngine.Serialization;

public delegate void BinaryWriteFunc<T>(BinaryAssetWriter writer, in T value);

public delegate T BinaryReadFunc<T>(ref BinaryAssetReader reader);

/// <summary>
/// A type whose values can be cooked to the binary format, described by generated code. Types are found by
/// <see cref="Key"/> when loading, and <see cref="LayoutHash"/> proves the data was cooked from the same layout.
/// </summary>
public abstract class SerializedType
{
	protected SerializedType(string key, ulong layoutHash)
	{
		Key = key;
		LayoutHash = layoutHash;
	}

	/// <summary>The type's assembly and full name, which is what cooked data refers to it by.</summary>
	public string Key { get; }

	/// <summary>A hash of the serialized members and their types, recursively.</summary>
	public ulong LayoutHash { get; }

	public abstract Type Type { get; }

	/// <summary>Writes a value of <see cref="Type"/>.</summary>
	public abstract void WriteBoxed(BinaryAssetWriter writer, object value);

	/// <summary>Reads a value of <see cref="Type"/>.</summary>
	public abstract object ReadBoxed(ref BinaryAssetReader reader);

	/// <summary>Throws when data cooked with <paramref name="cookedLayoutHash"/> does not match this build's layout.</summary>
	public void EnsureLayout(ulong cookedLayoutHash)
	{
		if (cookedLayoutHash == LayoutHash)
		{
			return;
		}

		throw new InvalidDataException(
			$"Cooked data for '{Key}' was written with a different member layout " +
			$"({cookedLayoutHash:X16}, this build has {LayoutHash:X16}). Rebuild the game content.");
	}
}

/// <summary>An entity component that can be read straight into a world without reflection.</summary>
public abstract class SerializedComponentType : SerializedType
{
	protected SerializedComponentType(string key, ulong layoutHash)
		: base(key, layoutHash)
	{
	}

	/// <summary>Reads one value and adds it to <paramref name="entity"/>.</summary>
	public abstract void ReadAndAdd(ref BinaryAssetReader reader, World world, Entity entity);
}

public sealed class SerializedComponentType<T> : SerializedComponentType
	where T : struct, IEntityComponent
{
	private readonly BinaryWriteFunc<T> _write;
	private readonly BinaryReadFunc<T> _read;

	public SerializedComponentType(string key, ulong layoutHash, BinaryWriteFunc<T> write, BinaryReadFunc<T> read)
		: base(key, layoutHash)
	{
		_write = write;
		_read = read;
	}

	public override Type Type => typeof(T);

	public void Write(BinaryAssetWriter writer, in T value)
	{
		_write(writer, in value);
	}

	public T Read(ref BinaryAssetReader reader)
	{
		return _read(ref reader);
	}

	public override void WriteBoxed(BinaryAssetWriter writer, object value)
	{
		var typed = (T)value;
		_write(writer, in typed);
	}

	public override object ReadBoxed(ref BinaryAssetReader reader)
	{
		return _read(ref reader);
	}

	public override void ReadAndAdd(ref BinaryAssetReader reader, World world, Entity entity)
	{
		var value = _read(ref reader);
		world.AddComponent(entity, in value);
	}
}

/// <summary>A data asset class. Data assets are reference types and are cooked one per asset.</summary>
public sealed class SerializedDataAssetType<T> : SerializedType
	where T : class
{
	private readonly BinaryWriteFunc<T> _write;
	private readonly BinaryReadFunc<T> _read;

	public SerializedDataAssetType(string key, ulong layoutHash, BinaryWriteFunc<T> write, BinaryReadFunc<T> read)
		: base(key, layoutHash)
	{
		_write = write;
		_read = read;
	}

	public override Type Type => typeof(T);

	public override void WriteBoxed(BinaryAssetWriter writer, object value)
	{
		var typed = (T)value;
		_write(writer, in typed);
	}

	public override object ReadBoxed(ref BinaryAssetReader reader)
	{
		return _read(ref reader);
	}
}
