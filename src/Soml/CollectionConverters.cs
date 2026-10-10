using System.Collections;
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Soml.Serialization;

namespace Soml;

/*
Builds a collection from a `List<T>` or `Dictionary<TKey, TValue>` through assignment, a frozen collection factory, an items constructor, default construction and `Add`, or a `CreateRange()` factory, in that order.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal static class CollectionFactory
{
	private static MethodInfo FrozenSetFactory => field ??= typeof(FrozenSet).GetMethods().Single(method => method.Name == nameof(FrozenSet.ToFrozenSet) && method.GetParameters().Length == 2);
	private static MethodInfo FrozenDictionaryFactory => field ??= typeof(FrozenDictionary).GetMethods().Single(method => method.Name == nameof(FrozenDictionary.ToFrozenDictionary) && method.GetGenericArguments().Length == 2 && method.GetParameters() is [_, var comparer] && comparer.ParameterType.IsGenericType && comparer.ParameterType.GetGenericTypeDefinition() == typeof(IEqualityComparer<>));

	/*
	A function that makes a value of `type` from the collected items, of `collectedType`, or `null` when there is no way to. An exception from the collection's own code is thrown as it is, not wrapped in a `TargetInvocationException`.
	*/
	public static Func<object, object>? Find(Type type, Type collectedType, Type itemsInterface, Type[] typeArguments)
	{
		if (type.IsAssignableFrom(collectedType))
		{
			return static items => items;
		}

		// The frozen collections are abstract, and made by an extension method.
		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FrozenSet<>))
		{
			var method = FrozenSetFactory.MakeGenericMethod(typeArguments);
			var invoker = MethodInvoker.Create(method);
			return items => invoker.Invoke(null, items, null)!;
		}

		if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(FrozenDictionary<,>))
		{
			var method = FrozenDictionaryFactory.MakeGenericMethod(typeArguments);
			var invoker = MethodInvoker.Create(method);
			return items => invoker.Invoke(null, items, null)!;
		}

		if (type.IsAbstract)
		{
			return null;
		}

		// A constructor that takes the items, as `ReadOnlyCollection<T>`, `SortedDictionary<TKey, TValue>`, `Queue<T>`, and `HashSet<T>` have.
		// Note: Not using Type.GetConstructor with the default binder because its most-specific overload selection and acceptance of object differ from the generated path's first-compatible rule.
		foreach (var constructor in type.GetConstructors())
		{
			if (constructor.GetParameters() is [var parameter] && parameter.ParameterType.IsAssignableFrom(collectedType) && parameter.ParameterType != typeof(object))
			{
				var invoker = ConstructorInvoker.Create(constructor);
				return items => invoker.Invoke(items);
			}
		}

		// Default construction and a non-generic instance `Add` with the exact item types, as a collection type of your own may have.
		var add = type.GetMethod("Add", 0, BindingFlags.Public | BindingFlags.Instance | BindingFlags.ExactBinding, null, typeArguments, null);
		var parameterless = type.GetConstructor(Type.EmptyTypes);

		if ((parameterless is not null || type.IsValueType) && add is not null && (add.ReturnType == typeof(void) || add.ReturnType == typeof(bool)))
		{
			var create = parameterless is null ? null : ConstructorInvoker.Create(parameterless);
			var append = MethodInvoker.Create(add);

			return items =>
			{
				var result = create is null ? Activator.CreateInstance(type)! : create.Invoke();

				if (items is IDictionary dictionary)
				{
					// `IDictionary` enumerates its entries as `DictionaryEntry`, where `IEnumerable` would box a `KeyValuePair`.
					var entries = dictionary.GetEnumerator();

					while (entries.MoveNext())
					{
						append.Invoke(result, entries.Key, entries.Value);
					}
				}
				else
				{
					foreach (var item in (IEnumerable)items)
					{
						append.Invoke(result, item);
					}
				}

				return result;
			};
		}

		// `CreateRange()` of the static class with the type's name, as `ImmutableArray.CreateRange<T>()`.
		// A nested type, such as `ImmutableArray<T>.Builder`, has no factory class of its own name.
		var backtick = type.Name.IndexOf('`');
		var name = backtick == -1 ? type.Name : type.Name[..backtick];
		var factoryType = type.IsNested ? null : type.Assembly.GetType(string.IsNullOrEmpty(type.Namespace) ? name : $"{type.Namespace}.{name}");

		foreach (var method in factoryType?.GetMethods(BindingFlags.Public | BindingFlags.Static) ?? [])
		{
			if (method.Name != "CreateRange" || !method.IsGenericMethodDefinition || method.GetGenericArguments().Length != typeArguments.Length || method.GetParameters() is not [var parameter] || !parameter.ParameterType.IsGenericType || parameter.ParameterType.GetGenericTypeDefinition() != itemsInterface.GetGenericTypeDefinition())
			{
				continue;
			}

			var factory = method.MakeGenericMethod(typeArguments);

			if (factory.GetParameters()[0].ParameterType.IsAssignableFrom(collectedType) && type.IsAssignableFrom(factory.ReturnType))
			{
				var invoker = MethodInvoker.Create(factory);
				return items => invoker.Invoke(null, items)!;
			}
		}

		return null;
	}
}

/*
An array, a list, a set, or another collection, as a SOML array, through the typed `CollectionConverter<TCollection, TItem>` of the generated code.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal static class ListConverter
{
	private static readonly MethodInfo Factory = typeof(ListConverter).GetMethod(nameof(Create), BindingFlags.NonPublic | BindingFlags.Static)!;

	public static Converter? TryCreate(Type type, Type[]? interfaces = null)
	{
		if (type == typeof(string))
		{
			return null;
		}

		if (type.IsArray)
		{
			return (Converter)Factory.MakeGenericMethod(type, type.GetElementType()!).Invoke(null, BindingFlags.DoNotWrapExceptions, null, [false, null], null)!;
		}

		interfaces ??= type.GetInterfaces();
		var enumerable = Converter.FindInterface(type, interfaces, typeof(IEnumerable<>));
		var elementType = enumerable?.GetGenericArguments()[0];

		if (elementType is null)
		{
			return null;
		}

		var isSet = Converter.FindInterface(type, interfaces, typeof(ISet<>), typeof(IReadOnlySet<>)) is not null;
		var listType = typeof(List<>).MakeGenericType(elementType);
		var itemsInterface = typeof(IEnumerable<>).MakeGenericType(elementType);

		// An array is made by the typed converter, and an `ISet<T>` or an `IReadOnlySet<T>` is a `HashSet<T>`.
		var create = CollectionFactory.Find(isSet && type.IsInterface ? typeof(HashSet<>).MakeGenericType(elementType) : type, listType, itemsInterface, [elementType]);
		return (Converter)Factory.MakeGenericMethod(type, elementType).Invoke(null, BindingFlags.DoNotWrapExceptions, null, [isSet, create], null)!;
	}

	private static TypedConverter<TCollection> Create<TCollection, TItem>(bool isSet, Func<object, object>? create) where TCollection : IEnumerable<TItem>
	{
		if (typeof(TCollection) == typeof(TItem[]))
		{
			return new TypedConverter<TCollection>((SomlConverter<TCollection>)(object)SomlConverters.ArrayOf(new ItemConverter<TItem>(), Converter.CanBeNull(typeof(TItem))));
		}

		Func<List<TItem>, TCollection> createCollection = create is null
			? _ => throw new NotSupportedException("it has no public parameterless constructor with an Add method, no constructor that takes the items, and no CreateRange() factory")
			: items => (TCollection)create(items);

		return new TypedConverter<TCollection>(new CollectionConverter<TCollection, TItem>(new ItemConverter<TItem>(), Converter.CanBeNull(typeof(TItem)), isSet, createCollection));
	}
}

/*
A dictionary as a SOML object, through the typed `DictionaryConverter<TDictionary, TKey, TValue>` of the generated code, with the typed converter of the key type.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal static class DictionaryConverter
{
	private static readonly MethodInfo Factory = typeof(DictionaryConverter).GetMethod(nameof(Create), BindingFlags.NonPublic | BindingFlags.Static)!;
	private static readonly MethodInfo NullableFactory = typeof(SomlConverters).GetMethod(nameof(SomlConverters.Nullable))!;

	public static Converter? TryCreate(Type type, SomlSerializerOptions options, Type[] interfaces)
	{
		var dictionary = Converter.FindInterface(type, interfaces, typeof(IDictionary<,>), typeof(IReadOnlyDictionary<,>));

		if (dictionary is null)
		{
			return null;
		}

		var arguments = dictionary.GetGenericArguments();
		var pairType = typeof(KeyValuePair<,>).MakeGenericType(arguments);
		var create = CollectionFactory.Find(type, typeof(Dictionary<,>).MakeGenericType(arguments), typeof(IEnumerable<>).MakeGenericType(pairType), arguments);
		return (Converter)Factory.MakeGenericMethod([type, .. arguments]).Invoke(null, BindingFlags.DoNotWrapExceptions, null, [options, create], null)!;
	}

	private static TypedConverter<TDictionary> Create<TDictionary, TKey, TValue>(SomlSerializerOptions options, Func<object, object>? create) where TDictionary : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
	{
		Func<Dictionary<TKey, TValue>, TDictionary> createDictionary = create is null
			? _ => throw new NotSupportedException("it has no public parameterless constructor with an Add method, no constructor that takes the members, and no CreateRange() factory")
			: members => (TDictionary)create(members);

		// A type other than an enum that reads an object, an array, or any value, such as a class, is not a key type, so its converter is not made, and `DictionaryKeys<TKey>` refuses its keys.
		var underlyingType = Nullable.GetUnderlyingType(typeof(TKey)) ?? typeof(TKey);
		var key = !underlyingType.IsEnum && Converter.DescribeExpected(underlyingType) is "an object" or "an array" or "a value" ? null : KeyConverter<TKey>(options.GetConverter(typeof(TKey)).Typed);
		var needsParsableKey = key?.EnumKeys is null && key?.Expected is not ("a string" or "an instant" or "a duration");
		var parsableKey = needsParsableKey && Converter.IsParsable(underlyingType) ? KeyConverter<TKey>(Converter.CreateParsable(underlyingType)) : null;
		return new TypedConverter<TDictionary>(new DictionaryConverter<TDictionary, TKey, TValue>(new DictionaryKeys<TKey>(key, parsableKey), new ItemConverter<TValue>(), Converter.CanBeNull(typeof(TValue)), createDictionary));
	}

	/*
	The typed converter of a key type, with `SomlConverters.Nullable` around that of the underlying type for a nullable value type, such as `Guid?`.
	*/
	private static SomlConverter<TKey>? KeyConverter<TKey>(object? typed) => typed switch
	{
		SomlConverter<TKey> converter => converter,
		not null when Nullable.GetUnderlyingType(typeof(TKey)) is { } underlyingType => (SomlConverter<TKey>)NullableFactory.MakeGenericMethod(underlyingType).Invoke(null, [typed])!,
		_ => null,
	};
}

/*
An item of a collection, or a value of a dictionary, through the reflection path: with the typed converter of its type when it has one, so that it is not boxed, and otherwise with the converter of the reflection path, which writes a value of a derived class as what it is.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal sealed class ItemConverter<T> : SomlConverter<T>
{
	/*
	Found on first use rather than when the collection's converter is made, because the item type can hold the collection, as in `class Tree : List<Tree>`. The collection's converter belongs to one options instance, so the lookup gives the same converter each time, and two threads that race to set it are harmless.
	*/
	private Converter? _converter;
	private static readonly string ExpectedValue = Converter.DescribeExpected(typeof(T));

	public override string Expected => ExpectedValue;

	public override T Read(SomlValue value, SomlConverterState state)
	{
		var converter = _converter ??= state.Options.GetConverter(typeof(T));
		return converter.Typed is SomlConverter<T> typed ? typed.Read(value, state) : (T)converter.Read(value, (SerializerState)state)!;
	}

	/*
	A typed converter writes any value of its type, so the runtime type decides only for the other converters, as `SerializerState.Write` does.
	*/
	public override SomlValue Write(T value, SomlConverterState state)
	{
		var converter = _converter ??= state.Options.GetConverter(typeof(T));
		return converter.Typed is SomlConverter<T> typed ? typed.Write(value, state) : converter.WriteRuntime(value!, typeof(T), (SerializerState)state);
	}
}
