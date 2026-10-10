using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Soml.Cli;
using Soml.Serialization;

namespace Soml.Tests;

public sealed class EditingAndConversionTests
{
	[Fact]
	public void NumericKeysAndIndexesRemainDistinctInMixedPaths()
	{
		Assert.Equal("'0': [{ '': 7 }]", SomlEditor.Set("'0': [{ '': 1 }]", ["0", 0, ""], 7));
	}

	[Fact]
	public void DefaultPathSegmentsAddressTheFirstArrayItem()
	{
		Assert.Equal("[9, 2]", SomlEditor.Set("[1, 2]", [default(SomlPathSegment)], 9));
	}

	[Fact]
	public void EditingAcceptsASliceOfACallersPath()
	{
		SomlPathSegment[] path = ["unused", "servers", 0, "port", "unused"];
		Assert.Equal("servers: [{port: 81}]", SomlEditor.Set("servers: [{port: 80}]", path.AsSpan(1, 3), 81));
		Assert.Equal("unused", path[0].Key);
		Assert.Equal("unused", path[^1].Key);
	}

	[Fact]
	public void RemovingAMissingNestedMemberReturnsTheOriginalTextInstance()
	{
		var text = new string("# keep\nouter: {present: 1}\n".ToCharArray());
		Assert.Same(text, SomlEditor.Remove(text, ["outer", "absent"], out var removed));
		Assert.False(removed);
	}

	[Fact]
	public void RemovingAnArrayIndexPastItsEndReturnsTheOriginalTextInstance()
	{
		var text = new string("items: [1, 2] # keep".ToCharArray());
		Assert.Same(text, SomlEditor.Remove(text, ["items", 10], out var removed));
		Assert.False(removed);
	}

	[Fact]
	public void OrdinaryQuotedUnicodeKeysDoNotChangeNeighboringEscapes()
	{
		const string text = "\"中😀\": 1\n\"tab\\there\": 2\n";
		Assert.Equal("\"中😀\": 7\n\"tab\\there\": 2\n", SomlEditor.Set(text, ["中😀"], 7));
	}

	[Fact]
	public void EditingAnEscapedUnicodeKeyKeepsItsOriginalSpelling()
	{
		Assert.Equal("\"\\u{1f600}\": 9\n", SomlEditor.Set("\"\\u{1f600}\": 1\n", ["😀"], 9));
	}

	[Fact]
	public void RemovingAnOnlyMemberKeepsUnownedCommentsInsideItsContainer()
	{
		var prefix = string.Concat(Enumerable.Repeat("# outside\n", 2000));
		Assert.Equal(prefix + "value: {\n\t# keep\n}\n", SomlEditor.Remove(prefix + "value: {\n\t# keep\n\titem: 1\n}\n", ["value", "item"]));
	}

	[Fact]
	public void RemovingTheFirstMemberKeepsTheRestOfTheDocumentVerbatim()
	{
		Assert.Equal("# keep\nsecond: 0xF\n", SomlEditor.Remove("first: 1 # remove\n# keep\nsecond: 0xF\n", ["first"]));
	}

	[Fact]
	public void RemovingTheLastMemberDoesNotRequireAFinalLineFeed()
	{
		Assert.Equal("first: 0xF\n", SomlEditor.Remove("first: 0xF\nlast: 2", ["last"]));
	}

	[Fact]
	public void MissingQuotedAndEmptyKeysCreateCanonicalNestedObjects()
	{
		var value = new SomlObject { ["z"] = 1, ["a"] = 2 };
		const string expected = "existing: 0\n'a b': {\n\t'': {\n\t\ta: 2\n\t\tz: 1\n\t}\n}\n";
		var edited = SomlEditor.Set("existing: 0\n", ["a b", ""], value, canonical: true);
		Assert.Equal(expected, edited);
		Assert.Equal(2, SomlValue.Parse(edited)["a b"][""]["a"].GetInt64());
		Assert.True(SomlFormatter.IsFormatted(edited));
	}

	[Fact]
	public void ReplacingABlockStringKeepsACommentBetweenItsKeyAndValue()
	{
		Assert.Equal("value: /* keep */\n\t'new'", SomlEditor.Set("value: /* keep */\n\t'''\n\told\n\t'''", ["value"], "new"));
	}

	[Fact]
	public void CommasInsideLongBlockCommentsAreNeverSeparators()
	{
		var comment = "/*" + new string(',', 20_000) + "*/";
		Assert.Equal($"[1, {comment} 2]\n", SomlFormatter.Format($"[1, {comment}2]"));
	}

	[Fact]
	public void OneLineFormattingKeepsCommentsOnBothSidesOfSeparators()
	{
		Assert.Equal("[/* before */ 1 /* after */, /* next */ 2 /* end */]\n", SomlFormatter.Format("[/* before */1/* after */,/* next */2/* end */]"));
	}

	[Fact]
	public void ReindentingBlockStringsPreservesInteriorBlankLines()
	{
		const string text = "outer: {\n  value: '''\n  \n    first\n    \n    last\n  \n  '''\n}";
		var formatted = SomlFormatter.Format(text);
		Assert.Equal(SomlValue.Parse(text), SomlValue.Parse(formatted));
		Assert.Equal("  first\n\n  last", SomlValue.Parse(formatted)["outer"]["value"].GetString());
		Assert.True(SomlFormatter.IsFormatted(formatted));
	}

	[Fact]
	public void LongWhitespaceRunsBetweenTokensFormatWithoutLosingItems()
	{
		var spaces = string.Concat(Enumerable.Repeat(" \t", 15_000));
		Assert.Equal("value: [1, 2]\n", SomlFormatter.Format($"value:{spaces}[1,{spaces}2]{spaces}"));
	}

	[Fact]
	public void LateErrorsKeepOnlyTwoContextLinesAndAlignAWiderGutter()
	{
		var text = string.Concat(Enumerable.Range(1, 99).Select(index => $"key{index}: {index}\n")) + "last: nope";
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		Assert.Equal(100, error.Line);
		Assert.Equal(7, error.Column);
		Assert.Equal("   98 | key98: 98\n   99 | key99: 99\n> 100 | last: nope\n      |       ^", error.CodeFrame);
	}

	[Fact]
	public void FirstLineErrorsHaveNoUninitializedContextLines()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("value: nope"));
		Assert.Equal("> 1 | value: nope\n    |        ^", error.CodeFrame);
	}

	[Fact]
	public void ClippedErrorFramesKeepWholeEmojiAtTheWindowBoundary()
	{
		var text = "value: \"" + new string('a', 100) + "😀" + new string('b', 48) + "\\q" + new string('c', 100) + "\"";
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		Assert.Contains("…😀", error.CodeFrame, StringComparison.Ordinal);
		Assert.DoesNotContain('\uFFFD', error.CodeFrame);
		Assert.Contains("\\q", error.CodeFrame, StringComparison.Ordinal);
		Assert.Equal(2, error.CodeFrame.Split('\n').Length);
	}

	public sealed class KnownModel
	{
		public int Known { get; set; }
	}

	[Fact]
	public void UnknownKeyDiagnosticsEscapeEveryJsonShortEscape()
	{
		var value = new SomlObject { ["\b\f\n\r\t\"\\\0"] = 1 };
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<KnownModel>(value, new SomlSerializerOptions { RejectUnknownKeys = true }));
		Assert.Contains("\\b\\f\\n\\r\\t\\\"\\\\\\u0000", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain('\0', error.Message);
		Assert.DoesNotContain('\r', error.Message);
	}

	[Fact]
	public void PrintableAsciiDiagnosticsStayReadableAtBothBoundaries()
	{
		var value = new SomlObject { [" ~"] = 1 };
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<KnownModel>(value, new SomlSerializerOptions { RejectUnknownKeys = true }));
		Assert.Contains("\" ~\"", error.Message, StringComparison.Ordinal);
		Assert.Equal("' ~'", error.Path);
	}

	[Fact]
	public void NonprintingCharactersAdjacentToAsciiAreStillEscaped()
	{
		var value = new SomlObject { ["~\u007F\u202E"] = 1 };
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<KnownModel>(value, new SomlSerializerOptions { RejectUnknownKeys = true }));
		Assert.Contains("\\u{7f}", error.Message, StringComparison.Ordinal);
		Assert.Contains("\\u{202e}", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain('\u202E', error.Message);
	}

	[Fact]
	public void TaggedJsonValuesOwnTheirStringsAfterInputBytesAreChanged()
	{
		var bytes = Encoding.UTF8.GetBytes("{\"中😀\":{\"type\":\"string\",\"value\":\"é😀\"}}");
		var value = TaggedJson.Read(bytes);
		Array.Fill(bytes, (byte)'x');
		Assert.Equal("é😀", value["中😀"].GetString());
	}

	[Fact]
	public void TaggedScalarsAcceptReorderedAndEscapedPropertyNames()
	{
		Assert.Equal(7, TaggedJson.Read("{\"value\":\"7\",\"ty\\u0070e\":\"int\"}"u8).GetInt64());
	}

	[Fact]
	public void ObjectsWithTypeAndValueMembersAreNotMistakenForScalars()
	{
		var value = TaggedJson.Read("{\"type\":{\"type\":\"string\",\"value\":\"int\"},\"value\":{\"type\":\"int\",\"value\":\"7\"},\"extra\":{\"type\":\"null\",\"value\":\"null\"}}"u8);
		Assert.Equal(3, value.GetObject().Count);
		Assert.Equal("int", value["type"].GetString());
		Assert.Equal(7, value["value"].GetInt64());
		Assert.Equal(SomlValue.Null, value["extra"]);
	}

	[Theory]
	[InlineData("1e0")]
	[InlineData("NaN")]
	[InlineData("Infinity")]
	[InlineData("-0.0")]
	[InlineData("1")]
	[InlineData("1e9999")]
	[InlineData("0e-9999")]
	public void TaggedFloatsRejectNoncanonicalAndNonrepresentableSpellings(string text)
	{
		var error = Assert.Throws<FormatException>(() => TaggedJson.Read(Encoding.UTF8.GetBytes($"{{\"type\":\"float\",\"value\":\"{text}\"}}")));
		Assert.Contains("not in canonical form", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("int", "-9223372036854775808")]
	[InlineData("int", "9223372036854775807")]
	[InlineData("duration", "-9223372036854775808")]
	[InlineData("duration", "9223372036854775807")]
	[InlineData("instant", "0001-01-01T00:00:00Z")]
	[InlineData("instant", "9999-12-31T23:59:59.999999999Z")]
	public void TaggedScalarWritingPreservesBoundaryValues(string type, string text)
	{
		var value = TaggedJson.Read(Encoding.UTF8.GetBytes($"{{\"type\":\"{type}\",\"value\":\"{text}\"}}"));
		using var stream = new MemoryStream();
		TaggedJson.Write(value, stream);
		using var json = JsonDocument.Parse(stream.ToArray());
		Assert.Equal(type, json.RootElement.GetProperty("type").GetString());
		Assert.Equal(text, json.RootElement.GetProperty("value").GetString());
	}

	[Fact]
	public void TaggedArrayWritingKeepsMixedScalarOrder()
	{
		using var stream = new MemoryStream();
		TaggedJson.Write(new SomlArray { "first", 7, true, SomlValue.Null, double.NegativeInfinity }, stream);
		using var json = JsonDocument.Parse(stream.ToArray());
		Assert.Equal(["string", "int", "bool", "null", "float"], json.RootElement.EnumerateArray().Select(item => item.GetProperty("type").GetString()));
		Assert.Equal(["first", "7", "true", "null", "-infinity"], json.RootElement.EnumerateArray().Select(item => item.GetProperty("value").GetString()));
	}

	[Fact]
	public void DuplicateJsonKeysAreRejectedBeforeEarlierInvalidScalarValues()
	{
		var error = Assert.Throws<FormatException>(() => TaggedJson.Read("{\"bad\":true,\"a\":[],\"a\":[]}"u8));
		Assert.StartsWith("The input is not valid JSON:", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void LoneSurrogateJsonKeysAreRejectedBeforeConversionBegins()
	{
		var error = Assert.Throws<FormatException>(() => TaggedJson.Read("{\"bad\":true,\"\\ud800\":[]}"u8));
		Assert.StartsWith("The input is not valid JSON:", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void CliEncodingStartsAtTheInputPositionAndEmitsNoByteOrderMark()
	{
		using var input = new MemoryStream("junk{\"a\":{\"type\":\"int\",\"value\":\"7\"}}"u8.ToArray());
		input.Position = 4;
		using var output = new MemoryStream();
		using var error = new StringWriter(CultureInfo.InvariantCulture);
		Assert.Equal(0, Program.Run(["encode"], input, output, error));
		Assert.Equal("a: 7\n"u8.ToArray(), output.ToArray());
		Assert.Equal("", error.ToString());
		Assert.True(input.CanRead);
		Assert.True(output.CanWrite);
	}

	[Fact]
	public void CliEncodingWritesLargeUnicodeValuesAtTheOutputPosition()
	{
		var value = string.Concat(Enumerable.Repeat("中😀", 10_000));
		using var input = new MemoryStream(Encoding.UTF8.GetBytes($"{{\"a\":{{\"type\":\"string\",\"value\":\"{value}\"}}}}"));
		using var output = new MemoryStream();
		output.WriteByte(0x7F);
		using var error = new StringWriter(CultureInfo.InvariantCulture);
		Assert.Equal(0, Program.Run(["encode"], input, output, error));
		Assert.Equal(Encoding.UTF8.GetBytes($"\u007fa: '{value}'\n"), output.ToArray());
		Assert.True(output.CanWrite);
	}

	private sealed class ShortReadStream(byte[] bytes) : Stream
	{
		private readonly MemoryStream _inner = new(bytes);
		public override bool CanRead => true;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}
		public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, Math.Min(count, 3));
		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_inner.Dispose();
			}

			base.Dispose(disposing);
		}
	}

	[Fact]
	public void CliEncodingAcceptsNonseekableShortReadsThroughUtf8Characters()
	{
		using var input = new ShortReadStream("{\"a\":{\"type\":\"string\",\"value\":\"中😀\"}}"u8.ToArray());
		using var output = new MemoryStream();
		using var error = new StringWriter(CultureInfo.InvariantCulture);
		Assert.Equal(0, Program.Run(["encode"], input, output, error));
		Assert.Equal("a: '中😀'\n"u8.ToArray(), output.ToArray());
	}

	[Fact]
	public void CliHelpDoesNotConsumeInputAndEndsWithOneLineFeed()
	{
		using var input = new MemoryStream([1, 2, 3]);
		using var output = new MemoryStream();
		using var error = new StringWriter(CultureInfo.InvariantCulture);
		Assert.Equal(0, Program.Run(["--help"], input, output, error));
		var text = Encoding.UTF8.GetString(output.ToArray());
		Assert.StartsWith("Usage:", text, StringComparison.Ordinal);
		Assert.EndsWith(".\n", text, StringComparison.Ordinal);
		Assert.DoesNotContain('\r', text);
		Assert.Equal(0, input.Position);
		Assert.True(output.CanWrite);
	}

	public readonly record struct CaseKey(string Text) : IParsable<CaseKey>, IFormattable
	{
		public static CaseKey Parse(string text, IFormatProvider? provider) => new(text);
		public static bool TryParse(string? text, IFormatProvider? provider, out CaseKey result)
		{
			result = new(text ?? "");
			return text is not null;
		}
		public bool Equals(CaseKey other) => StringComparer.OrdinalIgnoreCase.Equals(Text, other.Text);
		public override int GetHashCode() => 0;
		public override string ToString() => Text;
		public string ToString(string? format, IFormatProvider? formatProvider) => Text;
	}

	[Fact]
	public void DuplicateTypedDictionaryKeysWinOverInvalidDuplicateValues()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<CaseKey, int>>("ABC: 1\nabc: 'wrong'"));
		Assert.Equal("abc", error.Path);
		Assert.Contains("must be unique", error.Message, StringComparison.Ordinal);
		Assert.Contains("line 2, column 1", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void TypedDictionaryReadsKeepDistinctKeysWithCollidingHashes()
	{
		var text = string.Concat(Enumerable.Range(0, 100).Select(index => $"key{index}: {index}\n"));
		var value = SomlSerializer.Deserialize<Dictionary<CaseKey, int>>(text);
		Assert.Equal(100, value.Count);
		Assert.Equal(Enumerable.Range(0, 100), value.Values);

		for (var index = 0; index < 100; index++)
		{
			Assert.Equal(index, value[new CaseKey($"KEY{index}")]);
		}
	}

	[Fact]
	public void ObjectDictionaryKeysAcceptStringsButRejectOtherRuntimeTypes()
	{
		Assert.Equal("HTTPServer: 1\n'中': 2\n", SomlSerializer.Serialize(new Dictionary<object, int> { ["HTTPServer"] = 1, ["中"] = 2 }));
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new Dictionary<object, int> { [7] = 1 }));
		Assert.Contains("Cannot write the keys of Dictionary<object, int>", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void FreshOptionsKeepScalarConversionsIndependentOfPriorFailures()
	{
		var first = new SomlSerializerOptions();
		var second = new SomlSerializerOptions { OmitNullMembers = true };
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, byte>>("value: 256", first));
		Assert.Equal("value", error.Path);
		Assert.Equal(255, SomlSerializer.Deserialize<Dictionary<string, byte>>("value: 255", second)["value"]);
		Assert.Equal("value: 0\n", SomlSerializer.Serialize(new Dictionary<string, byte> { ["value"] = 0 }, first));
	}

	[Flags]
	public enum CompositeFlags
	{
		None = 0,
		Both = 3,
	}

	[Fact]
	public void FlagsWithoutSingleBitMembersCanWriteZeroAndReadCompositeNames()
	{
		Assert.Equal("value: []\n", SomlSerializer.Serialize(new Dictionary<string, CompositeFlags> { ["value"] = CompositeFlags.None }));
		Assert.Equal(CompositeFlags.Both, SomlSerializer.Deserialize<Dictionary<string, CompositeFlags>>("value: ['both']")["value"]);
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new Dictionary<string, CompositeFlags> { ["value"] = CompositeFlags.Both }));
		Assert.Equal("value", error.Path);
	}

	public sealed class CountOnlySet(IEnumerable<int> items) : IReadOnlySet<int>
	{
		private readonly HashSet<int> _items = [.. items];
		public int Count => _items.Count;
		public bool Contains(int item) => _items.Contains(item);
		public bool IsProperSubsetOf(IEnumerable<int> other) => _items.IsProperSubsetOf(other);
		public bool IsProperSupersetOf(IEnumerable<int> other) => _items.IsProperSupersetOf(other);
		public bool IsSubsetOf(IEnumerable<int> other) => _items.IsSubsetOf(other);
		public bool IsSupersetOf(IEnumerable<int> other) => _items.IsSupersetOf(other);
		public bool Overlaps(IEnumerable<int> other) => _items.Overlaps(other);
		public bool SetEquals(IEnumerable<int> other) => _items.SetEquals(other);
		public IEnumerator<int> GetEnumerator() => throw new InvalidOperationException("Enumeration is unavailable");
		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	[Fact]
	public void SetReadsUseTheAvailableCountWithoutEnumeratingTheResult()
	{
		var value = SomlSerializer.Deserialize<CountOnlySet>("[1, 2]");
		Assert.Equal(2, value.Count);
		Assert.True(value.SetEquals([1, 2]));
	}

	[Fact]
	public void SetReadsStillRejectDuplicatesWhenResultEnumerationIsUnavailable()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, CountOnlySet>>("items: [1, 1]"));
		Assert.Equal("items", error.Path);
		Assert.Contains("duplicate", error.Message, StringComparison.OrdinalIgnoreCase);
	}

	[Fact]
	public void NestedConfigurationArrayPathsKeepMultipleDigitIndexes()
	{
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes("outer: [" + string.Join(", ", Enumerable.Range(0, 12)) + "]"));
		var configuration = new ConfigurationBuilder().AddSomlStream(stream).Build();
		Assert.Equal("9", configuration["outer:9"]);
		Assert.Equal("10", configuration["outer:10"]);
		Assert.Equal("11", configuration["outer:11"]);
		Assert.Equal(12, configuration.GetSection("outer").GetChildren().Count());
	}

	[Fact]
	public void RootConfigurationArraysKeepNullAndEmptyContainerEntries()
	{
		using var stream = new MemoryStream("[7, null, {}, []]"u8.ToArray());
		var configuration = new ConfigurationBuilder().AddSomlStream(stream).Build();
		Assert.Equal("7", configuration["0"]);
		Assert.Null(configuration["1"]);
		Assert.Null(configuration["2"]);
		Assert.Equal("", configuration["3"]);
		Assert.Equal(["0", "1", "2", "3"], configuration.GetChildren().Select(child => child.Key));
	}

	[Fact]
	public void NegativeConfigurationDurationsDistinguishTicksFromNanoseconds()
	{
		using var stream = new MemoryStream("tick: -0.0000001s\nfine: -0.000000001s"u8.ToArray());
		var configuration = new ConfigurationBuilder().AddSomlStream(stream).Build();
		Assert.Equal("-00:00:00.0000001", configuration["tick"]);
		Assert.Equal("-0.000000001s", configuration["fine"]);
	}
}
