using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using Soml.Serialization;

namespace Soml.Tests;

public sealed class ParsingAndMetadataTests
{
	[Fact]
	public void GeneralNumericTokensKeepTheirKindsBetweenComments()
	{
		var items = SomlValue.Parse("[0xF/* hex */, -9_223_372_036_854_775_808, 1e2, 2h3m, 2000-01-01T00:00:00Z]").GetArray();
		Assert.Equal([SomlValueKind.Integer, SomlValueKind.Integer, SomlValueKind.Float, SomlValueKind.Duration, SomlValueKind.Instant], items.Select(item => item.Kind));
		Assert.Equal(15, items[0].GetInt64());
		Assert.Equal(long.MinValue, items[1].GetInt64());
		Assert.Equal(100, items[2].GetDouble());
		Assert.Equal(7_380_000_000_000, items[3].GetDuration().Nanoseconds);
		Assert.Equal(946_684_800, items[4].GetInstant().UnixSeconds);
	}

	[Fact]
	public void SeparatorsInEveryFloatPartPreserveItsValue()
	{
		Assert.Equal(-1_234_000_000_000d, SomlValue.Parse("[-1_2.3_4e1_1]")[0].GetDouble());
	}

	[Fact]
	public void LongSeparatedMantissasKeepTheExponentScale()
	{
		var number = "0." + string.Join('_', Enumerable.Repeat("0", 512)) + "_1e512";
		Assert.Equal(0.1, SomlValue.Parse($"[{number}]")[0].GetDouble());
	}

	[Fact]
	public void LongNegativeZeroMantissasNormalizeEvenWithHugeExponents()
	{
		var number = "-0." + string.Join('_', Enumerable.Repeat("0", 512)) + "e999999999999";
		Assert.Equal(0, BitConverter.DoubleToInt64Bits(SomlValue.Parse($"[{number}]")[0].GetDouble()));
	}

	[Fact]
	public void NineteenDigitMagnitudesOutsideIntegerRangeStillParseAsFloats()
	{
		Assert.Equal(9_223_372_036_854_775_808d, SomlValue.Parse("[9223372036854775808.0]")[0].GetDouble());
	}

	[Fact]
	public void TwentyDigitIntegerPartsRemainValidFloatMantissas()
	{
		Assert.Equal(1e19, SomlValue.Parse("[10000000000000000000.0]")[0].GetDouble());
	}

	[Fact]
	public void LongRadixLeadingZerosDoNotConsumeTheIntegerRange()
	{
		Assert.Equal(long.MaxValue, SomlValue.Parse("[0x" + new string('0', 1000) + "7FFF_FFFF_FFFF_FFFF]")[0].GetInt64());
	}

	[Fact]
	public void IntegerOverflowLocationsCountAnEmojiKeyAsOneColumn()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("'😀': 9223372036854775808"));
		Assert.Equal(6, error.Column);
		Assert.Equal(6, error.Offset);
		Assert.Contains("9223372036854775808", error.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void ScientificUnderflowPointsAtTheWholeTokenAfterAComment()
	{
		const string text = "value: /* keep */ 1_0e-9999";
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		Assert.Equal(text.IndexOf("1_0", StringComparison.Ordinal), error.Offset);
		Assert.Contains("too small", error.Reason, StringComparison.Ordinal);
		Assert.Contains("1_0e-9999", error.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void NegativeScientificOverflowSuggestsNegativeInfinity()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("[-1_0e9999]"));
		Assert.Contains("Use -infinity", error.Reason, StringComparison.Ordinal);
		Assert.Equal(1, error.Offset);
	}

	[Fact]
	public void VeryLongInvalidNumbersKeepDiagnosticsBounded()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("value: " + new string('1', 20_000) + ".x"));
		Assert.Equal(7, error.Offset);
		Assert.Contains('…', error.Reason);
		Assert.True(error.Reason.Length < 500);
		Assert.True(error.CodeFrame.Length < 500);
	}

	[Fact]
	public void BadDurationUnitsRetainTheOriginalSeparatedToken()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("'😀': -1_2h3MS"));
		Assert.Contains("Invalid duration -1_2h3MS", error.Reason, StringComparison.Ordinal);
		Assert.Contains("lowercase: ms", error.Reason, StringComparison.Ordinal);
		Assert.Equal(6, error.Offset);
	}

	[Fact]
	public void InstantRangeErrorsRetainTheOriginalOffsetSpelling()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("date: 2000-02-30T00:00:00+01:00"));
		Assert.Contains("Invalid instant 2000-02-30T00:00:00+01:00", error.Reason, StringComparison.Ordinal);
		Assert.Contains("01 to 29", error.Reason, StringComparison.Ordinal);
		Assert.Equal(6, error.Offset);
	}

	[Fact]
	public void InstantFractionErrorsPrecedeInvalidCalendarFields()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("[0000-00-00T99:99:99.1234567890Z]"));
		Assert.Contains("at most nine digits", error.Reason, StringComparison.Ordinal);
		Assert.Equal(1, error.Offset);
	}

	[Fact]
	public void JsonSurrogatePairHintsUseBothEscapesBesideLiteralEmoji()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("'😀': \"😀\\uDBFF\\uDFFFrest\""));
		Assert.Contains("\\uDBFF\\uDFFF", error.Reason, StringComparison.Ordinal);
		Assert.Contains("Write \\u{10ffff}", error.Reason, StringComparison.Ordinal);
		Assert.Equal(9, error.Offset);
	}

	[Fact]
	public void JsonHighSurrogateFollowedByAnOrdinaryEscapeRemainsALoneSurrogate()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("[\"\\uD800\\u0041\"]"));
		Assert.Contains("lone surrogate", error.Reason, StringComparison.Ordinal);
		Assert.DoesNotContain("Write \\u{", error.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void WhitespaceInsideBlockCommentsDoesNotSeparateArrayItems()
	{
		var whitespace = new string(' ', 10_000) + "\n\t";
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse($"[1 /*{whitespace}*/ 2]"));
		Assert.Contains("Expected “,”, a line break", error.Reason, StringComparison.Ordinal);
	}

	[Fact]
	public void ALineBreakBeforeABlockCommentSeparatesArrayItems()
	{
		var whitespace = string.Concat(Enumerable.Repeat(" \t", 10_000));
		var items = SomlValue.Parse($"[1{whitespace}\n/*\n*/{whitespace}2]").GetArray();
		Assert.Equal([1L, 2L], items.Select(item => item.GetInt64()));
	}

	[Fact]
	public void ALineBreakAfterABlockCommentSeparatesObjectMembers()
	{
		var members = SomlValue.Parse("{a: 1 /*\n*/\t \n\t b: 2}").GetObject();
		Assert.Equal(["a", "b"], members.Keys);
		Assert.Equal(2, members["b"].GetInt64());
	}

	[Fact]
	public void DeepArrayOnlyNestingStillChecksDuplicateObjectKeys()
	{
		var text = new string('[', 30) + "{a: 1, b: 2, c: 3, d: 4, e: 5, f: 6, g: 7, h: 8, i: 9, a: 10}" + new string(']', 30);
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		Assert.Contains("Duplicate", error.Reason, StringComparison.OrdinalIgnoreCase);
		Assert.Equal(text.LastIndexOf("a:", StringComparison.Ordinal), error.Offset);
	}

	[Fact]
	public void SiblingObjectsCanReuseEveryKeyAcrossDifferentSizes()
	{
		var large = "{a: 1, b: 2, c: 3, d: 4, e: 5, f: 6, g: 7, h: 8, i: 9}";
		var items = SomlValue.Parse($"[{{a: 0}}, {large}, {{}}, {large}, {{a: 10}}]").GetArray();
		Assert.Equal([1, 9, 0, 9, 1], items.Select(item => item.GetObject().Count));
		Assert.Equal(9, items[3]["i"].GetInt64());
		Assert.Equal(10, items[4]["a"].GetInt64());
	}

	[Fact]
	public void FormattingChecksDuplicatesAtTheSameDeepOffsetAsParsing()
	{
		var text = "[" + new string('[', 20) + "{a: 1, a: 2}" + new string(']', 20) + "]";
		var parsed = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		var formatted = Assert.Throws<SomlParseException>(() => SomlFormatter.Format(text));
		Assert.Equal(28, parsed.Offset);
		Assert.Equal(parsed.Offset, formatted.Offset);
		Assert.Equal(parsed.Reason, formatted.Reason);
	}

	[Fact]
	public void SiblingKeySetsDoNotHideADuplicateInALaterObject()
	{
		const string text = "[{a: 1, b: 2, c: 3, d: 4, e: 5, f: 6, g: 7, h: 8, i: 9}, {a: 1, b: 2, c: 3, d: 4, e: 5, f: 6, g: 7, h: 8, i: 9, i: 10}]";
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		Assert.Equal(text.LastIndexOf("i:", StringComparison.Ordinal), error.Offset);
	}

	[Fact]
	public void TrailingWhitespaceAndCommentsCanEndAtEndOfInput()
	{
		var text = "{a: 1}\n/*\n*/" + string.Concat(Enumerable.Repeat(" \t\n", 10_000)) + "# end";
		Assert.Equal(1, SomlValue.Parse(text)["a"].GetInt64());
		Assert.Equal("{a: 1}\n/*\n*/\n\n# end\n", SomlFormatter.Format(text));
	}

	[Fact]
	public void ScalarIntegerDisplayIgnoresACustomNegativeSign()
	{
		var previous = CultureInfo.CurrentCulture;

		try
		{
			var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
			culture.NumberFormat.NegativeSign = "minus";
			CultureInfo.CurrentCulture = culture;
			Assert.Equal("-9223372036854775808", ((SomlValue)long.MinValue).ToString());
		}
		finally
		{
			CultureInfo.CurrentCulture = previous;
		}
	}

	[Fact]
	public void ScalarFloatDisplayIgnoresCustomDecimalAndExponentSigns()
	{
		var previous = CultureInfo.CurrentCulture;

		try
		{
			var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
			culture.NumberFormat.NumberDecimalSeparator = ",";
			culture.NumberFormat.NegativeSign = "minus";
			CultureInfo.CurrentCulture = culture;
			Assert.Equal("-1.25e-7", ((SomlValue)(-1.25e-7)).ToString());
		}
		finally
		{
			CultureInfo.CurrentCulture = previous;
		}
	}

	[Fact]
	public void ScalarTimeDisplayKeepsNanosecondPrecision()
	{
		Assert.Equal("1969-12-31T23:59:59.999999999Z", ((SomlValue)SomlInstant.Parse("1970-01-01T00:59:59.999999999+01:00")).ToString());
		Assert.Equal("-1h1m1.000000001s", ((SomlValue)new SomlDuration(-3_661_000_000_001)).ToString());
	}

	[Fact]
	public void ScalarStringDisplayUsesLiteralQuotesForBackslashesAndDoubleQuotes()
	{
		Assert.Equal("'\\path\"中😀'", ((SomlValue)"\\path\"中😀").ToString());
		Assert.Equal("\"'\\\\path\\\"中😀\"", ((SomlValue)"'\\path\"中😀").ToString());
	}

	[Fact]
	public void LenientCollectionDisplayEscapesUnsupportedKeysAndValues()
	{
		SomlValue value = new SomlObject { ["bad\r"] = new SomlArray { "\uD800", true, SomlValue.Null } };
		Assert.Equal("{\n\t\"bad\\u{d}\": [\n\t\t\"\\u{d800}\"\n\t\ttrue\n\t\tnull\n\t]\n}", value.ToString());
		var error = Assert.Throws<SomlException>(() => value.ToDocumentString());
		Assert.Equal("'bad\\u{d}'", error.Path);
	}

	[Fact]
	public void DisplayPreservesMixedScalarOrderAndFloatKinds()
	{
		SomlValue value = new SomlArray { SomlValue.Null, false, 0, -0.0, double.PositiveInfinity, new SomlObject() };
		Assert.Equal("[\n\tnull\n\tfalse\n\t0\n\t0.0\n\tinfinity\n\t{}\n]", value.ToString());
		var read = SomlValue.Parse(value.ToString());
		Assert.Equal(SomlValueKind.Integer, read[2].Kind);
		Assert.Equal(SomlValueKind.Float, read[3].Kind);
	}

	[Fact]
	public void DisplayStillRejectsACycleAfterScalarFormatting()
	{
		var array = new SomlArray { 1, true, "before" };
		array.Add(array);
		var error = Assert.Throws<SomlException>(() => ((SomlValue)array).ToString());
		Assert.Contains("100 levels", error.Message, StringComparison.Ordinal);
		Assert.StartsWith("[3][3]", error.Path, StringComparison.Ordinal);
	}

	private static T Read<T>(string text, SomlTypeInfo<T> info, bool generated) => generated ? SomlSerializer.Deserialize(text, info) : SomlSerializer.Deserialize<T>(text, info.Options);

	public sealed class Defaults
	{
		public List<int> Items { get; set; } = [7];
		public string Label { get; set; } = "initial";
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ParameterlessConstructionKeepsDefaultCollectionsIndependent(bool generated)
	{
		var values = Read("[{}, {label: 'second'}]", ParsingMetadataContext.Default.DefaultsList, generated);
		values[0].Items.Add(9);
		Assert.Equal([7, 9], values[0].Items);
		Assert.Equal([7], values[1].Items);
		Assert.Equal("initial", values[0].Label);
		Assert.Equal("second", values[1].Label);
	}

	public sealed class RequiredSetter
	{
		private int _count;
		public required int Count
		{
			get => _count;
			set
			{
				_count = value;
				Writes++;
			}
		}

		[SomlIgnore]
		public int Writes { get; private set; }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RequiredWritableMembersAreAssignedOnceEvenWhenZero(bool generated)
	{
		var value = Read("count: 0", ParsingMetadataContext.Default.RequiredSetter, generated);
		Assert.Equal(0, value.Count);
		Assert.Equal(1, value.Writes);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void MissingRequiredSettersReportTheContainingArrayItem(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => Read("[{count: 1}, {}]", ParsingMetadataContext.Default.RequiredSetterList, generated));
		Assert.Equal("[1]", error.Path);
		Assert.Contains("Missing the key count", error.Message, StringComparison.Ordinal);
	}

	public enum Mode
	{
		First,
		Last,
	}

	public sealed record IgnoredEnum([property: SomlIgnore] Mode? Secret = Mode.Last, int Count = 3);

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void IgnoredNullableEnumParametersUseTheirTypedDefaults(bool generated)
	{
		var value = Read("secret: 'wrong'\ncount: 4", ParsingMetadataContext.Default.IgnoredEnum, generated);
		Assert.Equal(Mode.Last, value.Secret);
		Assert.Equal(4, value.Count);
		Assert.Equal("count: 4\n", generated ? SomlSerializer.Serialize(value, ParsingMetadataContext.Default.IgnoredEnum) : SomlSerializer.Serialize(value));
	}

	public class Animal
	{
		public string Name { get; set; } = "";
	}

	public class Mammal : Animal;
	public sealed class Dog : Mammal;

	public class BaseOwner(Animal animal)
	{
		[SomlName("base")]
		public virtual Animal Animal { get; } = animal;
	}

	public class MiddleOwner(Mammal animal) : BaseOwner(animal)
	{
		[SomlName("nearest")]
		public override Mammal Animal { get; } = animal;
	}

	public sealed class Owner(Dog animal) : MiddleOwner(animal)
	{
		public override Dog Animal { get; } = animal;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CovariantMetadataUsesTheNearestNameAcrossThreeLevels(bool generated)
	{
		var context = new ParsingMetadataContext(new SomlSerializerOptions { RejectUnknownKeys = true });
		var owner = Read("nearest: {name: 'Rex'}", context.Owner, generated);
		Assert.Equal("Rex", owner.Animal.Name);
		Assert.Equal("nearest: {\n\tname: 'Rex'\n}\n", generated ? SomlSerializer.Serialize(owner, context.Owner) : SomlSerializer.Serialize(owner));
	}

	private sealed class ConstantPolicy : JsonNamingPolicy
	{
		public override string ConvertName(string name) => "same";
	}

	public sealed record Collision(int First, int Second);
	public sealed class Single
	{
		public int Value { get; set; }
	}

	[Fact]
	public void FailedMetadataCreationDoesNotPoisonOtherTypesInTheSameOptions()
	{
		var options = new SomlSerializerOptions { PropertyNamingPolicy = new ConstantPolicy() };
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Collision>("same: 7", options));
		Assert.Equal(9, SomlSerializer.Deserialize<Single>("same: 9", options).Value);
		Assert.Equal("same: 10\n", SomlSerializer.Serialize(new Single { Value = 10 }, options));
	}

	public sealed record NamedCount([SomlName("the count")] int Count, string? Note);

	[Fact]
	public async Task ConcurrentFirstReadsWithSharedOptionsKeepArgumentsAndPathsIndependent()
	{
		var options = new SomlSerializerOptions { RejectUnknownKeys = true };
		var tasks = Enumerable.Range(0, 100).Select(index => Task.Run(() =>
		{
			var value = SomlSerializer.Deserialize<NamedCount>($"'the count': {index}\nnote: '{index}'", options);
			Assert.Equal(index, value.Count);
			Assert.Equal(index.ToString(CultureInfo.InvariantCulture), value.Note);
			var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<NamedCount>("'the count': 'bad'", options));
			Assert.Equal("'the count'", error.Path);
		}, TestContext.Current.CancellationToken));
		await Task.WhenAll(tasks);
	}

	private static readonly SomlException ConstructorFailure = new("deliberate constructor failure");

	public sealed class FailingConstructor
	{
		public FailingConstructor()
		{
			throw ConstructorFailure;
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SomlConstructorExceptionsAreWrappedWithTheirOriginalIdentity(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => Read("{nested: [{}]}", ParsingMetadataContext.Default.FailureMap, generated));
		Assert.Equal("nested[0]", error.Path);
		Assert.Same(ConstructorFailure, error.InnerException);
		Assert.NotSame(ConstructorFailure, error);
	}

	public sealed class RequiredText
	{
		public required string Text { get; init; }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PresentRequiredNullValuesReportTheMemberRatherThanAMissingKey(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => Read("[{text: 'ok'}, {text: null}]", ParsingMetadataContext.Default.RequiredTextList, generated));
		Assert.Equal("[1].text", error.Path);
		Assert.DoesNotContain("Missing", error.Message, StringComparison.Ordinal);
		Assert.Contains("line 1, column 23", error.Message, StringComparison.Ordinal);
	}

	public sealed record PolicyRecord(int RetryCount)
	{
		public string? ExtraNote { get; set; }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ConstructorBindingFlagsRemainIndependentAcrossNamingPolicies(bool generated)
	{
		var first = new ParsingMetadataContext(new SomlSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, RejectUnknownKeys = true });
		var second = new ParsingMetadataContext(new SomlSerializerOptions { PropertyNamingPolicy = null, RejectUnknownKeys = true });
		Assert.Equal(new PolicyRecord(7) { ExtraNote = "first" }, Read("retry_count: 7\nextra_note: 'first'", first.PolicyRecord, generated));
		Assert.Equal(new PolicyRecord(9) { ExtraNote = "second" }, Read("RetryCount: 9\nExtraNote: 'second'", second.PolicyRecord, generated));
	}

	public sealed class RequiredNullable
	{
		public required string? Note;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RequiredNullableFieldsDistinguishExplicitNullFromAbsence(bool generated)
	{
		Assert.Null(Read("note: null", ParsingMetadataContext.Default.RequiredNullable, generated).Note);
		var error = Assert.Throws<SomlException>(() => Read("{}", ParsingMetadataContext.Default.RequiredNullable, generated));
		Assert.Contains("Missing the key note", error.Message, StringComparison.Ordinal);
	}

	public sealed class RenamedRequired
	{
		[SetsRequiredMembers]
		public RenamedRequired([SomlName("listen on")] int port)
		{
			Port = port;
		}

		public required int Port { get; init; }
		public int Extra { get; set; }
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RequiredConstructorBindingsUseExplicitParameterNamesAndKeepOtherSetters(bool generated)
	{
		var result = Read("'listen on': 80\nextra: 9", ParsingMetadataContext.Default.RenamedRequired, generated);
		Assert.Equal(80, result.Port);
		Assert.Equal(9, result.Extra);
	}
}

[SomlSerializable(typeof(List<ParsingAndMetadataTests.Defaults>), TypeInfoPropertyName = "DefaultsList")]
[SomlSerializable(typeof(ParsingAndMetadataTests.RequiredSetter))]
[SomlSerializable(typeof(List<ParsingAndMetadataTests.RequiredSetter>), TypeInfoPropertyName = "RequiredSetterList")]
[SomlSerializable(typeof(ParsingAndMetadataTests.IgnoredEnum))]
[SomlSerializable(typeof(ParsingAndMetadataTests.Owner))]
[SomlSerializable(typeof(Dictionary<string, List<ParsingAndMetadataTests.FailingConstructor>>), TypeInfoPropertyName = "FailureMap")]
[SomlSerializable(typeof(List<ParsingAndMetadataTests.RequiredText>), TypeInfoPropertyName = "RequiredTextList")]
[SomlSerializable(typeof(ParsingAndMetadataTests.PolicyRecord))]
[SomlSerializable(typeof(ParsingAndMetadataTests.RequiredNullable))]
[SomlSerializable(typeof(ParsingAndMetadataTests.RenamedRequired))]
internal partial class ParsingMetadataContext : SomlSerializerContext;
