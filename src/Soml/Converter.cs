using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Soml.Serialization;

namespace Soml;

/*
The state of one call of the reflection path of `SomlSerializer`, which finds the converter of each type at runtime.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal sealed class SerializerState(SomlSerializerOptions options) : SomlConverterState(options)
{
	/*
	Reads a value into a type. Null is only allowed when `isNullable` is true.
	*/
	public object? Read(SomlValue value, Type type, bool isNullable)
	{
		if (value.IsNull)
		{
			// A `SomlValue` holds null itself, whatever the annotations say, because null is one of its values.
			if (type == typeof(SomlValue))
			{
				return SomlValue.Null;
			}

			return isNullable ? null : throw Mismatch(value, Converter.DescribeExpected(type), type);
		}

		CheckDepth("deserialize", value.Kind is SomlValueKind.Object or SomlValueKind.Array);
		return Options.GetConverter(type).Read(value, this);
	}

	/*
	Writes a value. A value whose declared type is `object` or a base class is written as its runtime type.
	*/
	public SomlValue Write(object? value, Type declaredType)
	{
		if (value is null)
		{
			return SomlValue.Null;
		}

		CheckDepth("serialize");

		return Options.GetConverter(declaredType).WriteRuntime(value, declaredType, this);
	}
}

/*
Reads and writes the values of one type. A converter is made once per type and options, and cached in the options.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal abstract class Converter
{
	/*
	The typed converters of the scalar types, which the generated path uses too, so both paths read and write these types the same way.
	*/
	private static readonly MethodInfo EnumFactory = typeof(Converter).GetMethod(nameof(CreateEnum), BindingFlags.NonPublic | BindingFlags.Static)!;

	// Note: Not using FrozenDictionary because it increased construction and lookup costs for this small registry on .NET 10 ARM64, and per-type caches limit registry reads.
	private static readonly Dictionary<Type, Converter> ScalarConverters = new()
	{
		[typeof(string)] = Adapt(SomlConverters.String),
		[typeof(bool)] = Adapt(SomlConverters.Boolean),
		[typeof(char)] = Adapt(SomlConverters.Char),
		[typeof(SomlValue)] = Adapt(SomlConverters.Value),
		[typeof(SomlObject)] = Adapt(SomlConverters.Object),
		[typeof(SomlArray)] = Adapt(SomlConverters.Array),
		[typeof(SomlInstant)] = Adapt(SomlConverters.Instant),
		[typeof(SomlDuration)] = Adapt(SomlConverters.Duration),
		[typeof(DateTimeOffset)] = Adapt(SomlConverters.DateTimeOffset),
		[typeof(DateTime)] = Adapt(SomlConverters.DateTime),
		[typeof(TimeSpan)] = Adapt(SomlConverters.TimeSpan),
		[typeof(DateOnly)] = Adapt(SomlConverters.DateOnly),
		[typeof(TimeOnly)] = Adapt(SomlConverters.TimeOnly),
		[typeof(Version)] = Adapt(SomlConverters.Version),
		[typeof(Uri)] = Adapt(SomlConverters.Uri),
		[typeof(double)] = Adapt(SomlConverters.Double),
		[typeof(float)] = Adapt(SomlConverters.Single),
		[typeof(Half)] = Adapt(SomlConverters.Half),
		[typeof(decimal)] = Adapt(SomlConverters.Decimal),
		[typeof(sbyte)] = Adapt(SomlConverters.SByte),
		[typeof(byte)] = Adapt(SomlConverters.Byte),
		[typeof(short)] = Adapt(SomlConverters.Int16),
		[typeof(ushort)] = Adapt(SomlConverters.UInt16),
		[typeof(int)] = Adapt(SomlConverters.Int32),
		[typeof(uint)] = Adapt(SomlConverters.UInt32),
		[typeof(long)] = Adapt(SomlConverters.Int64),
		[typeof(ulong)] = Adapt(SomlConverters.UInt64),
		[typeof(Int128)] = Adapt(SomlConverters.Int128),
		[typeof(UInt128)] = Adapt(SomlConverters.UInt128),
		[typeof(nint)] = Adapt(SomlConverters.IntPtr),
		[typeof(nuint)] = Adapt(SomlConverters.UIntPtr),
	};

	/*
	Reads a value that is not null.
	*/
	public abstract object? Read(SomlValue value, SerializerState state);

	/*
	Writes a value that is not null.
	*/
	public abstract SomlValue Write(object value, SerializerState state);

	/*
	The declared type decides for a collection, while an object, class, or unsupported base type is written through its runtime converter.
	*/
	public SomlValue WriteRuntime(object value, Type declaredType, SerializerState state)
	{
		// The declared type decides, so that a property declared as `IEnumerable<int>` is an array whatever its value's class is. The value's own type decides for `object`, and for a class or an interface that has no converter of its own, so that a derived class is written with its own members.
		if (this is MembersConverter or UnsupportedConverter or ObjectValueConverter && value.GetType() is var type && type != declaredType)
		{
			return state.Options.GetConverter(type).Write(value, state);
		}

		return Write(value, state);
	}

	/*
	A built-in scalar's typed converter is independent of options. The generic cache avoids boxing a value through the reflection adapter.
	*/
	public static SomlConverter<T>? Scalar<T>() => ScalarCache<T>.Converter;

	[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
	[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
	private static class ScalarCache<T>
	{
		public static readonly SomlConverter<T>? Converter = ScalarConverters.TryGetValue(typeof(T), out var converter) ? converter.Typed as SomlConverter<T> : null;
	}

	/*
	The typed converter that this one adapts, for the keys of a dictionary, and so that the items of a collection and the values of a dictionary are read and written without boxing.
	*/
	public virtual object? Typed => null;

	public static Converter Create(Type type, SomlSerializerOptions options)
	{
		// An array of pointers too, as a pointer cannot be a type argument of the list converter.
		if (type == typeof(void) || type.IsByRefLike || type.IsPointer || type.ContainsGenericParameters || type.GetElementType() is { IsPointer: true })
		{
			return new UnsupportedConverter(type, "it is not a type that values have");
		}

		if (Nullable.GetUnderlyingType(type) is { } underlyingType)
		{
			return options.GetConverter(underlyingType);
		}

		// Before the scalars, because the type code of an enum is the type code of its underlying integer type. The `SomlException` for two members with one name is thrown as it is, not wrapped.
		if (type.IsEnum)
		{
			// Note: Not using MethodInvoker for this cached converter creation because building it added time and allocations on .NET 10 ARM64.
			return (Converter)EnumFactory.MakeGenericMethod(type).Invoke(null, BindingFlags.DoNotWrapExceptions, null, [options], null)!;
		}

		if (ScalarConverters.TryGetValue(type, out var scalar))
		{
			return scalar;
		}

		if (type.IsArray)
		{
			return type.IsSZArray ? ListConverter.TryCreate(type)! : new UnsupportedConverter(type, "a multidimensional or non-vector array is not supported; use a jagged array, such as int[][]");
		}

		var interfaces = type.GetInterfaces();

		if (DictionaryConverter.TryCreate(type, options, interfaces) is { } dictionary)
		{
			return dictionary;
		}

		if (ListConverter.TryCreate(type, interfaces) is { } list)
		{
			return list;
		}

		if (IsParsable(type, interfaces))
		{
			return Adapt(type, CreateParsable(type));
		}

		// A non-generic collection, such as `ArrayList`, says nothing about its items.
		if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(object))
		{
			return new UnsupportedConverter(type, "a non-generic collection is not supported; use a generic one, such as List<T> or Dictionary<string, T>");
		}

		if (type.IsAbstract || type.IsInterface)
		{
			return new UnsupportedConverter(type, "an abstract type or an interface cannot be created");
		}

		if (type == typeof(object))
		{
			return ObjectValueConverter.Instance;
		}

		return new MembersConverter(type, options);
	}

	/*
	A typed converter, a `SomlConverter<T>`, as a converter of the reflection path.
	*/
	private static TypedConverter<T> Adapt<T>(SomlConverter<T> typed) => new(typed);

	// Note: Not using a one-shot ConstructorInvoker because it saved allocation but did not consistently improve cold public serialization.
	private static Converter Adapt(Type type, object typed) => (Converter)Activator.CreateInstance(typeof(TypedConverter<>).MakeGenericType(type), typed)!;

	private static TypedConverter<TEnum> CreateEnum<TEnum>(SomlSerializerOptions options) where TEnum : struct, Enum
	{
		// Note: Not using Enum.GetNames/GetValues because they sort by unsigned value rather than the declaration order used for flag output and alias preference.
		// Note: Not using GetRawConstantValue because it uses the same metadata reader, returns underlying integers, and showed no consistent common-case public gain.
		var members = typeof(TEnum).GetFields(BindingFlags.Public | BindingFlags.Static).Where(field => !field.IsDefined(typeof(SomlIgnoreAttribute))).Select(field => (field.Name, field.GetCustomAttribute<SomlNameAttribute>()?.Name, (TEnum)field.GetValue(null)!)).ToArray();
		return Adapt(SomlConverters.Enum(options, members, typeof(TEnum).IsDefined(typeof(FlagsAttribute))));
	}

	public static bool IsParsable(Type type, Type[]? interfaces = null)
	{
		if (!typeof(IFormattable).IsAssignableFrom(type))
		{
			return false;
		}

		// Note: Not using MakeGenericType to test IParsable<T> because its self constraint throws for types such as Version that do not implement it.
		// Note: Not using GetInterface because it cannot select the self-typed interface when a type implements IParsable with multiple type arguments.
		foreach (var implemented in interfaces ?? type.GetInterfaces())
		{
			if (implemented.IsGenericType && implemented.GetGenericTypeDefinition() == typeof(IParsable<>) && implemented.GenericTypeArguments[0] == type)
			{
				return true;
			}
		}

		return false;
	}

	/*
	The `ParsableConverter<T>` of a type that `IsParsable`, which is also how such a type is a dictionary key.
	*/
	public static object CreateParsable(Type type) => typeof(ParsableConverter<>).MakeGenericType(type).GetProperty("Instance")!.GetValue(null)!;

	/*
	The type itself or its first interface matching either generic definition. Callers reuse one interface snapshot while discovering a converter.
	*/
	public static Type? FindInterface(Type type, Type[] interfaces, Type definition, Type? secondDefinition = null)
	{
		if (Matches(type, definition, secondDefinition))
		{
			return type;
		}

		foreach (var implemented in interfaces)
		{
			if (Matches(implemented, definition, secondDefinition))
			{
				return implemented;
			}
		}

		return null;

		static bool Matches(Type candidate, Type definition, Type? secondDefinition)
		{
			if (!candidate.IsGenericType)
			{
				return false;
			}

			var generic = candidate.GetGenericTypeDefinition();
			return generic == definition || generic == secondDefinition;
		}
	}

	public static string DescribeType(Type type) => SomlConverterState.DescribeType(type);

	/*
	Whether a value of a type may be null, without annotations: a reference type or a nullable value type.
	*/
	public static bool CanBeNull(Type type) => !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;

	/*
	The kind of value that a type reads from, for an error message, such as “an int”.
	*/
	public static string DescribeExpected(Type type)
	{
		type = Nullable.GetUnderlyingType(type) ?? type;

		if (type.IsEnum)
		{
			return type.IsDefined(typeof(FlagsAttribute)) ? "an array" : "a string";
		}

		// A scalar type reads what its typed converter says. The converters of the other types are not asked, because making one can make the converters of the types it holds, which for a type such as `class Tree : List<Tree>` would not end.
		if (ScalarConverters.TryGetValue(type, out var scalar))
		{
			return ((IExpected)scalar.Typed!).Expected;
		}

		if (type == typeof(object))
		{
			return "a value";
		}

		if (type.IsArray)
		{
			return "an array";
		}

		var interfaces = type.GetInterfaces();

		if (FindInterface(type, interfaces, typeof(IDictionary<,>), typeof(IReadOnlyDictionary<,>)) is not null)
		{
			return "an object";
		}

		if (FindInterface(type, interfaces, typeof(IEnumerable<>)) is not null)
		{
			return "an array";
		}

		return IsParsable(type, interfaces) ? "a string" : "an object";
	}
}

/*
A typed converter, which the generated path uses too, as a converter of the reflection path.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal sealed class TypedConverter<T>(SomlConverter<T> typed) : Converter
{
	public override object? Typed => typed;

	public override object? Read(SomlValue value, SerializerState state) => typed.Read(value, state);

	public override SomlValue Write(object value, SerializerState state) => typed.Write((T)value, state);
}

/*
A type that cannot be read or written, which fails when it is used rather than when it is found, so that a type with such a member can still be used when the member is ignored.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal sealed class UnsupportedConverter(Type type, string reason) : Converter
{
	public override object? Read(SomlValue value, SerializerState state) => throw state.Error($"Cannot read {DescribeType(type)}, because {reason}");

	public override SomlValue Write(object value, SerializerState state) => throw state.Error($"Cannot write {DescribeType(type)}, because {reason}");
}

/*
`object` as plain .NET values: a string, a long, a double, a bool, null, a date, an instant, a duration, a list, or a dictionary.
*/
[RequiresUnreferencedCode(SomlSerializer.ReflectionMessage)]
[RequiresDynamicCode(SomlSerializer.ReflectionMessage)]
internal sealed class ObjectValueConverter : Converter
{
	public static ObjectValueConverter Instance { get; } = new();

	public override object? Read(SomlValue value, SerializerState state) => value.Kind switch
	{
		SomlValueKind.Null => null,
		SomlValueKind.String => value.GetString(),
		SomlValueKind.Integer => value.GetInt64(),
		SomlValueKind.Float => value.GetDouble(),
		SomlValueKind.Boolean => value.GetBoolean(),
		SomlValueKind.Date => value.GetDate(),
		SomlValueKind.Instant => value.GetInstant(),
		SomlValueKind.Duration => value.GetDuration(),
		SomlValueKind.Array => ReadArray(value.GetArray(), state),
		_ => ReadObject(value.GetObject(), state),
	};

	/*
	The items and members are read through the state, which checks their depth, as a `SomlValue` built in code can hold itself.
	*/
	private static List<object?> ReadArray(SomlArray array, SerializerState state)
	{
		var result = new List<object?>(array.Count);
		CollectionsMarshal.SetCount(result, array.Count);
		var destination = CollectionsMarshal.AsSpan(result);

		for (var index = 0; index < array.Count; index++)
		{
			state.Push(index);
			destination[index] = ReadNested(array[index], state);
			state.Pop();
		}

		return result;
	}

	private static Dictionary<string, object?> ReadObject(SomlObject members, SerializerState state)
	{
		var result = new Dictionary<string, object?>(members.Count, StringComparer.Ordinal);

		foreach (var (key, member) in members.AsSpan())
		{
			state.Push(key);
			result.Add(key, ReadNested(member, state));
			state.Pop();
		}

		return result;
	}

	private static object? ReadNested(SomlValue value, SerializerState state)
	{
		state.CheckDepth("deserialize", value.Kind is SomlValueKind.Object or SomlValueKind.Array);
		return Instance.Read(value, state);
	}

	public override SomlValue Write(object value, SerializerState state) => throw state.Error("Cannot write an instance of object, which has no members");
}
