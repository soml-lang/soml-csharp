using System.Text;
using System.Text.Json.Nodes;

namespace Soml.Tests;

public sealed class ValueTests
{
	[Fact]
	public void ParsesADocumentAndReadsItsValues()
	{
		var document = SomlValue.Parse("""
			name: 'api'
			port: 8080
			ratio: 0.75
			enabled: true
			owner: null
			timeout: 30s
			released: 2026-09-19T14:00:00Z
			tags: ['a', 'b']
			pool: {min: 2}
			""");

		Assert.Equal(SomlValueKind.Object, document.Kind);
		Assert.Equal("api", document["name"].GetString());
		Assert.Equal(8080, document["port"].GetInt64());
		Assert.Equal(0.75, document["ratio"].GetDouble());
		Assert.True(document["enabled"].GetBoolean());
		Assert.True(document["owner"].IsNull);
		Assert.Equal(SomlDuration.Parse("30s"), document["timeout"].GetDuration());
		Assert.Equal(SomlInstant.Parse("2026-09-19T14:00:00Z"), document["released"].GetInstant());
		Assert.Equal("b", document["tags"][1].GetString());
		Assert.Equal(2, (long)document["pool"]["min"]);
	}

	[Fact]
	public void ReadsAnOptionalMember()
	{
		var document = SomlValue.Parse("port: 8080");

		Assert.True(document.TryGetValue("port", out var port));
		Assert.Equal(8080, port.GetInt64());
		Assert.False(document.TryGetValue("timeout", out _));
		Assert.False(document["port"].TryGetValue("port", out _));
	}

	[Fact]
	public async Task ParsesAStreamAsynchronously()
	{
		var document = await SomlValue.ParseAsync(new MemoryStream("a: 1"u8.ToArray()), TestContext.Current.CancellationToken);

		Assert.Equal(1, document["a"].GetInt64());
	}

	[Fact]
	public void KeepsIntAndFloatApart()
	{
		var document = SomlValue.Parse("[3, 3.0]");

		Assert.Equal(SomlValueKind.Integer, document[0].Kind);
		Assert.Equal(SomlValueKind.Float, document[1].Kind);
		Assert.NotEqual(document[0], document[1]);
		Assert.False(document[1].TryGetInt64(out _));

		// An int reads as a double only when the double holds it exactly.
		Assert.Equal(3.0, document[0].GetDouble());
		Assert.False(((SomlValue)long.MaxValue).TryGetDouble(out _));
		Assert.True(((SomlValue)(1L << 53)).TryGetDouble(out _));
		Assert.False(((SomlValue)((1L << 53) + 1)).TryGetDouble(out _));
	}

	[Fact]
	public void ThrowsForTheWrongKind()
	{
		var document = SomlValue.Parse("a: 'x'");

		var error = Assert.Throws<InvalidOperationException>(() => document["a"].GetInt64());
		Assert.Equal("Expected an int, but the value is a string", error.Message);
		Assert.Throws<KeyNotFoundException>(() => document["missing"]);
		Assert.Throws<InvalidOperationException>(() => document[0]);
	}

	[Fact]
	public void ComparesObjectsWithoutOrder()
	{
		var first = SomlValue.Parse("{a: 1, b: [1, {c: 2}]}");
		var second = SomlValue.Parse("{b: [1, {c: 2}], a: 1}");

		Assert.Equal(first, second);
		Assert.Equal(first.GetHashCode(), second.GetHashCode());
		Assert.NotEqual(first, SomlValue.Parse("{a: 1, b: [{c: 2}, 1]}"));
	}

	[Fact]
	public void HasOneZeroAndNoNaN()
	{
		SomlValue negativeZero = -0.0;

		Assert.Equal((SomlValue)0.0, negativeZero);
		Assert.Equal(0.0, negativeZero.GetDouble());
		Assert.False(double.IsNegative(negativeZero.GetDouble()));
		Assert.Equal((SomlValue)0.0, SomlValue.Parse("[-0.0]")[0]);
		Assert.Throws<ArgumentException>(() => (SomlValue)double.NaN);
	}

	[Fact]
	public void DefaultIsNull()
	{
		Assert.True(default(SomlValue).IsNull);
		Assert.Equal(SomlValue.Null, default);
		Assert.True(((SomlValue)(string?)null).IsNull);
	}

	[Fact]
	public void BuildsValuesWithCollectionExpressions()
	{
		SomlArray ports = [80, 443];
		var server = new SomlObject
		{
			["host"] = "localhost",
			["ports"] = ports,
			["timeout"] = SomlDuration.FromTimeSpan(TimeSpan.FromSeconds(90)),
		};

		Assert.Equal("{\n\thost: 'localhost'\n\tports: [\n\t\t80\n\t\t443\n\t]\n\ttimeout: 1m30s\n}", server.ToString());
		Assert.Equal("1.5", ((SomlValue)1.5).ToString());
		Assert.Equal("\"it's\"", ((SomlValue)"it's").ToString());
	}

	[Fact]
	public void KeepsTheOrderOfMembers()
	{
		var document = SomlValue.Parse("b: 1\na: 2\nc: 3").GetObject();

		Assert.Equal(["b", "a", "c"], document.Keys);
		document.Remove("a");
		document["d"] = 4;
		Assert.Equal(["b", "c", "d"], document.Keys);
		Assert.Equal(new KeyValuePair<string, SomlValue>("c", 3), document.GetAt(1));
	}

	[Fact]
	public void ParsesUtf8BytesAndStreams()
	{
		var bytes = "a: 'é'"u8.ToArray();

		Assert.Equal("é", SomlValue.Parse(bytes)["a"].GetString());
		Assert.Equal("é", SomlValue.Parse(new MemoryStream(bytes))["a"].GetString());
	}

	[Fact]
	public async Task ParsesTheRestOfAStream()
	{
		var stream = new MemoryStream("xxa: 1"u8.ToArray()) { Position = 2 };
		Assert.Equal(1, SomlValue.Parse(stream)["a"].GetInt64());

		// A stream that cannot seek has no length to size the buffer with.
		Assert.Equal("é", (await SomlValue.ParseAsync(new UnseekableStream("a: 'é'"u8.ToArray()), TestContext.Current.CancellationToken))["a"].GetString());
		Assert.Equal("é", SomlValue.Parse(new BufferedStream(new UnseekableStream("a: 'é'"u8.ToArray())))["a"].GetString());

		// A position past the end reads nothing.
		Assert.Throws<SomlParseException>(() => SomlValue.Parse(new MemoryStream("a: 1"u8.ToArray()) { Position = 10 }));
	}

	private sealed class UnseekableStream(byte[] bytes) : MemoryStream(bytes)
	{
		public override bool CanSeek => false;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}
	}

	[Fact]
	public void RejectsInvalidUtf8WithItsPosition()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse([.. "a: 'x"u8, 0xFF, .. "'"u8]));

		Assert.Equal("Invalid UTF-8 byte 0xFF", error.Reason);
		Assert.Equal((1, 6), (error.Line, error.Column));

		var truncated = Assert.Throws<SomlParseException>(() => SomlValue.Parse([.. "a: 'x"u8, 0xE2, 0x82]));
		Assert.Equal("Incomplete UTF-8 sequence at the end of the input", truncated.Reason);
	}

	[Fact]
	public void RejectsALoneSurrogateInAString()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse($"a: '{(char)0xD800}'"));

		Assert.Equal("A lone surrogate (U+D800) is not a Unicode scalar value", error.Reason);
		Assert.Equal((1, 5), (error.Line, error.Column));
	}

	[Theory]
	[InlineData("a: \"\\u{D}\"", "A carriage return (U+000D) cannot be represented, so \\u{D} is not allowed")]
	[InlineData("a: \"\\u{D800}\"", "\\u{D800} is a surrogate, which is not a Unicode scalar value")]
	[InlineData("a: \"\\u{FFFFFF}\"", "\\u{FFFFFF} is above U+10FFFF, the largest Unicode scalar value")]
	[InlineData("a: \"\\u{00E9}\"", "A Unicode escape uses lowercase hexadecimal digits")]
	[InlineData("a: 2026-09-19T14:00:00 Z", "An instant's offset follows its time directly, without a space, as in 2026-09-19T14:00:00Z")]
	[InlineData("a: [2026-09-19T14:00:00 +02:00, 1]", "An instant's offset follows its time directly, without a space, as in 2026-09-19T14:00:00+02:00")]
	[InlineData("a: 2026-02-30T14:00:00 Z", "An instant's offset follows its time directly, without a space")]
	[InlineData("a: 2026-09-19T14:00:00 Zulu", "An instant needs an offset: Z or ±HH:MM. A date and time without an offset is a local time, which is not an instant, so write it as a string, as in '2026-09-19T14:00:00', or add the offset it was meant in")]
	[InlineData("mac: 00:1A:2B:3C:4D:5E", "Invalid number “00:1A:2B:3C:4D:5E”. A string value must be quoted, as in '00:1A:2B:3C:4D:5E'")]
	[InlineData("a: 2years", "Invalid duration 2years: there is no year unit, because a year is not a fixed length. Write 8760h for a fixed 365 days")]
	[InlineData("a: [1Y, 1]", "Invalid duration 1Y: there is no year unit, because a year is not a fixed length. Write 8760h for a fixed 365 days")]
	[InlineData("a: 1y6m", "Invalid duration 1y6m: there is no year unit, because a year is not a fixed length. Write 8760h for a fixed 365 days")]
	[InlineData("a: 100yen", "Invalid number “100yen”. A string value must be quoted, as in '100yen'")]
	[InlineData("a: 1byte", "Invalid number “1byte”. A string value must be quoted, as in '1byte'")]
	[InlineData("a: -verbose", "Expected a digit or “infinity” after “-”")]
	[InlineData("a: 1.5e-", "Expected digits after the exponent marker “e”")]
	[InlineData("a: -nano", "Expected a digit or “infinity” after “-”")]
	[InlineData("a: -info", "Expected a digit or “infinity” after “-”")]
	[InlineData("a: -NaN", "NaN is not representable. Use null for a missing value")]
	[InlineData("a: -Infinity", "“-Infinity” is not a value. Negative infinity is written -infinity")]
	[InlineData("speed: 100 km/h", "Expected a line break before the next entry, but found “k”")]
	[InlineData("a: 2026-09-19T14:00:00/2026-09-20T15:00:00", "An instant needs an offset: Z or ±HH:MM")]
	[InlineData("a: 2026-09-19T14:00:00 Z/x", "An instant needs an offset: Z or ±HH:MM. A date and time without an offset is a local time, which is not an instant, so write it as a string, as in '2026-09-19T14:00:00', or add the offset it was meant in")]
	[InlineData("a: 5 m/* c */", "A unit cannot follow a number after a space. Write a duration without the space, as in 5m, and anything else, such as a size, as a string, as in '5 m'")]
	[InlineData("a: 4k_video", "Invalid number “4k_video”. A string value must be quoted, as in '4k_video'")]
	[InlineData("a: 1e-_5", "An underscore in a number must be between two digits")]
	[InlineData("price: 5EUR", "Invalid number “5EUR”. A string value must be quoted, as in '5EUR'")]
	[InlineData("a: 1.5E-3", "An exponent marker is a lowercase “e”")]
	public void RejectsAValueWithTheReasonOfTheReferenceImplementation(string text, string reason)
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));

		Assert.Equal(reason, error.Reason);
	}

	[Fact]
	public void WritesCodeFramesAsTheReferenceImplementationDoes()
	{
		foreach (var line in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "code-frames.jsonl")))
		{
			var expected = JsonNode.Parse(line)!;
			var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(expected["text"]!.GetValue<string>()));

			Assert.Equal(expected["message"]!.GetValue<string>(), error.Message);
			Assert.Equal((expected["line"]!.GetValue<int>(), expected["column"]!.GetValue<int>(), expected["offset"]!.GetValue<int>()), (error.Line, error.Column, error.Offset));
		}
	}

	[Fact]
	public void RefusesToWriteWhatCannotBeRead()
	{
		var withCarriageReturn = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new SomlObject { ["a"] = "x\ry" }));
		Assert.Equal("Cannot serialize a string containing a carriage return (U+000D), which is not representable (at a)", withCarriageReturn.Message);

		var withLoneSurrogate = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new SomlObject { [$"{(char)0xDC00}"] = 1 }));
		Assert.StartsWith("Cannot serialize a key containing a lone surrogate", withLoneSurrogate.Message, StringComparison.Ordinal);

		SomlValue deep = new SomlArray();

		for (var depth = 1; depth < 100; depth++)
		{
			deep = new SomlArray { deep };
		}

		Assert.Equal(100, SomlSerializer.Serialize(deep).Count(character => character == '['));
		Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new SomlArray { deep }));
	}

	[Fact]
	public void SortsCanonicalKeysByCodePoint()
	{
		var value = new SomlObject { [$"{(char)0xFF61}"] = 1, ["\U0001F600"] = 2, ["b"] = 3, ["a"] = 4 };
		var canonical = SomlSerializer.Serialize(value, new SomlSerializerOptions { Canonical = true });

		Assert.Equal($"a: 4\nb: 3\n'{(char)0xFF61}': 1\n'\U0001F600': 2\n", canonical);
		Assert.Equal(Encoding.UTF8.GetBytes(canonical), SomlSerializer.SerializeToUtf8Bytes(value, new SomlSerializerOptions { Canonical = true }));
	}
}
