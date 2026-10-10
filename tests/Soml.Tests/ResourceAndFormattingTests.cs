using System.Text;
using System.Text.Json;
using Soml.Cli;
using Soml.Serialization;

namespace Soml.Tests;

public sealed class ResourceAndFormattingTests
{
	[Fact]
	public void HugeBareKeysDoNotAllocateHugeErrorPaths()
	{
		var key = new string('x', 1_000_000);
		var value = new SomlObject { [key] = "wrong type" };
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, int>>(value));

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, int>>(value));
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Equal(new string('x', 200) + "…", error.Path);
		Assert.True(allocated < 100_000, $"An error path allocated {allocated} bytes");
	}

	[Fact]
	public void MissingEditPathsRejectExcessiveDepthBeforeBuildingObjects()
	{
		var path = Enumerable.Repeat((SomlPathSegment)"item", 10_000).ToArray();
		Assert.Throws<SomlException>(() => SomlEditor.Set("{}", path, 1));

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlException>(() => SomlEditor.Set("{}", path, 1));
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Contains("more than 100 levels deep", error.Message, StringComparison.Ordinal);
		Assert.True(allocated < 100_000, $"An unsupported edit allocated {allocated} bytes");
		var supportedPath = path.AsSpan(0, 100).ToArray();
		var supportedValue = SomlValue.Parse(SomlEditor.Set("{}", supportedPath, 1));

		foreach (var segment in supportedPath)
		{
			supportedValue = supportedValue.GetObject()[segment.Key!];
		}

		Assert.Equal(1, supportedValue.GetInt64());
	}

	[Fact]
	public void SpanTimeParsingDoesNotAllocateForValidSlices()
	{
		const string instantText = "!2026-09-19T16:00:00.123456789+02:00!";
		const string durationText = "!1h30m0.123456789s!";
		Assert.True(SomlInstant.TryParse(instantText.AsSpan(1, instantText.Length - 2), null, out var instant));
		Assert.Equal("2026-09-19T14:00:00.123456789Z", instant.ToString());
		Assert.True(SomlDuration.TryParse(durationText.AsSpan(1, durationText.Length - 2), null, out var duration));
		Assert.Equal(5_400_123_456_789, duration.Nanoseconds);

		for (var index = 0; index < 100; index++)
		{
			SomlInstant.TryParse(instantText.AsSpan(1, instantText.Length - 2), null, out _);
			SomlDuration.TryParse(durationText.AsSpan(1, durationText.Length - 2), null, out _);
		}

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var parsed = 0;

		for (var index = 0; index < 100; index++)
		{
			parsed += SomlInstant.TryParse(instantText.AsSpan(1, instantText.Length - 2), null, out _) ? 1 : 0;
			parsed += SomlDuration.TryParse(durationText.AsSpan(1, durationText.Length - 2), null, out _) ? 1 : 0;
		}

		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal(200, parsed);
		Assert.Equal(0, allocated);
	}

	[Fact]
	public void RejectedDurationUnitsDoNotCopyUnboundedInput()
	{
		var text = "1" + new string('h', 1_000_000);
		Assert.False(SomlDuration.TryParse(text.AsSpan(), null, out _));
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		Assert.False(SomlDuration.TryParse(text.AsSpan(), null, out _));
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.True(allocated < 100_000, $"A rejected duration allocated {allocated} bytes");

		var error = Assert.Throws<FormatException>(() => SomlDuration.Parse(text));
		Assert.Contains("“hhhhhhhhhh…” is not a unit", error.Message, StringComparison.Ordinal);
		Assert.True(error.Message.Length < 500);
	}

	[Fact]
	public void CollectionConvertersRejectMissingFactoriesImmediately()
	{
		var error = Assert.Throws<ArgumentNullException>(() => SomlConverters.Collection<List<int>, int>(SomlConverters.Int32, isItemNullable: false, isSet: false, create: null!));
		Assert.Equal("create", error.ParamName);
	}

	[Theory]
	[InlineData("2026-02-29T00:00:00Z", "1h60m")]
	[InlineData("0001-01-01T00:00:00+00:01", "-0s")]
	[InlineData("2026-09-19T00:00:00-00:00", "1ns1s")]
	[InlineData("2026-09-19T00:00:00.1234567890Z", "0.0000000001s")]
	[InlineData("2026-09-19T00:00:00Z ", "1h ")]
	public void TimeSlicesFollowTheirExpectedGrammar(string instant, string duration)
	{
		// 1h60m is valid and canonicalizes to 2h; the other duration cases are invalid.
		var instantSlice = ("!" + instant + "!").AsSpan(1, instant.Length);
		var durationSlice = ("!" + duration + "!").AsSpan(1, duration.Length);
		Assert.False(SomlInstant.TryParse(instant, out var stringInstant));
		Assert.False(SomlInstant.TryParse(instantSlice, null, out var spanInstant));
		Assert.Equal(stringInstant, spanInstant);
		Assert.Equal(duration == "1h60m", SomlDuration.TryParse(duration, out var stringDuration));
		Assert.Equal(duration == "1h60m", SomlDuration.TryParse(durationSlice, null, out var spanDuration));
		Assert.Equal(stringDuration, spanDuration);
	}

	[Fact]
	public void ObjectCopiesKeepValueReferencesAndIndependentMemberStorage()
	{
		var child = new SomlArray { 1, 2 };
		var source = new SomlObject(Enumerable.Range(0, 32).Select(index => new KeyValuePair<string, SomlValue>($"key{index}", child)));
		var copy = new SomlObject(source);
		Assert.Equal(source.Select(member => member.Key), copy.Select(member => member.Key));
		Assert.Same(child, copy["key31"].GetArray());

		copy.Remove("key0");
		source["key31"] = 99;
		copy.Add("other", 100);
		Assert.True(source.ContainsKey("key0"));
		Assert.Same(child, copy["key31"].GetArray());
		Assert.False(source.ContainsKey("other"));
	}

	private sealed class Context : SomlSerializerContext
	{
		public Context() : base(null)
		{
		}
	}

	[Fact]
	public void ArrayConvertersReadIntoFinalStorageAndKeepNestedPaths()
	{
		var converter = SomlConverters.ArrayOf(SomlConverters.Nullable(SomlConverters.Int32), isItemNullable: true);
		var typeInfo = new SomlTypeInfo<int?[]>(new Context(), () => converter);
		Assert.Equal([1, null, 3], SomlSerializer.Deserialize("[1, null, 3]", typeInfo));
		Assert.Equal("[\n\t1\n\tnull\n\t3\n]\n", SomlSerializer.Serialize(new int?[] { 1, null, 3 }, typeInfo));
		Assert.Empty(SomlSerializer.Deserialize("[]", typeInfo));
		Assert.Equal("[1]", Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("[1, 'bad']", typeInfo)).Path);
	}

	[Fact]
	public void CollectionFactoriesNeverReceiveAPartiallyReadList()
	{
		var creations = 0;
		var converter = SomlConverters.Collection<List<int>, int>(SomlConverters.Int32, isItemNullable: false, isSet: false, items =>
		{
			creations++;
			Assert.Equal([1, 2, 3], items);
			return items;
		});
		var typeInfo = new SomlTypeInfo<List<int>>(new Context(), () => converter);
		Assert.Equal("[1]", Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("[1, 'bad', 3]", typeInfo)).Path);
		Assert.Equal(0, creations);
		Assert.Equal([1, 2, 3], SomlSerializer.Deserialize("[1, 2, 3]", typeInfo));
		Assert.Equal(1, creations);
	}

	private sealed class CountedSequence<T>(T[] values) : IReadOnlyCollection<T>
	{
		public int CountReads { get; private set; }
		public int Enumerations { get; private set; }
		public int Count
		{
			get
			{
				CountReads++;
				return values.Length;
			}
		}

		public IEnumerator<T> GetEnumerator()
		{
			Enumerations++;
			return ((IEnumerable<T>)values).GetEnumerator();
		}

		System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
	}

	[Fact]
	public void ReadOnlyOnlySequencesReadTheirCountOnceAndEnumerateOnce()
	{
		var sequence = new CountedSequence<int>([1, 2, 3]);
		var converter = SomlConverters.Collection<CountedSequence<int>, int>(SomlConverters.Int32, isItemNullable: false, isSet: false, items => new([.. items]));
		var typeInfo = new SomlTypeInfo<CountedSequence<int>>(new Context(), () => converter);
		Assert.Equal("[\n\t1\n\t2\n\t3\n]\n", SomlSerializer.Serialize(sequence, typeInfo));
		Assert.Equal(1, sequence.CountReads);
		Assert.Equal(1, sequence.Enumerations);
	}

	[Fact]
	public void ReadOnlyOnlyDictionarySequencesReadTheirCountOnceAndEnumerateOnce()
	{
		var sequence = new CountedSequence<KeyValuePair<string, int>>([new("a", 1), new("b", 2)]);
		var converter = SomlConverters.Dictionary<CountedSequence<KeyValuePair<string, int>>, string, int>(SomlConverters.String, null, SomlConverters.Int32, isValueNullable: false, members => new([.. members]));
		var typeInfo = new SomlTypeInfo<CountedSequence<KeyValuePair<string, int>>>(new Context(), () => converter);
		Assert.Equal("a: 1\nb: 2\n", SomlSerializer.Serialize(sequence, typeInfo));
		Assert.Equal(1, sequence.CountReads);
		Assert.Equal(1, sequence.Enumerations);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void IgnoredConstructorParametersDoNotInvokeTheNamingPolicy(bool generated)
	{
		var policy = new IgnoredParameterPolicy();
		var options = new SomlSerializerOptions { PropertyNamingPolicy = policy, RejectUnknownKeys = true };
		var value = generated
			? SomlSerializer.Deserialize("Value: 42", new ResourceContext(options).IgnoredRecord)
			: SomlSerializer.Deserialize<IgnoredRecord>("Value: 42", options);
		Assert.Equal(new IgnoredRecord(0, 42), value);
		Assert.Equal(1, policy.Calls);
	}

	private sealed class IgnoredParameterPolicy : JsonNamingPolicy
	{
		public int Calls { get; private set; }

		public override string ConvertName(string name)
		{
			Calls++;

			if (name.Equals("HiddenValue", StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("An ignored parameter must not be named");
			}

			return name;
		}
	}

	[Theory]
	[InlineData("int", "9")]
	[InlineData("float", "9")]
	[InlineData("duration", "9")]
	[InlineData("unknown", "x")]
	public void TaggedJsonErrorsBoundUntrustedText(string type, string character)
	{
		var text = new string(character[0], 100_000);
		var scalar = type == "unknown" ? new { type = text, value = "1" } : new { type, value = text };
		using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new { a = scalar }));
		using var output = new MemoryStream();
		using var error = new StringWriter();

		Assert.Equal(1, Program.Run(["encode"], input, output, error));
		Assert.True(error.ToString().Length < 500);
		Assert.Contains(type == "unknown" ? "Unknown tagged scalar type." : "too long to be in canonical form", error.ToString(), StringComparison.Ordinal);
		Assert.Equal(0, output.Length);
	}
}

internal sealed record IgnoredRecord([property: SomlIgnore] int HiddenValue, int Value);

[SomlSerializable(typeof(IgnoredRecord))]
internal partial class ResourceContext : SomlSerializerContext;
