using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Soml.Serialization;

/// <summary>
/// The converters for the types that <see cref="SomlSerializer"/> maps, for the code that the source generator writes. The reflection path of <see cref="SomlSerializer"/> uses the same converters for these types, so both give the same values and errors.
/// </summary>
public static class SomlConverters
{
	// Note: Not using TimeOnly's "O" format because it always writes seven fractional digits rather than an optional fraction.
	// Note: Not using explicit AsSpan with TimeOnly.ParseExact because its string overload already forwards to spans in .NET 10 and this adds no simplification.
	private static readonly string[] TimeOnlyFormats = ["HH:mm:ss.FFFFFFF", "HH:mm"];

	/// <summary>A <see cref="string"/> as a string.</summary>
	public static SomlConverter<string> String { get; } = new Simple<string>("a string", (value, state) => ReadString(value, state, typeof(string)), (value, _) => value);

	/// <summary>A <see cref="bool"/> as a bool.</summary>
	public static SomlConverter<bool> Boolean { get; } = new Simple<bool>("a bool", (value, state) => value.TryGetBoolean(out var boolean) ? boolean : throw state.Mismatch(value, "a bool", typeof(bool)), (value, _) => value);

	/// <summary>A <see cref="char"/> as a string of one UTF-16 code unit.</summary>
	public static SomlConverter<char> Char { get; } = new Simple<char>("a string", (value, state) => value.TryGetString(out var text) && text.Length == 1 ? text[0] : throw state.Mismatch(value, "a string", typeof(char), ". A char is a string of one UTF-16 code unit"), (value, _) => value.ToString());

	/// <summary>A <see cref="SomlValue"/> as it is.</summary>
	public static SomlConverter<SomlValue> Value { get; } = new Simple<SomlValue>("a value", (value, _) => value, (value, _) => value);

	/// <summary>A <see cref="SomlObject"/> as it is.</summary>
	public static SomlConverter<SomlObject> Object { get; } = new Simple<SomlObject>("an object", (value, state) => state.ReadObject(value, typeof(SomlObject)), (value, _) => value);

	/// <summary>A <see cref="SomlArray"/> as it is.</summary>
	public static SomlConverter<SomlArray> Array { get; } = new Simple<SomlArray>("an array", (value, state) => value.TryGetArray(out var result) ? result : throw state.Mismatch(value, "an array", typeof(SomlArray)), (value, _) => value);

	/// <summary>A <see cref="SomlInstant"/> as an instant.</summary>
	public static SomlConverter<SomlInstant> Instant { get; } = new Simple<SomlInstant>("an instant", (value, state) => ReadInstant(value, state, typeof(SomlInstant)), (value, _) => value);

	/// <summary>A <see cref="SomlDuration"/> as a duration.</summary>
	public static SomlConverter<SomlDuration> Duration { get; } = new Simple<SomlDuration>("a duration", (value, state) => ReadDuration(value, state, typeof(SomlDuration)), (value, _) => value);

	/// <summary>A <see cref="System.DateTimeOffset"/> as an instant, which must be a whole number of ticks.</summary>
	public static SomlConverter<DateTimeOffset> DateTimeOffset { get; } = new Simple<DateTimeOffset>("an instant", (value, state) => Convert(state, ReadInstant(value, state, typeof(DateTimeOffset)), static instant => instant.ToDateTimeOffset()), (value, state) => Convert(state, value, static value => (SomlValue)SomlInstant.FromDateTimeOffset(value)));

	/// <summary>A <see cref="System.DateTime"/> as an instant, read in UTC. It must be a whole number of ticks, and its kind must be UTC or local to be written.</summary>
	public static SomlConverter<DateTime> DateTime { get; } = new Simple<DateTime>("an instant", (value, state) => Convert(state, ReadInstant(value, state, typeof(DateTime)), static instant => instant.ToDateTime()), (value, state) => Convert(state, value, static value => (SomlValue)SomlInstant.FromDateTime(value)));

	/// <summary>A <see cref="System.TimeSpan"/> as a duration, which must be a whole number of ticks.</summary>
	public static SomlConverter<TimeSpan> TimeSpan { get; } = new Simple<TimeSpan>("a duration", (value, state) => Convert(state, ReadDuration(value, state, typeof(TimeSpan)), static duration => duration.ToTimeSpan()), (value, state) => Convert(state, value, static value => (SomlValue)SomlDuration.FromTimeSpan(value)));

	/// <summary>
	/// A <see cref="System.DateOnly"/> as a native proleptic Gregorian date, preserving its components without a time or timezone. Quoted text remains a string.
	/// </summary>
	public static SomlConverter<DateOnly> DateOnly { get; } = new Simple<DateOnly>("a date", (value, state) => value.TryGetDate(out var date) ? date : throw state.Mismatch(value, "a date", typeof(DateOnly)), (value, _) => value);

	/// <summary>A <see cref="System.TimeOnly"/> as a string, <c>HH:mm:ss</c> with a fraction of a second when it has one, and read also as <c>HH:mm</c>.</summary>
	public static SomlConverter<TimeOnly> TimeOnly { get; } = new Simple<TimeOnly>("a string", (value, state) => Convert(state, ReadString(value, state, typeof(TimeOnly)), static text => System.TimeOnly.ParseExact(text, TimeOnlyFormats, CultureInfo.InvariantCulture)), (value, _) => value.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture));

	/// <summary>
	/// A <see cref="System.Version"/> as a string. It has <c>Parse()</c> and is <see cref="IFormattable"/>, but it is not <see cref="IParsable{TSelf}"/>.
	/// </summary>
	public static SomlConverter<Version> Version { get; } = new Simple<Version>("a string", (value, state) => Convert(state, ReadString(value, state, typeof(Version)), System.Version.Parse), (value, _) => value.ToString());

	/// <summary>A <see cref="System.Uri"/> as a string, relative or absolute.</summary>
	public static SomlConverter<Uri> Uri { get; } = new Simple<Uri>("a string", (value, state) => Convert(state, ReadString(value, state, typeof(Uri)), static text => new Uri(text, UriKind.RelativeOrAbsolute)), (value, _) => value.OriginalString);

	/// <summary>A <see cref="double"/> as a float. An int reads when a double holds it exactly. NaN cannot be written.</summary>
	public static SomlConverter<double> Double { get; } = new Simple<double>("a float", (value, state) => value.TryGetDouble(out var number) ? number : throw state.Mismatch(value, "a float", typeof(double), value.Kind == SomlValueKind.Integer ? ", which a double cannot hold exactly" : ""), (value, state) => double.IsNaN(value) ? throw NaN(state) : value);

	/// <summary>A <see cref="float"/> as a float, written with its own shortest digits, so <c>0.1f</c> is <c>0.1</c>.</summary>
	public static SomlConverter<float> Single { get; } = new Simple<float>("a float", ReadFloating<float>, WriteSingle);

	/// <summary>A <see cref="System.Half"/> as a float, written with its own shortest digits.</summary>
	public static SomlConverter<Half> Half { get; } = new Simple<Half>("a float", ReadFloating<Half>, WriteHalf);

	/// <summary>A <see cref="decimal"/> as a float, read from the shortest digits of the float, so <c>0.1</c> is <c>0.1m</c>.</summary>
	public static SomlConverter<decimal> Decimal { get; } = new Simple<decimal>("a float", ReadDecimal, WriteDecimal);

	/// <summary>An <see cref="sbyte"/> as an int, range-checked.</summary>
	public static SomlConverter<sbyte> SByte { get; } = new Integer<sbyte>();

	/// <summary>A <see cref="byte"/> as an int, range-checked.</summary>
	public static SomlConverter<byte> Byte { get; } = new Integer<byte>();

	/// <summary>A <see cref="short"/> as an int, range-checked.</summary>
	public static SomlConverter<short> Int16 { get; } = new Integer<short>();

	/// <summary>A <see cref="ushort"/> as an int, range-checked.</summary>
	public static SomlConverter<ushort> UInt16 { get; } = new Integer<ushort>();

	/// <summary>An <see cref="int"/> as an int, range-checked.</summary>
	public static SomlConverter<int> Int32 { get; } = new Integer<int>();

	/// <summary>A <see cref="uint"/> as an int, range-checked.</summary>
	public static SomlConverter<uint> UInt32 { get; } = new Integer<uint>();

	/// <summary>A <see cref="long"/> as an int.</summary>
	public static SomlConverter<long> Int64 { get; } = new Integer<long>();

	/// <summary>A <see cref="ulong"/> as an int, range-checked, so a value above <see cref="long.MaxValue"/> cannot be written.</summary>
	public static SomlConverter<ulong> UInt64 { get; } = new Integer<ulong>();

	/// <summary>An <see cref="System.Int128"/> as an int, range-checked both ways.</summary>
	public static SomlConverter<Int128> Int128 { get; } = new Integer<Int128>();

	/// <summary>A <see cref="System.UInt128"/> as an int, range-checked both ways.</summary>
	public static SomlConverter<UInt128> UInt128 { get; } = new Integer<UInt128>();

	/// <summary>A <see cref="nint"/> as an int, range-checked for the process's pointer size.</summary>
	public static SomlConverter<nint> IntPtr { get; } = new Integer<nint>();

	/// <summary>A <see cref="nuint"/> as an int, range-checked for the process's pointer size and the signed SOML range.</summary>
	public static SomlConverter<nuint> UIntPtr { get; } = new Integer<nuint>();

	/// <summary>
	/// A type that is <see cref="IParsable{TSelf}"/> and <see cref="IFormattable"/>, such as <see cref="Guid"/>, as a string in the invariant culture. Failures from its parser or formatter become a <see cref="SomlException"/> with the path and the original exception as its <see cref="Exception.InnerException"/>.
	/// </summary>
	public static SomlConverter<T> Parsable<T>() where T : IParsable<T>, IFormattable => ParsableConverter<T>.Instance;

	/// <summary>
	/// A nullable value type, as its value. A null is handled by <see cref="SomlConverterState.Read{T}(SomlValue, SomlConverter{T}, bool)"/>.
	/// </summary>
	public static SomlConverter<T?> Nullable<T>(SomlConverter<T> converter) where T : struct
	{
		ArgumentNullException.ThrowIfNull(converter);
		return new NullableConverter<T>(converter);
	}

	/// <summary>
	/// An enum as the string of a member's name, or a [Flags] enum as an array of them. The names are the members' <see cref="SomlNameAttribute"/>, or their names through the naming policy of the options. Writing a [Flags] value requires each set bit to have a single-bit member name.
	/// </summary>
	/// <param name="options">The options, for the naming policy.</param>
	/// <param name="members">The members in their order of declaration: each one's name, its <see cref="SomlNameAttribute"/> or <c>null</c>, and its value.</param>
	/// <param name="isFlags">Whether the enum is a [Flags] enum.</param>
	public static SomlConverter<TEnum> Enum<TEnum>(SomlSerializerOptions options, ReadOnlySpan<(string Name, string? ExplicitName, TEnum Value)> members, bool isFlags) where TEnum : struct, Enum
	{
		ArgumentNullException.ThrowIfNull(options);
		return new EnumConverter<TEnum>(options, members, isFlags);
	}

	/// <summary>
	/// A collection as an array. It is read into a <see cref="List{T}"/>, which <paramref name="create"/> turns into the collection.
	/// </summary>
	/// <param name="item">The converter of the items.</param>
	/// <param name="isItemNullable">Whether an item may be null.</param>
	/// <param name="isSet">Whether the collection is a set. Its comparer must keep every item distinct; duplicates are reported at the collection's path.</param>
	/// <param name="create">Makes the collection from the items.</param>
	public static SomlConverter<TCollection> Collection<TCollection, TItem>(SomlConverter<TItem> item, bool isItemNullable, bool isSet, Func<List<TItem>, TCollection> create) where TCollection : IEnumerable<TItem>
	{
		ArgumentNullException.ThrowIfNull(item);
		ArgumentNullException.ThrowIfNull(create);
		return new CollectionConverter<TCollection, TItem>(item, isItemNullable, isSet, create);
	}

	/// <summary>
	/// An array, read directly into its final storage without an intermediate list.
	/// </summary>
	/// <param name="item">The converter of the items.</param>
	/// <param name="isItemNullable">Whether an item may be null.</param>
	public static SomlConverter<TItem[]> ArrayOf<TItem>(SomlConverter<TItem> item, bool isItemNullable)
	{
		ArgumentNullException.ThrowIfNull(item);
		return new CollectionConverter<TItem[], TItem>(item, isItemNullable, isSet: false, create: null);
	}

	/// <summary>
	/// A dictionary as an object. It is read into a <see cref="Dictionary{TKey, TValue}"/>, which <paramref name="create"/> turns into the dictionary. A string key is never renamed. Every non-string key must be spelled as its type writes it, so that a key has one spelling. An enum key is one member's written name, excluding aliases that write as another name, and an instant or a duration key is its canonical text.
	/// </summary>
	/// <param name="key">The converter of the key type.</param>
	/// <param name="parsableKey">The <see cref="Parsable{T}"/> converter of a key type whose values are not strings, such as <see cref="int"/>, or <c>null</c>.</param>
	/// <param name="value">The converter of the values.</param>
	/// <param name="isValueNullable">Whether a value may be null.</param>
	/// <param name="create">Makes the dictionary from the members.</param>
	public static SomlConverter<TDictionary> Dictionary<TDictionary, TKey, TValue>(SomlConverter<TKey> key, SomlConverter<TKey>? parsableKey, SomlConverter<TValue> value, bool isValueNullable, Func<Dictionary<TKey, TValue>, TDictionary> create) where TDictionary : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
	{
		ArgumentNullException.ThrowIfNull(key);
		ArgumentNullException.ThrowIfNull(value);
		ArgumentNullException.ThrowIfNull(create);
		return new DictionaryConverter<TDictionary, TKey, TValue>(new DictionaryKeys<TKey>(key, parsableKey), value, isValueNullable, create);
	}

	/*
	The count without enumerating. A read-only collection's Count is used too.

	Note: Not using Enumerable.TryGetNonEnumeratedCount because it does not use a read-only collection's Count.
	*/
	internal static int KnownCount<T>(IEnumerable<T> items) => items switch
	{
		IReadOnlyCollection<T> counted => counted.Count,
		_ when items.TryGetNonEnumeratedCount(out var count) => count,
		_ => 0,
	};

	/*
	Converts a value with a BCL method, whose exception becomes an error at the path. The value is an argument, rather than captured, so the lambdas are static and allocate nothing.
	*/
	private static TResult Convert<TValue, TResult>(SomlConverterState state, TValue value, Func<TValue, TResult> convert)
	{
		try
		{
			return convert(value);
		}
		catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or OverflowException or FormatException)
		{
			// The message of an `ArgumentException` ends with the parameter name, which means nothing to the reader of a document.
			var message = SomlConverterState.ExceptionMessage(exception);
			message = exception is ArgumentException { ParamName: { } name } ? message.Replace($" (Parameter '{name}')", "") : message;
			throw state.Error(message, innerException: exception);
		}
	}

	private static string ReadString(SomlValue value, SomlConverterState state, Type type) => value.TryGetString(out var text) ? text : throw state.Mismatch(value, "a string", type);

	private static SomlInstant ReadInstant(SomlValue value, SomlConverterState state, Type type) => value.TryGetInstant(out var instant) ? instant : throw state.Mismatch(value, "an instant", type);

	private static SomlDuration ReadDuration(SomlValue value, SomlConverterState state, Type type) => value.TryGetDuration(out var duration) ? duration : throw state.Mismatch(value, "a duration", type);

	private static SomlException NaN(SomlConverterState state) => state.Error(Message.NaN);

	/*
	A float or an exact int as a `float` or a `Half`. A finite value that is too large for the type is an error rather than an infinity.
	*/
	private static T ReadFloating<T>(SomlValue value, SomlConverterState state) where T : IBinaryFloatingPointIeee754<T>
	{
		T result;
		var type = typeof(T);

		if (value.TryGetInt64(out var integer))
		{
			result = T.CreateTruncating(integer);

			// Compared as an `Int128`, because converting the float back to a `long` saturates, so 2^63 would compare equal to `long.MaxValue`.
			if (!T.IsFinite(result) || System.Int128.CreateTruncating(result) != integer)
			{
				throw state.Mismatch(value, "a float", type, $", which a {SomlConverterState.DescribeType(type)} cannot hold exactly");
			}

			return result;
		}

		if (!value.TryGetDouble(out var number))
		{
			throw state.Mismatch(value, "a float", type);
		}

		result = T.CreateTruncating(number);

		// A finite float that becomes an infinity, or a nonzero one that becomes zero, is not rounded silently.
		if ((T.IsInfinity(result) && !double.IsInfinity(number)) || (T.IsZero(result) && number != 0))
		{
			throw state.Error($"The float {SomlWriter.FormatFloat(number)} is outside the range of {SomlConverterState.DescribeType(type)}");
		}

		return result;
	}

	/*
	A `float` is written with its own shortest digits, so 0.1f is written 0.1 rather than as the double it widens to, 0.10000000149011612. When those digits read back as a double that does not narrow to the same float, the double it widens to is written instead. The conversion from `float` to `SomlValue` does this, and only its error for NaN differs.
	*/
	private static SomlValue WriteSingle(float value, SomlConverterState state) => float.IsNaN(value) ? throw NaN(state) : value;

	/*
	A `Half` is written with its own shortest digits, as a `float` is, so `(Half)0.1` is written 0.1.
	*/
	private static SomlValue WriteHalf(Half value, SomlConverterState state)
	{
		if (!System.Half.IsFinite(value))
		{
			return WriteSingle((float)value, state);
		}

		return SomlWriter.ToShortestDouble(value);
	}

	private static decimal ReadDecimal(SomlValue value, SomlConverterState state)
	{
		if (value.TryGetInt64(out var integer))
		{
			return integer;
		}

		if (!value.TryGetDouble(out var number))
		{
			throw state.Mismatch(value, "a float", typeof(decimal));
		}

		return SomlWriter.TryConvertToDecimal(number, out var result) ? result : throw state.Error($"The float {SomlWriter.FormatFloat(number)} is outside the range of decimal");
	}

	/*
	A decimal is written as a float, which must read back as the same decimal, so a decimal that no double holds exactly enough is an error rather than a rounding.
	*/
	private static SomlValue WriteDecimal(decimal value, SomlConverterState state)
	{
		var number = (double)value;

		if (!SomlWriter.TryConvertToDecimal(number, out var readBack) || readBack != value)
		{
			throw state.Error($"The decimal {value.ToString(CultureInfo.InvariantCulture)} cannot be written exactly as a float, which holds about 15 to 17 significant digits. Write it as a string instead");
		}

		return number;
	}

	private sealed class Simple<T>(string expected, Func<SomlValue, SomlConverterState, T> read, Func<T, SomlConverterState, SomlValue> write) : SomlConverter<T>
	{
		public override string Expected => expected;

		public override T Read(SomlValue value, SomlConverterState state) => read(value, state);

		public override SomlValue Write(T value, SomlConverterState state) => write(value, state);
	}

	/*
	An integer type as an int. Read bounds are clamped to the SOML int range, and write bounds to the target type's range, so comparisons use each operation's native integer type. The message gives the type's own range.
	*/
	private sealed class Integer<T> : SomlConverter<T> where T : IBinaryInteger<T>, IMinMaxValue<T>
	{
		// Note: Not using CreateChecked because explicit bounds avoid an extra overflow exception before the path-aware error for an out-of-range integer.
		private static readonly long Minimum = long.CreateSaturating(T.MinValue);
		private static readonly long Maximum = long.CreateSaturating(T.MaxValue);
		private static readonly T MinimumWritten = T.CreateSaturating(long.MinValue);
		private static readonly T MaximumWritten = T.CreateSaturating(long.MaxValue);

		public override string Expected => "an int";

		public override T Read(SomlValue value, SomlConverterState state)
		{
			if (!value.TryGetInt64(out var integer))
			{
				var hint = value.Kind == SomlValueKind.Float ? ". A float does not convert to an integer type, so write an int without a fraction, as in 3" : "";
				throw state.Mismatch(value, Expected, typeof(T), hint);
			}

			if (integer < Minimum || integer > Maximum)
			{
				throw state.Error(string.Create(CultureInfo.InvariantCulture, $"The int {integer} is outside the range of {SomlConverterState.DescribeType(typeof(T))} ({T.MinValue} to {T.MaxValue})"));
			}

			return T.CreateTruncating(integer);
		}

		public override SomlValue Write(T value, SomlConverterState state)
		{
			if (value < MinimumWritten || value > MaximumWritten)
			{
				throw state.Error(string.Create(CultureInfo.InvariantCulture, $"The {SomlConverterState.DescribeType(typeof(T))} {value} is outside the 64-bit range of a SOML int {Message.IntRange}. Write it as a string instead"));
			}

			return long.CreateTruncating(value);
		}
	}

	private sealed class NullableConverter<T>(SomlConverter<T> converter) : SomlConverter<T?>, IEnumKeys<T?> where T : struct
	{
		public override string Expected => converter.Expected;

		public override T? Read(SomlValue value, SomlConverterState state) => converter.Read(value, state);

		public override SomlValue Write(T? value, SomlConverterState state) => value is { } actual ? converter.Write(actual, state) : SomlValue.Null;

		internal override IEnumKeys<T?>? EnumKeys => converter.EnumKeys is null ? null : this;

		string IEnumKeys<T?>.WriteKey(T? value, SomlConverterState state) => converter.EnumKeys!.WriteKey(value.GetValueOrDefault(), state);

		bool IEnumKeys<T?>.TryReadKey(string text, out T? value)
		{
			var found = converter.EnumKeys!.TryReadKey(text, out var result);
			value = found ? result : null;
			return found;
		}
	}
}

/*
A type that is `IParsable<T>` and `IFormattable` as a string in the invariant culture, which is also how such a type is a dictionary key.
*/
internal sealed class ParsableConverter<T> : SomlConverter<T> where T : IParsable<T>, IFormattable
{
	public static ParsableConverter<T> Instance { get; } = new();

	public override string Expected => "a string";

	public override T Read(SomlValue value, SomlConverterState state)
	{
		if (!value.TryGetString(out var text))
		{
			throw state.Mismatch(value, Expected, typeof(T));
		}

		return Parse(text, state);
	}

	public static T Parse(string text, SomlConverterState state)
	{
		try
		{
			return T.Parse(text, CultureInfo.InvariantCulture);
		}
		catch (Exception exception)
		{
			throw state.Error($"{Message.DescribeString(text)} is not a valid {SomlConverterState.DescribeType(typeof(T))}: {SomlConverterState.ExceptionMessage(exception)}", innerException: exception);
		}
	}

	public override SomlValue Write(T value, SomlConverterState state)
	{
		try
		{
			return value.ToString(null, CultureInfo.InvariantCulture);
		}
		catch (Exception exception)
		{
			throw state.WriteFailed(typeof(T), exception);
		}
	}
}

/*
A dictionary key of an enum, which is one member's name, of a [Flags] enum too. A converter gives it through `SomlConverter<T>.EnumKeys`, so that the converter of a nullable enum gives its enum's.
*/
internal interface IEnumKeys<T>
{
	bool TryReadKey(string text, out T value);

	string WriteKey(T value, SomlConverterState state);
}

/*
An enum as the string of a member's name, or a [Flags] enum as an array of them.
*/
internal sealed class EnumConverter<TEnum> : SomlConverter<TEnum>, IEnumKeys<TEnum> where TEnum : struct, Enum
{
	private readonly Dictionary<string, TEnum> _values;
	private readonly Dictionary<TEnum, string> _names;
	private readonly bool _isFlags;
	private readonly (ulong Bits, string Name)[] _flags;

	public EnumConverter(SomlSerializerOptions options, ReadOnlySpan<(string Name, string? ExplicitName, TEnum Value)> members, bool isFlags)
	{
		_isFlags = isFlags;
		_values = new Dictionary<string, TEnum>(members.Length, StringComparer.Ordinal);
		_names = new Dictionary<TEnum, string>(members.Length);
		var flags = isFlags ? new List<(ulong Bits, string Name)>(Math.Min(members.Length, Unsafe.SizeOf<TEnum>() * 8)) : null;

		foreach (var (memberName, explicitName, value) in members)
		{
			var name = SomlMetadata.Key(options, memberName, explicitName);

			if (!_values.TryAdd(name, value))
			{
				throw new SomlException(Message.DescribeText($"Two members of {typeof(TEnum).Name} are both written as '{Message.Abbreviate(name)}'. Use [SomlName] on one of them"));
			}

			// The first member of a value names it, as `Enum.GetName()` does for an alias.
			if (_names.TryAdd(value, name) && flags is not null)
			{
				var bits = ToBits(value);

				if (BitOperations.IsPow2(bits))
				{
					flags.Add((bits, name));
				}
			}
		}

		_flags = flags is null ? [] : [.. flags];
	}

	public override string Expected => _isFlags ? "an array" : "a string";

	/*
	The bits of a value as they are stored, so the sign bit of a signed enum, as in `High = int.MinValue`, is one bit rather than every bit above it.

	Note: Not using Convert.ToUInt64 because it throws for negative enum values.

	Note: Not using Enum.ToObject because it boxes rather than preserving these allocation-free bit casts.
	*/
	private static ulong ToBits(TEnum value) => Unsafe.SizeOf<TEnum>() switch
	{
		1 => Unsafe.BitCast<TEnum, byte>(value),
		2 => Unsafe.BitCast<TEnum, ushort>(value),
		4 => Unsafe.BitCast<TEnum, uint>(value),
		_ => Unsafe.BitCast<TEnum, ulong>(value),
	};

	private static TEnum FromBits(ulong bits) => Unsafe.SizeOf<TEnum>() switch
	{
		1 => Unsafe.BitCast<byte, TEnum>(unchecked((byte)bits)),
		2 => Unsafe.BitCast<ushort, TEnum>(unchecked((ushort)bits)),
		4 => Unsafe.BitCast<uint, TEnum>(unchecked((uint)bits)),
		_ => Unsafe.BitCast<ulong, TEnum>(bits),
	};

	public override TEnum Read(SomlValue value, SomlConverterState state)
	{
		if (!_isFlags)
		{
			return value.TryGetString(out var text) ? ReadName(text, state) : throw state.Mismatch(value, Expected, typeof(TEnum));
		}

		if (!value.TryGetArray(out var array))
		{
			throw state.Mismatch(value, Expected, typeof(TEnum));
		}

		ulong bits = 0;

		for (var index = 0; index < array.Count; index++)
		{
			state.Push(index);

			if (!array[index].TryGetString(out var text))
			{
				throw state.Error($"Expected a string for a member of {typeof(TEnum).Name}, but found {SomlConverterState.DescribeValue(array[index])}");
			}

			bits |= ToBits(ReadName(text, state));
			state.Pop();
		}

		return FromBits(bits);
	}

	private TEnum ReadName(string text, SomlConverterState state)
	{
		// Note: Not using Enum.TryParse because it accepts numeric strings and ignores renamed or excluded members.
		if (_values.TryGetValue(text, out var result))
		{
			return result;
		}

		var names = string.Join(", ", _values.Keys.Select(name => SomlWriter.FormatString(name)));
		throw state.Error($"{Message.DescribeString(text)} is not a member of {typeof(TEnum).Name}. The members are {names}");
	}

	public override SomlValue Write(TEnum value, SomlConverterState state)
	{
		if (!_isFlags)
		{
			return WriteName(value, state);
		}

		var bits = ToBits(value);
		var result = new SomlArray(BitOperations.PopCount(bits));

		foreach (var (flag, name) in _flags)
		{
			if (bits == 0)
			{
				break;
			}

			// Note: Not using Enum.HasFlag because it added boxing allocations in this generic flags writer on .NET 10 ARM64.
			if ((bits & flag) != 0)
			{
				result.Add(name);
				bits &= ~flag;
			}
		}

		if (bits != 0)
		{
			throw state.Error(string.Create(CultureInfo.InvariantCulture, $"The value {value:D} of {typeof(TEnum).Name} has bits that no single-bit member names"));
		}

		return result;
	}

	private string WriteName(TEnum value, SomlConverterState state) => _names.TryGetValue(value, out var name) ? name : throw state.Error(string.Create(CultureInfo.InvariantCulture, $"The value {value:D} is not a member of {typeof(TEnum).Name}"));

	internal override IEnumKeys<TEnum> EnumKeys => this;

	string IEnumKeys<TEnum>.WriteKey(TEnum value, SomlConverterState state) => WriteName(value, state);

	/*
	A key of a dictionary, which is a member's name.
	*/
	bool IEnumKeys<TEnum>.TryReadKey(string text, out TEnum value) => _values.TryGetValue(text, out value);
}

/*
A collection as an array, read into a `List<T>` that `create` turns into the collection.
*/
internal sealed class CollectionConverter<TCollection, TItem>(SomlConverter<TItem> item, bool isItemNullable, bool isSet, Func<List<TItem>, TCollection>? create) : SomlConverter<TCollection> where TCollection : IEnumerable<TItem>
{
	// A stack is written from the top and made by pushing its items in order, so its items are reversed to keep the order on a round trip.
	private static readonly bool IsStack = typeof(TCollection).IsGenericType && typeof(TCollection).GetGenericTypeDefinition() is var definition && (definition == typeof(Stack<>) || definition == typeof(ConcurrentStack<>) || definition == typeof(ImmutableStack<>));

	public override string Expected => "an array";

	public override TCollection Read(SomlValue value, SomlConverterState state)
	{
		if (!value.TryGetArray(out var array))
		{
			throw state.Mismatch(value, Expected, typeof(TCollection));
		}

		if (create is null)
		{
			var result = new TItem[array.Count];
			ReadItems(result, array, state);
			return (TCollection)(object)result;
		}

		var items = new List<TItem>(array.Count);
		CollectionsMarshal.SetCount(items, array.Count);
		ReadItems(CollectionsMarshal.AsSpan(items), array, state);

		if (IsStack)
		{
			items.Reverse();
		}

		TCollection collection;
		int count;

		// Creating and counting the collection runs its own code, so every exception from it gets the path, as on the reflection path.
		try
		{
			collection = create(items);
			count = items.Count;

			if (isSet)
			{
				count = collection is IReadOnlyCollection<TItem> counted ? counted.Count : collection.Count();
			}
		}
		catch (Exception exception)
		{
			throw state.CreateFailed(typeof(TCollection), exception);
		}

		// A set holds each item once, so a duplicate under its own comparer would be dropped without a word.
		if (count != items.Count)
		{
			throw state.Error($"Duplicate item in {SomlConverterState.DescribeType(typeof(TCollection))}");
		}

		return collection;
	}

	private void ReadItems(Span<TItem> destination, SomlArray array, SomlConverterState state)
	{
		for (var index = 0; index < destination.Length; index++)
		{
			state.Push(index);
			destination[index] = state.Read(array[index], item, isItemNullable);
			state.Pop();
		}
	}

	public override SomlValue Write(TCollection value, SomlConverterState state)
	{
		SomlArray result;
		var index = 0;

		try
		{
			// Room for every item when the collection knows its count, as a list or an array does, rather than growing to it. The count is read in the `try`, because a default `ImmutableArray<T>` throws for it.
			result = new SomlArray(SomlConverters.KnownCount(value));

			if (value is TItem[] array)
			{
				foreach (var itemValue in array)
				{
					WriteItem(itemValue, result, state, ref index);
				}
			}
			else if (value is List<TItem> list && list.GetType() == typeof(List<TItem>))
			{
				// Note: Not using CollectionsMarshal.AsSpan because item converters run user code and the span requires the list to stay unchanged.
				foreach (var itemValue in list)
				{
					WriteItem(itemValue, result, state, ref index);
				}
			}
			else
			{
				foreach (var itemValue in value)
				{
					WriteItem(itemValue, result, state, ref index);
				}
			}
		}
		catch (Exception exception) when (exception is not SomlException)
		{
			throw state.WriteFailed(typeof(TCollection), exception);
		}

		return result;
	}

	private void WriteItem(TItem value, SomlArray result, SomlConverterState state, ref int index)
	{
		state.Push(index);
		result.Add(state.Write(value, item));
		state.Pop();
		index++;
	}
}

/*
A dictionary as an object, read into a `Dictionary<TKey, TValue>` that `create` turns into the dictionary.
*/
internal sealed class DictionaryConverter<TDictionary, TKey, TValue>(DictionaryKeys<TKey> keys, SomlConverter<TValue> valueConverter, bool isValueNullable, Func<Dictionary<TKey, TValue>, TDictionary> create) : SomlConverter<TDictionary> where TDictionary : IEnumerable<KeyValuePair<TKey, TValue>> where TKey : notnull
{
	public override string Expected => "an object";

	public override TDictionary Read(SomlValue value, SomlConverterState state)
	{
		var members = state.ReadObject(value, typeof(TDictionary));

		var result = new Dictionary<TKey, TValue>(members.Count);

		foreach (var (key, member) in members.AsSpan())
		{
			state.Push(key);
			var typedKey = keys.Read(key, state, typeof(TDictionary));

			// Two keys of a document are never the same string, but they can read as the same value of another type, as `1h` and `60m` do.
			ref var slot = ref CollectionsMarshal.GetValueRefOrAddDefault(result, typedKey, out var exists);

			if (exists)
			{
				throw state.Error($"The keys of {SomlConverterState.DescribeType(typeof(TDictionary))} must be unique, but this key reads as the same {SomlConverterState.DescribeType(typeof(TKey))} as another key", isAboutKey: true);
			}

			slot = state.Read(member, valueConverter, isValueNullable);
			state.Pop();
		}

		TDictionary dictionary;
		int count;

		try
		{
			dictionary = create(result);
			count = dictionary is IReadOnlyCollection<KeyValuePair<TKey, TValue>> counted ? counted.Count : dictionary.Count();
		}
		catch (Exception exception)
		{
			throw state.CreateFailed(typeof(TDictionary), exception);
		}

		if (count != result.Count)
		{
			throw state.Error($"Duplicate key in {SomlConverterState.DescribeType(typeof(TDictionary))}");
		}

		return dictionary;
	}

	public override SomlValue Write(TDictionary value, SomlConverterState state)
	{
		SomlObject result;

		try
		{
			result = new SomlObject(SomlConverters.KnownCount(value));

			foreach (var (key, member) in value)
			{
				var writtenKey = keys.Write(key, state, typeof(TDictionary));
				state.Push(writtenKey);

				if (!result.TryAdd(writtenKey, state.Write(member, valueConverter)))
				{
					throw state.Error($"Two keys of {SomlConverterState.DescribeType(typeof(TDictionary))} are both written as {Message.DescribeKey(writtenKey)}");
				}

				state.Pop();
			}
		}
		catch (Exception exception) when (exception is not SomlException)
		{
			throw state.WriteFailed(typeof(TDictionary), exception);
		}

		return result;
	}
}

/*
How a dictionary reads and writes its keys, on both paths: a string as it is, an enum as one member's name, an instant or a duration as its canonical text, and another type as the string of its converter, or of its `ParsableConverter<T>` when its values are not strings, as for `int`. A key must be spelled as its type writes it, so `007` is not the int key 7.
*/
internal sealed class DictionaryKeys<TKey> where TKey : notnull
{
	private const string Requirement = "a key must be a string, an enum, a date, an instant or a duration, or a type that is IParsable and IFormattable";

	/*
	The reflection path also reads an `object` key, as the string that it is.
	*/
	private static readonly bool IsText = typeof(TKey) == typeof(string) || typeof(TKey) == typeof(object);

	/*
	The type's own converter when it is an enum's, or its values are strings, dates, instants, or durations, and otherwise the parsable one.
	*/
	private readonly SomlConverter<TKey>? _converter;
	private readonly IEnumKeys<TKey>? _enumKeys;
	private readonly string? _expected;

	public DictionaryKeys(SomlConverter<TKey>? converter, SomlConverter<TKey>? parsable)
	{
		_converter = converter?.EnumKeys is not null || converter?.Expected is "a string" or "a date" or "an instant" or "a duration" ? converter : parsable;
		_enumKeys = _converter?.EnumKeys;
		_expected = _converter?.Expected;
	}

	public TKey Read(string key, SomlConverterState state, Type type)
	{
		if (IsText)
		{
			return (TKey)(object)key;
		}

		try
		{
			var result = ReadText(key, state, type);
			var written = Write(result, state, type);

			if (written != key)
			{
				throw state.Error($"The key {Message.DescribeKey(key)} is not how {SomlConverterState.DescribeType(typeof(TKey))} writes it, which is {Message.DescribeKey(written)}", isAboutKey: true);
			}

			return result;
		}
		catch (SomlException exception) when (!exception.IsAboutKey && exception.PathlessReason is not null)
		{
			// The error is about the key, so it points at the key rather than at its value.
			throw state.Error(exception.PathlessReason, isAboutKey: true, exception.InnerException);
		}
	}

	private TKey ReadText(string key, SomlConverterState state, Type type)
	{
		if (_enumKeys is { } enumKeys)
		{
			return enumKeys.TryReadKey(key, out var member) ? member : throw state.Error($"The key {Message.DescribeKey(key)} is not a member of {(Nullable.GetUnderlyingType(typeof(TKey)) ?? typeof(TKey)).Name}", isAboutKey: true);
		}

		var expected = _expected;

		if (expected == "a string")
		{
			return _converter!.Read(SomlValue.FromString(key), state);
		}

		if (expected is "a date" or "an instant" or "a duration")
		{
			return Parser.TryParseTime(key, expected == "a date" ? SomlValueKind.Date : expected == "an instant" ? SomlValueKind.Instant : SomlValueKind.Duration, out var time) ? _converter!.Read(time, state) : throw state.Error($"The key {Message.DescribeKey(key)} is not {expected}", isAboutKey: true);
		}

		throw state.Error($"Cannot read the keys of {SomlConverterState.DescribeType(type)}, because {Requirement}");
	}

	public string Write(TKey key, SomlConverterState state, Type type)
	{
		if (IsText && key is string text)
		{
			return text;
		}

		if (_enumKeys is { } enumKeys)
		{
			return enumKeys.WriteKey(key, state);
		}

		return _expected switch
		{
			"a string" => _converter!.Write(key, state).GetString(),
			"a date" or "an instant" or "a duration" => _converter!.Write(key, state).ToString(),
			_ => throw state.Error($"Cannot write the keys of {SomlConverterState.DescribeType(type)}, because {Requirement}"),
		};
	}
}
