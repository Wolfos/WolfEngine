using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace WolfEngine.SourceGenerators.Serialization;

/// <summary>
/// Turns types into <see cref="TypeModel"/>s. Which members are cooked follows System.Text.Json's reflection rules
/// with the engine's <c>IncludeFields</c> options, because cooking reads the authored JSON with those rules and every
/// member it can round-trip has to survive the trip to binary.
/// </summary>
internal sealed class ModelBuilder
{
	// JsonIgnoreCondition.Always. JsonIgnore without a condition means Always.
	private const int JsonIgnoreConditionAlways = 1;

	private static readonly SymbolDisplayFormat TypeNameFormat = SymbolDisplayFormat.FullyQualifiedFormat
		.WithMiscellaneousOptions(
			SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
			SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

	private readonly Compilation _compilation;
	private readonly WellKnownSymbols _symbols;
	private readonly List<Diagnostic> _diagnostics;
	private readonly Dictionary<ITypeSymbol, TypeModel> _models = new(SymbolEqualityComparer.Default);

	public ModelBuilder(Compilation compilation, WellKnownSymbols symbols, List<Diagnostic> diagnostics)
	{
		_compilation = compilation;
		_symbols = symbols;
		_diagnostics = diagnostics;
	}

	/// <summary>Every model with generated helpers, in the order they were first reached.</summary>
	public List<TypeModel> HelperModels { get; } = new();

	public static string GetTypeName(ITypeSymbol type)
	{
		return type.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString(TypeNameFormat);
	}

	public TypeModel Get(ITypeSymbol type)
	{
		if (type.NullableAnnotation == NullableAnnotation.Annotated && type.IsReferenceType)
		{
			type = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
		}

		if (_models.TryGetValue(type, out var existing))
		{
			return existing;
		}

		var model = Create(type);
		if (model is ObjectModel == false)
		{
			// Object models register themselves before building members, so self-references resolve.
			_models[type] = model;
			Track(model);
		}

		return model;
	}

	private void Track(TypeModel model)
	{
		if (model.NeedsHelpers == false)
		{
			return;
		}

		model.HelperId = HelperModels.Count;
		HelperModels.Add(model);
	}

	private TypeModel Create(ITypeSymbol type)
	{
		var typeName = GetTypeName(type);
		var scalar = TryCreateScalar(type, typeName);
		if (scalar is not null)
		{
			return scalar;
		}

		if (type is IArrayTypeSymbol array)
		{
			if (array.Rank != 1)
			{
				return new UnsupportedModel(typeName, "only single-dimensional arrays are supported");
			}

			return WrapElement(typeName, array.ElementType, element => new CollectionModel(typeName, CollectionKind.Array, element));
		}

		if (type is not INamedTypeSymbol named)
		{
			return new UnsupportedModel(typeName, DescribeUnsupportedKind(type));
		}

		if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
		{
			return WrapElement(typeName, named.TypeArguments[0], inner => new NullableModel(typeName, inner));
		}

		var collection = TryCreateCollection(named, typeName);
		if (collection is not null)
		{
			return collection;
		}

		if (IsUnsupportedFrameworkType(named, out var frameworkReason))
		{
			return new UnsupportedModel(typeName, frameworkReason);
		}

		return CreateObject(named, typeName);
	}

	private TypeModel? TryCreateScalar(ITypeSymbol type, string typeName)
	{
		switch (type.SpecialType)
		{
			case SpecialType.System_Boolean:
				return new ScalarModel(typeName, "Boolean", "bool");

			case SpecialType.System_Byte:
				return new ScalarModel(typeName, "Byte", "u8");

			case SpecialType.System_SByte:
				return new ScalarModel(typeName, "SByte", "i8");

			case SpecialType.System_Int16:
				return new ScalarModel(typeName, "Int16", "i16");

			case SpecialType.System_UInt16:
				return new ScalarModel(typeName, "UInt16", "u16");

			case SpecialType.System_Char:
				return new ScalarModel(typeName, "Char", "char");

			case SpecialType.System_Int32:
				return new ScalarModel(typeName, "Int32", "i32");

			case SpecialType.System_UInt32:
				return new ScalarModel(typeName, "UInt32", "u32");

			case SpecialType.System_Int64:
				return new ScalarModel(typeName, "Int64", "i64");

			case SpecialType.System_UInt64:
				return new ScalarModel(typeName, "UInt64", "u64");

			case SpecialType.System_Single:
				return new ScalarModel(typeName, "Single", "f32");

			case SpecialType.System_Double:
				return new ScalarModel(typeName, "Double", "f64");

			case SpecialType.System_Decimal:
				return new ScalarModel(typeName, "Decimal", "dec");

			case SpecialType.System_String:
				return new ScalarModel(typeName, "String", "str");

			case SpecialType.System_DateTime:
				return new ScalarModel(typeName, "DateTime", "datetime");
		}

		if (type is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
		{
			var underlyingScalar = (ScalarModel)TryCreateScalar(underlying, GetTypeName(underlying))!;
			return new ScalarModel(
				typeName,
				underlyingScalar.MethodSuffix,
				$"enum({underlyingScalar.Schema})",
				underlyingScalar.TypeName);
		}

		if (SymbolEqualityComparer.Default.Equals(type, _symbols.Entity))
		{
			return new ScalarModel(typeName, "Entity", "entity");
		}

		var metadataName = GetMetadataFullName(type);
		switch (metadataName)
		{
			case "System.Guid":
				return new ScalarModel(typeName, "Guid", "guid");

			case "System.DateTimeOffset":
				return new ScalarModel(typeName, "DateTimeOffset", "datetimeoffset");

			case "System.TimeSpan":
				return new ScalarModel(typeName, "TimeSpan", "timespan");

			case "System.Numerics.Vector2":
				return new ScalarModel(typeName, "Vector2", "vec2");

			case "System.Numerics.Vector3":
				return new ScalarModel(typeName, "Vector3", "vec3");

			case "System.Numerics.Vector4":
				return new ScalarModel(typeName, "Vector4", "vec4");

			case "System.Numerics.Quaternion":
				return new ScalarModel(typeName, "Quaternion", "quat");

			case "System.Numerics.Plane":
				return new ScalarModel(typeName, "Plane", "plane");

			case "System.Numerics.Matrix3x2":
				return new ScalarModel(typeName, "Matrix3x2", "mat3x2");

			case "System.Numerics.Matrix4x4":
				return new ScalarModel(typeName, "Matrix4x4", "mat4x4");
		}

		return null;
	}

	private TypeModel? TryCreateCollection(INamedTypeSymbol named, string typeName)
	{
		var definition = GetMetadataFullName(named.OriginalDefinition);
		switch (definition)
		{
			case "System.Collections.Generic.List`1":
			case "System.Collections.Generic.IList`1":
			case "System.Collections.Generic.ICollection`1":
			case "System.Collections.Generic.IEnumerable`1":
			case "System.Collections.Generic.IReadOnlyList`1":
			case "System.Collections.Generic.IReadOnlyCollection`1":
				return WrapElement(typeName, named.TypeArguments[0], element => new CollectionModel(typeName, CollectionKind.List, element));

			case "System.Collections.Generic.HashSet`1":
			case "System.Collections.Generic.ISet`1":
			case "System.Collections.Generic.IReadOnlySet`1":
				return WrapElement(typeName, named.TypeArguments[0], element => new CollectionModel(typeName, CollectionKind.Set, element));

			case "System.Collections.Generic.Dictionary`2":
			case "System.Collections.Generic.IDictionary`2":
			case "System.Collections.Generic.IReadOnlyDictionary`2":
			{
				var key = Get(named.TypeArguments[0]);
				if (key is UnsupportedModel unsupportedKey)
				{
					return new UnsupportedModel(typeName, $"its key type '{unsupportedKey.TypeName}' is not supported: {unsupportedKey.Reason}");
				}

				var value = Get(named.TypeArguments[1]);
				if (value is UnsupportedModel unsupportedValue)
				{
					return new UnsupportedModel(typeName, $"its value type '{unsupportedValue.TypeName}' is not supported: {unsupportedValue.Reason}");
				}

				return new DictionaryModel(typeName, key, value);
			}
		}

		return null;
	}

	private TypeModel WrapElement(string typeName, ITypeSymbol elementType, System.Func<TypeModel, TypeModel> wrap)
	{
		var element = Get(elementType);
		if (element is UnsupportedModel unsupported)
		{
			return new UnsupportedModel(typeName, $"its element type '{unsupported.TypeName}' is not supported: {unsupported.Reason}");
		}

		return wrap(element);
	}

	private bool IsUnsupportedFrameworkType(INamedTypeSymbol named, out string reason)
	{
		reason = string.Empty;
		if (named.SpecialType == SpecialType.System_Object)
		{
			reason = "'object' could hold any type, so its layout cannot be known when cooking";
			return true;
		}

		if (named.TypeKind == TypeKind.Interface)
		{
			reason = "interfaces could hold any implementation, so their layout cannot be known when cooking";
			return true;
		}

		if (named.TypeKind == TypeKind.Delegate)
		{
			reason = "delegates are code, not data";
			return true;
		}

		if (named.IsRefLikeType)
		{
			reason = "ref structs cannot be stored";
			return true;
		}

		var ns = named.ContainingNamespace?.ToDisplayString() ?? string.Empty;
		if (ns == "System" || ns.StartsWith("System.", System.StringComparison.Ordinal) || ns.StartsWith("Microsoft.", System.StringComparison.Ordinal))
		{
			// Framework types beyond the ones handled explicitly have layouts this generator does not control,
			// and many hold handles or state that is not data. ValueTuple is the useful exception.
			if (GetMetadataFullName(named.OriginalDefinition).StartsWith("System.ValueTuple", System.StringComparison.Ordinal))
			{
				return false;
			}

			reason = "framework types other than primitives, strings, Guid, date and time types, System.Numerics vectors and matrices, and generic lists, sets and dictionaries are not supported";
			return true;
		}

		return false;
	}

	private TypeModel CreateObject(INamedTypeSymbol named, string typeName)
	{
		if (named.IsAbstract && named.TypeKind == TypeKind.Class)
		{
			return new UnsupportedModel(typeName, "abstract classes could hold any derived type, so their layout cannot be known when cooking");
		}

		if (named.TypeKind != TypeKind.Struct && named.TypeKind != TypeKind.Class)
		{
			return new UnsupportedModel(typeName, DescribeUnsupportedKind(named));
		}

		if (_compilation.IsSymbolAccessibleWithin(named, _compilation.Assembly) == false)
		{
			return new UnsupportedModel(typeName, "generated code in this assembly cannot access it; make it public or internal and not nested in a private type");
		}

		var isClass = named.TypeKind == TypeKind.Class;
		var model = new ObjectModel(named, typeName, isClass)
		{
			IsBeingBuilt = true
		};
		_models[named] = model;
		Track(model);

		if (TrySelectConstructor(named, out var constructor, out var constructorError) == false)
		{
			model.HasErrors = true;
			var unsupported = new UnsupportedModel(typeName, constructorError);
			_models[named] = unsupported;
			return unsupported;
		}

		if (constructor is not null)
		{
			model.UsesConstructorAccessor = IsAccessible(constructor) == false;
			if (constructor.Parameters.Length > 0)
			{
				model.Constructor = constructor;
			}
		}

		if (isClass == false)
		{
			model.HasDeclaredParameterlessConstructor = named.InstanceConstructors.Any(candidate =>
				candidate.Parameters.Length == 0 &&
				candidate.IsImplicitlyDeclared == false);
		}

		model.CallsOnDeserializing = Implements(named, _symbols.OnDeserializing);
		model.CallsOnDeserialized = Implements(named, _symbols.OnDeserialized);

		var boundToConstructor = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
		if (model.Constructor is not null)
		{
			foreach (var parameter in model.Constructor.Parameters)
			{
				var member = FindConstructorMember(named, parameter);
				if (member is null)
				{
					ReportUnboundParameter(named, model, parameter);
					continue;
				}

				boundToConstructor.Add(member.Name);
				AddMember(named, model, member, isConstructorArgument: true);
			}
		}

		foreach (var member in SelectMembers(named))
		{
			if (boundToConstructor.Contains(member.Name))
			{
				continue;
			}

			AddMember(named, model, member, isConstructorArgument: false);
		}

		if (model.MembersInLayoutOrder.Any(member => member.Symbol is IPropertySymbol { IsRequired: true } || member.Symbol is IFieldSymbol { IsRequired: true }))
		{
			model.UsesConstructorAccessor = true;
		}

		model.IsBeingBuilt = false;

		if (model.HasErrors && IsDeclaredInThisCompilation(named) == false)
		{
			// Errors inside another assembly's type cannot be reported where they are, so they become the reason
			// the member using this type is unsupported.
			var unsupported = new UnsupportedModel(typeName, "one of its members cannot be stored");
			_models[named] = unsupported;
			return unsupported;
		}

		return model;
	}

	/// <summary>
	/// Picks the constructor System.Text.Json deserializes through: the one marked [JsonConstructor]; otherwise a
	/// struct's default value; otherwise a class's public parameterless constructor, or its only public constructor.
	/// A null constructor means the type starts from <c>default</c>.
	/// </summary>
	private bool TrySelectConstructor(INamedTypeSymbol named, out IMethodSymbol? constructor, out string error)
	{
		constructor = null;
		error = string.Empty;
		var annotated = named.InstanceConstructors
			.Where(candidate => HasAttribute(candidate, _symbols.JsonConstructorAttribute))
			.ToList();
		if (annotated.Count > 1)
		{
			error = "more than one constructor is marked [JsonConstructor]";
			return false;
		}

		if (annotated.Count == 1)
		{
			constructor = annotated[0];
			return true;
		}

		if (named.TypeKind == TypeKind.Struct)
		{
			return true;
		}

		var parameterless = named.InstanceConstructors.FirstOrDefault(candidate => candidate.Parameters.Length == 0);
		if (parameterless is not null)
		{
			constructor = parameterless;
			return true;
		}

		var publicConstructors = named.InstanceConstructors
			.Where(candidate => candidate.DeclaredAccessibility == Accessibility.Public)
			.ToList();
		if (publicConstructors.Count == 1)
		{
			constructor = publicConstructors[0];
			return true;
		}

		error = "it has no parameterless constructor and no single public constructor or [JsonConstructor] to read it through";
		return false;
	}

	/// <summary>
	/// The member a constructor parameter is filled from. System.Text.Json matches them by name, ignoring case, and
	/// the member only has to be readable, which is what lets get-only properties round-trip through a constructor.
	/// </summary>
	private ISymbol? FindConstructorMember(INamedTypeSymbol type, IParameterSymbol parameter)
	{
		for (var current = type; current is not null; current = current.BaseType)
		{
			foreach (var member in current.GetMembers())
			{
				if (string.Equals(member.Name, parameter.Name, System.StringComparison.OrdinalIgnoreCase) == false ||
				    member.IsStatic ||
				    member.IsImplicitlyDeclared ||
				    IsIgnored(member))
				{
					continue;
				}

				var hasJsonInclude = HasAttribute(member, _symbols.JsonIncludeAttribute);
				if (member is IFieldSymbol field &&
				    field.IsConst == false &&
				    (field.DeclaredAccessibility == Accessibility.Public || hasJsonInclude) &&
				    SymbolEqualityComparer.Default.Equals(field.Type, parameter.Type))
				{
					return member;
				}

				if (member is IPropertySymbol { IsIndexer: false, GetMethod: not null } property &&
				    (property.GetMethod.DeclaredAccessibility == Accessibility.Public || hasJsonInclude) &&
				    SymbolEqualityComparer.Default.Equals(property.Type, parameter.Type))
				{
					return member;
				}
			}
		}

		return null;
	}

	private void ReportUnboundParameter(INamedTypeSymbol owner, ObjectModel model, IParameterSymbol parameter)
	{
		model.HasErrors = true;
		if (IsDeclaredInThisCompilation(owner) == false)
		{
			return;
		}

		var location = parameter.Locations.FirstOrDefault(candidate => candidate.IsInSource) ?? Location.None;
		_diagnostics.Add(Diagnostic.Create(
			SerializationDiagnostics.UnsupportedType,
			location,
			owner.ToDisplayString(),
			$"constructor parameter '{parameter.Name}' has no readable property or field of the same name and type to cook its value from"));
	}

	private void AddMember(INamedTypeSymbol owner, ObjectModel model, ISymbol member, bool isConstructorArgument)
	{
		var memberType = member is IFieldSymbol field ? field.Type : ((IPropertySymbol)member).Type;
		var typeModel = Get(memberType);
		if (typeModel is UnsupportedModel unsupported)
		{
			ReportUnsupportedMember(owner, model, member, unsupported);
			return;
		}

		if (typeModel is ObjectModel { HasErrors: true })
		{
			model.HasErrors = true;
			return;
		}

		var declaringType = member.ContainingType;
		if (declaringType.IsGenericType && RequiresAccessor(member, isConstructorArgument))
		{
			ReportUnsupportedMember(
				owner,
				model,
				member,
				new UnsupportedModel(GetTypeName(memberType), "it is not public, and non-public members of generic types cannot be reached from generated code"));
			return;
		}

		var memberModel = new MemberModel(member.Name, typeModel, member)
		{
			DeclaringTypeName = GetTypeName(declaringType),
			DeclaringTypeIsClass = declaringType.TypeKind == TypeKind.Class
		};

		if (member is IFieldSymbol)
		{
			var accessible = IsAccessible(member);
			memberModel.GetKind = accessible ? MemberGetKind.Direct : MemberGetKind.FieldAccessor;
			memberModel.SetKind = accessible ? MemberSetKind.Direct : MemberSetKind.FieldAccessor;
		}
		else
		{
			var property = (IPropertySymbol)member;
			memberModel.GetKind = IsAccessible(property.GetMethod!) ? MemberGetKind.Direct : MemberGetKind.GetterAccessor;
			// Constructor arguments are never assigned, so their setters, which may not exist, do not matter.
			memberModel.SetKind = isConstructorArgument ||
			                      (IsAccessible(property.SetMethod!) && property.SetMethod!.IsInitOnly == false)
				? MemberSetKind.Direct
				: MemberSetKind.SetterAccessor;
		}

		if (isConstructorArgument)
		{
			model.ConstructorArguments.Add(memberModel);
			return;
		}

		model.Members.Add(memberModel);
	}

	private bool RequiresAccessor(ISymbol member, bool isConstructorArgument)
	{
		if (member is IFieldSymbol)
		{
			return IsAccessible(member) == false;
		}

		var property = (IPropertySymbol)member;
		if (isConstructorArgument)
		{
			return IsAccessible(property.GetMethod!) == false;
		}

		return IsAccessible(property.GetMethod!) == false ||
		       IsAccessible(property.SetMethod!) == false ||
		       property.SetMethod!.IsInitOnly;
	}

	private void ReportUnsupportedMember(INamedTypeSymbol owner, ObjectModel model, ISymbol member, UnsupportedModel unsupported)
	{
		model.HasErrors = true;
		if (IsDeclaredInThisCompilation(owner) == false)
		{
			return;
		}

		var location = member.Locations.FirstOrDefault(candidate => candidate.IsInSource) ?? Location.None;
		_diagnostics.Add(Diagnostic.Create(
			SerializationDiagnostics.UnsupportedMember,
			location,
			member.Name,
			owner.ToDisplayString(),
			unsupported.TypeName.Replace("global::", string.Empty),
			unsupported.Reason));
	}

	/// <summary>
	/// The members System.Text.Json reads back with the engine's options, base type members first, a member
	/// redeclared in a derived type replacing the base one.
	/// </summary>
	private IEnumerable<ISymbol> SelectMembers(INamedTypeSymbol type)
	{
		var hierarchy = new List<INamedTypeSymbol>();
		for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object && current.SpecialType != SpecialType.System_ValueType; current = current.BaseType)
		{
			hierarchy.Insert(0, current);
		}

		var selected = new List<ISymbol>();
		var indexByName = new Dictionary<string, int>();
		foreach (var declaring in hierarchy)
		{
			foreach (var member in declaring.GetMembers())
			{
				if (IsCookedMember(member) == false)
				{
					continue;
				}

				if (indexByName.TryGetValue(member.Name, out var index))
				{
					selected[index] = member;
					continue;
				}

				indexByName[member.Name] = selected.Count;
				selected.Add(member);
			}
		}

		return selected;
	}

	private bool IsCookedMember(ISymbol member)
	{
		if (member.IsStatic || member.IsImplicitlyDeclared)
		{
			return false;
		}

		var hasJsonInclude = HasAttribute(member, _symbols.JsonIncludeAttribute);
		if (member is IFieldSymbol field)
		{
			if (field.IsConst || field.IsReadOnly || field.IsFixedSizeBuffer)
			{
				return false;
			}

			if (field.DeclaredAccessibility != Accessibility.Public && hasJsonInclude == false)
			{
				return false;
			}

			return IsIgnored(member) == false;
		}

		if (member is IPropertySymbol property)
		{
			if (property.IsIndexer || property.ExplicitInterfaceImplementations.Length > 0)
			{
				return false;
			}

			if (property.GetMethod is null || property.SetMethod is null)
			{
				return false;
			}

			var isPublic = property.DeclaredAccessibility == Accessibility.Public &&
			               property.GetMethod.DeclaredAccessibility == Accessibility.Public &&
			               property.SetMethod.DeclaredAccessibility == Accessibility.Public;
			if (isPublic == false && hasJsonInclude == false)
			{
				return false;
			}

			return IsIgnored(member) == false;
		}

		return false;
	}

	private bool IsIgnored(ISymbol member)
	{
		if (HasAttribute(member, _symbols.NotSerializedAttribute))
		{
			return true;
		}

		foreach (var attribute in member.GetAttributes())
		{
			if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, _symbols.JsonIgnoreAttribute) == false)
			{
				continue;
			}

			var condition = JsonIgnoreConditionAlways;
			foreach (var argument in attribute.NamedArguments)
			{
				if (argument.Key == "Condition" && argument.Value.Value is int value)
				{
					condition = value;
				}
			}

			// Only Always drops the member on read. The WhenWriting conditions still read it back, and Never keeps it.
			return condition == JsonIgnoreConditionAlways;
		}

		return false;
	}

	private bool IsAccessible(ISymbol symbol)
	{
		return _compilation.IsSymbolAccessibleWithin(symbol, _compilation.Assembly);
	}

	private bool IsDeclaredInThisCompilation(ISymbol symbol)
	{
		return SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, _compilation.Assembly);
	}

	private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType)
	{
		if (attributeType is null)
		{
			return false;
		}

		return symbol.GetAttributes().Any(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, attributeType));
	}

	private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol? interfaceType)
	{
		if (interfaceType is null)
		{
			return false;
		}

		return type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, interfaceType));
	}

	private static string DescribeUnsupportedKind(ITypeSymbol type)
	{
		switch (type.TypeKind)
		{
			case TypeKind.Pointer:
			case TypeKind.FunctionPointer:
				return "pointers are addresses, not data";

			case TypeKind.TypeParameter:
				return "open generic type parameters have no known layout";

			case TypeKind.Dynamic:
				return "'dynamic' could hold any type";

			default:
				return $"{type.TypeKind} types are not supported";
		}
	}

	/// <summary>The name reflection reports as <c>Type.FullName</c>: namespace, then nested types joined by '+'.</summary>
	public static string GetMetadataFullName(ITypeSymbol type)
	{
		var name = type.MetadataName;
		var containing = type.ContainingType;
		while (containing is not null)
		{
			name = containing.MetadataName + "+" + name;
			containing = containing.ContainingType;
		}

		var ns = type.ContainingNamespace;
		if (ns is null || ns.IsGlobalNamespace)
		{
			return name;
		}

		return ns.ToDisplayString() + "." + name;
	}
}
