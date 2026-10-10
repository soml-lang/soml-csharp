using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Soml.SourceGeneration;

/*
Analyzes the types of one context and writes its code. A type gets one converter, which the context makes once, when it is first used. A scalar uses a converter of `SomlConverters`, and an enum, a collection, and a dictionary are made from one. A class, a struct, or a record gets a converter class of its own, nested in the context.
*/
internal sealed class Emitter
{
	private const string Converters = "global::Soml.Serialization.SomlConverters";
	private const string ConstructorOverlapReason = "it has constructors with overlapping parameter types after generic substitution, which the generated code cannot bind unambiguously";

	/*
	The most types that resolving a type may nest. It equals the most levels that a document nests.
	*/
	private const int MaxNesting = 100;

	private static readonly SymbolDisplayFormat MetadataFormat = SymbolDisplayFormat.CSharpErrorMessageFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.None);

	private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

	/*
	The scalar types, indexed by their metadata name, with the full name and the property of `SomlConverters` for each.
	*/
	private static readonly Dictionary<string, (string FullName, string Property)> Scalars = new()
	{
		["SomlValue"] = ("Soml.SomlValue", "Value"),
		["SomlObject"] = ("Soml.SomlObject", "Object"),
		["SomlArray"] = ("Soml.SomlArray", "Array"),
		["SomlInstant"] = ("Soml.SomlInstant", "Instant"),
		["SomlDuration"] = ("Soml.SomlDuration", "Duration"),
		["DateTimeOffset"] = ("System.DateTimeOffset", "DateTimeOffset"),
		["DateTime"] = ("System.DateTime", "DateTime"),
		["TimeSpan"] = ("System.TimeSpan", "TimeSpan"),
		["DateOnly"] = ("System.DateOnly", "DateOnly"),
		["TimeOnly"] = ("System.TimeOnly", "TimeOnly"),
		["Version"] = ("System.Version", "Version"),
		["Uri"] = ("System.Uri", "Uri"),
		["Half"] = ("System.Half", "Half"),
		["Int128"] = ("System.Int128", "Int128"),
		["UInt128"] = ("System.UInt128", "UInt128"),
	};

	private readonly INamedTypeSymbol _context;
	private readonly Compilation _compilation;
	private INamedTypeSymbol? _listDefinition;
	private INamedTypeSymbol? _dictionaryDefinition;
	private readonly CancellationToken _cancellationToken;
	private readonly List<DiagnosticInfo> _diagnostics = [];
	private bool _hasUnresolvedType;
	private readonly Dictionary<ITypeSymbol, Entry> _entries = new(SymbolEqualityComparer.Default);
	private readonly List<Entry> _ordered = [];

	/*
	The entries that are being made, from the root to the current one.
	*/
	private readonly List<Entry> _resolving = [];

	private Emitter(INamedTypeSymbol context, Compilation compilation, CancellationToken cancellationToken)
	{
		_context = context;
		_compilation = compilation;
		_cancellationToken = cancellationToken;
	}

	public static GenerationResult Generate(INamedTypeSymbol context, Compilation compilation, ImmutableArray<AttributeData> attributes, CancellationToken cancellationToken)
	{
		var emitter = new Emitter(context, compilation, cancellationToken);
		var source = emitter.Emit(attributes);
		var hintName = $"{context.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", "").Replace("@", "").Replace('<', '_').Replace('>', '_').Replace(',', '_')}.Soml.g.cs";
		return new GenerationResult(hintName, source, new EquatableArray<DiagnosticInfo>([.. emitter._diagnostics]));
	}

	private string? Emit(ImmutableArray<AttributeData> attributes)
	{
		var location = _context.Locations.FirstOrDefault();

		if (!InheritsFrom(_context, "Soml.Serialization.SomlSerializerContext"))
		{
			Report(Descriptors.NotContext, location, _context.Name);
			return null;
		}

		for (var type = _context; type is not null; type = type.ContainingType)
		{
			var isPartial = type.DeclaringSyntaxReferences.All(reference => reference.GetSyntax(_cancellationToken) is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax declaration && declaration.Modifiers.Any(SyntaxKind.PartialKeyword));

			if (!isPartial || type.IsGenericType)
			{
				Report(Descriptors.NotPartial, location, _context.Name);
				return null;
			}
		}

		// The attributes must be on one declaration, as each declaration with them is generated on its own.
		SyntaxNode? declaration = null;

		foreach (var attribute in _context.GetAttributes())
		{
			if (attribute.AttributeClass is not { } attributeClass || !MatchesName(attributeClass, "Soml.Serialization.SomlSerializableAttribute"))
			{
				continue;
			}

			var current = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).Parent?.Parent;

			if (declaration is not null && current != declaration)
			{
				Report(Descriptors.SplitContext, attributes.FirstOrDefault()?.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? location, _context.Name);
				return null;
			}

			declaration = current;
		}

		var roots = new List<(string PropertyName, Entry Entry, Location? Location)>(attributes.Length);

		// A property of a type must not hide a member of the context, or take the name of one the generator writes.
		var propertyNames = new HashSet<string>(StringComparer.Ordinal) { "Default", "Options", _context.Name };

		for (var type = _context; type is not null; type = type.BaseType)
		{
			propertyNames.UnionWith(type.GetMembers().Select(member => member.Name));
		}

		var existingNames = new HashSet<string>(propertyNames, StringComparer.Ordinal);

		foreach (var attribute in attributes)
		{
			_cancellationToken.ThrowIfCancellationRequested();

			if (attribute.ConstructorArguments.Length != 1 || attribute.ConstructorArguments[0].Value is not ITypeSymbol rootType)
			{
				continue;
			}

			var attributeLocation = attribute.ApplicationSyntaxReference?.GetSyntax(_cancellationToken).GetLocation() ?? location;
			var propertyName = attribute.NamedArguments.FirstOrDefault(argument => argument.Key == "TypeInfoPropertyName").Value.Value as string ?? DefaultPropertyName(rootType);

			if (propertyName.StartsWith("@", StringComparison.Ordinal))
			{
				propertyName = propertyName.Substring(1);
			}

			if (!SyntaxFacts.IsValidIdentifier(propertyName))
			{
				Report(Descriptors.InvalidTypeInfoName, attributeLocation, _context.Name, propertyName);
				continue;
			}

			propertyName = SyntaxFactory.ParseToken(Identifier(propertyName)).ValueText;

			if (!propertyNames.Add(propertyName))
			{
				Report(Descriptors.DuplicateTypeInfoName, attributeLocation, _context.Name, propertyName);
				continue;
			}

			// A private or protected type, which a context nested next to it can see, cannot be the type of a property that is internal or public.
			if (!IsInternalOrPublic(rootType))
			{
				Report(Descriptors.NotAccessible, attributeLocation, rootType.ToDisplayString());
				continue;
			}

			var entry = Resolve(rootType, attributeLocation, rootType.ToDisplayString());

			if (entry is not null)
			{
				roots.Add((propertyName, entry, attributeLocation));
			}
		}

		// A root's property must not take the name of a field, converter, or nested class that is generated for these types.
		var generatedNames = new HashSet<string>(StringComparer.Ordinal);

		foreach (var entry in _ordered)
		{
			generatedNames.Add($"Converter{entry.Index}");

			if (entry.Kind == EntryKind.Object)
			{
				generatedNames.Add($"Converter{entry.Index}Implementation");
			}
		}

		foreach (var generatedName in generatedNames)
		{
			if (existingNames.Contains(generatedName))
			{
				Report(Descriptors.DuplicateTypeInfoName, location, _context.Name, generatedName);
			}
		}

		foreach (var (propertyName, _, propertyLocation) in roots)
		{
			if (generatedNames.Contains(propertyName))
			{
				Report(Descriptors.DuplicateTypeInfoName, propertyLocation, _context.Name, propertyName);
			}
		}

		// With an error, the context gets no types, so that the error is not followed by errors about the code it would have needed.
		return WriteContext(_hasUnresolvedType || _diagnostics.Any(diagnostic => diagnostic.Descriptor.DefaultSeverity == DiagnosticSeverity.Error) ? [] : roots);
	}

	private void Report(DiagnosticDescriptor descriptor, Location? location, params string[] arguments) => _diagnostics.Add(DiagnosticInfo.Create(descriptor, location, arguments));

	// Note: Not using GetTypeByMetadataName with IsAssignable because it shortened this traversal but gave mixed public generation timings and allocation in local tests.
	private static bool InheritsFrom(INamedTypeSymbol type, string fullName)
	{
		for (var current = type.BaseType; current is not null; current = current.BaseType)
		{
			if (FullName(current) == fullName)
			{
				return true;
			}
		}

		return false;
	}

	/*
	The full metadata name of a type, such as `System.Collections.Generic.List`1`.
	*/
	// Note: Not using ToDisplayString with symbol display formats for metadata names because they do not preserve generic metadata arity or the `+` between nested types.
	private static string FullName(ITypeSymbol type)
	{
		if (type is not INamedTypeSymbol named)
		{
			return type.ToDisplayString();
		}

		var name = named.MetadataName;

		for (var container = named.ContainingType; container is not null; container = container.ContainingType)
		{
			name = $"{container.MetadataName}+{name}";
		}

		return named.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace ? $"{containingNamespace.ToDisplayString(MetadataFormat)}.{name}" : name;
	}

	private static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeFormat);

	/*
	The name of a root's property, such as `Config`, `ListConfig`, or `ConfigArray`.
	*/
	private static string DefaultPropertyName(ITypeSymbol type) => type switch
	{
		IArrayTypeSymbol array => $"{DefaultPropertyName(array.ElementType)}Array",
		INamedTypeSymbol { IsGenericType: true } named => named.Name + string.Concat(named.TypeArguments.Select(DefaultPropertyName)),
		_ => type.Name,
	};

	// Note: Not using SyntaxFactory.Literal because it calls this formatter and adds a syntax token when only its text is needed.
	private static string Literal(string text) => SymbolDisplay.FormatLiteral(text, quote: true);

	private static string Identifier(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? $"@{name}" : name;

	private static string? ExplicitName(ISymbol symbol) => FindAttribute(symbol, "Soml.SomlNameAttribute", inherit: true)?.ConstructorArguments.FirstOrDefault().Value as string;

	private static bool HasAttribute(ISymbol symbol, string fullName) => FindAttribute(symbol, fullName) is not null;

	private static bool IsIgnored(ISymbol symbol) => FindAttribute(symbol, "Soml.SomlIgnoreAttribute", inherit: true) is not null;

	/*
	The attributes of a symbol, then those of each property that it overrides, as reflection finds `[SomlName]` and `[SomlIgnore]` with `inherit: true`, so an override without them has its base property's.
	*/
	private static AttributeData? FindAttribute(ISymbol symbol, string fullName, bool inherit = false)
	{
		for (var current = symbol; current is not null; current = inherit ? (current as IPropertySymbol)?.OverriddenProperty : null)
		{
			foreach (var attribute in current.GetAttributes())
			{
				if (attribute.AttributeClass is { } attributeClass && MatchesName(attributeClass, fullName))
				{
					return attribute;
				}
			}
		}

		return null;
	}

	private static bool MatchesName(INamedTypeSymbol type, string fullName)
	{
		var separator = fullName.LastIndexOf('.');
		return type.MetadataName.Length == fullName.Length - separator - 1 && string.CompareOrdinal(type.MetadataName, 0, fullName, separator + 1, type.MetadataName.Length) == 0 && FullName(type) == fullName;
	}

	/*
	The converter of a type, made on first use, or `null`, with a diagnostic, when the generator does not support it.
	*/
	private Entry? Resolve(ITypeSymbol type, Location? location, string subject)
	{
		var depth = _resolving.Count;

		try
		{
			return ResolveCore(type, location, subject);
		}
		finally
		{
			_resolving.RemoveRange(depth, _resolving.Count - depth);
		}
	}

	private Entry? ResolveCore(ITypeSymbol type, Location? location, string subject)
	{
		_cancellationToken.ThrowIfCancellationRequested();

		if (_entries.TryGetValue(type, out var existing))
		{
			// A collection, a dictionary, or a nullable value makes the converter of its items when it is made, so a cycle of them without an object, which makes its converters when it reads, would never end.
			var start = _resolving.IndexOf(existing);

			var hasObject = start >= 0 && _resolving.FindIndex(start, static entry => entry.Kind == EntryKind.Object) >= 0;

			if (start >= 0 && !hasObject)
			{
				return Unsupported(subject, $"{existing.Type.ToDisplayString()} holds itself as an item", location);
			}

			return existing;
		}

		// A generic type whose members expand its type arguments, as `Node<List<T>> Child` in `Node<T>`, has a new type at each level, which would be resolved without end. No document nests deeper than 100 levels anyway.
		if (_resolving.Count >= MaxNesting)
		{
			return Unsupported(_resolving[0].Type.ToDisplayString(), $"it nests types more than {MaxNesting} levels deep, as a generic type whose members expand its type arguments without end does", location);
		}

		// A type that does not exist has an error of its own from the compiler.
		if (type.TypeKind == TypeKind.Error)
		{
			_hasUnresolvedType = true;
			return null;
		}

		if (type is IPointerTypeSymbol or IFunctionPointerTypeSymbol or ITypeParameterSymbol || type.IsRefLikeType || type.SpecialType == SpecialType.System_Void)
		{
			return Unsupported(subject, $"{type.ToDisplayString()} is not a type that values have", location);
		}

		if (type.IsTupleType)
		{
			return Unsupported(subject, $"{type.ToDisplayString()} is a tuple; use a record", location);
		}

		if (NullableUnderlyingType(type) is { } underlyingType)
		{
			var inner = Resolve(underlyingType, location, subject);
			return inner is null ? null : Add(new Entry(type, EntryKind.Nullable) { Item = inner });
		}

		if (type.TypeKind == TypeKind.Enum)
		{
			return Add(new Entry(type, EntryKind.Enum));
		}

		if (type is IArrayTypeSymbol array)
		{
			if (array.Rank != 1)
			{
				return Unsupported(subject, $"{type.ToDisplayString()} is a multidimensional array; use a jagged array, such as int[][]", location);
			}

			return Collection(type, array.ElementType, location, subject);
		}

		if (Scalar(type) is { } scalar)
		{
			var entry = new Entry(type, EntryKind.Scalar) { Expression = $"{Converters}.{scalar}" };
			_entries.Add(type, entry);
			return entry;
		}

		if (FindInterface(type, "System.Collections.Generic.IDictionary`2", "System.Collections.Generic.IReadOnlyDictionary`2") is { } dictionary)
		{
			return Dictionary(type, dictionary, location, subject);
		}

		if (FindInterface(type, SpecialType.System_Collections_Generic_IEnumerable_T) is { } enumerable)
		{
			return Collection(type, enumerable.TypeArguments[0], location, subject);
		}

		if (IsParsable(type))
		{
			var entry = new Entry(type, EntryKind.Scalar);
			entry.Expression = $"{Converters}.Parsable<{entry.TypeName}>()";
			_entries.Add(type, entry);
			return entry;
		}

		if (FindInterface(type, SpecialType.System_Collections_IEnumerable) is not null)
		{
			return Unsupported(subject, $"{type.ToDisplayString()} is a non-generic collection; use a generic one, such as List<T> or Dictionary<string, T>", location);
		}

		if (type.SpecialType == SpecialType.System_Object || type.TypeKind == TypeKind.Dynamic)
		{
			return Unsupported(subject, "object is written as the type of its value, which needs reflection; use SomlValue for a value of any type", location);
		}

		if (type.IsAbstract || type.TypeKind is TypeKind.Interface or TypeKind.Delegate || type is not INamedTypeSymbol { TypeKind: TypeKind.Class or TypeKind.Struct } named || named.IsStatic)
		{
			return Unsupported(subject, $"{type.ToDisplayString()} is an abstract class, an interface, or a delegate, which cannot be created", location);
		}

		return Members(named, location);
	}

	private Entry? Unsupported(string subject, string reason, Location? location)
	{
		Report(Descriptors.UnsupportedType, location, subject, reason);
		return null;
	}

	private Entry Add(Entry entry)
	{
		entry.Index = _ordered.Count;
		_entries[entry.Type] = entry;
		_ordered.Add(entry);
		_resolving.Add(entry);
		return entry;
	}

	/*
	The type itself or the first of its interfaces with the framework identity that Roslyn recognizes.
	*/
	private static INamedTypeSymbol? FindInterface(ITypeSymbol type, SpecialType specialType)
	{
		if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == specialType)
		{
			return named;
		}

		foreach (var implemented in type.AllInterfaces)
		{
			if (implemented.OriginalDefinition.SpecialType == specialType)
			{
				return implemented;
			}
		}

		return null;
	}

	/*
	The type itself or the first of its interfaces that is one of the generic definitions, by metadata name.
	*/
	private static INamedTypeSymbol? FindInterface(ITypeSymbol type, string fullName, string? secondName = null)
	{
		if (type is INamedTypeSymbol named && Matches(named))
		{
			return named;
		}

		foreach (var implemented in type.AllInterfaces)
		{
			if (Matches(implemented))
			{
				return implemented;
			}
		}

		return null;

		bool Matches(INamedTypeSymbol candidate)
		{
			return MatchesName(candidate.OriginalDefinition, fullName) || (secondName is not null && MatchesName(candidate.OriginalDefinition, secondName));
		}
	}

	private static bool IsParsable(ITypeSymbol type)
	{
		var isParsable = false;
		var isFormattable = false;

		foreach (var implemented in type.AllInterfaces)
		{
			isParsable |= MatchesName(implemented.OriginalDefinition, "System.IParsable`1") && SymbolEqualityComparer.Default.Equals(implemented.TypeArguments[0], type);
			isFormattable |= MatchesName(implemented, "System.IFormattable");

			if (isParsable && isFormattable)
			{
				return true;
			}
		}

		return false;
	}

	/*
	Built-in types have a stable Roslyn identity, so recognizing them needs no metadata-name formatting or dictionary lookup.
	*/
	private static string? Scalar(ITypeSymbol type) => type.SpecialType switch
	{
		SpecialType.System_String => "String",
		SpecialType.System_Boolean => "Boolean",
		SpecialType.System_Char => "Char",
		SpecialType.System_Double => "Double",
		SpecialType.System_Single => "Single",
		SpecialType.System_Decimal => "Decimal",
		SpecialType.System_SByte => "SByte",
		SpecialType.System_Byte => "Byte",
		SpecialType.System_Int16 => "Int16",
		SpecialType.System_UInt16 => "UInt16",
		SpecialType.System_Int32 => "Int32",
		SpecialType.System_UInt32 => "UInt32",
		SpecialType.System_Int64 => "Int64",
		SpecialType.System_UInt64 => "UInt64",
		SpecialType.System_IntPtr => "IntPtr",
		SpecialType.System_UIntPtr => "UIntPtr",
		_ => type is INamedTypeSymbol named && Scalars.TryGetValue(named.MetadataName, out var scalar) && MatchesName(named, scalar.FullName) ? scalar.Property : null,
	};

	/*
	Whether a value of a type may be null, without annotations: a reference type or a nullable value type.
	*/
	private static bool CanBeNull(ITypeSymbol type) => !type.IsValueType || NullableUnderlyingType(type) is not null;

	/*
	The `T` of a `Nullable<T>`, or `null` for another type.
	*/
	private static ITypeSymbol? NullableUnderlyingType(ITypeSymbol type) => type is INamedTypeSymbol { IsGenericType: true } named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T ? named.TypeArguments[0] : null;

	private Entry? Collection(ITypeSymbol type, ITypeSymbol itemType, Location? location, string subject)
	{
		var entry = Add(new Entry(type, EntryKind.Collection));
		entry.Item = Resolve(itemType, location, subject);

		if (entry.Item is null)
		{
			return null;
		}

		// As the reflection path builds a collection from the items it collected into a `List<T>`, while arrays are filled directly.
		if (type is IArrayTypeSymbol)
		{
			return entry;
		}

		var itemName = entry.Item.TypeName;
		entry.IsSet = FindInterface(type, "System.Collections.Generic.ISet`1", "System.Collections.Generic.IReadOnlySet`1") is not null;
		string? unsupportedReason = null;

		if (entry.IsSet && type.TypeKind == TypeKind.Interface)
		{
			entry.Create = $"static items => new global::System.Collections.Generic.HashSet<{itemName}>(items)";
		}
		else
		{
			var listType = (_listDefinition ??= _compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")!).Construct(itemType);
			entry.Create = CreateFrom(entry, listType, [itemType], isDictionary: false, out unsupportedReason);
		}

		return entry.Create is null ? Unsupported(subject, unsupportedReason ?? $"{type.ToDisplayString()} has no public constructor that takes its items, no public parameterless constructor with an Add method, and no CreateRange() factory", location) : entry;
	}

	private Entry? Dictionary(ITypeSymbol type, INamedTypeSymbol dictionary, Location? location, string subject)
	{
		if (dictionary.TypeArguments[0].SpecialType != SpecialType.System_String)
		{
			return Unsupported(subject, $"{type.ToDisplayString()} does not have string keys, which are the only dictionary keys that the source generator supports; the reflection path also reads enum, time, and IParsable keys", location);
		}

		var entry = Add(new Entry(type, EntryKind.Dictionary));
		var valueType = dictionary.TypeArguments[1];
		entry.Item = Resolve(valueType, location, subject);

		if (entry.Item is null)
		{
			return null;
		}

		var collected = (_dictionaryDefinition ??= _compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2")!).Construct(dictionary.TypeArguments[0], valueType);
		entry.Create = CreateFrom(entry, collected, [dictionary.TypeArguments[0], valueType], isDictionary: true, out var unsupportedReason);
		return entry.Create is null ? Unsupported(subject, unsupportedReason ?? $"{type.ToDisplayString()} has no public constructor that takes its members, no public parameterless constructor with an Add method, and no CreateRange() factory", location) : entry;
	}

	/*
	The lambda that makes a collection from the `List<T>` or `Dictionary<TKey, TValue>` of its items, in the order that the reflection path tries: the collected type itself, the frozen collections, a constructor that takes the items, a parameterless constructor and an `Add` that changes the collection, and `CreateRange()` of the static class with the type's name.
	*/
	private string? CreateFrom(Entry entry, INamedTypeSymbol collected, ITypeSymbol[] typeArguments, bool isDictionary, out string? unsupportedReason)
	{
		unsupportedReason = null;
		var type = entry.Type;
		var parameter = isDictionary ? "members" : "items";

		if (IsAssignable(collected, type))
		{
			return $"static {parameter} => {parameter}";
		}

		if (type is not INamedTypeSymbol named)
		{
			return null;
		}

		var definition = FullName(named.OriginalDefinition);

		if (definition == "System.Collections.Frozen.FrozenSet`1")
		{
			// Note: Not using span-based FrozenSet.Create because it saved allocation but slowed small generated reads in local tests.
			return $"static {parameter} => global::System.Collections.Frozen.FrozenSet.ToFrozenSet({parameter})";
		}

		if (definition == "System.Collections.Frozen.FrozenDictionary`2")
		{
			return $"static {parameter} => global::System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary({parameter})";
		}

		if (named.IsAbstract)
		{
			return null;
		}

		var typeName = entry.TypeName;
		IMethodSymbol? collectionConstructor = null;
		var hasParameterlessConstructor = false;

		foreach (var constructor in named.InstanceConstructors)
		{
			if (constructor.DeclaredAccessibility != Accessibility.Public)
			{
				continue;
			}

			hasParameterlessConstructor |= constructor.Parameters.IsEmpty;

			if (collectionConstructor is null && constructor.Parameters is { Length: 1 } arguments && arguments[0] is var argument && argument.RefKind == RefKind.None && argument.Type.SpecialType != SpecialType.System_Object && IsAssignable(collected, argument.Type))
			{
				collectionConstructor = constructor;
			}
		}

		if (collectionConstructor is not null)
		{
			if (HasConstructorOverlap(collectionConstructor))
			{
				unsupportedReason = ConstructorOverlapReason;
				return null;
			}

			return $"static {parameter} => new {typeName}(({TypeName(collectionConstructor.Parameters[0].Type)}){parameter})";
		}

		IMethodSymbol? add = null;

		for (var declaring = named; declaring is not null && add is null; declaring = declaring.BaseType)
		{
			foreach (var symbol in declaring.GetMembers("Add"))
			{
				if (symbol is not IMethodSymbol { DeclaredAccessibility: Accessibility.Public, IsStatic: false, IsGenericMethod: false } method || method.Parameters.Length != typeArguments.Length)
				{
					continue;
				}

				var matches = true;

				for (var index = 0; index < typeArguments.Length; index++)
				{
					if (method.Parameters[index].RefKind != RefKind.None || !SymbolEqualityComparer.Default.Equals(method.Parameters[index].Type, typeArguments[index]))
					{
						matches = false;
						break;
					}
				}

				if (matches)
				{
					add = method;
					break;
				}
			}
		}

		if (hasParameterlessConstructor && add is not null && (add.ReturnsVoid || add.ReturnType.SpecialType == SpecialType.System_Boolean))
		{
			var target = SymbolEqualityComparer.Default.Equals(add.ContainingType, type) ? "result" : $"(({TypeName(add.ContainingType)})result)";
			var call = isDictionary ? $"{target}.Add(item.Key, item.Value);" : $"{target}.Add(item);";
			return $"static {parameter} => {{ var result = new {typeName}(); foreach (var item in {parameter}) {{ {call} }} return result; }}";
		}

		if (named.ContainingType is not null)
		{
			return null;
		}

		var factoryName = named.ContainingNamespace.IsGlobalNamespace ? named.Name : $"{named.ContainingNamespace.ToDisplayString(MetadataFormat)}.{named.Name}";
		var factory = _compilation.GetTypeByMetadataName(factoryName);

		// Note: Not using SpecialType for this IEnumerable metadata-name test because it saved allocations but gave conflicting full-generator timings on .NET 10 ARM64.
		foreach (var symbol in factory?.GetMembers("CreateRange") ?? [])
		{
			if (symbol is not IMethodSymbol { IsStatic: true, DeclaredAccessibility: Accessibility.Public } method || method.TypeParameters.Length != typeArguments.Length || method.Parameters.Length != 1 || method.Parameters[0] is not { } argument || argument.RefKind != RefKind.None || argument.Type is not INamedTypeSymbol singleType || FullName(singleType.OriginalDefinition) != "System.Collections.Generic.IEnumerable`1")
			{
				continue;
			}

			var createRange = method.Construct(typeArguments);

			if (IsAssignable(collected, createRange.Parameters[0].Type) && IsAssignable(createRange.ReturnType, type))
			{
				return $"static {parameter} => {TypeName(factory!)}.CreateRange<{string.Join(", ", typeArguments.Select(TypeName))}>(({TypeName(createRange.Parameters[0].Type)}){parameter})";
			}
		}

		return null;
	}

	/*
	CLR assignment compatibility, as Type.IsAssignableFrom uses on the reflection path. A user-defined conversion runs extra code and is not an assignment.
	*/
	// Note: Not using Conversion.IsImplicit alone because it also accepts user-defined conversions for collection factories.
	private bool IsAssignable(ITypeSymbol source, ITypeSymbol target)
	{
		var conversion = _compilation.ClassifyConversion(source, target);
		return conversion.IsIdentity || (conversion.IsImplicit && (conversion.IsReference || conversion.IsBoxing));
	}

	/*
	Generic substitution can make distinct constructor signatures overlap, and C# overload resolution can then choose a different constructor from the one reflection invokes.
	*/
	private bool HasConstructorOverlap(IMethodSymbol constructor)
	{
		foreach (var candidate in constructor.ContainingType.InstanceConstructors)
		{
			if (SymbolEqualityComparer.Default.Equals(candidate, constructor) || candidate.Parameters.Length != constructor.Parameters.Length)
			{
				continue;
			}

			var matches = true;

			for (var index = 0; index < candidate.Parameters.Length; index++)
			{
				var left = candidate.Parameters[index];
				var right = constructor.Parameters[index];

				if (left.RefKind != right.RefKind || !SymbolEqualityComparer.Default.Equals(left.Type, right.Type))
				{
					matches = false;
					break;
				}
			}

			if (matches && _compilation.IsSymbolAccessibleWithin(candidate, _context))
			{
				return true;
			}
		}

		return false;
	}

	/*
	A class, a struct, or a record, with the members and the constructor that the reflection path finds for it.
	*/
	private Entry? Members(INamedTypeSymbol type, Location? location)
	{
		var entry = Add(new Entry(type, EntryKind.Object));

		var constructor = FindConstructor(type, out var hasDuplicateMarked);

		if (hasDuplicateMarked)
		{
			return Unsupported(type.ToDisplayString(), $"{type.ToDisplayString()} has more than one constructor with [SomlConstructor]", location);
		}

		if (constructor is not null && HasConstructorOverlap(constructor))
		{
			return Unsupported(type.ToDisplayString(), ConstructorOverlapReason, location);
		}

		entry.Constructor = constructor;

		if (!entry.HasConstructor)
		{
			Report(Descriptors.NoConstructor, location, type.ToDisplayString());
		}

		var hierarchy = new List<ImmutableArray<ISymbol>>();
		/*
		Whether the type or a base type has a required member, which `new` must set unless the constructor has `[SetsRequiredMembers]`.
		*/
		var hasRequiredMembers = false;

		for (var current = type; current is not null && current.SpecialType is not (SpecialType.System_Object or SpecialType.System_ValueType); current = current.BaseType)
		{
			var declared = current.GetMembers();
			hierarchy.Add(declared);
			hasRequiredMembers |= declared.Any(member => member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true });
		}

		entry.UsesConstructorAccessor = hasRequiredMembers && constructor is not null && !HasAttribute(constructor, "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");
		var parameters = constructor?.Parameters ?? [];
		var parametersByName = parameters.IsEmpty ? null : new Dictionary<string, IParameterSymbol>(parameters.Length, StringComparer.OrdinalIgnoreCase);

		foreach (var parameter in parameters)
		{
			if (!parametersByName!.ContainsKey(parameter.Name))
			{
				parametersByName!.Add(parameter.Name, parameter);
			}
		}

		// Reflection lists a property of a base class unless a derived class overrides it, or declares an instance property, of any accessibility, with its name and the type it is declared with, as `T` for a property of a generic base. A static property hides nothing, and a field is never hidden.
		var overridden = new HashSet<ISymbol>(SymbolEqualityComparer.Default);

		if (hierarchy.Count > 1)
		{
			var derivedProperties = new Dictionary<string, List<IPropertySymbol>>(StringComparer.Ordinal);

			foreach (var declared in hierarchy)
			{
				foreach (var symbol in declared)
				{
					if (symbol is not IPropertySymbol { IsStatic: false, IsIndexer: false } property)
					{
						continue;
					}

					for (var current = property.OverriddenProperty; current is not null; current = current.OverriddenProperty)
					{
						overridden.Add(current.OriginalDefinition);
					}

					if (derivedProperties.TryGetValue(property.Name, out var derived))
					{
						foreach (var candidate in derived)
						{
							if (IsSameSignatureType(candidate.OriginalDefinition.Type, property.OriginalDefinition.Type))
							{
								overridden.Add(property.OriginalDefinition);
								break;
							}
						}
					}
				}

				foreach (var symbol in declared)
				{
					if (symbol is IPropertySymbol { IsStatic: false, IsIndexer: false } property)
					{
						if (!derivedProperties.TryGetValue(property.Name, out var derived))
						{
							derivedProperties.Add(property.Name, derived = []);
						}

						derived.Add(property);
					}
				}
			}
		}

		// The members of a base class come first, and within each class, the properties in the order of their declaration, then the fields, as in the reflection path.
		var membersByName = parameters.IsEmpty ? null : new Dictionary<string, MemberModel>(StringComparer.OrdinalIgnoreCase);
		var ignoredNames = parameters.IsEmpty ? null : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		for (var level = hierarchy.Count - 1; level >= 0; level--)
		{
			var declared = hierarchy[level];

			foreach (var member in declared)
			{
				if (member is IPropertySymbol)
				{
					AddMember(member);
				}
			}

			foreach (var member in declared)
			{
				if (member is IFieldSymbol)
				{
					AddMember(member);
				}
			}
		}

		void AddMember(ISymbol member)
		{
			if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public || overridden.Contains(member.OriginalDefinition))
			{
				return;
			}

			if (IsIgnored(member))
			{
				ignoredNames?.Add(member.Name);
				return;
			}

			var model = CreateMember(type, member, parametersByName);

			if (model is not null)
			{
				entry.Members.Add(model);

				if (membersByName is not null && !membersByName.ContainsKey(model.Name))
				{
					membersByName.Add(model.Name, model);
				}
			}
		}

		foreach (var parameter in entry.Constructor?.Parameters ?? [])
		{
			// The reflection path cannot read a parameter of a by-ref type, and the generated call would need `ref` or `out`.
			if (parameter.RefKind != RefKind.None)
			{
				return Unsupported($"The parameter {parameter.Name} of {type.ToDisplayString()}", "it is passed by reference; take it by value", parameter.Locations.FirstOrDefault() ?? location);
			}

			membersByName!.TryGetValue(parameter.Name, out var matched);
			var isIgnored = matched is null && ignoredNames!.Contains(parameter.Name);
			var converter = isIgnored ? null : Resolve(parameter.Type, parameter.Locations.FirstOrDefault() ?? location, $"The parameter {parameter.Name} of {type.ToDisplayString()}");

			if (!isIgnored && converter is null)
			{
				return null;
			}

			entry.Parameters.Add(new ParameterModel(parameter.Name, converter?.TypeName ?? TypeName(parameter.Type), converter, matched, ExplicitName(parameter), IsNullable(parameter.Type, parameter.NullableAnnotation, parameter), parameter.HasExplicitDefaultValue ? DefaultLiteral(parameter) : null, isIgnored));

			// A member that the constructor sets, such as the property of a record, is not set again.
			if (matched is not null && !isIgnored)
			{
				matched.IsSetByConstructor = true;
			}
		}

		// The `UnsafeAccessor` class of a generic type has its type parameters, but not their constraints.
		var accessorTypes = entry.Members.Where(member => !member.IsSkipped && !member.IsSetByConstructor && member.Setter == "init").Select(member => member.Symbol.ContainingType);

		if (entry.UsesConstructorAccessor)
		{
			accessorTypes = accessorTypes.Append(type);
		}

		if (accessorTypes.Any(HasTypeParameterConstraints))
		{
			return Unsupported(type.ToDisplayString(), $"{type.ToDisplayString()} has an init accessor or a required member in a generic class whose type parameters have constraints, which the generated code cannot set; use set accessors, and [SetsRequiredMembers] on the constructor of a type with required members", location);
		}

		return entry;
	}

	/*
	Whether two types are the same in a signature, as reflection compares them when a property hides another: a type parameter of a class by its position, so the `T` of a derived class is the `T` of its base class.
	*/
	// Note: Not using SymbolEqualityComparer for this positional comparison because it distinguishes those declaring types.
	private static bool IsSameSignatureType(ITypeSymbol left, ITypeSymbol right) => (left, right) switch
	{
		(ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Type } leftParameter, ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Type } rightParameter) => Position(leftParameter) == Position(rightParameter),
		(IArrayTypeSymbol leftArray, IArrayTypeSymbol rightArray) => leftArray.Rank == rightArray.Rank && IsSameSignatureType(leftArray.ElementType, rightArray.ElementType),
		(INamedTypeSymbol { IsGenericType: true } leftNamed, INamedTypeSymbol { IsGenericType: true } rightNamed) => SameNamedSignature(leftNamed, rightNamed),
		_ => SymbolEqualityComparer.Default.Equals(left, right),
	};

	private static bool SameNamedSignature(INamedTypeSymbol left, INamedTypeSymbol right)
	{
		if (!SymbolEqualityComparer.Default.Equals(left.OriginalDefinition, right.OriginalDefinition) || left.TypeArguments.Length != right.TypeArguments.Length)
		{
			return false;
		}

		for (var index = 0; index < left.TypeArguments.Length; index++)
		{
			if (!IsSameSignatureType(left.TypeArguments[index], right.TypeArguments[index]))
			{
				return false;
			}
		}

		return (left.ContainingType, right.ContainingType) switch
		{
			(null, null) => true,
			({ } leftContainer, { } rightContainer) => SameNamedSignature(leftContainer, rightContainer),
			_ => false,
		};
	}

	/*
	The position of a class's type parameter as the runtime numbers it, after those of the classes around a nested class, so the `U` of `Outer<T>.Inner<U>` is at 1.
	*/
	private static int Position(ITypeParameterSymbol parameter)
	{
		var position = parameter.Ordinal;

		for (var outer = parameter.DeclaringType?.ContainingType; outer is not null; outer = outer.ContainingType)
		{
			position += outer.TypeParameters.Length;
		}

		return position;
	}

	private MemberModel? CreateMember(INamedTypeSymbol type, ISymbol member, Dictionary<string, IParameterSymbol>? parametersByName)
	{
		ITypeSymbol memberType;
		NullableAnnotation annotation;
		string setter;

		switch (member)
		{
			case IPropertySymbol property when property.Parameters.IsEmpty && property.GetMethod is { DeclaredAccessibility: Accessibility.Public }:
			{
				memberType = property.Type;
				annotation = property.NullableAnnotation;
				setter = property.SetMethod is { DeclaredAccessibility: Accessibility.Public } set ? (set.IsInitOnly ? "init" : "set") : "none";
				break;
			}
			case IFieldSymbol field when !field.IsConst:
			{
				memberType = field.Type;
				annotation = field.NullableAnnotation;
				setter = field.IsReadOnly ? "none" : "set";
				break;
			}
			default:
			{
				return null;
			}
		}

		// A ref struct, such as the `Span` of a `Memory<T>`, is not a value to write, as in the reflection path.
		if (memberType.IsRefLikeType)
		{
			return null;
		}

		var location = member.Locations.FirstOrDefault();
		var converter = Resolve(memberType, location, $"The member {member.Name} of {type.ToDisplayString()}");
		var isRequired = member is IPropertySymbol { IsRequired: true } or IFieldSymbol { IsRequired: true };
		IParameterSymbol? parameterName = null;
		parametersByName?.TryGetValue(member.Name, out parameterName);
		var explicitName = ExplicitName(member) ?? (parameterName is null ? null : ExplicitName(parameterName));
		// A member that a derived class declares a member with its name over is reached through its own class, as in `((Base)value).X`.
		var isShadowed = false;

		for (var declaring = type; declaring is not null && !SymbolEqualityComparer.Default.Equals(declaring, member.ContainingType); declaring = declaring.BaseType)
		{
			if (!declaring.GetMembers(member.Name).IsEmpty)
			{
				isShadowed = true;
				break;
			}
		}

		ISymbol? declared = SymbolEqualityComparer.Default.Equals(type, member.ContainingType) ? member.OriginalDefinition : null;

		for (var declaring = type.OriginalDefinition; declaring is not null && declared is null; declaring = declaring.BaseType)
		{
			foreach (var candidate in declaring.GetMembers(member.Name))
			{
				if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, member.OriginalDefinition))
				{
					declared = candidate;
					break;
				}
			}
		}

		return new MemberModel(member, converter, explicitName, IsNullable(memberType, annotation, member, declared), isRequired, setter) { Owner = isShadowed ? TypeName(member.ContainingType) : null };
	}

	/*
	Whether a member or a parameter may be set to null, as `NullabilityInfoContext` gives it for the reflection path: a nullable value type, and a reference type that is annotated, or has no annotations, unless `[DisallowNull]` or `[AllowNull]` says otherwise.
	*/
	private static bool IsNullable(ITypeSymbol type, NullableAnnotation annotation, ISymbol symbol, ISymbol? declared = null)
	{
		// Note: Not using NullableAnnotation alone because it does not apply AllowNull/DisallowNull to the deserialization contract.
		// A member of a generic type whose type is a type parameter, such as `T Value` in `Box<string>`, has no annotation that reflection can see, so it allows null. Reflection resolves a type parameter through the base classes of the type it reads, as `declared` gives the member, so `T` is `string` in `class A : Box<string>`, but stays a type parameter in `class B<V> : Box<V>`.
		var declaredType = (declared ?? symbol.OriginalDefinition) switch
		{
			IPropertySymbol property => property.Type,
			IFieldSymbol field => field.Type,
			IParameterSymbol parameter => parameter.Type,
			_ => type,
		};

		var setterParameter = (symbol as IPropertySymbol)?.SetMethod?.Parameters.LastOrDefault();

		if (HasAttribute(symbol, "System.Diagnostics.CodeAnalysis.DisallowNullAttribute") || (setterParameter is not null && HasAttribute(setterParameter, "System.Diagnostics.CodeAnalysis.DisallowNullAttribute")))
		{
			return false;
		}

		if (type.IsValueType)
		{
			return NullableUnderlyingType(type) is not null;
		}

		if (HasAttribute(symbol, "System.Diagnostics.CodeAnalysis.AllowNullAttribute") || (setterParameter is not null && HasAttribute(setterParameter, "System.Diagnostics.CodeAnalysis.AllowNullAttribute")))
		{
			return true;
		}

		// A type parameter allows null unless its constraints say that it is not null: `notnull`, or `class` without a `?`.
		if (declaredType is ITypeParameterSymbol typeParameter && (typeParameter.NullableAnnotation != NullableAnnotation.NotAnnotated || !(typeParameter.HasNotNullConstraint || (typeParameter.HasReferenceTypeConstraint && typeParameter.ReferenceTypeConstraintNullableAnnotation != NullableAnnotation.Annotated))))
		{
			return true;
		}

		return annotation != NullableAnnotation.NotAnnotated;
	}

	/*
	The constructor that the reflection path creates a type with: the one with `[SomlConstructor]`, or the public parameterless one, or the only public one. A record's synthesized copy constructor is not public.
	*/
	private static IMethodSymbol? FindConstructor(INamedTypeSymbol type, out bool hasDuplicateMarked)
	{
		IMethodSymbol? marked = null;
		IMethodSymbol? parameterless = null;
		IMethodSymbol? only = null;
		var count = 0;
		hasDuplicateMarked = false;

		foreach (var constructor in type.InstanceConstructors)
		{
			if (constructor.DeclaredAccessibility != Accessibility.Public || (constructor.IsImplicitlyDeclared && (!constructor.Parameters.IsEmpty || type.IsValueType)))
			{
				continue;
			}

			count++;
			only = constructor;

			if (constructor.Parameters.IsEmpty)
			{
				parameterless = constructor;
			}

			if (HasAttribute(constructor, "Soml.SomlConstructorAttribute"))
			{
				hasDuplicateMarked |= marked is not null;
				marked ??= constructor;
			}
		}

		return marked ?? parameterless ?? (count == 1 ? only : null);
	}

	/*
	A parameter's default value as C# source.
	*/
	private static string DefaultLiteral(IParameterSymbol parameter)
	{
		var type = NullableUnderlyingType(parameter.Type) ?? parameter.Type;
		var value = parameter.ExplicitDefaultValue;
		// Note: Not using FormatPrimitive because it produces display text without the numeric suffixes and NaN/infinity references that valid C# source needs.

		if (value is null)
		{
			return parameter.Type.IsValueType ? "default" : "null";
		}

		if (type.TypeKind == TypeKind.Enum)
		{
			return $"({TypeName(type)})({Convert.ToString(value, CultureInfo.InvariantCulture)})";
		}

		return value switch
		{
			DateTime dateTime => $"new global::System.DateTime({dateTime.Ticks.ToString(CultureInfo.InvariantCulture)}L)",
			string text => Literal(text),
			char character => SymbolDisplay.FormatLiteral(character, quote: true),
			bool boolean => boolean ? "true" : "false",
			float number when float.IsNaN(number) => "float.NaN",
			float number when float.IsPositiveInfinity(number) => "float.PositiveInfinity",
			float number when float.IsNegativeInfinity(number) => "float.NegativeInfinity",
			float number => $"{number.ToString("R", CultureInfo.InvariantCulture)}F",
			double number when double.IsNaN(number) => "double.NaN",
			double number when double.IsPositiveInfinity(number) => "double.PositiveInfinity",
			double number when double.IsNegativeInfinity(number) => "double.NegativeInfinity",
			double number => $"{number.ToString("R", CultureInfo.InvariantCulture)}D",
			decimal number => $"{number.ToString(CultureInfo.InvariantCulture)}M",
			long number => $"{number.ToString(CultureInfo.InvariantCulture)}L",
			ulong number => $"{number.ToString(CultureInfo.InvariantCulture)}UL",
			uint number => $"{number.ToString(CultureInfo.InvariantCulture)}U",
			int number => number.ToString(CultureInfo.InvariantCulture),
			_ => $"({TypeName(type)})({Convert.ToString(value, CultureInfo.InvariantCulture)})",
		};
	}

	/*
	The expression of a converter, from the context, or from a converter class nested in it.
	*/
	private static string Expression(Entry entry, bool isNested) => entry.Kind == EntryKind.Scalar ? entry.Expression! : isNested ? $"_context.Converter{entry.Index}" : $"Converter{entry.Index}";

	private string WriteContext(List<(string PropertyName, Entry Entry, Location? Location)> roots)
	{
		var writer = new CodeWriter(_cancellationToken);
		writer.Line("// <auto-generated/>");
		writer.Line("#nullable enable annotations");
		writer.Line("#nullable disable warnings");
		writer.Line("#pragma warning disable CS0612, CS0618");
		writer.Line();

		if (_context.ContainingNamespace is { IsGlobalNamespace: false } containingNamespace)
		{
			writer.Open($"namespace {containingNamespace.ToDisplayString()}");
		}

		var containers = new List<INamedTypeSymbol>();

		for (var container = _context.ContainingType; container is not null; container = container.ContainingType)
		{
			containers.Add(container);
		}

		containers.Reverse();

		foreach (var container in containers)
		{
			writer.Open($"partial {Keyword(container)} {Identifier(container.Name)}");
		}

		var name = Identifier(_context.Name);
		writer.Open($"partial class {name}");
		writer.Summary($"The context with the default options.");
		writer.Open($"public static {name} Default");
		writer.Line($"get => field ??= new {name}();");
		writer.Close();
		writer.Line();
		writer.Summary("Creates a context with the default options.");
		writer.Line($"public {name}() : base(null)");
		writer.Line("{");
		writer.Line("}");
		writer.Line();
		writer.Summary("Creates a context with options.");
		writer.Line($"public {name}(global::Soml.SomlSerializerOptions options) : base(options)");
		writer.Line("{");
		writer.Line("}");

		for (var index = 0; index < roots.Count; index++)
		{
			var (propertyName, entry, _) = roots[index];
			var typeName = entry.TypeName;
			var accessibility = IsPublic(entry.Type) ? "public" : "internal";
			writer.Line();
			writer.Summary($"How to read and write {SecurityElementEscape(entry.Type.ToDisplayString())}.");
			writer.Open($"{accessibility} global::Soml.Serialization.SomlTypeInfo<{typeName}> {Identifier(propertyName)}");
			writer.Line($"get => field ??= new global::Soml.Serialization.SomlTypeInfo<{typeName}>(this, () => {Expression(entry, isNested: false)});");
			writer.Close();
		}

		foreach (var entry in roots.Count == 0 ? [] : _ordered)
		{
			WriteConverterProperty(writer, entry);

			if (entry.Kind == EntryKind.Object)
			{
				WriteObjectConverter(writer, entry);
			}
		}

		writer.Close();

		foreach (var _ in containers)
		{
			writer.Close();
		}

		if (_context.ContainingNamespace is { IsGlobalNamespace: false })
		{
			writer.Close();
		}

		return writer.ToString();
	}

	/*
	Whether a type can be part of a public property: the type, the types it is nested in, and its type arguments are public. Otherwise the property is internal.
	*/
	private static bool IsPublic(ITypeSymbol type) => type switch
	{
		IArrayTypeSymbol array => IsPublic(array.ElementType),
		INamedTypeSymbol named => named.DeclaredAccessibility == Accessibility.Public && (named.ContainingType is null || IsPublic(named.ContainingType)) && named.TypeArguments.All(IsPublic),
		_ => true,
	};

	// Note: Not using Compilation.IsSymbolAccessibleWithin because it increased allocations and slowed fresh-driver generation in local tests.
	private static bool IsInternalOrPublic(ITypeSymbol type) => type switch
	{
		IArrayTypeSymbol array => IsInternalOrPublic(array.ElementType),
		INamedTypeSymbol named => named.DeclaredAccessibility is Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal or Accessibility.NotApplicable && (named.ContainingType is null || IsInternalOrPublic(named.ContainingType)) && named.TypeArguments.All(IsInternalOrPublic),
		_ => true,
	};

	// Note: Not using SecurityElement.Escape because it allocated more for generic type names on .NET 10 ARM64; this output is XML text rather than an attribute value.
	private static string SecurityElementEscape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

	private static string Keyword(INamedTypeSymbol type) => (type.IsRecord, type.TypeKind) switch
	{
		(true, TypeKind.Struct) => "record struct",
		(true, _) => "record",
		(_, TypeKind.Struct) => "struct",
		(_, TypeKind.Interface) => "interface",
		_ => "class",
	};

	private static void WriteConverterProperty(CodeWriter writer, Entry entry)
	{
		var typeName = entry.TypeName;
		var converterType = $"global::Soml.Serialization.SomlConverter<{typeName}>";
		string creation;

		switch (entry.Kind)
		{
			case EntryKind.Nullable:
			{
				creation = $"{Converters}.Nullable({Expression(entry.Item!, isNested: false)})";
				break;
			}
			case EntryKind.Enum:
			{
				var members = entry.Type.GetMembers().OfType<IFieldSymbol>().Where(member => member.IsConst && !IsIgnored(member)).Select(member => $"({Literal(member.Name)}, {(ExplicitName(member) is { } explicitName ? Literal(explicitName) : "null")}, {typeName}.{Identifier(member.Name)})");
				var isFlags = HasAttribute(entry.Type, "System.FlagsAttribute") ? "true" : "false";
				creation = $"{Converters}.Enum<{typeName}>(Options, [{string.Join(", ", members)}], isFlags: {isFlags})";
				break;
			}
			case EntryKind.Collection:
			{
				var itemName = entry.Item!.TypeName;
				creation = entry.Type is IArrayTypeSymbol
					? $"{Converters}.ArrayOf<{itemName}>({Expression(entry.Item!, isNested: false)}, isItemNullable: {(CanBeNull(entry.Item!.Type) ? "true" : "false")})"
					: $"{Converters}.Collection<{typeName}, {itemName}>({Expression(entry.Item!, isNested: false)}, isItemNullable: {(CanBeNull(entry.Item!.Type) ? "true" : "false")}, isSet: {(entry.IsSet ? "true" : "false")}, {entry.Create})";
				break;
			}
			case EntryKind.Dictionary:
			{
				var valueName = entry.Item!.TypeName;
				creation = $"{Converters}.Dictionary<{typeName}, string, {valueName}>({Converters}.String, null, {Expression(entry.Item!, isNested: false)}, isValueNullable: {(CanBeNull(entry.Item!.Type) ? "true" : "false")}, {entry.Create})";
				break;
			}
			default:
			{
				creation = $"new Converter{entry.Index}Implementation(this)";
				break;
			}
		}

		writer.Line();
		writer.Open($"internal {converterType} Converter{entry.Index}");
		writer.Line($"get => field ??= {creation};");
		writer.Close();
	}

	private void WriteObjectConverter(CodeWriter writer, Entry entry)
	{
		var type = (INamedTypeSymbol)entry.Type;
		var typeName = entry.TypeName;
		var typeOf = $"typeof({typeName})";
		var className = $"Converter{entry.Index}Implementation";
		// Code is emitted only after every member has a converter.
		var members = entry.Members;
		var accessor = entry.UsesConstructorAccessor ? Accessor(type, $"Accessors{entry.Index}") : default;

		for (var index = 0; index < members.Count; index++)
		{
			members[index].Index = index;

			if (members[index] is { Setter: "init", IsSetByConstructor: false } member)
			{
				member.SetterAccessor = Accessor(member.Symbol.ContainingType, $"Setter{entry.Index}x{index}");
			}
		}

		writer.Line();
		writer.Open($"private sealed class {className} : global::Soml.Serialization.SomlConverter<{typeName}>");
		var needsContext = members.Any(member => member.Converter!.Kind != EntryKind.Scalar) || entry.Parameters.Any(parameter => parameter.Converter is { Kind: not EntryKind.Scalar });

		if (needsContext)
		{
			writer.Line($"private readonly {Identifier(_context.Name)} _context;");
		}

		for (var index = 0; index < members.Count; index++)
		{
			var explicitName = members[index].ExplicitName;
			writer.Line(explicitName is null ? $"private readonly string _key{index};" : $"private const string _key{index} = {Literal(explicitName)};");
		}

		for (var index = 0; index < entry.Parameters.Count; index++)
		{
			if (!entry.Parameters[index].IsIgnored && entry.Parameters[index].Member is null)
			{
				var explicitName = entry.Parameters[index].ExplicitName;
				writer.Line(explicitName is null ? $"private readonly string _parameterKey{index};" : $"private const string _parameterKey{index} = {Literal(explicitName)};");
			}
		}

		var readParameterKeys = ReadParameterKeys(entry);
		var hasReadParameters = readParameterKeys.Count > 0;

		for (var index = 0; index < members.Count; index++)
		{
			if (HasParameterKeyCheck(members[index], hasReadParameters))
			{
				writer.Line($"private readonly bool _isSetByConstructor{index};");
			}
		}

		if (members.Count > 0 || hasReadParameters)
		{
			writer.Line("private readonly global::System.Collections.Generic.HashSet<string>? _knownKeys;");
		}
		writer.Line();
		writer.Open($"public {className}({Identifier(_context.Name)} context)");
		if (needsContext)
		{
			writer.Line("_context = context;");
		}
		writer.Line("var options = context.Options;");

		for (var index = 0; index < members.Count; index++)
		{
			var member = members[index];
			if (member.ExplicitName is null)
			{
				writer.Line($"_key{index} = global::Soml.Serialization.SomlMetadata.Key(options, {Literal(member.Name)}, null);");
			}
		}

		if (members.Count > 1 && NeedsKeyCheck(members.Select(member => member.ExplicitName)))
		{
			writer.Line($"global::Soml.Serialization.SomlMetadata.CheckUniqueKeys({typeOf}, [{string.Join(", ", members.Select(member => Literal(member.Name)))}], [{string.Join(", ", members.Select((_, index) => $"_key{index}"))}]);");
		}

		for (var index = 0; index < entry.Parameters.Count; index++)
		{
			var parameter = entry.Parameters[index];

			if (!parameter.IsIgnored && parameter.Member is null && parameter.ExplicitName is null)
			{
				writer.Line($"_parameterKey{index} = global::Soml.Serialization.SomlMetadata.Key(options, {Literal(parameter.Name)}, null);");
			}
		}

		// A member whose key a constructor parameter reads under another name, as with `[SomlName]`, is set by the constructor too, as reflection compares the keys. The keys depend on the options, so they are compared here.
		for (var index = 0; index < members.Count; index++)
		{
			if (HasParameterKeyCheck(members[index], hasReadParameters))
			{
				writer.Line($"_isSetByConstructor{index} = {string.Join(" || ", readParameterKeys.Select(key => $"_key{index} == {key}"))};");
			}
		}

		if (readParameterKeys.Count > 1 && NeedsKeyCheck(entry.Parameters.Where(parameter => !parameter.IsIgnored).Select(parameter => parameter.Member is { } member ? member.ExplicitName : parameter.ExplicitName)))
		{
			writer.Line($"global::Soml.Serialization.SomlMetadata.CheckUniqueParameterKeys({typeOf}, [{string.Join(", ", readParameterKeys)}]);");
		}

		var extraKeys = entry.Parameters.Select((parameter, index) => (parameter, index)).Where(item => !item.parameter.IsIgnored && item.parameter.Member is null).Select(item => $"_parameterKey{item.index}").ToList();
		var knownKeys = members.Select((_, index) => $"_key{index}").Concat(extraKeys);
		if (members.Count + extraKeys.Count > 0)
		{
			writer.Open("if (options.RejectUnknownKeys)");
			writer.Line($"_knownKeys = new global::System.Collections.Generic.HashSet<string>({members.Count + extraKeys.Count}, global::System.StringComparer.Ordinal) {{ {string.Join(", ", knownKeys)} }};");
			writer.Close();
		}
		writer.Close();
		writer.Line();
		writer.Line("public override string Expected => \"an object\";");
		writer.Line();
		WriteRead(writer, entry, members, accessor, hasReadParameters);
		writer.Line();
		WriteWrite(writer, entry, members);

		WriteAccessors(writer, entry, members, accessor);

		writer.Close();
	}

	private static bool NeedsKeyCheck(IEnumerable<string?> names)
	{
		var seen = new HashSet<string>(StringComparer.Ordinal);

		foreach (var name in names)
		{
			if (name is null || !seen.Add(name))
			{
				return true;
			}
		}

		return false;
	}

	/*
	The expressions of the keys of the constructor parameters that are read, reusing the key of a matched member.
	*/
	private static List<string> ReadParameterKeys(Entry entry)
	{
		var keys = new List<string>(entry.Parameters.Count);

		for (var index = 0; index < entry.Parameters.Count; index++)
		{
			if (!entry.Parameters[index].IsIgnored)
			{
				keys.Add(ParameterKey(entry, index));
			}
		}

		return keys;
	}

	/*
	The field of the key of a constructor parameter that is read: a matching member's `_key0`, or its own `_parameterKey0`.
	*/
	private static string ParameterKey(Entry entry, int index)
	{
		var parameter = entry.Parameters[index];
		return parameter.Member is { } member ? $"_key{member.Index}" : $"_parameterKey{index}";
	}

	/*
	Whether a member needs the check, at run time, of whether a constructor parameter reads its key under another name.
	*/
	private static bool HasParameterKeyCheck(MemberModel member, bool hasReadParameters) => hasReadParameters && member.Setter != "none" && !member.IsSetByConstructor;

	/*
	A class of `UnsafeAccessor` methods for a type: how to call it, how to declare it, and the type its methods take. For a generic type, the class must be generic over the type's own type parameters, as the runtime does not find the member of a closed generic type otherwise.
	*/
	private static (string Call, string Declaration, string Target) Accessor(INamedTypeSymbol type, string name)
	{
		if (!type.IsGenericType)
		{
			return (name, name, TypeName(type));
		}

		var parameters = AllTypeParameters(type.OriginalDefinition);
		return ($"{name}<{string.Join(", ", AllTypeArguments(type).Select(TypeName))}>", $"{name}<{string.Join(", ", parameters.Select(parameter => Identifier(parameter.Name)))}>", TypeName(type.OriginalDefinition));
	}

	private static bool HasTypeParameterConstraints(INamedTypeSymbol type)
	{
		for (var current = type; current is not null; current = current.ContainingType)
		{
			foreach (var parameter in current.TypeParameters)
			{
				if (HasConstraint(parameter))
				{
					return true;
				}
			}
		}

		return false;
	}

	private static bool HasConstraint(ITypeParameterSymbol parameter) => parameter.HasReferenceTypeConstraint || parameter.HasValueTypeConstraint || parameter.HasUnmanagedTypeConstraint || parameter.HasNotNullConstraint || parameter.HasConstructorConstraint || !parameter.ConstraintTypes.IsEmpty;

	private static List<ITypeParameterSymbol> AllTypeParameters(INamedTypeSymbol type)
	{
		var result = new List<ITypeParameterSymbol>();

		for (var current = type; current is not null; current = current.ContainingType)
		{
			for (var index = current.TypeParameters.Length - 1; index >= 0; index--)
			{
				result.Add(current.TypeParameters[index]);
			}
		}

		result.Reverse();
		return result;
	}

	private static List<ITypeSymbol> AllTypeArguments(INamedTypeSymbol type)
	{
		var result = new List<ITypeSymbol>();

		for (var current = type; current is not null; current = current.ContainingType)
		{
			for (var index = current.TypeArguments.Length - 1; index >= 0; index--)
			{
				result.Add(current.TypeArguments[index]);
			}
		}

		result.Reverse();
		return result;
	}

	private static void WriteRead(CodeWriter writer, Entry entry, List<MemberModel> members, (string Call, string Declaration, string Target) accessor, bool hasReadParameters)
	{
		var type = (INamedTypeSymbol)entry.Type;
		var typeName = entry.TypeName;
		var typeOf = $"typeof({typeName})";
		var useConstructorAccessor = entry.UsesConstructorAccessor;

		writer.Open($"public override {typeName} Read(global::Soml.SomlValue value, global::Soml.Serialization.SomlConverterState state)");
		writer.Line($"var members = state.ReadObject(value, {typeOf});");

		if (!entry.HasConstructor)
		{
			writer.Line($"throw global::Soml.Serialization.SomlMetadata.NoConstructor(state, {typeOf});");
			writer.Close();
			return;
		}

		if (members.Count == 0 && !hasReadParameters)
		{
			writer.Line($"state.CheckUnknownKeys(members, global::System.Array.Empty<string>(), {typeOf});");
		}
		else
		{
			writer.Open("if (_knownKeys is not null)");
			writer.Line($"state.CheckUnknownKeys(members, _knownKeys, {typeOf});");
			writer.Close();
		}

		for (var index = 0; index < entry.Parameters.Count; index++)
		{
			var parameter = entry.Parameters[index];
			writer.Line($"{parameter.TypeName} argument{index};");

			if (parameter.IsIgnored)
			{
				writer.Line($"argument{index} = {parameter.DefaultLiteral ?? "default"};");
				continue;
			}

			writer.Open($"if (members.TryGetValue({ParameterKey(entry, index)}, out var parameterValue{index}))");
			writer.Line($"state.Push({ParameterKey(entry, index)});");
			writer.Line($"argument{index} = state.Read(parameterValue{index}, {Expression(parameter.Converter!, isNested: true)}, isNullable: {(parameter.IsNullable ? "true" : "false")});");
			writer.Line("state.Pop();");
			writer.Close();
			writer.Open("else");

			if (parameter.DefaultLiteral is not null)
			{
				writer.Line($"argument{index} = {parameter.DefaultLiteral};");
			}
			else if (parameter.IsNullable)
			{
				writer.Line($"argument{index} = default;");
			}
			else
			{
				writer.Line($"throw state.MissingKey({ParameterKey(entry, index)}, {typeOf});");
			}

			writer.Close();
		}

		var arguments = string.Join(", ", entry.Parameters.Select((_, index) => $"argument{index}"));
		writer.Line($"{typeName} result;");
		// Only the type's own code runs in the `try`, so every exception from it, a `SomlException` too, gets the path, as reflection wraps it in a `TargetInvocationException`. The same holds for the setter and the getter.
		if (entry.Constructor is null)
		{
			writer.Line($"result = default({typeName});");
		}
		else
		{
			writer.Open("try");
			writer.Line(useConstructorAccessor ? $"result = {accessor.Call}.Create({arguments});" : $"result = new {typeName}({arguments});");
			writer.Close();
			writer.Open("catch (global::System.Exception exception)");
			writer.Line($"throw state.ConstructorFailed({typeOf}, exception);");
			writer.Close();
		}

		for (var index = 0; index < members.Count; index++)
		{
			var member = members[index];

			var readsRequiredMember = member.IsRequired && !member.IsSetByConstructor && member.Setter != "none";

			if (readsRequiredMember)
			{
				writer.Line($"var hasMember{index} = members.TryGetValue(_key{index}, out var memberValue{index});");
			}

			if (member.IsRequired)
			{
				writer.Open(readsRequiredMember ? $"if (!hasMember{index})" : $"if (!members.ContainsKey(_key{index}))");
				writer.Line($"throw state.MissingKey(_key{index}, {typeOf});");
				writer.Close();
			}

			if (member.IsSetByConstructor)
			{
				continue;
			}

			var guard = HasParameterKeyCheck(member, hasReadParameters) ? $"!_isSetByConstructor{index} && " : "";

			if (member.Setter == "none")
			{
				// A member without a public setter, such as a computed property, is written but not read. Required members are checked above.
				continue;
			}

			writer.Open(readsRequiredMember ? $"if ({guard}hasMember{index})" : $"if ({guard}members.TryGetValue(_key{index}, out var memberValue{index}))");
			writer.Line($"state.Push(_key{index});");
			writer.Line($"var read{index} = state.Read(memberValue{index}, {Expression(member.Converter!, isNested: true)}, isNullable: {(member.IsNullable ? "true" : "false")});");
			var invokesSetter = member.Symbol is IPropertySymbol;

			if (invokesSetter)
			{
				writer.Open("try");
			}

			if (member.Setter == "init")
			{
				writer.Line($"{member.SetterAccessor.Call}.Set({(type.IsValueType ? "ref " : "")}result, read{index});");
			}
			else
			{
				writer.Line($"{member.On("result")} = read{index};");
			}

			if (invokesSetter)
			{
				writer.Close();
				writer.Open("catch (global::System.Exception exception)");
				writer.Line($"throw state.SetterFailed({Literal(member.Name)}, {typeOf}, exception);");
				writer.Close();
			}
			writer.Line("state.Pop();");
			writer.Close();
		}

		writer.Line("return result;");
		writer.Close();
	}

	private static void WriteWrite(CodeWriter writer, Entry entry, List<MemberModel> members)
	{
		var typeName = entry.TypeName;
		var typeOf = $"typeof({typeName})";

		writer.Open($"public override global::Soml.SomlValue Write({typeName} value, global::Soml.Serialization.SomlConverterState state)");
		writer.Line($"var result = new global::Soml.SomlObject({members.Count});");

		if (members.Any(member => CanBeNull(member.Converter!.Type)))
		{
			writer.Line("var omitNullMembers = state.Options.OmitNullMembers;");
		}

		for (var index = 0; index < members.Count; index++)
		{
			var member = members[index];
			if (member.Symbol is IFieldSymbol)
			{
				writer.Line($"var memberValue{index} = {member.On("value")};");
			}
			else
			{
				writer.Line($"{member.Converter!.TypeName} memberValue{index};");
				writer.Open("try");
				writer.Line($"memberValue{index} = {member.On("value")};");
				writer.Close();
				writer.Open("catch (global::System.Exception exception)");
				writer.Line($"state.Push(_key{index});");
				writer.Line($"throw state.GetterFailed({Literal(member.Name)}, {typeOf}, exception);");
				writer.Close();
			}

			if (CanBeNull(member.Converter!.Type))
			{
				writer.Open($"if (memberValue{index} is not null || !omitNullMembers)");
			}

			writer.Line($"state.Push(_key{index});");
			writer.Line($"global::Soml.Serialization.SomlMetadata.AddMember(result, _key{index}, state.Write(memberValue{index}, {Expression(member.Converter!, isNested: true)}));");
			writer.Line("state.Pop();");

			if (CanBeNull(member.Converter!.Type))
			{
				writer.Close();
			}
		}

		writer.Line("return result;");
		writer.Close();
	}

	private static void WriteAccessors(CodeWriter writer, Entry entry, List<MemberModel> members, (string Call, string Declaration, string Target) accessor)
	{
		var type = (INamedTypeSymbol)entry.Type;

		if (entry.UsesConstructorAccessor)
		{
			var constructor = entry.Constructor!.OriginalDefinition;
			var parameters = string.Join(", ", constructor.Parameters.Select((parameter, index) => $"{TypeName(parameter.Type)} argument{index}"));
			writer.Line();
			writer.Open($"private static class {accessor.Declaration}");
			writer.Line("[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Constructor)]");
			writer.Line($"public static extern {accessor.Target} Create({parameters});");
			writer.Close();
		}

		for (var index = 0; index < members.Count; index++)
		{
			var member = members[index];

			if (member.Setter != "init" || member.IsSetByConstructor)
			{
				continue;
			}

			var setter = member.SetterAccessor;
			var property = (IPropertySymbol)member.Symbol.OriginalDefinition;
			writer.Line();
			writer.Open($"private static class {setter.Declaration}");
			writer.Line($"[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Method, Name = {Literal($"set_{member.Name}")})]");
			writer.Line($"public static extern void Set({(type.IsValueType ? "ref " : "")}{setter.Target} target, {TypeName(property.Type)} value);");
			writer.Close();
		}
	}

	private enum EntryKind
	{
		Scalar,
		Nullable,
		Enum,
		Collection,
		Dictionary,
		Object,
	}

	private sealed class Entry(ITypeSymbol type, EntryKind kind)
	{
		public ITypeSymbol Type { get; } = type;

		public string TypeName { get; } = Emitter.TypeName(type);

		public EntryKind Kind { get; } = kind;

		public int Index { get; set; }

		public string? Expression { get; set; }

		public Entry? Item { get; set; }

		public bool IsSet { get; set; }

		public string? Create { get; set; }

		public IMethodSymbol? Constructor { get; set; }

		public bool HasConstructor => Constructor is not null || Type.IsValueType;

		/*
		A constructor that leaves required members unset, and an `init` accessor, can only be called through `UnsafeAccessor`, which works with trimming and Native AOT.
		*/
		public bool UsesConstructorAccessor { get; set; }

		public List<MemberModel> Members
		{
			get => field ??= [];
		}

		public List<ParameterModel> Parameters
		{
			get => field ??= [];
		}
	}

	private sealed class MemberModel(ISymbol symbol, Entry? converter, string? explicitName, bool isNullable, bool isRequired, string setter)
	{
		public int Index { get; set; }

		public string Name => symbol.Name;

		public Entry? Converter { get; } = converter;

		public string? ExplicitName { get; } = explicitName;

		public bool IsNullable { get; } = isNullable;

		public bool IsRequired { get; } = isRequired;

		/*
		`set`, `init`, or `none`.
		*/
		public string Setter { get; } = setter;

		public bool IsSetByConstructor { get; set; }

		/*
		The accessor of an `init` setter, which must name the type that declares the property, as the runtime looks for the method on that type only.
		*/
		public (string Call, string Declaration, string Target) SetterAccessor { get; set; }

		public ISymbol Symbol => symbol;

		/*
		The class to reach the member through, when a derived class hides its name, or `null`.
		*/
		public string? Owner { get; init; }

		/*
		The member on a value, as in `value.X`, or `((Base)value).X` when a derived class hides its name.
		*/
		public string On(string target) => $"{(Owner is null ? target : $"(({Owner}){target})")}.{Identifier(Name)}";

		/*
		Whether the member's type is not supported, which a diagnostic reports.
		*/
		public bool IsSkipped => Converter is null;
	}

	private sealed record ParameterModel(string Name, string TypeName, Entry? Converter, MemberModel? Member, string? ExplicitName, bool IsNullable, string? DefaultLiteral, bool IsIgnored);

	private sealed class CodeWriter(CancellationToken cancellationToken)
	{
		private readonly StringBuilder _builder = new();
		private int _level;

		public void Line(string text = "")
		{
			cancellationToken.ThrowIfCancellationRequested();

			if (text.Length > 0)
			{
				_builder.Append('\t', _level);
			}

			_builder.Append(text).Append('\n');
		}

		public void Summary(string text)
		{
			Line("/// <summary>");
			Line($"/// {text}");
			Line("/// </summary>");
		}

		public void Open(string header)
		{
			if (header.Length > 0)
			{
				Line(header);
			}

			Line("{");
			_level++;
		}

		public void Close()
		{
			_level--;
			Line("}");
		}

		public override string ToString() => _builder.ToString();
	}
}
