using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;

namespace Soml;

/// <summary>
/// The type of a <see cref="SomlValue"/>.
/// </summary>
public enum SomlValueKind
{
	/// <summary><c>null</c>. This is also the kind of <c>default(SomlValue)</c>.</summary>
	Null,

	/// <summary>A string, such as <c>'text'</c>.</summary>
	String,

	/// <summary>A 64-bit integer, such as <c>3</c> or <c>0xFF</c>.</summary>
	Integer,

	/// <summary>A 64-bit floating-point number, such as <c>0.75</c> or <c>infinity</c>.</summary>
	Float,

	/// <summary><c>true</c> or <c>false</c>.</summary>
	Boolean,

	/// <summary>A Gregorian calendar date, such as <c>2027-09-19</c>, without a time or timezone.</summary>
	Date,

	/// <summary>A point in time, such as <c>2026-09-19T14:00:00Z</c>.</summary>
	Instant,

	/// <summary>A length of time, such as <c>1h30m</c>.</summary>
	Duration,

	/// <summary>An array, such as <c>[1, 2]</c>.</summary>
	Array,

	/// <summary>An object, such as <c>{a: 1}</c>.</summary>
	Object,
}

/// <summary>
/// A SOML value: a string, an int, a float, a bool, null, a date, an instant, a duration, an array, or an object.
/// </summary>
/// <remarks>
/// <para>A <see cref="SomlValue"/> is a small immutable struct, so a scalar allocates nothing. An array is a <see cref="SomlArray"/> and an object is a <see cref="SomlObject"/>, which are mutable classes. <c>default(SomlValue)</c> is null.</para>
/// <para>Create one with an implicit conversion, as in <c>SomlValue port = 8080;</c>, and read one with the <c>Get</c> and <c>TryGet</c> methods or an explicit conversion, as in <c>(long)value</c>. An int and a float are different types: <c>3</c> is an int and <c>3.0</c> is a float.</para>
/// <para>Equality is deep. Two objects are equal when they have the same members in any order, because the order of an object's members is not part of its value. Equality and hash codes are recursive, so they are not supported for an object or an array that holds itself.</para>
/// </remarks>
[DebuggerDisplay("{DebuggerDisplay,nq}")]
public readonly struct SomlValue : IEquatable<SomlValue>
{
	// A string, a `SomlArray`, or a `SomlObject`.
	private readonly object? _reference;

	// An int, the bits of a float, a bool as 0 or 1, the Gregorian day number of a date, the Unix seconds of an instant, or the nanoseconds of a duration.
	private readonly long _bits;

	// The nanoseconds within the second of an instant.
	private readonly int _nanosecond;

	private SomlValue(SomlValueKind kind, object? reference = null, long bits = 0, int nanosecond = 0)
	{
		Kind = kind;
		_reference = reference;
		_bits = bits;
		_nanosecond = nanosecond;
	}

	/// <summary>
	/// The null value, which is also <c>default(SomlValue)</c>.
	/// </summary>
	public static SomlValue Null => default;

	/// <summary>
	/// The type of the value.
	/// </summary>
	public SomlValueKind Kind { get; }

	/// <summary>
	/// Whether the value is null.
	/// </summary>
	public bool IsNull => Kind == SomlValueKind.Null;

	/// <summary>
	/// Reads a document. Returns an object or an array, because a document is always a collection.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid.</exception>
	public static SomlValue Parse(string text)
	{
		ArgumentNullException.ThrowIfNull(text);
		return Parser.ParseDocument(text);
	}

	/// <summary>
	/// Reads a document from UTF-8 bytes. Returns an object or an array, because a document is always a collection.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid, including when the bytes are not valid UTF-8 or start with a byte order mark.</exception>
	public static SomlValue Parse(ReadOnlySpan<byte> utf8) => Parser.ParseDocument(Parser.Decode(utf8));

	/// <summary>
	/// Reads a document from a stream of UTF-8 bytes. Returns an object or an array, because a document is always a collection.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid, including when the bytes are not valid UTF-8 or start with a byte order mark.</exception>
	public static SomlValue Parse(Stream utf8)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		return Parser.ParseDocument(Parser.Decode(utf8));
	}

	/// <summary>
	/// Reads a document from a stream of UTF-8 bytes, reading the stream asynchronously. Returns an object or an array, because a document is always a collection.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid, including when the bytes are not valid UTF-8 or start with a byte order mark.</exception>
	public static async ValueTask<SomlValue> ParseAsync(Stream utf8, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		return Parser.ParseDocument(await Parser.DecodeAsync(utf8, cancellationToken).ConfigureAwait(false));
	}

	/// <summary>
	/// Gets a member of an object when the value is an object that has it. For a member that a document may leave out, as in <c>if (config.TryGetValue("timeout", out var timeout))</c>.
	/// </summary>
	public bool TryGetValue(string key, out SomlValue value)
	{
		if (TryGetObject(out var result))
		{
			return result.TryGetValue(key, out value);
		}

		value = default;
		return false;
	}

	/// <summary>
	/// Gets a member of an object.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an object.</exception>
	/// <exception cref="KeyNotFoundException">When the object has no member with the key.</exception>
	public SomlValue this[string key] => GetObject()[key];

	/// <summary>
	/// Gets an item of an array.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an array.</exception>
	/// <exception cref="ArgumentOutOfRangeException">When the index is outside the array.</exception>
	public SomlValue this[int index] => GetArray()[index];

	/// <summary>
	/// The value as a string.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not a string.</exception>
	public string GetString() => TryGetString(out var value) ? value : throw WrongKind("a string");

	/// <summary>
	/// The value as a 64-bit integer.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an int. A float is not converted, because <c>3.0</c> and <c>3</c> are different values.</exception>
	public long GetInt64() => TryGetInt64(out var value) ? value : throw WrongKind("an int");

	/// <summary>
	/// The value as a 64-bit floating-point number. An int converts when the conversion is exact.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not a float, or is an int that a <see cref="double"/> cannot hold exactly.</exception>
	public double GetDouble() => TryGetDouble(out var value) ? value : throw WrongKind("a float");

	/// <summary>
	/// The value as a bool.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not a bool.</exception>
	public bool GetBoolean() => TryGetBoolean(out var value) ? value : throw WrongKind("a bool");

	/// <summary>
	/// The value as a Gregorian calendar date, without conversion through an instant.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not a date.</exception>
	public DateOnly GetDate() => TryGetDate(out var value) ? value : throw WrongKind("a date");

	/// <summary>
	/// The value as an instant.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an instant.</exception>
	public SomlInstant GetInstant() => TryGetInstant(out var value) ? value : throw WrongKind("an instant");

	/// <summary>
	/// The value as a duration.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not a duration.</exception>
	public SomlDuration GetDuration() => TryGetDuration(out var value) ? value : throw WrongKind("a duration");

	/// <summary>
	/// The value as an array.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an array.</exception>
	public SomlArray GetArray() => TryGetArray(out var value) ? value : throw WrongKind("an array");

	/// <summary>
	/// The value as an object.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an object.</exception>
	public SomlObject GetObject() => TryGetObject(out var value) ? value : throw WrongKind("an object");

	/// <summary>
	/// Gets the value as a string when it is one.
	/// </summary>
	public bool TryGetString([NotNullWhen(true)] out string? value)
	{
		value = _reference as string;
		return value is not null;
	}

	/// <summary>
	/// Gets the value as a 64-bit integer when it is an int.
	/// </summary>
	public bool TryGetInt64(out long value)
	{
		value = Kind == SomlValueKind.Integer ? _bits : 0;
		return Kind == SomlValueKind.Integer;
	}

	/// <summary>
	/// Gets the value as a 64-bit floating-point number when it is a float, or an int that a <see cref="double"/> holds exactly.
	/// </summary>
	public bool TryGetDouble(out double value)
	{
		switch (Kind)
		{
			case SomlValueKind.Float:
			{
				value = BitConverter.Int64BitsToDouble(_bits);
				return true;
			}
			case SomlValueKind.Integer when IsExactDouble(_bits):
			{
				value = _bits;
				return true;
			}
			default:
			{
				value = 0;
				return false;
			}
		}
	}

	/*
	Whether a double holds an int exactly. `(double)long.MaxValue` rounds up to 2^63, which is outside the range of a long, so it is checked before the conversion back.
	*/
	internal static bool IsExactDouble(long value)
	{
		// Note: Not using double.CreateChecked because it checks range, not whether rounding loses integer precision.
		var converted = (double)value;
		return converted < 9_223_372_036_854_775_808.0 && (long)converted == value;
	}

	/// <summary>
	/// Gets the value as a bool when it is one.
	/// </summary>
	public bool TryGetBoolean(out bool value)
	{
		value = Kind == SomlValueKind.Boolean && _bits != 0;
		return Kind == SomlValueKind.Boolean;
	}

	/// <summary>
	/// Gets the value as a date when it is one.
	/// </summary>
	public bool TryGetDate(out DateOnly value)
	{
		value = Kind == SomlValueKind.Date ? DateOnly.FromDayNumber((int)_bits) : default;
		return Kind == SomlValueKind.Date;
	}

	/// <summary>
	/// Gets the value as an instant when it is one.
	/// </summary>
	public bool TryGetInstant(out SomlInstant value)
	{
		value = Kind == SomlValueKind.Instant ? new SomlInstant(_bits, _nanosecond) : default;
		return Kind == SomlValueKind.Instant;
	}

	/// <summary>
	/// Gets the value as a duration when it is one.
	/// </summary>
	public bool TryGetDuration(out SomlDuration value)
	{
		value = Kind == SomlValueKind.Duration ? new SomlDuration(_bits) : default;
		return Kind == SomlValueKind.Duration;
	}

	/// <summary>
	/// Gets the value as an array when it is one.
	/// </summary>
	public bool TryGetArray([NotNullWhen(true)] out SomlArray? value)
	{
		value = _reference as SomlArray;
		return value is not null;
	}

	/// <summary>
	/// Gets the value as an object when it is one.
	/// </summary>
	public bool TryGetObject([NotNullWhen(true)] out SomlObject? value)
	{
		value = _reference as SomlObject;
		return value is not null;
	}

	private InvalidOperationException WrongKind(string expected) => new($"Expected {expected}, but the value is {DescribeKind(Kind)}");

	internal static string DescribeKind(SomlValueKind kind) => kind switch
	{
		SomlValueKind.Null => "null",
		SomlValueKind.String => "a string",
		SomlValueKind.Integer => "an int",
		SomlValueKind.Float => "a float",
		SomlValueKind.Boolean => "a bool",
		SomlValueKind.Date => "a date",
		SomlValueKind.Instant => "an instant",
		SomlValueKind.Duration => "a duration",
		SomlValueKind.Array => "an array",
		_ => "an object",
	};

	/// <summary>
	/// The value as SOML text, as it is written as the value of a member: on one line for a scalar or an empty collection, and with one member or item per line otherwise. Members keep their order.
	/// </summary>
	/// <remarks>
	/// It is for showing a value, so it does not throw for a string that SOML cannot represent: a carriage return or a lone surrogate is shown as an escape, such as <c>\u{d}</c>, which SOML does not accept. Use <see cref="ToDocumentString(bool)"/> for text that must read back.
	/// </remarks>
	/// <exception cref="SomlException">When the value is nested more than 100 levels deep, as a value that holds itself is.</exception>
	public override string ToString() => Kind switch
	{
		SomlValueKind.Null => "null",
		SomlValueKind.Boolean => _bits == 0 ? "false" : "true",
		SomlValueKind.Integer => _bits.ToString(CultureInfo.InvariantCulture),
		SomlValueKind.Float => SomlWriter.FormatFloat(GetDouble()),
		SomlValueKind.String => SomlWriter.FormatString(GetString(), isLenient: true),
		SomlValueKind.Date => GetDate().ToString("O", CultureInfo.InvariantCulture),
		SomlValueKind.Instant => GetInstant().ToString(),
		SomlValueKind.Duration => GetDuration().ToString(),
		_ => FormatCollection(),
	};

	private string FormatCollection()
	{
		var builder = new StringBuilder();
		SomlWriter.WriteValue(builder, this, canonical: false, isLenient: true);
		return builder.ToString();
	}

	/// <summary>
	/// The value as a SOML document, which ends with one line feed. The members of objects keep their order, unless <paramref name="canonical"/> sorts them by key, which gives canonical form: equal values give the same text, for hashing, signing, or comparing.
	/// </summary>
	/// <exception cref="SomlException">When the value is not an object or an array, because a document is a collection, or when it holds what SOML cannot represent: a string or a key with a carriage return or a lone surrogate, or nesting deeper than 100 levels.</exception>
	public string ToDocumentString(bool canonical = false) => SomlWriter.WriteDocument(this, canonical);

	private string DebuggerDisplay => Kind switch
	{
		SomlValueKind.Array => $"Array, Count = {((SomlArray)_reference!).Count}",
		SomlValueKind.Object => $"Object, Count = {((SomlObject)_reference!).Count}",
		_ => $"{Kind}: {ToString()}",
	};

	/// <summary>
	/// Whether two values are deeply equal. Two objects are equal when they have the same members in any order.
	/// </summary>
	public bool Equals(SomlValue other)
	{
		if (Kind != other.Kind)
		{
			return false;
		}

		return Kind switch
		{
			SomlValueKind.Null => true,
			SomlValueKind.String => (string)_reference! == (string)other._reference!,
			SomlValueKind.Array => ((SomlArray)_reference!).ContentEquals((SomlArray)other._reference!),
			SomlValueKind.Object => ((SomlObject)_reference!).ContentEquals((SomlObject)other._reference!),
			// Comparing the bits of a float is comparing its value, because the conversion from a double and the parser turn negative zero into zero and refuse NaN.
			_ => _bits == other._bits && _nanosecond == other._nanosecond,
		};
	}

	/// <inheritdoc/>
	public override bool Equals([NotNullWhen(true)] object? obj) => obj is SomlValue other && Equals(other);

	/// <summary>
	/// A hash code that is consistent with <see cref="Equals(SomlValue)"/>, so an object's hash code does not depend on the order of its members.
	/// </summary>
	public override int GetHashCode() => Kind switch
	{
		SomlValueKind.Null => 0,
		SomlValueKind.String => HashCode.Combine(Kind, _reference),
		SomlValueKind.Array => HashCode.Combine(Kind, ((SomlArray)_reference!).ContentHashCode()),
		SomlValueKind.Object => HashCode.Combine(Kind, ((SomlObject)_reference!).ContentHashCode()),
		_ => HashCode.Combine(Kind, _bits, _nanosecond),
	};

	/// <inheritdoc/>
	public static bool operator ==(SomlValue left, SomlValue right) => left.Equals(right);

	/// <inheritdoc/>
	public static bool operator !=(SomlValue left, SomlValue right) => !left.Equals(right);

	internal static SomlValue FromString(string value) => new(SomlValueKind.String, value);

	internal static SomlValue FromFloat(double value) => new(SomlValueKind.Float, bits: BitConverter.DoubleToInt64Bits(value));

	/// <summary>
	/// A string value, or null for a null string.
	/// </summary>
	public static implicit operator SomlValue(string? value) => value is null ? Null : FromString(value);

	/// <summary>
	/// An int value.
	/// </summary>
	public static implicit operator SomlValue(long value) => new(SomlValueKind.Integer, bits: value);

	/// <summary>
	/// An int value.
	/// </summary>
	public static implicit operator SomlValue(int value) => (long)value;

	/// <summary>
	/// An int value.
	/// </summary>
	public static implicit operator SomlValue(uint value) => (long)value;

	/// <summary>
	/// An int value. With the conversions from <see cref="uint"/> and <see cref="ulong"/>, C# would otherwise find a <see cref="byte"/> ambiguous.
	/// </summary>
	public static implicit operator SomlValue(byte value) => (long)value;

	/// <summary>
	/// An int value. With the conversions from <see cref="uint"/> and <see cref="ulong"/>, C# would otherwise find a <see cref="ushort"/> ambiguous.
	/// </summary>
	public static implicit operator SomlValue(ushort value) => (long)value;

	/// <summary>
	/// An int value. Without this conversion, C# would convert a <see cref="ulong"/> to a float.
	/// </summary>
	/// <exception cref="OverflowException">When the value is larger than <see cref="long.MaxValue"/>, because a SOML int is 64-bit and signed.</exception>
	public static implicit operator SomlValue(ulong value) => value > long.MaxValue ? throw new OverflowException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The value {value} is outside the 64-bit range of a SOML int {Message.IntRange}")) : new(SomlValueKind.Integer, bits: (long)value);

	/// <summary>
	/// A string value of one character. Without this conversion, C# would convert a <see cref="char"/> to an int.
	/// </summary>
	public static implicit operator SomlValue(char value) => FromString(value.ToString());

	/// <summary>
	/// A float value, with the shortest digits of the <see cref="float"/>, so <c>0.1f</c> is <c>0.1</c> rather than the double it widens to.
	/// </summary>
	/// <exception cref="ArgumentException">When the value is NaN, which SOML cannot represent.</exception>
	public static implicit operator SomlValue(float value)
	{
		// An infinity converts as it is, and NaN throws in the conversion from a double.
		if (!float.IsFinite(value))
		{
			return (double)value;
		}

		return SomlWriter.ToShortestDouble(value);
	}

	/// <summary>
	/// A float value. Negative zero becomes zero, because SOML has one zero.
	/// </summary>
	/// <exception cref="ArgumentException">When the value is NaN, which SOML cannot represent.</exception>
	public static implicit operator SomlValue(double value)
	{
		if (double.IsNaN(value))
		{
			throw new ArgumentException(Message.NaN, nameof(value));
		}

		// Adding zero turns negative zero into zero and leaves every other value as it is.
		return FromFloat(value + 0.0);
	}

	/// <summary>
	/// A bool value.
	/// </summary>
	public static implicit operator SomlValue(bool value) => new(SomlValueKind.Boolean, bits: value ? 1 : 0);

	/// <summary>
	/// A Gregorian date value, preserving the exact year, month, and day.
	/// </summary>
	public static implicit operator SomlValue(DateOnly value) => new(SomlValueKind.Date, bits: value.DayNumber);

	/// <summary>
	/// An instant value.
	/// </summary>
	public static implicit operator SomlValue(SomlInstant value) => new(SomlValueKind.Instant, bits: value.UnixSeconds, nanosecond: value.Nanosecond);

	/// <summary>
	/// A duration value.
	/// </summary>
	public static implicit operator SomlValue(SomlDuration value) => new(SomlValueKind.Duration, bits: value.Nanoseconds);

	/// <summary>
	/// An array value, or null for a null array.
	/// </summary>
	public static implicit operator SomlValue(SomlArray? value) => value is null ? Null : new(SomlValueKind.Array, value);

	/// <summary>
	/// An object value, or null for a null object.
	/// </summary>
	public static implicit operator SomlValue(SomlObject? value) => value is null ? Null : new(SomlValueKind.Object, value);

	/// <summary>
	/// The value as a string, or <c>null</c> when it is null.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is neither a string nor null.</exception>
	public static explicit operator string?(SomlValue value) => value.IsNull ? null : value.GetString();

	/// <inheritdoc cref="GetInt64"/>
	public static explicit operator long(SomlValue value) => value.GetInt64();

	/// <summary>
	/// The value as an int, range-checked for <see cref="int"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an int.</exception>
	/// <exception cref="OverflowException">When the int is outside the range of <see cref="int"/>.</exception>
	public static explicit operator int(SomlValue value) => checked((int)value.GetInt64());

	/// <summary>
	/// The value as an int, range-checked for <see cref="uint"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an int.</exception>
	/// <exception cref="OverflowException">When the int is outside the range of <see cref="uint"/>.</exception>
	public static explicit operator uint(SomlValue value) => checked((uint)value.GetInt64());

	/// <summary>
	/// The value as an int, range-checked for <see cref="short"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an int.</exception>
	/// <exception cref="OverflowException">When the int is outside the range of <see cref="short"/>.</exception>
	public static explicit operator short(SomlValue value) => checked((short)value.GetInt64());

	/// <summary>
	/// The value as an int, range-checked for <see cref="ushort"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an int.</exception>
	/// <exception cref="OverflowException">When the int is outside the range of <see cref="ushort"/>.</exception>
	public static explicit operator ushort(SomlValue value) => checked((ushort)value.GetInt64());

	/// <summary>
	/// The value as an int, range-checked for <see cref="byte"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an int.</exception>
	/// <exception cref="OverflowException">When the int is outside the range of <see cref="byte"/>.</exception>
	public static explicit operator byte(SomlValue value) => checked((byte)value.GetInt64());

	/// <summary>
	/// The value as an int, range-checked for <see cref="sbyte"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an int.</exception>
	/// <exception cref="OverflowException">When the int is outside the range of <see cref="sbyte"/>.</exception>
	public static explicit operator sbyte(SomlValue value) => checked((sbyte)value.GetInt64());

	/// <summary>
	/// The value as an int, range-checked for <see cref="ulong"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not an int.</exception>
	/// <exception cref="OverflowException">When the int is outside the range of <see cref="ulong"/>.</exception>
	public static explicit operator ulong(SomlValue value) => checked((ulong)value.GetInt64());

	/// <inheritdoc cref="GetDouble"/>
	public static explicit operator double(SomlValue value) => value.GetDouble();

	/// <summary>
	/// The value as a float, or an int that a <see cref="double"/> holds exactly, as a <see cref="float"/>, rounded as a cast from <see cref="double"/> rounds it.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is neither a float nor such an int.</exception>
	public static explicit operator float(SomlValue value) => (float)value.GetDouble();

	/// <summary>
	/// The value as a <see cref="decimal"/>: an int exactly, and a float as its shortest digits, so <c>0.1</c> is <c>0.1m</c>, as <see cref="SomlSerializer"/> reads it.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is neither an int nor a float.</exception>
	/// <exception cref="OverflowException">When the float is outside the range of <see cref="decimal"/>, including an infinity and a nonzero float too small for one.</exception>
	public static explicit operator decimal(SomlValue value)
	{
		if (value.TryGetInt64(out var integer))
		{
			return integer;
		}

		var floating = value.GetDouble();

		if (SomlWriter.TryConvertToDecimal(floating, out var result))
		{
			return result;
		}

		throw new OverflowException($"The float {SomlWriter.FormatFloat(floating)} is outside the range of decimal");
	}

	/// <summary>
	/// The value as a string of one UTF-16 code unit, as the conversion from <see cref="char"/> makes it.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the value is not a string of one UTF-16 code unit.</exception>
	public static explicit operator char(SomlValue value) => value.GetString() is [var character] ? character : throw new InvalidOperationException("Expected a string of one UTF-16 code unit");

	/// <inheritdoc cref="GetBoolean"/>
	public static explicit operator bool(SomlValue value) => value.GetBoolean();

	/// <inheritdoc cref="GetDate"/>
	public static explicit operator DateOnly(SomlValue value) => value.GetDate();

	/// <inheritdoc cref="GetInstant"/>
	public static explicit operator SomlInstant(SomlValue value) => value.GetInstant();

	/// <inheritdoc cref="GetDuration"/>
	public static explicit operator SomlDuration(SomlValue value) => value.GetDuration();
}
