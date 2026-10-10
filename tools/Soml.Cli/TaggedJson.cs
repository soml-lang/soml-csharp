using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Soml.Cli;

/// <summary>
/// The tagged JSON of the SOML conformance suite, as in toml-test: a scalar is <c>{"type": "int", "value": "3"}</c>, and objects and arrays are plain JSON.
/// </summary>
/// <remarks>
/// The types are <c>string</c>, <c>int</c>, <c>float</c>, <c>bool</c>, <c>null</c>, <c>date</c>, <c>instant</c>, and <c>duration</c>. A float's value is its canonical spelling, including <c>infinity</c> and <c>-infinity</c>, a date's is <c>YYYY-MM-DD</c>, an instant's is its canonical UTC form, and a duration's is its length in nanoseconds as a decimal int.
/// </remarks>
public static class TaggedJson
{
	// Note: Not using cached JsonEncodedText property names because they added startup allocations even for read-only use.
	private static readonly JsonWriterOptions WriterOptions = new()
	{
		Indented = true,
		IndentCharacter = '\t',
		IndentSize = 1,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
		MaxDepth = 101,
	};

	private static readonly JsonDocumentOptions DocumentOptions = new()
	{
		AllowDuplicateProperties = false,

		// A document is at most 100 levels deep, and a tagged scalar adds one level, so 128 is enough.
		MaxDepth = 128,
	};

	/// <summary>
	/// Writes a value as tagged JSON, indented with tabs, ending with a line feed.
	/// </summary>
	public static void Write(SomlValue value, Stream output)
	{
		ArgumentNullException.ThrowIfNull(output);

		using (var writer = new Utf8JsonWriter(output, WriterOptions))
		{
			Write(value, writer);
		}

		output.WriteByte((byte)'\n');
	}

	private static void Write(SomlValue value, Utf8JsonWriter writer)
	{
		switch (value.Kind)
		{
			case SomlValueKind.Object:
			{
				writer.WriteStartObject();

				foreach (var (key, member) in value.GetObject())
				{
					writer.WritePropertyName(key);
					Write(member, writer);
				}

				writer.WriteEndObject();
				break;
			}
			case SomlValueKind.Array:
			{
				writer.WriteStartArray();

				var items = value.GetArray();

				for (var index = 0; index < items.Count; index++)
				{
					Write(items[index], writer);
				}

				writer.WriteEndArray();
				break;
			}
			case SomlValueKind.Integer:
			{
				WriteScalar(value.GetInt64(), "int"u8, writer);
				break;
			}
			case SomlValueKind.Instant:
			{
				WriteScalar(value.GetInstant(), "instant"u8, writer);
				break;
			}
			case SomlValueKind.Duration:
			{
				WriteScalar(value.GetDuration().Nanoseconds, "duration"u8, writer);
				break;
			}
			default:
			{
				var (type, text) = Tag(value);
				writer.WriteStartObject();
				writer.WriteString("type"u8, type);
				writer.WriteString("value"u8, text);
				writer.WriteEndObject();
				break;
			}
		}
	}

	/*
	Every integer, duration nanosecond count, and canonical instant fits in 40 characters, so writing them needs no temporary string.
	*/
	private static void WriteScalar<T>(T value, ReadOnlySpan<byte> type, Utf8JsonWriter writer) where T : IUtf8SpanFormattable
	{
		Span<byte> text = stackalloc byte[40];
		value.TryFormat(text, out var length, default, CultureInfo.InvariantCulture);
		writer.WriteStartObject();
		writer.WriteString("type"u8, type);
		writer.WriteString("value"u8, text[..length]);
		writer.WriteEndObject();
	}

	private static (string Type, string Value) Tag(SomlValue value) => value.Kind switch
	{
		SomlValueKind.String => ("string", value.GetString()),
		SomlValueKind.Float => ("float", value.ToString()),
		SomlValueKind.Date => ("date", value.ToString()),
		SomlValueKind.Boolean => ("bool", value.GetBoolean() ? "true" : "false"),
		_ => ("null", "null"),
	};

	/// <summary>
	/// Reads tagged JSON as a value. A tagged scalar must be in canonical form, as <see cref="Write(SomlValue, Stream)"/> writes it, so <c>{"type": "int", "value": "+5"}</c> is an error.
	/// </summary>
	/// <exception cref="FormatException">When the input is not tagged JSON, or a tagged scalar is not in canonical form.</exception>
	public static SomlValue Read(ReadOnlySpan<byte> utf8)
	{
		// Note: Not using a scoped JsonDocument for span reads because it reduced metadata allocations but slowed the measured single-scalar read on .NET 10 ARM64.
		JsonElement node;

		try
		{
			node = JsonElement.Parse(utf8, DocumentOptions);
		}
		// The check for duplicate keys throws `InvalidOperationException` for a key with a lone surrogate.
		catch (Exception exception) when (exception is JsonException or InvalidOperationException)
		{
			throw new FormatException($"The input is not valid JSON: {exception.Message}", exception);
		}

		return ReadDocument(node);
	}

	/// <summary>
	/// Reads tagged JSON from a UTF-8 stream, leaving the stream open.
	/// </summary>
	/// <exception cref="FormatException">When the input is not tagged JSON, or a tagged scalar is not in canonical form.</exception>
	public static SomlValue Read(Stream utf8)
	{
		ArgumentNullException.ThrowIfNull(utf8);
		// Do not size the allocation from an arbitrary stream's advertised length. The document borrows this buffer until it is disposed below.
		// Note: Not using JsonDocument.Parse(Stream) because it trusts the advertised length for its initial buffer rental.
		using var buffer = new MemoryStream();
		utf8.CopyTo(buffer, bufferSize: 16 * 1024);
		JsonDocument document;

		try
		{
			document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), DocumentOptions);
		}
		catch (Exception exception) when (exception is JsonException or InvalidOperationException)
		{
			throw new FormatException($"The input is not valid JSON: {exception.Message}", exception);
		}

		using (document)
		{
			return ReadDocument(document.RootElement);
		}
	}

	private static SomlValue ReadDocument(JsonElement node)
	{
		try
		{
			return Read(node);
		}
		catch (InvalidOperationException exception)
		{
			// `JsonElement` throws this for a string with a lone surrogate, such as `"\ud800"`, and for a value that is not a string where one is read.
			throw new FormatException(exception.Message, exception);
		}
	}

	private static SomlValue Read(JsonElement node)
	{
		switch (node.ValueKind)
		{
			case JsonValueKind.Array:
			{
				var result = new SomlArray(node.GetArrayLength());

				foreach (var item in node.EnumerateArray())
				{
					result.Add(Read(item));
				}

				return result;
			}
			case JsonValueKind.Object:
			{
				var count = node.GetPropertyCount();

				if (count == 2 && node.TryGetProperty("type"u8, out var type) && type.ValueKind == JsonValueKind.String && node.TryGetProperty("value"u8, out var text) && text.ValueKind == JsonValueKind.String)
				{
					return ReadScalar(type, text);
				}

				var result = new SomlObject(count);

				foreach (var member in node.EnumerateObject())
				{
					result.Add(member.Name, Read(member.Value));
				}

				return result;
			}
			default:
			{
				var found = node.ValueKind switch
				{
					JsonValueKind.String => "a string",
					JsonValueKind.Number => "a number",
					JsonValueKind.True => "true",
					JsonValueKind.False => "false",
					_ => "null",
				};
				throw new FormatException($"Expected an object, an array, or a tagged scalar, but found {found}");
			}
		}
	}

	private static SomlValue ReadScalar(JsonElement type, JsonElement text)
	{
		if (type.ValueEquals("string"u8))
		{
			return text.GetString()!;
		}

		if (type.ValueEquals("bool"u8))
		{
			if (text.ValueEquals("true"u8))
			{
				return true;
			}

			return text.ValueEquals("false"u8) ? false : throw new FormatException("The bool value is not in canonical form");
		}

		if (type.ValueEquals("null"u8))
		{
			return text.ValueEquals("null"u8) ? SomlValue.Null : throw new FormatException("The null value is not in canonical form");
		}

		var name = type.ValueEquals("int"u8) ? "int" : type.ValueEquals("float"u8) ? "float" : type.ValueEquals("date"u8) ? "date" : type.ValueEquals("instant"u8) ? "instant" : type.ValueEquals("duration"u8) ? "duration" : throw new FormatException("Unknown tagged scalar type. The types are string, int, float, bool, null, date, instant, and duration");
		var raw = JsonMarshal.GetRawUtf8Value(text);
		// Every canonical non-string scalar fits in 40 ASCII characters, each possibly written as a six-byte JSON escape, plus its quotes.
		const int maximumEncodedLength = 40 * 6;

		if (raw.Length > maximumEncodedLength + 2)
		{
			throw new FormatException($"The {name} value is too long to be in canonical form");
		}

		// Decode into bounded storage while the element's document is alive, including escaped JSON spellings.
		// Note: Not using Utf8JsonReader.CopyString(Span<byte>) with UTF-8 integer parsing because it did not establish consistent public reader gains on .NET 10 ARM64.
		Span<char> buffer = stackalloc char[maximumEncodedLength];
		var reader = new Utf8JsonReader(raw);
		reader.Read();
		var spelling = buffer[..reader.CopyString(buffer)];
		return name switch
		{
			"int" => ParseInt64(name, spelling),
			"float" => ReadFloat(spelling),
			"date" => DateOnly.TryParseExact(spelling, "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw NotCanonical(name, spelling),
			"instant" => SomlInstant.TryParse(spelling, null, out var instant) && IsCanonical(instant, spelling) ? instant : throw NotCanonical(name, spelling),
			_ => new SomlDuration(ParseInt64(name, spelling)),
		};
	}

	private static SomlValue ReadFloat(ReadOnlySpan<char> text)
	{
		var number = text switch
		{
			"infinity" => double.PositiveInfinity,
			"-infinity" => double.NegativeInfinity,
			_ => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && !double.IsNaN(value) ? value : throw NotCanonical("float", text),
		};
		SomlValue result = number;
		// One spelling for each value, so that a runner can compare the canonical form byte for byte.
		return text.SequenceEqual(result.ToString()) ? result : throw NotCanonical("float", text);
	}

	private static long ParseInt64(string type, ReadOnlySpan<char> text)
	{
		if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
		{
			if (!IsCanonical(value, text))
			{
				throw NotCanonical(type, text);
			}

			return value;
		}

		// An integer in canonical form that does not fit.
		var digits = text.StartsWith('-') ? text[1..] : text;
		if (digits is [>= '1' and <= '9', ..] && digits.IndexOfAnyExceptInRange('0', '9') < 0)
		{
			throw new FormatException($"The {type} value “{Abbreviate(text)}” is out of range");
		}

		throw NotCanonical(type, text);
	}

	/*
	Compare fixed-size canonical scalars without allocating their formatted strings.
	*/
	private static bool IsCanonical<T>(T value, ReadOnlySpan<char> text) where T : ISpanFormattable
	{
		Span<char> canonical = stackalloc char[40];
		return value.TryFormat(canonical, out var length, default, CultureInfo.InvariantCulture) && canonical[..length].SequenceEqual(text);
	}

	private static FormatException NotCanonical(string type, ReadOnlySpan<char> text) => new($"The {type} value “{Abbreviate(text)}” is not in canonical form");

	/*
	Keep diagnostics bounded even when a rejected JSON scalar or type name is huge. Do not split a surrogate pair at the boundary.
	*/
	private static string Abbreviate(ReadOnlySpan<char> text)
	{
		const int limit = 40;
		return text.Length <= limit ? text.ToString() : string.Concat(text[..(char.IsHighSurrogate(text[limit - 1]) ? limit - 1 : limit)], "…");
	}
}
