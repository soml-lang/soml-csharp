using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Soml.Serialization;

namespace Soml.Tests;

public sealed class ModernizationTests
{
	[Fact]
	public void UnicodeEscapesDecodeBothSupplementaryBoundariesAlongsideLiteralText()
	{
		var value = SomlValue.Parse("value: \"before\\u{10000}中\\u{10ffff}after\"").GetObject()["value"];
		Assert.Equal("before\U00010000中\U0010FFFFafter", value.GetString());
	}

	[Fact]
	public void UnicodeEscapesInKeysUseTheDecodedKeyForLookupAndDuplicateDetection()
	{
		var value = SomlValue.Parse("\"\\u{1f600}\": 7").GetObject();
		Assert.Equal(7, value["😀"].GetInt64());
		Assert.Throws<SomlParseException>(() => SomlValue.Parse("\"\\u{1f600}\": 7\n'😀': 8"));
	}

	[Fact]
	public void InvalidUnicodeValuesTakePrecedenceOverTheirNoncanonicalSpelling()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("value: \"\\u{00D800}\""));
		Assert.Contains("surrogate", error.Message, StringComparison.Ordinal);
		Assert.DoesNotContain("lowercase", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ZeroUnicodeEscapeSuggestsASingleZero()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("value: \"\\u{000000}\""));
		Assert.Contains("write \\u{0}", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void EscapedBlockStringsPreserveInteriorBlankLinesAndRemoveBoundaryBlanks()
	{
		var text = "value: \"\"\"\n\n\t\\u{10000}\n \t \n\tend\\t\\u{10ffff}\n\n\t\"\"\"";
		Assert.Equal("\U00010000\n\nend\t\U0010FFFF", SomlValue.Parse(text).GetObject()["value"].GetString());
	}

	[Fact]
	public void LongLiteralBlocksPreserveAlternatingContentAndBlankLines()
	{
		var lines = Enumerable.Range(0, 200).Select(index => $"line {index}").ToArray();
		var text = "value: '''\n\t" + string.Join("\n\n\t", lines) + "\n\t'''";
		Assert.Equal(string.Join("\n\n", lines), SomlValue.Parse(text).GetObject()["value"].GetString());
	}

	[Fact]
	public void BlockIndentationErrorsStillWinOverAnEarlierBadEscape()
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse("value: \"\"\"\n\t\\q\nwrong\n\t\"\"\""));
		Assert.Contains("indentation", error.Message, StringComparison.Ordinal);
		Assert.Equal(3, error.Line);
	}

	[Fact]
	public void DurationFractionsIgnoreSeparatedTrailingZerosWithoutLosingScale()
	{
		var duration = SomlDuration.Parse("2h3m0.000_000_001_000_000s");
		Assert.Equal(7_380_000_000_001, duration.Nanoseconds);
	}

	[Fact]
	public void ThirteenDigitHourFractionsCanStillRepresentAWholeNanosecond()
	{
		Assert.Equal(9, SomlDuration.Parse("0.000_000_000_002_5h").Nanoseconds);
	}

	[Fact]
	public void InsignificantDurationZerosDoNotCountTowardFractionPrecision()
	{
		Assert.Equal(1, SomlDuration.Parse("0.000_000_001" + new string('0', 4000) + "s").Nanoseconds);
	}

	[Fact]
	public void SeparatedAllZeroDurationFractionsStillRejectNegativeZero()
	{
		var error = Assert.Throws<FormatException>(() => SomlDuration.Parse("-0.000_000_000s"));
		Assert.Contains("zero has one spelling", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void DurationFractionPrecisionCountsDigitsInsteadOfSeparators()
	{
		var error = Assert.Throws<FormatException>(() => SomlDuration.Parse("0.000_000_000_000_01h"));
		Assert.Contains("whole number of nanoseconds", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void LeapCenturyInstantsReportTheActualLastDayOfFebruary()
	{
		Assert.Equal("2000-02-29T00:00:00Z", SomlInstant.Parse("2000-02-29T00:00:00Z").ToString());
		var error = Assert.Throws<FormatException>(() => SomlInstant.Parse("1900-02-29T00:00:00Z"));
		Assert.Contains("01 to 28", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void InstantsBeforeTheEpochKeepPositiveFractionalNanoseconds()
	{
		var value = SomlInstant.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddTicks(-1));
		Assert.Equal(-1, value.UnixSeconds);
		Assert.Equal(999_999_900, value.Nanosecond);
		Assert.Equal("1969-12-31T23:59:59.9999999Z", value.ToString());
	}

	[Fact]
	public void BareKeysAcceptTheEntireAsciiAlphabetButQuoteUnicodeAndEmptyKeys()
	{
		const string bare = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-";
		var value = new SomlObject { [bare] = 1, ["é"] = 2, [""] = 3 };
		Assert.Equal($"{bare}: 1\n'é': 2\n'': 3\n", value.ToDocumentString());
	}

	[Fact]
	public void ArrayContentHashesRespectNestedObjectOrderIndependence()
	{
		SomlValue first = new SomlArray { new SomlObject { ["a"] = 1, ["b"] = new SomlArray { 2, 3 } }, 4 };
		SomlValue second = new SomlArray { new SomlObject { ["b"] = new SomlArray { 2, 3 }, ["a"] = 1 }, 4 };
		Assert.Equal(first, second);
		Assert.Equal(first.GetHashCode(), second.GetHashCode());
	}

	private static T Read<T>(string text, SomlTypeInfo<T> info, bool generated) => generated ? SomlSerializer.Deserialize(text, info) : SomlSerializer.Deserialize<T>(text, info.Options);

	public sealed class SelectiveCollection : IEnumerable<int>
	{
		public List<int> Attempts { get; } = [];

		public bool Add(int item)
		{
			Attempts.Add(item);
			return item % 2 == 0;
		}

		public IEnumerator<int> GetEnumerator() => Attempts.GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AFalseAddResultDoesNotStopCollectionPopulation(bool generated)
	{
		var result = Read("[1, 2, 3, 4]", ModernizationContext.Default.SelectiveCollection, generated);
		Assert.Equal([1, 2, 3, 4], result.Attempts);
	}

	public sealed class TypedMap : Dictionary<string, int>
	{
		public new void Add(string key, int value)
		{
			base.Add(key.ToUpperInvariant(), value * 2);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DictionaryAddReceivesSeparateTypedKeysAndValues(bool generated)
	{
		var result = Read("one: 1\ntwo: 2", ModernizationContext.Default.TypedMap, generated);
		Assert.Equal(2, result["ONE"]);
		Assert.Equal(4, result["TWO"]);
		Assert.Equal(2, result.Count);
	}

	private static readonly InvalidOperationException CollectionFailure = new("collection failed");

	public sealed class FailingAdd : IEnumerable<int>
	{
		public void Add(int item)
		{
			throw CollectionFailure;
		}

		public IEnumerator<int> GetEnumerator() => Enumerable.Empty<int>().GetEnumerator();

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public sealed class FailingItemsConstructor : List<int>
	{
		public FailingItemsConstructor(IEnumerable<int> items)
		{
			throw CollectionFailure;
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CollectionAddFailuresPreserveTheirCauseAndNestedPath(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => Read("items: [1]", ModernizationContext.Default.AddFailureMap, generated));
		Assert.Equal("items", error.Path);
		Assert.Same(CollectionFailure, error.InnerException);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void CollectionConstructorFailuresPreserveTheirCauseAndNestedPath(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => Read("items: [1]", ModernizationContext.Default.ConstructorFailureMap, generated));
		Assert.Equal("items", error.Path);
		Assert.Same(CollectionFailure, error.InnerException);
	}

	[Fact]
	public void FrozenCollectionFactoriesRetainSetAndDictionarySemantics()
	{
		var set = SomlSerializer.Deserialize<FrozenSet<int>>("[3, 1, 2]");
		Assert.True(set.SetEquals([1, 2, 3]));
		var map = SomlSerializer.Deserialize<FrozenDictionary<string, int>>("one: 1\ntwo: 2");
		Assert.Equal(1, map["one"]);
		Assert.Equal(2, map["two"]);
	}

	[Fact]
	public void ImmutableCreateRangeFactoriesPreserveCollectionOrder()
	{
		var value = SomlSerializer.Deserialize<ImmutableList<int>>("[8, 3, 5]");
		Assert.Equal([8, 3, 5], value);
	}

	public sealed class EmptyModel;

	public sealed record NamedModel(string DisplayName, int RetryCount, string? Note = null);

	public sealed class OneMember
	{
		public int Count { get; set; }
	}

	private sealed class ConstantNamingPolicy : JsonNamingPolicy
	{
		public override string ConvertName(string name) => "same";
	}

	[Fact]
	public void GeneratedEmptyModelsRejectUnknownKeysOnlyInStrictMode()
	{
		Assert.NotNull(SomlSerializer.Deserialize("extra: 1", ModernizationContext.Default.EmptyModel));
		var context = new ModernizationContext(new SomlSerializerOptions { RejectUnknownKeys = true });
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("extra: 1", context.EmptyModel));
		Assert.Equal("extra", error.Path);
	}

	[Fact]
	public void GeneratedSingleMemberModelsStillApplyCustomNames()
	{
		var context = new ModernizationContext(new SomlSerializerOptions { PropertyNamingPolicy = new ConstantNamingPolicy() });
		var value = SomlSerializer.Deserialize("same: 7", context.OneMember);
		Assert.Equal(7, value.Count);
		Assert.Equal("same: 7\n", SomlSerializer.Serialize(value, context.OneMember));
	}

	[Fact]
	public void GeneratedMultiMemberModelsStillRejectNamingPolicyCollisions()
	{
		var context = new ModernizationContext(new SomlSerializerOptions { PropertyNamingPolicy = new ConstantNamingPolicy() });
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new NamedModel("a", 2), context.NamedModel));
		Assert.Contains("same", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void GeneratedStrictMetadataUsesTheContextNamingPolicyForSuggestions()
	{
		var context = new ModernizationContext(new SomlSerializerOptions { RejectUnknownKeys = true, PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("display_name: 'a'\nretry_coun: 2", context.NamedModel));
		Assert.Equal("retry_coun", error.Path);
		Assert.Contains("Did you mean retry_count?", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void GeneratedTypeInformationAndConvertersAreCachedPerContext()
	{
		var first = new ModernizationContext(new SomlSerializerOptions { PropertyNamingPolicy = null });
		var second = new ModernizationContext();
		Assert.Same(first.NamedModel, first.NamedModel);
		Assert.Same(first.NamedModel.Converter, first.NamedModel.Converter);
		Assert.NotSame(first.NamedModel.Converter, second.NamedModel.Converter);
		Assert.Equal("DisplayName: 'a'\nRetryCount: 2\nNote: null\n", SomlSerializer.Serialize(new NamedModel("a", 2), first.NamedModel));
		Assert.Equal("display-name: 'a'\nretry-count: 2\nnote: null\n", SomlSerializer.Serialize(new NamedModel("a", 2), second.NamedModel));
	}

	[Fact]
	public void GeneratedNullOmissionKeepsNonnullableMembersAndEmptyStrings()
	{
		var context = new ModernizationContext(new SomlSerializerOptions { OmitNullMembers = true });
		Assert.Equal("display-name: ''\nretry-count: 0\n", SomlSerializer.Serialize(new NamedModel("", 0), context.NamedModel));
	}

	public sealed class ThrowingMembers
	{
		public int Value
		{
			get => throw CollectionFailure;
			set => throw CollectionFailure;
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void GetterFailuresRetainTheOriginalExceptionInsideAnArray(bool generated)
	{
		var value = new List<ThrowingMembers> { new() };
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Serialize(value, ModernizationContext.Default.ThrowingMembersList) : SomlSerializer.Serialize(value));
		Assert.Equal("[0].value", error.Path);
		Assert.Same(CollectionFailure, error.InnerException);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void SetterFailuresRetainTheOriginalExceptionAndTextLocation(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => Read("[\n{value: 1}\n]", ModernizationContext.Default.ThrowingMembersList, generated));
		Assert.Equal("[0].value", error.Path);
		Assert.Contains("line 2, column 9", error.Message, StringComparison.Ordinal);
		Assert.Same(CollectionFailure, error.InnerException);
	}

	public sealed class ThrowingConstructor(int value)
	{
		public int Value { get; } = value > 0 ? throw CollectionFailure : value;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void ObjectConstructorFailuresKeepTheirOriginalCauseInsideAnArray(bool generated)
	{
		var error = Assert.Throws<SomlException>(() => Read("[{value: 1}]", ModernizationContext.Default.ThrowingConstructorList, generated));
		Assert.Equal("[0]", error.Path);
		Assert.Same(CollectionFailure, error.InnerException);
	}

	public struct MutableStruct
	{
		public int Property { get; set; }

		public int Field;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PropertyInvokersMutateTheSameBoxAsFieldSetters(bool generated)
	{
		var result = Read("property: 7\nfield: 9", ModernizationContext.Default.MutableStruct, generated);
		Assert.Equal(7, result.Property);
		Assert.Equal(9, result.Field);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Utf8StreamWritesRespectPositionAndDoNotEmitUnusedPooledBytes(bool generated)
	{
		using var stream = new MemoryStream();
		stream.Write([0x7F]);
		var value = new NamedModel("中😀", 2);

		if (generated)
		{
			SomlSerializer.Serialize(stream, value, ModernizationContext.Default.NamedModel);
		}
		else
		{
			SomlSerializer.Serialize(stream, value);
		}

		Assert.Equal(Encoding.UTF8.GetBytes("\u007fdisplay-name: '中😀'\nretry-count: 2\nnote: null\n"), stream.ToArray());
		Assert.True(stream.CanWrite);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AsyncUtf8StreamWritesMatchExactUnicodeBytes(bool generated)
	{
		using var stream = new MemoryStream();
		var value = new NamedModel("中😀", 2);

		if (generated)
		{
			await SomlSerializer.SerializeAsync(stream, value, ModernizationContext.Default.NamedModel, TestContext.Current.CancellationToken);
		}
		else
		{
			await SomlSerializer.SerializeAsync(stream, value, cancellationToken: TestContext.Current.CancellationToken);
		}

		Assert.Equal(Encoding.UTF8.GetBytes("display-name: '中😀'\nretry-count: 2\nnote: null\n"), stream.ToArray());
		Assert.True(stream.CanWrite);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void InvalidValuesNeverPartiallyWriteAStream(bool generated)
	{
		using var stream = new MemoryStream();
		var value = new NamedModel("bad\rvalue", 2);
		Assert.Throws<SomlException>(() =>
		{
			if (generated)
			{
				SomlSerializer.Serialize(stream, value, ModernizationContext.Default.NamedModel);
			}
			else
			{
				SomlSerializer.Serialize(stream, value);
			}
		});
		Assert.Equal(0, stream.Length);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task CanceledAsyncWritesLeaveTheStreamEmptyAndOpen(bool generated)
	{
		using var stream = new MemoryStream();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();
		var value = new NamedModel("a", 2);
		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
		{
			if (generated)
			{
				await SomlSerializer.SerializeAsync(stream, value, ModernizationContext.Default.NamedModel, cancellation.Token);
			}
			else
			{
				await SomlSerializer.SerializeAsync(stream, value, cancellationToken: cancellation.Token);
			}
		});
		Assert.Equal(0, stream.Length);
		Assert.True(stream.CanWrite);
	}

	private static readonly IOException StreamFailure = new("write failed");

	private sealed class FailingStream : MemoryStream
	{
		public override void Write(ReadOnlySpan<byte> buffer)
		{
			throw StreamFailure;
		}

		public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException(StreamFailure);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task StreamWriteFailuresPropagateUnchangedAndLeaveTheStreamOpen(bool generated)
	{
		using var stream = new FailingStream();
		var value = new NamedModel("a", 2);
		var synchronous = Assert.Throws<IOException>(() =>
		{
			if (generated)
			{
				SomlSerializer.Serialize(stream, value, ModernizationContext.Default.NamedModel);
			}
			else
			{
				SomlSerializer.Serialize(stream, value);
			}
		});
		var asynchronous = await Assert.ThrowsAsync<IOException>(async () =>
		{
			if (generated)
			{
				await SomlSerializer.SerializeAsync(stream, value, ModernizationContext.Default.NamedModel, TestContext.Current.CancellationToken);
			}
			else
			{
				await SomlSerializer.SerializeAsync(stream, value, cancellationToken: TestContext.Current.CancellationToken);
			}
		});
		Assert.Same(StreamFailure, synchronous);
		Assert.Same(StreamFailure, asynchronous);
		Assert.True(stream.CanWrite);
		Assert.Equal(0, stream.Length);
	}

	private sealed class DelayedStream : MemoryStream
	{
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

		public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
		{
			await Release.Task.WaitAsync(cancellationToken);
			await base.WriteAsync(buffer, cancellationToken);
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task PendingAsyncWritesKeepTheirBytesDuringAnotherSerialization(bool generated)
	{
		using var delayed = new DelayedStream();
		using var other = new MemoryStream();
		var first = new NamedModel("first", 1);
		var pending = generated
			? SomlSerializer.SerializeAsync(delayed, first, ModernizationContext.Default.NamedModel, TestContext.Current.CancellationToken)
			: SomlSerializer.SerializeAsync(delayed, first, cancellationToken: TestContext.Current.CancellationToken);
		Assert.False(pending.IsCompleted);
		SomlSerializer.Serialize(other, new NamedModel("other", 2));
		delayed.Release.SetResult();
		await pending;
		Assert.Equal(Encoding.UTF8.GetBytes(SomlSerializer.Serialize(first)), delayed.ToArray());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task InvalidDocumentsAreReportedBeforeStreamCancellation(bool generated)
	{
		using var stream = new MemoryStream();
		using var cancellation = new CancellationTokenSource();
		await cancellation.CancelAsync();
		var value = new NamedModel("bad\rvalue", 2);
		var error = await Assert.ThrowsAsync<SomlException>(async () =>
		{
			if (generated)
			{
				await SomlSerializer.SerializeAsync(stream, value, ModernizationContext.Default.NamedModel, cancellation.Token);
			}
			else
			{
				await SomlSerializer.SerializeAsync(stream, value, cancellationToken: cancellation.Token);
			}
		});
		Assert.Equal("display-name", error.Path);
		Assert.Equal(0, stream.Length);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void LargeStreamWritesAreNotTruncatedAtThePoolBucketBoundary(bool generated)
	{
		using var stream = new MemoryStream();
		var value = new NamedModel(new string('中', 50_000), 2);

		if (generated)
		{
			SomlSerializer.Serialize(stream, value, ModernizationContext.Default.NamedModel);
		}
		else
		{
			SomlSerializer.Serialize(stream, value);
		}

		Assert.Equal(Encoding.UTF8.GetBytes(SomlSerializer.Serialize(value)), stream.ToArray());
	}
}

[SomlSerializable(typeof(ModernizationTests.SelectiveCollection))]
[SomlSerializable(typeof(ModernizationTests.TypedMap))]
[SomlSerializable(typeof(Dictionary<string, ModernizationTests.FailingAdd>), TypeInfoPropertyName = "AddFailureMap")]
[SomlSerializable(typeof(Dictionary<string, ModernizationTests.FailingItemsConstructor>), TypeInfoPropertyName = "ConstructorFailureMap")]
[SomlSerializable(typeof(ModernizationTests.EmptyModel))]
[SomlSerializable(typeof(ModernizationTests.NamedModel))]
[SomlSerializable(typeof(ModernizationTests.OneMember))]
[SomlSerializable(typeof(List<ModernizationTests.ThrowingMembers>), TypeInfoPropertyName = "ThrowingMembersList")]
[SomlSerializable(typeof(List<ModernizationTests.ThrowingConstructor>), TypeInfoPropertyName = "ThrowingConstructorList")]
[SomlSerializable(typeof(ModernizationTests.MutableStruct))]
internal partial class ModernizationContext : SomlSerializerContext;
