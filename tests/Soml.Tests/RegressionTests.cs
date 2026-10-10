using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Text.Json;
using Soml.Serialization;

namespace Soml.Tests;

[SomlSerializable(typeof(Dictionary<string, RegressionTests.FailingText>), TypeInfoPropertyName = "TextValues")]
[SomlSerializable(typeof(SortedSet<RegressionTests.OrderedItem>), TypeInfoPropertyName = "OrderedItems")]
internal sealed partial class RegressionContext : SomlSerializerContext;

public sealed class RegressionTests
{
	public sealed class OrderedItem(int identity, int rank) : IComparable<OrderedItem>, IEquatable<OrderedItem>
	{
		public int Identity { get; } = identity;

		public int Rank { get; } = rank;

		public int CompareTo(OrderedItem? other) => other is null ? 1 : Rank.CompareTo(other.Rank);

		public bool Equals(OrderedItem? other) => other is not null && Identity == other.Identity;

		public override bool Equals(object? other) => other is OrderedItem item && Equals(item);

		public override int GetHashCode() => Identity;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void RejectsItemsThatTheDestinationSetWouldDrop(bool generated)
	{
		const string text = "[{identity: 1, rank: 1}, {identity: 2, rank: 1}]";
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize(text, RegressionContext.Default.OrderedItems) : SomlSerializer.Deserialize<SortedSet<OrderedItem>>(text));
		Assert.Equal("Duplicate item in SortedSet<OrderedItem> (at line 1, column 1)", error.Message);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void AcceptsItemsThatTheDestinationSetConsidersDistinct(bool generated)
	{
		const string text = "[{identity: 1, rank: 1}, {identity: 1, rank: 2}]";
		var result = generated ? SomlSerializer.Deserialize(text, RegressionContext.Default.OrderedItems) : SomlSerializer.Deserialize<SortedSet<OrderedItem>>(text);
		Assert.Equal([1, 2], result.Select(item => item.Rank));
	}

	private static SomlObject NestedObjects(int depth)
	{
		var result = new SomlObject();
		var current = result;

		for (var level = 1; level < depth; level++)
		{
			var next = new SomlObject();
			current["a"] = next;
			current = next;
		}

		return result;
	}

	private static SomlArray NestedArrays(int depth)
	{
		var result = new SomlArray();
		var current = result;

		for (var level = 1; level < depth; level++)
		{
			var next = new SomlArray();
			current.Add(next);
			current = next;
		}

		return result;
	}

	[Fact]
	public void WritesExactlyOneHundredLevels()
	{
		foreach (SomlValue value in (SomlValue[])[NestedObjects(100), NestedArrays(100)])
		{
			var text = value.ToDocumentString();
			Assert.Equal(value, SomlValue.Parse(text));
		}

		foreach (SomlValue value in (SomlValue[])[NestedObjects(101), NestedArrays(101)])
		{
			var error = Assert.Throws<SomlException>(() => value.ToDocumentString());
			Assert.StartsWith("Cannot serialize a value nested more than 100 levels deep", error.Message, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ChecksReadDepthEvenWhenTheInnermostContainerIsEmpty()
	{
		foreach (var depth in new[] { 100, 101 })
		{
			SomlValue objects = new SomlObject();

			for (var level = 1; level < depth; level++)
			{
				objects = new SomlObject { ["next"] = objects };
			}

			Func<object?>[] readers =
			[
				() => SomlSerializer.Deserialize<SerializerParityTests.Linked>(objects),
				() => SomlSerializer.Deserialize(objects, ParityContext.Default.Linked),
				() => SomlSerializer.Deserialize<object>(objects),
				() => SomlSerializer.Deserialize<object>(NestedArrays(depth)),
			];

			foreach (var read in readers)
			{
				if (depth == 100)
				{
					Assert.NotNull(read());
				}
				else
				{
					var error = Assert.Throws<SomlException>(read);
					Assert.StartsWith("Cannot deserialize a value nested more than 100 levels deep", error.Message, StringComparison.Ordinal);
				}
			}
		}
	}

	[Fact]
	public void SerializesAScalarAtTheDeepestLevel()
	{
		object value = 1;

		for (var level = 0; level < 100; level++)
		{
			value = new Dictionary<string, object> { ["a"] = value };
		}

		var text = SomlSerializer.Serialize(value);
		Assert.Equal(100, text.Count(character => character == '{') + 1);
	}

	public sealed record WithSequence(IEnumerable<int> Items);

	private sealed class FixedKeyPolicy(string key) : JsonNamingPolicy
	{
		public override string ConvertName(string name) => key;
	}

	[Fact]
	public void SkipsImpossibleKeySuggestionsWithoutAllocatingDistanceBuffers()
	{
		var options = new SomlSerializerOptions { RejectUnknownKeys = true, PropertyNamingPolicy = new FixedKeyPolicy(new string('k', 50_000)) };
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<WithSequence>("x: 1", options));
		var before = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<WithSequence>("x: 1", options));
		var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

		Assert.DoesNotContain("Did you mean", error.Message, StringComparison.Ordinal);
		Assert.True(allocated < 100_000, $"An impossible suggestion allocated {allocated} bytes");
	}

	[Theory]
	[InlineData(false, 100, true)]
	[InlineData(true, 100, true)]
	[InlineData(false, 101, false)]
	[InlineData(true, 101, false)]
	[InlineData(false, 1000, false)]
	[InlineData(true, 1000, false)]
	public void LimitsTheLengthOfKeySuggestions(bool generated, int length, bool hasSuggestion)
	{
		var knownKey = new string('k', length);
		var unknownKey = knownKey[..^1] + "x";
		var options = new SomlSerializerOptions { RejectUnknownKeys = true, PropertyNamingPolicy = new FixedKeyPolicy(knownKey) };
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize($"{unknownKey}: []", new GeneratorContext(options).WithSequence) : SomlSerializer.Deserialize<WithSequence>($"{unknownKey}: []", options));

		Assert.StartsWith(unknownKey[..10], error.Path, StringComparison.Ordinal);
		Assert.StartsWith("Unknown key", error.Message, StringComparison.Ordinal);
		Assert.Equal(hasSuggestion, error.Message.Contains("Did you mean", StringComparison.Ordinal));
	}

	[Fact]
	public void WritesALazySequenceAsAnArray()
	{
		var value = new WithSequence(new[] { 1, 2, 3 }.Where(item => item > 1));

		Assert.Equal("items: [\n\t2\n\t3\n]\n", SomlSerializer.Serialize(value));
	}

	public sealed record Collections(
		ImmutableArray<int> Array,
		ImmutableList<string> List,
		ImmutableHashSet<int> Set,
		ImmutableDictionary<string, int> Dictionary,
		ReadOnlyCollection<int> ReadOnly,
		FrozenSet<string> Frozen,
		FrozenDictionary<string, int> FrozenDictionary,
		SortedDictionary<string, int> Sorted,
		Queue<int> Queue);

	[Fact]
	public void ReadsAndWritesImmutableAndOtherCollections()
	{
		var value = SomlSerializer.Deserialize<Collections>("""
			array: [1, 2]
			list: ['a']
			set: [3]
			dictionary: {a: 1}
			read-only: [4, 5]
			frozen: ['x']
			frozen-dictionary: {b: 2}
			sorted: {d: 4, c: 3}
			queue: [6, 7]
			""");

		Assert.Equal([1, 2], value.Array);
		Assert.Equal(["a"], value.List);
		Assert.Equal([3], value.Set);
		Assert.Equal(1, value.Dictionary["a"]);
		Assert.Equal([4, 5], value.ReadOnly);
		Assert.Equal(["x"], value.Frozen.Items);
		Assert.Equal(2, value.FrozenDictionary["b"]);
		Assert.Equal(["c", "d"], value.Sorted.Keys);
		Assert.Equal([6, 7], value.Queue);

		var empty = SomlSerializer.Deserialize<Dictionary<string, ImmutableArray<int>>>("a: []");
		Assert.True(empty["a"].IsEmpty);

		var text = SomlSerializer.Serialize(value);
		Assert.Equal(text, SomlSerializer.Serialize(SomlSerializer.Deserialize<Collections>(text)));
	}

	public sealed class ThrowingSetter
	{
		public int Port
		{
			get => 0;
			set => throw new ArgumentOutOfRangeException(nameof(value), "The port must not be 0");
		}
	}

	[Fact]
	public void WrapsAnExceptionFromASetterWithThePath()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<ThrowingSetter>("port: 0"));

		Assert.Contains("The port must not be 0", error.Message, StringComparison.Ordinal);
		Assert.Equal("port", error.Path);
		Assert.IsType<ArgumentOutOfRangeException>(error.InnerException);
	}

	[Fact]
	public void ConvertsInstantsBefore1970WithAFraction()
	{
		var instant = SomlInstant.FromDateTimeOffset(new DateTimeOffset(1969, 12, 31, 23, 59, 59, 500, TimeSpan.Zero));

		Assert.Equal("1969-12-31T23:59:59.5Z", instant.ToString());
		Assert.Equal("1969-12-31T23:59:59.9999999Z", SomlInstant.FromDateTimeOffset(DateTimeOffset.UnixEpoch.AddTicks(-1)).ToString());
	}

	[Fact]
	public void RefusesAnIntThatAFloatCannotHoldExactly()
	{
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, float>>("a: 9223372036854775807"));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, Half>>("a: 9223372036854775807"));
	}

	[Fact]
	public void RefusesAFloatThatADecimalRoundsToZero()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, decimal>>("a: 1e-30"));
		Assert.Contains("decimal", error.Message, StringComparison.Ordinal);
	}

	public sealed class WithDefaultArray
	{
		public ImmutableArray<string> Items { get; set; }
	}

	public sealed class WithClashingKeys
	{
		public string Url { get; set; } = "";

		public string URL { get; set; } = "";
	}

	[Flags]
	public enum LargeFlags : ulong
	{
		None = 0,
		Small = 1,
		Big = 1UL << 63,
	}

	[Fact]
	public void ThrowsOnlySomlExceptionFromTheSerializer()
	{
		Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new WithDefaultArray()));
		Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new WithClashingKeys()));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("a: []", typeof(Dictionary<,>).MakeGenericType(typeof(string), typeof(int).MakePointerType().MakeArrayType())));

		var flags = new Dictionary<string, LargeFlags> { ["a"] = LargeFlags.Big | LargeFlags.Small };
		var text = SomlSerializer.Serialize(flags);
		Assert.Equal("a: [\n\t'small'\n\t'big'\n]\n", text);
		Assert.Equal(flags["a"], SomlSerializer.Deserialize<Dictionary<string, LargeFlags>>(text)["a"]);
	}

	public sealed class Inner
	{
		public int A { get; set; }
	}

	public sealed class Outer
	{
		public Inner Inner { get; set; } = new();

		public List<string> Items { get; set; } = [];
	}

	[Fact]
	public void NamesTheExpectedShapeInErrors()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Outer>("inner: 'x'"));
		Assert.StartsWith("Expected an object for Inner, but found the string 'x'", error.Message, StringComparison.Ordinal);

		var arrayError = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Outer>("items: 'x'"));
		Assert.StartsWith("Expected an array for List<string>, but found the string 'x'", arrayError.Message, StringComparison.Ordinal);
	}

	public sealed class WithReadOnlyMembers
	{
		public int A { get; set; } = 1;

		public int Double => A * 2;

		public int Private { get; private set; } = 5;
	}

	[Fact]
	public void ReadsBackWhatItWrites()
	{
		var text = SomlSerializer.Serialize(new WithReadOnlyMembers { A = 3 });

		Assert.Equal("a: 3\ndouble: 6\nprivate: 5\n", text);
		Assert.Equal(3, SomlSerializer.Deserialize<WithReadOnlyMembers>(text).A);
		Assert.Equal(3, SomlSerializer.Deserialize<WithReadOnlyMembers>(text, new SomlSerializerOptions { RejectUnknownKeys = true }).A);
	}

	[Fact]
	public void ShowsAnyValueWithToString()
	{
		SomlValue value = "a\r\nb";

		Assert.Equal("\"a\\u{d}\\nb\"", value.ToString());
		Assert.Throws<SomlException>(() => new SomlObject { ["k"] = value }.ToDocumentString());
	}

	[Fact]
	public void ConvertsOtherNumericTypesToTheRightKind()
	{
		Assert.Equal(SomlValueKind.Integer, ((SomlValue)5UL).Kind);
		Assert.Equal(SomlValueKind.Integer, ((SomlValue)5U).Kind);
		Assert.Throws<OverflowException>(() => (SomlValue)ulong.MaxValue);
		Assert.Equal("c", ((SomlValue)'c').GetString());
		Assert.Equal("0.1", ((SomlValue)0.1f).ToString());
	}

	[Fact]
	public void DoesInstantArithmetic()
	{
		var start = SomlInstant.Parse("2026-09-19T14:00:00Z");
		var end = start + SomlDuration.Parse("1h30m");

		Assert.Equal("2026-09-19T15:30:00Z", end.ToString());
		Assert.Equal(SomlDuration.Parse("1h30m"), end - start);
		Assert.Equal(start, end - SomlDuration.Parse("1h30m"));
		Assert.Throws<OverflowException>(() => SomlInstant.MaxValue + SomlDuration.Parse("1ns"));
		Assert.Throws<OverflowException>(() => SomlInstant.MaxValue - SomlInstant.MinValue);
	}

	[Fact]
	public void LeavesNothingWrittenWhenFormattingFails()
	{
		Assert.False(SomlDuration.Parse("1.5s").TryFormat(new char[2], out var written));
		Assert.Equal(0, written);
	}

	[Fact]
	public void WritesTimeKeysInCanonicalForm()
	{
		var value = new Dictionary<DateTimeOffset, int> { [new DateTimeOffset(2026, 9, 19, 16, 0, 0, TimeSpan.FromHours(2))] = 1 };
		var text = SomlSerializer.Serialize(value);

		Assert.Equal("'2026-09-19T14:00:00Z': 1\n", text);
		Assert.Equal(1, SomlSerializer.Deserialize<Dictionary<DateTimeOffset, int>>(text)[value.Keys.First()]);
		Assert.Equal(2, SomlSerializer.Deserialize<Dictionary<TimeSpan, int>>("'1h30m': 2")[TimeSpan.FromMinutes(90)]);
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<TimeSpan, int>>("'90m': 2"));
	}

	[Fact]
	public void ConvertsEveryIntegerTypeToAnInt()
	{
		// Each line must compile, which the conversions from `uint` and `ulong` once broke for `byte` and `ushort`.
		SomlValue[] values = [(byte)1, (sbyte)1, (short)1, (ushort)1, 1, 1U, 1L, 1UL];

		Assert.All(values, value => Assert.Equal(1, value.GetInt64()));
	}

	[Fact]
	public void ReplacesAValueWhileEnumerating()
	{
		var members = new SomlObject { ["a"] = 1, ["b"] = 2 };

		foreach (var (key, _) in members)
		{
			members[key] = 3;
		}

		Assert.Equal([3, 3], members.Values);
		Assert.Throws<ArgumentException>(() => members.Keys.CopyTo(new string[1], 0));
	}

	public sealed class Headers : Dictionary<string, string>;

	public sealed class WithOddMembers
	{
		public Headers Headers { get; set; } = [];

		public Memory<int> Buffer { get; set; }

		public Dictionary<string, int>.KeyCollection? Keys { get; set; }
	}

	[Fact]
	public void ReadsADictionarySubclassAndSkipsRefStructMembers()
	{
		var value = SomlSerializer.Deserialize<WithOddMembers>("headers: {accept: 'text/plain'}");

		Assert.Equal("text/plain", value.Headers["accept"]);
		Assert.Contains("headers", SomlSerializer.Serialize(value), StringComparison.Ordinal);
	}

	[Fact]
	public void PointsAtAnInvalidDictionaryKey()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, Dictionary<int, int>>>("map: {\n\t1: 1\n\t'x': 2\n}"));

		Assert.EndsWith("(at map.x, line 3, column 2)", error.Message, StringComparison.Ordinal);
	}

	public readonly record struct FormattingKey(string Value) : IParsable<FormattingKey>, IFormattable
	{
		public static IOException Failure { get; } = new("The key cannot be formatted");

		public static FormattingKey Parse(string text, IFormatProvider? provider) => new(text);

		public static bool TryParse(string? text, IFormatProvider? provider, out FormattingKey result)
		{
			result = new(text ?? "");
			return text is not null;
		}

		public string ToString(string? format, IFormatProvider? formatProvider) => Value == "broken" ? throw Failure : Value;
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void PointsAtTheKeyWhenItsCanonicalFormatterThrows(bool withTypeInfo)
	{
		var keyConverter = SomlConverters.Parsable<FormattingKey>();
		var dictionaryConverter = SomlConverters.Dictionary<Dictionary<FormattingKey, int>, FormattingKey, int>(keyConverter, null, SomlConverters.Int32, false, static members => members);
		var converter = SomlConverters.Dictionary<Dictionary<string, Dictionary<FormattingKey, int>>, string, Dictionary<FormattingKey, int>>(SomlConverters.String, null, dictionaryConverter, false, static members => members);
		var typeInfo = new SomlTypeInfo<Dictionary<string, Dictionary<FormattingKey, int>>>(RegressionContext.Default, () => converter);
		const string text = "map: {\n\tbroken: 1\n}";
		var error = Assert.Throws<SomlException>(() => withTypeInfo ? SomlSerializer.Deserialize(text, typeInfo) : SomlSerializer.Deserialize<Dictionary<string, Dictionary<FormattingKey, int>>>(text));

		Assert.Equal("map.broken", error.Path);
		Assert.Same(FormattingKey.Failure, error.InnerException);
		Assert.Equal("Cannot write FormattingKey: The key cannot be formatted (at map.broken, line 2, column 2)", error.Message);

		const string valid = "map: {\n\tvalid: 1\n}\n";
		var value = withTypeInfo ? SomlSerializer.Deserialize(valid, typeInfo) : SomlSerializer.Deserialize<Dictionary<string, Dictionary<FormattingKey, int>>>(valid);
		Assert.Equal(valid, withTypeInfo ? SomlSerializer.Serialize(value, typeInfo) : SomlSerializer.Serialize(value));
	}

	public sealed class WithLazySequence
	{
		public IEnumerable<int> Items => Throwing();

		private static IEnumerable<int> Throwing()
		{
			yield return 1;
			throw new FormatException("broken");
		}
	}

	[Fact]
	public void WrapsAnExceptionFromALazySequence()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new WithLazySequence()));

		Assert.Equal("items", error.Path);
	}

	[Fact]
	public void RefusesANonGenericCollection()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new Dictionary<string, object> { ["a"] = new System.Collections.ArrayList { 1 } }));

		Assert.Contains("non-generic collection", error.Message, StringComparison.Ordinal);
	}

	public sealed class WithAnyValue
	{
		public SomlValue Value { get; set; }

		public List<SomlValue> Items { get; set; } = [];
	}

	[Fact]
	public void ReadsNullIntoASomlValue()
	{
		var value = SomlSerializer.Deserialize<WithAnyValue>("value: null\nitems: [null, 1]");

		Assert.True(value.Value.IsNull);
		Assert.Equal([SomlValue.Null, 1], value.Items);
		Assert.Equal("value: null\nitems: [\n\tnull\n\t1\n]\n", SomlSerializer.Serialize(value));
	}

	public sealed record WithIgnoredParameter(int A, [property: SomlIgnore] int B = 7);

	public sealed class WithIgnoredRequiredParameter(int a, int b)
	{
		public int A { get; } = a;

		[SomlIgnore]
		public int B { get; } = b;
	}

	[Fact]
	public void IgnoresTheParameterOfAnIgnoredMember()
	{
		Assert.Equal(new WithIgnoredParameter(1, 7), SomlSerializer.Deserialize<WithIgnoredParameter>("a: 1\nb: 5"));
		Assert.Equal(0, SomlSerializer.Deserialize<WithIgnoredRequiredParameter>("a: 1").B);
	}

	[Fact]
	public void WritesHalfWithItsOwnDigits()
	{
		Assert.Equal("a: 0.1\n", SomlSerializer.Serialize(new Dictionary<string, Half> { ["a"] = (Half)0.1 }));
	}

	public readonly record struct Throwing : IParsable<Throwing>, IFormattable
	{
		public static Throwing Parse(string s, IFormatProvider? provider) => throw new InvalidCastException("nope");

		public static bool TryParse(string? s, IFormatProvider? provider, out Throwing result) => throw new InvalidCastException("nope");

		public string ToString(string? format, IFormatProvider? formatProvider) => "x";
	}

	[Fact]
	public void WrapsAnExceptionFromAParsableType()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, Throwing>>("a: 'x'"));

		Assert.Contains("nope", error.Message, StringComparison.Ordinal);
	}

	public readonly record struct FailingText(bool ThrowsSomlException) : IParsable<FailingText>, IFormattable
	{
		public static FailingText Parse(string s, IFormatProvider? provider) => throw Failure(s == "soml");

		public static bool TryParse(string? s, IFormatProvider? provider, out FailingText result) => throw Failure(s == "soml");

		public string ToString(string? format, IFormatProvider? formatProvider) => throw Failure(ThrowsSomlException);

		private static Exception Failure(bool isSomlException) => isSomlException ? new SomlException("failed") : new InvalidOperationException("failed");
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void WrapsCustomTextConversionFailuresWithTheirPathAndCause(bool throwsSomlException)
	{
		var value = new Dictionary<string, FailingText> { ["a"] = new(throwsSomlException) };
		var text = throwsSomlException ? "a: 'soml'" : "a: 'other'";
		Action[] conversions =
		[
			() => SomlSerializer.Serialize(value),
			() => SomlSerializer.Serialize(value, RegressionContext.Default.TextValues),
			() => SomlSerializer.Deserialize<Dictionary<string, FailingText>>(text),
			() => SomlSerializer.Deserialize(text, RegressionContext.Default.TextValues),
		];

		foreach (var convert in conversions)
		{
			var error = Assert.Throws<SomlException>(convert);
			Assert.Equal("a", error.Path);
			Assert.Equal("failed", error.InnerException?.Message);
			Assert.Equal(throwsSomlException ? typeof(SomlException) : typeof(InvalidOperationException), error.InnerException?.GetType());
		}
	}

	public sealed record Numbers(long Integer, double Float, SomlDuration Duration);

	[Fact]
	public void WritesTheSameTextInEveryCulture()
	{
		var culture = System.Globalization.CultureInfo.CurrentCulture;

		try
		{
			// Swedish writes a minus sign as U+2212, which SOML does not accept.
			System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("sv-SE");
			var value = new SomlObject { ["integer"] = -5L, ["float"] = -1.5e-10, ["small"] = 1e-7, ["duration"] = SomlDuration.Parse("-90m") };

			Assert.Equal("integer: -5\nfloat: -1.5e-10\nsmall: 1e-7\nduration: -1h30m\n", value.ToDocumentString());
			Assert.Equal("integer: -5\nfloat: -1.5e-10\nduration: -1h30m\n", SomlSerializer.Serialize(new Numbers(-5, -1.5e-10, SomlDuration.Parse("-90m"))));
			Assert.Equal("The int -300 is outside the range of byte (0 to 255) (at a, line 1, column 4)", Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, byte>>("a: -300")).Message);
		}
		finally
		{
			System.Globalization.CultureInfo.CurrentCulture = culture;
		}
	}

	public sealed class Uncreatable : IEnumerable<int>
	{
		private readonly int[] _items;

		private Uncreatable(int[] items)
		{
			_items = items;
		}

		public static Uncreatable Of(params int[] items) => new(items);

		public IEnumerator<int> GetEnumerator() => ((IEnumerable<int>)_items).GetEnumerator();

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	public sealed record WithUncreatable(Uncreatable Items);

	[Fact]
	public void WritesACollectionThatItCannotCreate()
	{
		var text = SomlSerializer.Serialize(new WithUncreatable(Uncreatable.Of(1, 2)));
		Assert.Equal("items: [\n\t1\n\t2\n]\n", text);

		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<WithUncreatable>(text));
		Assert.Equal("Cannot create Uncreatable: it has no public parameterless constructor with an Add method, no constructor that takes the items, and no CreateRange() factory (at items, line 1, column 8)", error.Message);
	}

	// A nullable key breaks the `notnull` constraint of `Dictionary<TKey, TValue>`, which code without nullable annotations does not see.
#pragma warning disable CS8714
	[Fact]
	public void ReadsAndWritesNullableKeys()
	{
		var guid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

		Assert.Equal(1, SomlSerializer.Deserialize<Dictionary<Guid?, int>>("0f8fad5b-d9cb-469f-a165-70867728950e: 1")[guid]);
		Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e: 1\n", SomlSerializer.Serialize(new Dictionary<Guid?, int> { [guid] = 1 }));
		Assert.Equal(1, SomlSerializer.Deserialize<Dictionary<TimeSpan?, int>>("1h30m: 1")[TimeSpan.FromMinutes(90)]);
		Assert.Equal("1h30m: 1\n", SomlSerializer.Serialize(new Dictionary<TimeSpan?, int> { [TimeSpan.FromMinutes(90)] = 1 }));
		Assert.StartsWith("'x' is not a valid Guid", Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<Guid?, int>>("x: 1")).Message, StringComparison.Ordinal);

		// A nullable enum key, of a [Flags] enum too, is one member's name, as a key of the enum itself is.
		Assert.Equal("read: 'r'\n", SomlSerializer.Serialize(new Dictionary<SerializerTests.Permissions?, string> { [SerializerTests.Permissions.Read] = "r" }));
		Assert.Equal("r", SomlSerializer.Deserialize<Dictionary<SerializerTests.Permissions?, string>>("read: 'r'")[SerializerTests.Permissions.Read]);
		Assert.StartsWith("The key Read is not a member of Permissions", Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<SerializerTests.Permissions?, string>>("Read: 'r'")).Message, StringComparison.Ordinal);

		// A nullable key of a parsable type, such as `int?`, is parsed as the type is.
		Assert.Equal(1, SomlSerializer.Deserialize<Dictionary<int?, int>>("-7: 1")[-7]);
		Assert.Equal("-7: 1\n", SomlSerializer.Serialize(new Dictionary<int?, int> { [-7] = 1 }));
	}
#pragma warning restore CS8714

	[Fact]
	public void EscapesTerminalCharactersOfAKeyInSerializerErrors()
	{
		// The key holds U+009B, a C1 control that a terminal acts on, and U+202E, a bidi control, written as escapes, which the serializer's error messages must not show raw.
		var errors = new[]
		{
			Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<int, int>>("\"\\u{9b}\\u{202e}x\": 1")),
			Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<SerializerTests.Permissions, int>>("\"\\u{9b}\\u{202e}x\": 1")),
			Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, int>>("\"\\u{9b}\\u{202e}x\": 'a'")),
		};

		foreach (var error in errors)
		{
			Assert.DoesNotContain((char)0x9B, error.Message);
			Assert.DoesNotContain((char)0x202E, error.Message);
		}
	}

	[Fact]
	public void FindsEveryKeyOfALargeObjectFromManyThreadsAtOnce()
	{
		// A parsed object makes its index of keys on its first search, which many threads may do at once.
		var text = string.Concat(Enumerable.Range(0, 200).Select(index => $"key{index}: {index}\n"));

		for (var round = 0; round < 50; round++)
		{
			var document = SomlValue.Parse(text).GetObject();
			var misses = 0;

			Parallel.For(0, 16, _ =>
			{
				for (var index = 0; index < 200; index++)
				{
					if (!document.TryGetValue($"key{index}", out var value) || value.GetInt64() != index)
					{
						Interlocked.Increment(ref misses);
					}
				}
			});

			Assert.Equal(0, misses);
		}
	}
}
