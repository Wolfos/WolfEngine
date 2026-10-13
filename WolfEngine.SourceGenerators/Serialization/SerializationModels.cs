using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace WolfEngine.SourceGenerators.Serialization;

/// <summary>How one type is written to and read from the binary format.</summary>
internal abstract class TypeModel
{
	protected TypeModel(string typeName)
	{
		TypeName = typeName;
	}

	/// <summary>The fully qualified C# name generated code uses for the type.</summary>
	public string TypeName { get; }

	/// <summary>Index used to name the type's generated helpers. Only meaningful when <see cref="NeedsHelpers"/>.</summary>
	public int HelperId { get; set; } = -1;

	/// <summary>Whether reading and writing go through generated helper methods rather than an inline call.</summary>
	public abstract bool NeedsHelpers { get; }
}

/// <summary>A value written and read by a single <c>BinaryAssetWriter</c>/<c>BinaryAssetReader</c> method.</summary>
internal sealed class ScalarModel : TypeModel
{
	public ScalarModel(string typeName, string methodSuffix, string schema, string? enumUnderlyingTypeName = null)
		: base(typeName)
	{
		MethodSuffix = methodSuffix;
		Schema = schema;
		EnumUnderlyingTypeName = enumUnderlyingTypeName;
	}

	/// <summary>The part after Write/Read, such as <c>Int32</c> for <c>WriteInt32</c>.</summary>
	public string MethodSuffix { get; }

	public string Schema { get; }

	/// <summary>Set for enums, which are stored as their underlying integer.</summary>
	public string? EnumUnderlyingTypeName { get; }

	public override bool NeedsHelpers => false;
}

internal sealed class NullableModel : TypeModel
{
	public NullableModel(string typeName, TypeModel inner)
		: base(typeName)
	{
		Inner = inner;
	}

	public TypeModel Inner { get; }

	public override bool NeedsHelpers => true;
}

internal enum CollectionKind
{
	Array,
	List,
	Set,
}

internal sealed class CollectionModel : TypeModel
{
	public CollectionModel(string typeName, CollectionKind kind, TypeModel element)
		: base(typeName)
	{
		Kind = kind;
		Element = element;
	}

	public CollectionKind Kind { get; }

	public TypeModel Element { get; }

	public override bool NeedsHelpers => true;
}

internal sealed class DictionaryModel : TypeModel
{
	public DictionaryModel(string typeName, TypeModel key, TypeModel value)
		: base(typeName)
	{
		Key = key;
		Value = value;
	}

	public TypeModel Key { get; }

	public TypeModel Value { get; }

	public override bool NeedsHelpers => true;
}

/// <summary>A struct or class written member by member.</summary>
internal sealed class ObjectModel : TypeModel
{
	public ObjectModel(INamedTypeSymbol symbol, string typeName, bool isClass)
		: base(typeName)
	{
		Symbol = symbol;
		IsClass = isClass;
	}

	public INamedTypeSymbol Symbol { get; }

	public bool IsClass { get; }

	/// <summary>Members set after construction, in the order they are written.</summary>
	public List<MemberModel> Members { get; } = new();

	/// <summary>
	/// Members passed to <see cref="Constructor"/>, in parameter order. They are written before <see cref="Members"/>,
	/// read into locals, and handed to the constructor, which is how System.Text.Json fills types such as readonly
	/// structs whose members have no setters.
	/// </summary>
	public List<MemberModel> ConstructorArguments { get; } = new();

	/// <summary>The parameterized constructor values are read through, or null to construct and then set members.</summary>
	public IMethodSymbol? Constructor { get; set; }

	/// <summary>Every member in the order the binary data stores it.</summary>
	public IEnumerable<MemberModel> MembersInLayoutOrder => ConstructorArguments.Concat(Members);

	/// <summary>Construct through an UnsafeAccessor because the constructor is not accessible or members are required.</summary>
	public bool UsesConstructorAccessor { get; set; }

	/// <summary>
	/// Whether a struct declares its own parameterless constructor, which must run as it does in System.Text.Json.
	/// Other structs start from <c>default</c>, which is also the only form tuple types accept.
	/// </summary>
	public bool HasDeclaredParameterlessConstructor { get; set; }

	public bool CallsOnDeserializing { get; set; }

	public bool CallsOnDeserialized { get; set; }

	/// <summary>Set when a member could not be modelled. Errors have already been reported or are carried by a parent.</summary>
	public bool HasErrors { get; set; }

	/// <summary>True until every member has been modelled, which lets self-referencing types terminate.</summary>
	public bool IsBeingBuilt { get; set; }

	public override bool NeedsHelpers => true;
}

/// <summary>A type that cannot be cooked, with the reason shown to the user.</summary>
internal sealed class UnsupportedModel : TypeModel
{
	public UnsupportedModel(string typeName, string reason)
		: base(typeName)
	{
		Reason = reason;
	}

	public string Reason { get; }

	public override bool NeedsHelpers => false;
}

internal enum MemberGetKind
{
	Direct,
	FieldAccessor,
	GetterAccessor,
}

internal enum MemberSetKind
{
	Direct,
	FieldAccessor,
	SetterAccessor,
}

internal sealed class MemberModel
{
	public MemberModel(string name, TypeModel type, ISymbol symbol)
	{
		Name = name;
		Type = type;
		Symbol = symbol;
	}

	public string Name { get; }

	public TypeModel Type { get; }

	public ISymbol Symbol { get; }

	public MemberGetKind GetKind { get; set; }

	public MemberSetKind SetKind { get; set; }

	/// <summary>The type that declares the member, which UnsafeAccessors must target.</summary>
	public string DeclaringTypeName { get; set; } = string.Empty;

	public bool DeclaringTypeIsClass { get; set; }

	/// <summary>Index used to name this member's UnsafeAccessors.</summary>
	public int AccessorId { get; set; } = -1;
}
