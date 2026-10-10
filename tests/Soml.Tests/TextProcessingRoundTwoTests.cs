using System.Globalization;
using System.Text;
using System.Text.Json;
using Soml.Cli;
using Soml.Extensions.Configuration;

namespace Soml.Tests;

[Collection(nameof(ComplexityTests))]
public sealed class TextProcessingRoundTwoTests
{
	[Theory]
	[InlineData("\"", "\"")]
	[InlineData("", "")]
	public void RejectedRawJsonScalarsDoNotCopyTheirEntireContents(string opening, string closing)
	{
		var input = Encoding.UTF8.GetBytes("{\"a\":" + opening + new string('1', 1_000_000) + closing + "}");
		AssertBoundedRejectedJsonAllocation(input);
	}

	[Theory]
	[InlineData("unknown")]
	[InlineData("bool")]
	[InlineData("null")]
	[InlineData("int")]
	[InlineData("float")]
	[InlineData("instant")]
	[InlineData("duration")]
	public void RejectedTagsDoNotDecodeUnneededPayloads(string type)
	{
		var input = Encoding.UTF8.GetBytes("{\"a\":{\"type\":\"" + type + "\",\"value\":\"" + new string('x', 1_000_000) + "\"}}");
		AssertBoundedRejectedJsonAllocation(input);
	}

	[Fact]
	public void RejectedUnknownTagsDoNotDecodeTheirNames()
	{
		var input = Encoding.UTF8.GetBytes("{\"a\":{\"type\":\"" + new string('x', 1_000_000) + "\",\"value\":\"1\"}}");
		AssertBoundedRejectedJsonAllocation(input);
	}

	private static void AssertBoundedRejectedJsonAllocation(byte[] input)
	{
		Assert.Throws<FormatException>(() => TaggedJson.Read(input));
		_ = JsonElement.Parse(input, new() { AllowDuplicateProperties = false });
		var parsingAllocation = long.MaxValue;
		var readingAllocation = long.MaxValue;

		// JSON parsing rents scratch arrays. Compare the lowest warmed allocations so pool growth does not look like diagnostic copying.
		for (var iteration = 0; iteration < 8; iteration++)
		{
			var allocated = GC.GetAllocatedBytesForCurrentThread();
			_ = JsonElement.Parse(input, new() { AllowDuplicateProperties = false });
			parsingAllocation = Math.Min(parsingAllocation, GC.GetAllocatedBytesForCurrentThread() - allocated);
			allocated = GC.GetAllocatedBytesForCurrentThread();
			var error = Assert.Throws<FormatException>(() => TaggedJson.Read(input));
			readingAllocation = Math.Min(readingAllocation, GC.GetAllocatedBytesForCurrentThread() - allocated);
			Assert.True(error.Message.Length < 1000);
		}

		var diagnosticAllocation = readingAllocation - parsingAllocation;
		Assert.True(diagnosticAllocation < 100_000, $"A rejected scalar allocated {diagnosticAllocation} bytes beyond JSON parsing");
	}

	[Fact]
	public void ConfigurationPathsDoNotAllocateEveryIntermediatePrefix()
	{
		const int depth = 60;
		var key = new string('x', 1000);
		var text = string.Concat(Enumerable.Repeat(key + ": {", depth - 1)) + key + ": 7" + new string('}', depth - 1);
		var bytes = Encoding.UTF8.GetBytes(text);
		using var input = new MemoryStream(bytes);
		var provider = new SomlStreamConfigurationProvider(new SomlStreamConfigurationSource());
		provider.Load(input);
		input.Position = 0;
		_ = SomlValue.Parse(bytes);
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		_ = SomlValue.Parse(bytes);
		var parsingAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
		allocated = GC.GetAllocatedBytesForCurrentThread();
		provider.Load(input);
		var flatteningAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated - parsingAllocation;

		Assert.True(provider.TryGet(string.Join(':', Enumerable.Repeat(key, depth)), out var value));
		Assert.Equal("7", value);
		Assert.True(flatteningAllocation < 600_000, $"Flattening allocated {flatteningAllocation} bytes beyond parsing");
	}

	[Fact]
	public void ConfigurationPathsRestoreEmptySegmentsAndSiblingPrefixes()
	{
		using var input = new MemoryStream(Encoding.UTF8.GetBytes("'': {'': [1, {'': 2}], sibling: 3}\nother: {'': 4}\nlast: 5"));
		var provider = new SomlStreamConfigurationProvider(new SomlStreamConfigurationSource());
		provider.Load(input);

		foreach (var (key, expected) in new[] { ("::0", "1"), ("::1:", "2"), (":sibling", "3"), ("other:", "4"), ("last", "5") })
		{
			Assert.True(provider.TryGet(key, out var actual));
			Assert.Equal(expected, actual);
		}
	}

	[Fact]
	public void EditorDoesNotCopyIndentationForAnUnchangedValue()
	{
		var text = new string('\t', 1_000_000) + "value: 1\n";
		SomlEditor.Set(text, ["value"], 1);
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var result = SomlEditor.Set(text, ["value"], 1);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Same(text, result);
		Assert.True(allocated < 100_000, $"Editing an unchanged value allocated {allocated} bytes");
	}

	[Theory]
	[InlineData("int", "-9223372036854775808")]
	[InlineData("float", "-1.7976931348623157e308")]
	[InlineData("instant", "9999-12-31T23:59:59.999999999Z")]
	[InlineData("duration", "9223372036854775807")]
	public void ScalarTagsAcceptEscapedJsonStrings(string type, string value)
	{
		var escapedType = string.Concat(type.Select(character => $"\\u{(int)character:x4}"));
		var escapedValue = string.Concat(value.Select(character => $"\\u{(int)character:x4}"));
		var ordinary = Encoding.UTF8.GetBytes($"{{\"a\":{{\"type\":\"{type}\",\"value\":\"{value}\"}}}}");
		var escaped = Encoding.UTF8.GetBytes($"{{\"a\":{{\"type\":\"{escapedType}\",\"value\":\"{escapedValue}\"}}}}");

		Assert.Equal(TaggedJson.Read(ordinary)["a"], TaggedJson.Read(escaped)["a"]);
	}

	[Fact]
	public void InstantCalendarConversionAgreesWithGregorianDatesAcrossTheSupportedRange()
	{
		for (var day = 0; day <= DateOnly.MaxValue.DayNumber; day += 97)
		{
			var date = DateOnly.FromDayNumber(day);
			var text = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T12:34:56.123456789Z";
			var seconds = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 34, 56)), TimeSpan.Zero).ToUnixTimeSeconds();
			var instant = new SomlInstant(seconds, 123_456_789);

			Assert.Equal(text, instant.ToString());
			Assert.Equal(instant, SomlInstant.Parse(text));
		}
	}

	[Fact]
	public void InstantArithmeticAgreesWithWideIntegerNanoseconds()
	{
		var random = new Random(47391);
		var minimum = (Int128)SomlInstant.MinValue.UnixSeconds * 1_000_000_000;
		var maximum = ((Int128)SomlInstant.MaxValue.UnixSeconds * 1_000_000_000) + 999_999_999;

		foreach (var instant in new[] { SomlInstant.MinValue, SomlInstant.MaxValue, SomlInstant.UnixEpoch, new SomlInstant(-1, 999_999_999), new SomlInstant(0, 1) })
		{
			foreach (var duration in new[] { SomlDuration.MinValue, SomlDuration.MaxValue, new SomlDuration(-999_999_999), new SomlDuration(999_999_999), SomlDuration.Zero })
			{
				Check(instant, duration);
			}
		}

		for (var iteration = 0; iteration < 5000; iteration++)
		{
			var instant = new SomlInstant(random.NextInt64(SomlInstant.MinValue.UnixSeconds, SomlInstant.MaxValue.UnixSeconds), random.Next(1_000_000_000));
			Check(instant, new SomlDuration(random.NextInt64(long.MinValue, long.MaxValue)));
		}

		void Check(SomlInstant instant, SomlDuration duration)
		{
			var total = ((Int128)instant.UnixSeconds * 1_000_000_000) + instant.Nanosecond;

			foreach (var subtract in new[] { false, true })
			{
				var expected = subtract ? total - duration.Nanoseconds : total + duration.Nanoseconds;

				if (expected < minimum || expected > maximum)
				{
					Assert.Throws<OverflowException>(() => subtract ? instant - duration : instant + duration);
					continue;
				}

				var actual = subtract ? instant - duration : instant + duration;
				Assert.Equal(expected, ((Int128)actual.UnixSeconds * 1_000_000_000) + actual.Nanosecond);
				Assert.Equal(duration, subtract ? instant - actual : actual - instant);
			}
		}
	}

	[Theory]
	[InlineData(1, "000000001")]
	[InlineData(10, "00000001")]
	[InlineData(100, "0000001")]
	[InlineData(1000, "000001")]
	[InlineData(10_000, "00001")]
	[InlineData(100_000, "0001")]
	[InlineData(1_000_000, "001")]
	[InlineData(10_000_000, "01")]
	[InlineData(100_000_000, "1")]
	public void FractionFormattingKeepsLeadingZerosAtEveryPrecision(int nanoseconds, string fraction)
	{
		Assert.Equal($"0.{fraction}s", new SomlDuration(nanoseconds).ToString());
		Assert.Equal($"1970-01-01T00:00:00.{fraction}Z", new SomlInstant(0, nanoseconds).ToString());
	}

	[Theory]
	[InlineData(true)]
	[InlineData(false)]
	public void RejectedTimeSpansDoNotCopyTheEntireInput(bool isInstant)
	{
		var text = new string('x', 1_000_000);
		Reject();
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Reject();
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Equal($"The text is not a valid SOML {(isInstant ? "instant" : "duration")}", error.Message);
		Assert.True(allocated < 100_000, $"Rejecting a time span allocated {allocated} bytes");

		FormatException Reject() => Assert.Throws<FormatException>(() =>
		{
			if (isInstant)
			{
				SomlInstant.Parse(text.AsSpan(), null);
			}
			else
			{
				SomlDuration.Parse(text.AsSpan(), null);
			}
		});
	}
}
