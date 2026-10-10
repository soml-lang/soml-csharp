using System.Text;
using Microsoft.Extensions.Configuration;
using Soml.Serialization;

namespace Soml.Tests;

public sealed class OptimizationTests
{
	private static KeyValuePair<string, SomlValue>[] Members(int count) => [.. Enumerable.Range(0, count).Select(index => new KeyValuePair<string, SomlValue>($"key{index:D2}", index))];

	[Fact]
	public void ObjectConstructionEnumeratesAnUnknownLengthSourceOnce()
	{
		var enumerations = 0;

		IEnumerable<KeyValuePair<string, SomlValue>> Entries()
		{
			Assert.Equal(1, ++enumerations);

			foreach (var member in Members(17))
			{
				yield return member;
			}
		}

		var result = new SomlObject(Entries());
		Assert.Equal(Members(17), result);
		Assert.Equal(16, result["key16"].GetInt64());
	}

	[Fact]
	public void ObjectConstructionCopiesKnownLengthEntriesWithoutSharingStorage()
	{
		var entries = Members(17);
		var result = new SomlObject(entries);
		entries[0] = new("replaced", 99);
		result["key01"] = 100;

		Assert.Equal(0, result["key00"].GetInt64());
		Assert.Equal(1, entries[1].Value.GetInt64());
		Assert.False(result.ContainsKey("replaced"));
	}

	[Fact]
	public void ObjectConstructionRejectsADuplicateAfterTheLookupThreshold()
	{
		var error = Assert.Throws<ArgumentException>(() => new SomlObject([.. Members(17), new("key00", 99)]));
		Assert.Equal("key", error.ParamName);
		Assert.Contains("key00", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void PairRemovalAcceptsDeeplyEqualValuesWithDifferentMemberOrders()
	{
		var members = new SomlObject(Members(17));
		members["key08"] = new SomlObject { ["a"] = 1, ["b"] = new SomlArray { 2, 3 } };
		var collection = (ICollection<KeyValuePair<string, SomlValue>>)members;
		var equivalent = new SomlObject { ["b"] = new SomlArray { 2, 3 }, ["a"] = 1 };

		Assert.True(collection.Remove(new("key08", equivalent)));
		Assert.Equal(Members(17).Where(member => member.Key != "key08"), members);
		Assert.Equal(16, members["key16"].GetInt64());
	}

	[Fact]
	public void FailedPairRemovalKeepsAnEnumerationValid()
	{
		var members = new SomlObject(Members(17));
		using var enumerator = members.GetEnumerator();
		var collection = (ICollection<KeyValuePair<string, SomlValue>>)members;
		Assert.True(enumerator.MoveNext());

		Assert.False(collection.Remove(new("key08", 8.0)));
		Assert.False(collection.Remove(new("absent", 0)));
		Assert.True(enumerator.MoveNext());
		Assert.Equal("key01", enumerator.Current.Key);
		Assert.Equal(17, members.Count);
	}

	[Fact]
	public void SuccessfulPairRemovalInvalidatesEnumerationAndAllowsReinsertion()
	{
		var members = new SomlObject(Members(17));
		using var enumerator = members.GetEnumerator();
		Assert.True(enumerator.MoveNext());

		Assert.True(((ICollection<KeyValuePair<string, SomlValue>>)members).Remove(new("key08", 8)));
		Assert.Throws<InvalidOperationException>(() => enumerator.MoveNext());
		members.Add("key08", 80);
		Assert.Equal("key08", members.GetAt(16).Key);
		Assert.Equal(80, members["key08"].GetInt64());
	}

	[Fact]
	public void OrderedEqualityChecksTheFinalNestedValue()
	{
		var left = new SomlObject(Members(17));
		var right = new SomlObject(Members(17));
		left["key16"] = new SomlArray { new SomlObject { ["value"] = 1 } };
		right["key16"] = new SomlArray { new SomlObject { ["value"] = 1.0 } };

		Assert.False(((SomlValue)left).Equals(right));
		right["key16"] = new SomlArray { new SomlObject { ["value"] = 1 } };
		Assert.True(((SomlValue)left).Equals(right));
		Assert.Equal(((SomlValue)left).GetHashCode(), ((SomlValue)right).GetHashCode());
	}

	[Fact]
	public void EqualityFallsBackForAReorderedSuffixAfterAMatchingPrefix()
	{
		var entries = Members(17);
		var left = new SomlObject(entries);
		var right = new SomlObject([.. entries[..8], .. entries[8..].Reverse()]);

		Assert.True(((SomlValue)left).Equals(right));
		Assert.True(((SomlValue)right).Equals(left));
		right["key09"] = 99;
		Assert.False(((SomlValue)left).Equals(right));
	}

	[Fact]
	public void EqualityRejectsAMissingKeyDespiteMatchingValuesAndCounts()
	{
		var left = new SomlObject(Members(17));
		var right = new SomlObject(Members(17));
		right.Remove("key16");
		right.Add("other", 16);

		Assert.False(((SomlValue)left).Equals(right));
		Assert.False(((SomlValue)right).Equals(left));
	}

	[Fact]
	public void CanonicalOutputUsesScalarOrderInSortedAndUnsortedNestedObjects()
	{
		var sorted = new SomlObject { ["a"] = 1, ["\uE000"] = 2, ["😀"] = 3 };
		var unsorted = new SomlObject { ["😀"] = 3, ["a"] = 1, ["\uE000"] = 2 };
		var document = new SomlObject { ["a"] = sorted, ["b"] = unsorted };
		const string expected = "a: {\n\ta: 1\n\t'\uE000': 2\n\t'😀': 3\n}\nb: {\n\ta: 1\n\t'\uE000': 2\n\t'😀': 3\n}\n";

		Assert.Equal(expected, document.ToDocumentString(true));
		Assert.Equal((SomlValue)document, SomlValue.Parse(expected));
		Assert.Equal(expected, SomlFormatter.Format(expected));
	}

	[Fact]
	public void CanonicalWritingPreservesOrderAndActiveEnumerations()
	{
		var entries = Members(17).Reverse().ToArray();
		var members = new SomlObject(entries);
		using var enumerator = members.GetEnumerator();
		Assert.True(enumerator.MoveNext());

		var canonical = members.ToDocumentString(true);
		Assert.Equal(new SomlObject(Members(17)).ToDocumentString(), canonical);
		Assert.True(enumerator.MoveNext());
		Assert.Equal("key15", enumerator.Current.Key);
		Assert.Equal(entries, members);
	}

	[Fact]
	public void CanonicalWritingValidatesKeysInAnAlreadySortedObject()
	{
		var document = new SomlObject { ["outer"] = new SomlObject { ["a"] = 1, ["z\r"] = 2 } };
		var error = Assert.Throws<SomlException>(() => document.ToDocumentString(true));

		Assert.Equal("outer.'z\\u{d}'", error.Path);
		Assert.Contains("carriage return", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void EscapedWritingPreservesLongRunsAndAdjacentEscapes()
	{
		var prefix = new string('a', 4097);
		var suffix = new string('b', 4097);
		var text = prefix + "'\n\t\0\\\"😀" + suffix;
		var document = new SomlObject { ["value"] = text };
		var expected = "value: \"" + prefix + "'\\n\\t\\u{0}\\\\\\\"😀" + suffix + "\"\n";

		Assert.Equal(expected, document.ToDocumentString());
		Assert.Equal(text, SomlValue.Parse(expected)["value"].GetString());
	}

	[Fact]
	public void EscapedWritingKeepsPairedSurrogatesAtBothEdgesOfARun()
	{
		const string text = "😀\n😁middle😀\t😁";
		var written = new SomlObject { [text] = text }.ToDocumentString();

		Assert.Equal("\"😀\\n😁middle😀\\t😁\": \"😀\\n😁middle😀\\t😁\"\n", written);
		Assert.Equal(text, SomlValue.Parse(written)[text].GetString());
	}

	[Fact]
	public void LenientDisplayEscapesLoneSurrogatesWithoutLosingSurroundingText()
	{
		SomlValue value = "prefix\uD800😀\uDC00suffix\r";
		Assert.Equal("\"prefix\\u{d800}😀\\u{dc00}suffix\\u{d}\"", value.ToString());
		Assert.Throws<SomlException>(() => new SomlObject { ["value"] = value }.ToDocumentString());
	}

	[Fact]
	public void EveryRepresentableControlCharacterRoundTripsTogether()
	{
		var text = new string([.. Enumerable.Range(0, 32).Where(code => code != '\r').Select(code => (char)code), '\u007F']);
		var document = new SomlObject { [text] = text };
		var written = document.ToDocumentString();

		Assert.DoesNotContain('\0', written);
		Assert.DoesNotContain('\u007F', written);
		Assert.Equal(text, SomlValue.Parse(written)[text].GetString());
	}

	public sealed class ConstructorBound
	{
		private int _port;

		public ConstructorBound(int port)
		{
			Port = port;
		}

		public int Port
		{
			get => _port;
			set
			{
				_port = value;
				Writes++;
			}
		}

		[SomlIgnore]
		public int Writes { get; private set; }

		public string Computed => "unchanged";
	}

	[Fact]
	public void ConstructorBoundMembersAreNotSetAgainAndComputedKeysAreKnown()
	{
		var value = SomlSerializer.Deserialize<ConstructorBound>("port: 80\ncomputed: null", new SomlSerializerOptions { RejectUnknownKeys = true });

		Assert.Equal(80, value.Port);
		Assert.Equal(1, value.Writes);
		Assert.Equal("unchanged", value.Computed);
		Assert.Equal("port: 80\ncomputed: 'unchanged'\n", SomlSerializer.Serialize(value));
	}

	public sealed class RequiredDefault
	{
		[System.Diagnostics.CodeAnalysis.SetsRequiredMembers]
		public RequiredDefault(int port = 80)
		{
			Port = port;
		}

		public required int Port { get; init; }
	}

	[Fact]
	public void RequiredConstructorBoundMembersStillRequireDocumentPresence()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<RequiredDefault>("{}"));
		Assert.Contains("Missing the key port", error.Message, StringComparison.Ordinal);
		Assert.Equal(90, SomlSerializer.Deserialize<RequiredDefault>("port: 90").Port);
	}

	public sealed record ManyMembers(int A, int B, int C, int D, int E, string? Optional, int F, int G, int H, int I, int J);

	[Fact]
	public void WritingKnownMembersPreservesOrderWhenOmittingNulls()
	{
		var value = new ManyMembers(1, 2, 3, 4, 5, null, 6, 7, 8, 9, 10);
		var options = new SomlSerializerOptions { OmitNullMembers = true, RejectUnknownKeys = true };
		const string expected = "a: 1\nb: 2\nc: 3\nd: 4\ne: 5\nf: 6\ng: 7\nh: 8\ni: 9\nj: 10\n";

		Assert.Equal(expected, SomlSerializer.Serialize(value, options));
		Assert.Equal(value, SomlSerializer.Deserialize<ManyMembers>(expected, options));
	}

	[Flags]
	public enum SignedFlags : int
	{
		None = 0,
		High = int.MinValue,
		HighAlias = High,
		Low = 1,
		Both = High | Low,
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void FlagsUseFirstSingleBitAliasesInDeclarationOrder(bool generated)
	{
		var value = new Dictionary<string, SignedFlags> { ["bits"] = SignedFlags.Both, ["empty"] = SignedFlags.None };
		var written = generated ? SomlSerializer.Serialize(value, OptimizationContext.Default.Flags) : SomlSerializer.Serialize(value);

		Assert.Equal("bits: [\n\t'high'\n\t'low'\n]\nempty: []\n", written);
		var read = generated ? SomlSerializer.Deserialize("bits: ['high-alias', 'low']\nempty: []", OptimizationContext.Default.Flags) : SomlSerializer.Deserialize<Dictionary<string, SignedFlags>>("bits: ['high-alias', 'low']\nempty: []");
		Assert.Equal(value, read);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UnknownFlagBitsRetainTheNestedErrorPath(bool generated)
	{
		var value = new Dictionary<string, SignedFlags> { ["bits"] = (SignedFlags)2 };
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Serialize(value, OptimizationContext.Default.Flags) : SomlSerializer.Serialize(value));

		Assert.Equal("bits", error.Path);
		Assert.Contains("no single-bit member names", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void TimeOnlyFormatsHandleReducedPrecisionAndSevenFractionDigits(bool generated)
	{
		const string text = "short: '12:34'\nprecise: '23:59:59.1234567'";
		var value = generated ? SomlSerializer.Deserialize(text, OptimizationContext.Default.Times) : SomlSerializer.Deserialize<Dictionary<string, TimeOnly>>(text);
		Assert.Equal(new TimeOnly(12, 34), value["short"]);
		Assert.Equal(new TimeOnly(23, 59, 59).Add(TimeSpan.FromTicks(1_234_567)), value["precise"]);
		var written = generated ? SomlSerializer.Serialize(value, OptimizationContext.Default.Times) : SomlSerializer.Serialize(value);
		Assert.Equal("short: '12:34:00'\nprecise: '23:59:59.1234567'\n", written);
	}

	private sealed class Context(SomlSerializerOptions? options = null) : SomlSerializerContext(options);

	private sealed class KnownKeysConverter(string[] keys) : SomlConverter<SomlObject>
	{
		public override string Expected => "an object";

		public override SomlObject Read(SomlValue value, SomlConverterState state)
		{
			var members = state.ReadObject(value, typeof(ManyMembers));
			state.CheckUnknownKeys(members, keys, typeof(ManyMembers));
			return members;
		}

		public override SomlValue Write(SomlObject value, SomlConverterState state) => value;
	}

	private static SomlException UnknownKey(string key, params string[] knownKeys)
	{
		var context = new Context(new SomlSerializerOptions { RejectUnknownKeys = true });
		var typeInfo = new SomlTypeInfo<SomlObject>(context, () => new KnownKeysConverter(knownKeys));
		return Assert.Throws<SomlException>(() => SomlSerializer.Deserialize((SomlValue)new SomlObject { [key] = 1 }, typeInfo));
	}

	[Theory]
	[InlineData("configuration-alpha-tail", "configuration-alpah-tail", "configuration-alpha-tail")]
	[InlineData("configuration-alpha-tail", "configuration-alpha-tails", "configuration-alpha-tail")]
	[InlineData("configuration-alpha-tail", "configuration-XYZ-tail", "configuration-alpha-tail")]
	[InlineData("configuration-alpha-tail", "configuration-alpha-tai", "configuration-alpha-tail")]
	public void SuggestionsPreserveEditDistanceWithCommonPrefixesAndSuffixes(string candidate, string typo, string expected)
	{
		var error = UnknownKey(typo, candidate, "unrelated");

		Assert.Contains($"Did you mean {expected}?", error.Message, StringComparison.Ordinal);
		Assert.Equal(typo, error.Path);
	}

	[Fact]
	public void SuggestionTiesKeepTheFirstKnownKey()
	{
		var error = UnknownKey("prefix-cat-tail", "prefix-bat-tail", "prefix-hat-tail");
		Assert.Contains("Did you mean prefix-bat-tail?", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void SuggestionsHandleTheMaximumCandidateLength()
	{
		var key = new string('a', 100);
		var candidate = key + new string('b', 33);
		var error = UnknownKey(key, candidate);
		Assert.Contains($"Did you mean {new string('a', 40)}…?", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain(candidate, error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void SuggestionsSkipKeysBeyondTheDocumentedLengthLimit()
	{
		var key = new string('a', 101);
		var error = UnknownKey(key, new string('a', 100));
		Assert.DoesNotContain("Did you mean", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ConfigurationPathsKeepEmptySegmentsAndUnicodeKeys()
	{
		using var stream = new MemoryStream(Encoding.UTF8.GetBytes("'': {'😀': [{'': 7}]}\n'root:': {leaf: 8}"));
		var configuration = new ConfigurationBuilder().AddSomlStream(stream).Build();

		Assert.Equal("7", configuration[":😀:0:"]);
		Assert.Equal("8", configuration["root::leaf"]);
	}

	[Fact]
	public async Task EmptyUtf8InputsReportTheSameErrorForEveryEntryPoint()
	{
		var textError = Assert.Throws<SomlParseException>(() => SomlValue.Parse(""));
		var bytesError = Assert.Throws<SomlParseException>(() => SomlValue.Parse(ReadOnlySpan<byte>.Empty));
		using var stream = new MemoryStream();
		var streamError = Assert.Throws<SomlParseException>(() => SomlValue.Parse(stream));
		var asyncError = await Assert.ThrowsAsync<SomlParseException>(async () => await SomlValue.ParseAsync(stream, TestContext.Current.CancellationToken));

		foreach (var error in new[] { bytesError, streamError, asyncError })
		{
			Assert.Equal(textError.Reason, error.Reason);
			Assert.Equal(textError.Line, error.Line);
			Assert.Equal(textError.Column, error.Column);
		}

		Assert.True(stream.CanRead);
	}
}

[SomlSerializable(typeof(Dictionary<string, OptimizationTests.SignedFlags>), TypeInfoPropertyName = "Flags")]
[SomlSerializable(typeof(Dictionary<string, TimeOnly>), TypeInfoPropertyName = "Times")]
internal partial class OptimizationContext : SomlSerializerContext;
