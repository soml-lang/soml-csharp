using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using Soml.Serialization;

namespace Soml;

/// <summary>
/// Reads SOML documents into .NET types and writes .NET values as SOML documents.
/// </summary>
/// <remarks>
/// <para>A document is read and checked as a whole before any of it is bound to the type. Keys are kebab-case by default, so the property <c>AllowedOrigins</c> reads the key <c>allowed-origins</c>. See <see cref="SomlSerializerOptions"/>.</para>
/// <para>The types it maps:</para>
/// <list type="bullet">
/// <item><description>A string from <see cref="string"/>, <see cref="char"/>, <see cref="Uri"/>, an enum (by member name, through the naming policy), and any type that is <see cref="IParsable{TSelf}"/> and <see cref="IFormattable"/>, such as <see cref="Guid"/>. A <see cref="TimeOnly"/> is <c>HH:mm:ss</c> with an optional fraction, and <see cref="Version"/> is a string too.</description></item>
/// <item><description>An int from the integer types, range-checked. A float from <see cref="double"/>, <see cref="float"/>, <see cref="Half"/>, and <see cref="decimal"/>. An int reads into a floating-point type when the conversion is exact, and a float never reads into an integer type.</description></item>
/// <item><description>A native Gregorian date from <see cref="DateOnly"/>, preserving year, month, and day without a time or timezone.</description></item>
/// <item><description>An instant from <see cref="SomlInstant"/>, <see cref="DateTimeOffset"/>, and <see cref="DateTime"/>, and a duration from <see cref="SomlDuration"/> and <see cref="TimeSpan"/>. A value finer than 100 nanoseconds does not read into the BCL types, rather than being rounded.</description></item>
/// <item><description>An array from arrays, <see cref="List{T}"/>, <see cref="HashSet{T}"/>, their interfaces, and [Flags] enums. An object from classes, structs, records, and dictionaries whose keys are strings, enums, dates, instants or durations in canonical form, or a type that is <see cref="IParsable{TSelf}"/> and <see cref="IFormattable"/>, such as <see cref="int"/>.</description></item>
/// <item><description><see cref="SomlValue"/>, <see cref="SomlObject"/>, and <see cref="SomlArray"/> as they are, and <see cref="object"/> as plain .NET values: <see cref="string"/>, <see cref="long"/>, <see cref="double"/>, <see cref="bool"/>, null, <see cref="DateOnly"/>, <see cref="SomlInstant"/>, <see cref="SomlDuration"/>, <see cref="List{T}"/> of <see cref="object"/>, and <see cref="Dictionary{TKey, TValue}"/> of <see cref="string"/> and <see cref="object"/>.</description></item>
/// </list>
/// <para>Omitted members keep their initial values. A <c>required</c> member must be present, even when its constructor supplies a default. An omitted constructor parameter uses its optional default, or null if it is nullable; otherwise it must be present. Explicit null for a non-nullable member or parameter is an error.</para>
/// <para>Stream overloads leave the stream open. Deserialization reads from its current position to the end before binding the document. Cancellation tokens apply to stream I/O.</para>
/// </remarks>
public static class SomlSerializer
{
	internal const string ReflectionMessage = "SomlSerializer uses reflection, which trimming and Native AOT cannot analyze. Use a source-generated SomlTypeInfo<T> or SomlValue instead.";

	/// <summary>
	/// Reads a document into a value of type <typeparamref name="T"/>.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid.</exception>
	/// <exception cref="SomlException">When the document does not fit the type.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static T Deserialize<T>(string text, SomlSerializerOptions? options = null) => ReadDocument(text, options, static (value, options) => Deserialize<T>(value, options));

	/// <summary>
	/// Reads a document from UTF-8 bytes into a value of type <typeparamref name="T"/>.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid, including when the bytes are not valid UTF-8 or start with a byte order mark.</exception>
	/// <exception cref="SomlException">When the document does not fit the type.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static T Deserialize<T>(ReadOnlySpan<byte> utf8, SomlSerializerOptions? options = null) => Deserialize<T>(Parser.Decode(utf8), options);

	/// <summary>
	/// Reads a document from a stream of UTF-8 bytes into a value of type <typeparamref name="T"/>.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid, including when the bytes are not valid UTF-8 or start with a byte order mark.</exception>
	/// <exception cref="SomlException">When the document does not fit the type.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static T Deserialize<T>(Stream utf8, SomlSerializerOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		return Deserialize<T>(Parser.Decode(utf8), options);
	}

	/// <summary>
	/// Reads a document from a stream of UTF-8 bytes into a value of type <typeparamref name="T"/>, reading the stream asynchronously.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid, including when the bytes are not valid UTF-8 or start with a byte order mark.</exception>
	/// <exception cref="SomlException">When the document does not fit the type.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static async ValueTask<T> DeserializeAsync<T>(Stream utf8, SomlSerializerOptions? options = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		return Deserialize<T>(await Parser.DecodeAsync(utf8, cancellationToken).ConfigureAwait(false), options);
	}

	/// <summary>
	/// Reads a value, such as one from <see cref="SomlValue.Parse(string)"/>, into a value of type <typeparamref name="T"/>.
	/// </summary>
	/// <exception cref="SomlException">When the value does not fit the type.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static T Deserialize<T>(SomlValue value, SomlSerializerOptions? options = null)
	{
		options ??= SomlSerializerOptions.Default;

		if (Converter.Scalar<T>() is { } scalar)
		{
			return new SomlConverterState(options).Read(value, scalar, isNullable: SomlConverterState.CanBeNull<T>());
		}

		return (T)new SerializerState(options).Read(value, typeof(T), isNullable: Converter.CanBeNull(typeof(T)))!;
	}

	/// <summary>
	/// Reads a document into a value of a type.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid.</exception>
	/// <exception cref="SomlException">When the document does not fit the type.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static object? Deserialize(string text, Type type, SomlSerializerOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(type);
		return ReadDocument(text, (type, options), static (value, argument) => Deserialize(value, argument.type, argument.options));
	}

	/// <summary>
	/// Reads a value into a value of a type.
	/// </summary>
	/// <exception cref="SomlException">When the value does not fit the type.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static object? Deserialize(SomlValue value, Type type, SomlSerializerOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(type);
		options ??= SomlSerializerOptions.Default;
		var state = new SerializerState(options);
		return state.Read(value, type, isNullable: Converter.CanBeNull(type));
	}

	/// <summary>
	/// Writes a value as a document, which ends with one line feed. The value must be written as an object or an array, because a document is a collection.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written: a value that SOML cannot represent, such as NaN or a string with a carriage return, nesting deeper than 100 levels, a type that cannot be written, or a value that is not a collection.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static string Serialize<T>(T value, SomlSerializerOptions? options = null)
	{
		options ??= SomlSerializerOptions.Default;
		return SomlWriter.WriteDocument(SerializeToValue(value, options), options.Canonical);
	}

	/// <summary>
	/// Writes a value as a document in UTF-8 bytes, which end with one line feed.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static byte[] SerializeToUtf8Bytes<T>(T value, SomlSerializerOptions? options = null) => Encoding.UTF8.GetBytes(Serialize(value, options));

	/// <summary>
	/// Writes a value as a document to a stream, in UTF-8.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static void Serialize<T>(Stream utf8, T value, SomlSerializerOptions? options = null)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		WriteUtf8(utf8, Serialize(value, options));
	}

	/// <summary>
	/// Writes a value as a document to a stream, in UTF-8, writing the stream asynchronously.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static async Task SerializeAsync<T>(Stream utf8, T value, SomlSerializerOptions? options = null, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		await WriteUtf8Async(utf8, Serialize(value, options), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Converts a .NET value to a <see cref="SomlValue"/>, which may be a scalar.
	/// </summary>
	/// <remarks>String contents and existing SOML containers are checked when written to a document.</remarks>
	/// <exception cref="SomlException">When the value cannot be converted.</exception>
	[RequiresUnreferencedCode(ReflectionMessage)]
	[RequiresDynamicCode(ReflectionMessage)]
	public static SomlValue SerializeToValue<T>(T value, SomlSerializerOptions? options = null)
	{
		options ??= SomlSerializerOptions.Default;

		if (typeof(T).IsSealed && Converter.Scalar<T>() is { } scalar)
		{
			return new SomlConverterState(options).Write(value, scalar);
		}

		return new SerializerState(options).Write(value, value?.GetType() ?? typeof(T));
	}

	/// <summary>
	/// Reads a document into a value of type <typeparamref name="T"/>, with the code that the source generator wrote, so it needs no reflection.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid.</exception>
	/// <exception cref="SomlException">When the document does not fit the type.</exception>
	public static T Deserialize<T>(string text, SomlTypeInfo<T> typeInfo)
	{
		ArgumentNullException.ThrowIfNull(typeInfo);
		return ReadDocument(text, typeInfo, static (value, info) => Deserialize(value, info));
	}

	/// <summary>
	/// Reads a document from UTF-8 bytes into a value of type <typeparamref name="T"/>, with the code that the source generator wrote.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid, including when the bytes are not valid UTF-8 or start with a byte order mark.</exception>
	/// <exception cref="SomlException">When the document does not fit the type.</exception>
	public static T Deserialize<T>(ReadOnlySpan<byte> utf8, SomlTypeInfo<T> typeInfo)
	{
		ArgumentNullException.ThrowIfNull(typeInfo);
		return Deserialize(Parser.Decode(utf8), typeInfo);
	}

	/// <summary>
	/// Reads a document from a stream of UTF-8 bytes into a value of type <typeparamref name="T"/>, with the code that the source generator wrote.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid, including when the bytes are not valid UTF-8 or start with a byte order mark.</exception>
	/// <exception cref="SomlException">When the document does not fit the type.</exception>
	public static T Deserialize<T>(Stream utf8, SomlTypeInfo<T> typeInfo)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		ArgumentNullException.ThrowIfNull(typeInfo);
		return Deserialize(Parser.Decode(utf8), typeInfo);
	}

	/// <summary>
	/// Reads a document from a stream of UTF-8 bytes into a value of type <typeparamref name="T"/>, with the code that the source generator wrote, reading the stream asynchronously.
	/// </summary>
	/// <exception cref="SomlParseException">When the document is not valid, including when the bytes are not valid UTF-8 or start with a byte order mark.</exception>
	/// <exception cref="SomlException">When the document does not fit the type.</exception>
	public static async ValueTask<T> DeserializeAsync<T>(Stream utf8, SomlTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		ArgumentNullException.ThrowIfNull(typeInfo);
		return Deserialize(await Parser.DecodeAsync(utf8, cancellationToken).ConfigureAwait(false), typeInfo);
	}

	/// <summary>
	/// Reads a value into a value of type <typeparamref name="T"/>, with the code that the source generator wrote.
	/// </summary>
	/// <exception cref="SomlException">When the value does not fit the type.</exception>
	public static T Deserialize<T>(SomlValue value, SomlTypeInfo<T> typeInfo)
	{
		ArgumentNullException.ThrowIfNull(typeInfo);
		return new SomlConverterState(typeInfo.Options).Read(value, typeInfo.Converter, isNullable: SomlConverterState.CanBeNull<T>());
	}

	/// <summary>
	/// Writes a value as a document, with the code that the source generator wrote, so it needs no reflection. The document ends with one line feed.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written.</exception>
	public static string Serialize<T>(T value, SomlTypeInfo<T> typeInfo)
	{
		ArgumentNullException.ThrowIfNull(typeInfo);
		return SomlWriter.WriteDocument(SerializeToValue(value, typeInfo), typeInfo.Options.Canonical);
	}

	/// <summary>
	/// Writes a value as a document in UTF-8 bytes, with the code that the source generator wrote.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written.</exception>
	public static byte[] SerializeToUtf8Bytes<T>(T value, SomlTypeInfo<T> typeInfo) => Encoding.UTF8.GetBytes(Serialize(value, typeInfo));

	/// <summary>
	/// Writes a value as a document to a stream, in UTF-8, with the code that the source generator wrote.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written.</exception>
	public static void Serialize<T>(Stream utf8, T value, SomlTypeInfo<T> typeInfo)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		WriteUtf8(utf8, Serialize(value, typeInfo));
	}

	/// <summary>
	/// Writes a value as a document to a stream, in UTF-8, with the code that the source generator wrote, writing the stream asynchronously.
	/// </summary>
	/// <exception cref="SomlException">When the value cannot be written.</exception>
	public static async Task SerializeAsync<T>(Stream utf8, T value, SomlTypeInfo<T> typeInfo, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		ArgumentNullException.ThrowIfNull(typeInfo);
		await WriteUtf8Async(utf8, Serialize(value, typeInfo), cancellationToken).ConfigureAwait(false);
	}

	/// <summary>
	/// Converts a value to a <see cref="SomlValue"/>, with the code that the source generator wrote. Unlike a document, the value may be a scalar.
	/// </summary>
	/// <remarks>String contents and existing SOML containers are checked when written to a document.</remarks>
	/// <exception cref="SomlException">When the value cannot be converted.</exception>
	public static SomlValue SerializeToValue<T>(T value, SomlTypeInfo<T> typeInfo)
	{
		ArgumentNullException.ThrowIfNull(typeInfo);
		return new SomlConverterState(typeInfo.Options).Write(value, typeInfo.Converter);
	}

	/*
	Encodes a fully validated document into bounded chunks, preserving UTF-8 sequences across chunks. The rented buffer is cleared and returned even when the stream fails. The stream remains open.

	Note: Not using Utf8.FromUtf16 because full-stream probes had mixed timings with higher large-ASCII medians on .NET 10 ARM64, despite lower allocations.

	Note: Not using StreamWriter because it added buffers and small-write overhead in the stream comparisons.
	Note: Not using StringBuilder.GetChunks because preserving full validation before output would require builder-producing binding paths and nested chunk/encoder loops.
	*/
	private static void WriteUtf8(Stream stream, string text)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(text.Length, 16 * 1024));
		var encoder = Encoding.UTF8.GetEncoder();

		try
		{
			for (var position = 0; position < text.Length;)
			{
				encoder.Convert(text.AsSpan(position), buffer, flush: true, out var charactersUsed, out var bytesUsed, out _);
				position += charactersUsed;
				stream.Write(buffer.AsSpan(0, bytesUsed));
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
		}
	}

	private static async Task WriteUtf8Async(Stream stream, string text, CancellationToken cancellationToken)
	{
		var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(text.Length, 16 * 1024));
		var encoder = Encoding.UTF8.GetEncoder();

		try
		{
			for (var position = 0; position < text.Length;)
			{
				cancellationToken.ThrowIfCancellationRequested();
				encoder.Convert(text.AsSpan(position), buffer, flush: true, out var charactersUsed, out var bytesUsed, out _);
				position += charactersUsed;
				await stream.WriteAsync(buffer.AsMemory(0, bytesUsed), cancellationToken).ConfigureAwait(false);
			}
		}
		finally
		{
			ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
		}
	}

	/*
	Reads a document, and the value with `read`. An error about a value gets the line and column of the value, which are found only now, so that reading a valid document costs nothing for them.
	*/
	private static T ReadDocument<T, TArgument>(string text, TArgument argument, Func<SomlValue, TArgument, T> read)
	{
		var value = SomlValue.Parse(text);

		try
		{
			return read(value, argument);
		}
		catch (SomlException exception) when (exception is not SomlParseException && exception.PathSegments is not null)
		{
			throw exception.WithLocation(text);
		}
	}
}
