using System.Globalization;
using System.Text;

namespace Soml.Tests;

public sealed class ParserAndWriterPerformanceTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ExposedMemorySlicesConsumeOnlyTheirRemainingDocument(bool asynchronously)
	{
		var bytes = Encoding.UTF8.GetBytes("ignored!a: '😀'\n!ignored");
		using var stream = new MemoryStream(bytes, 8, bytes.Length - 16, writable: false, publiclyVisible: true);
		stream.Position = 0;

		var parsed = asynchronously ? await SomlValue.ParseAsync(stream, TestContext.Current.CancellationToken) : SomlValue.Parse(stream);

		Assert.Equal("😀", parsed["a"].GetString());
		Assert.Equal(stream.Length, stream.Position);
		Assert.True(stream.CanRead);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ExposedMemoryStreamsPreservePositionsPastTheirEnd(bool asynchronously)
	{
		using var stream = new MemoryStream();
		stream.Write("a: 1"u8);
		stream.Position = 100;

		if (asynchronously)
		{
			await Assert.ThrowsAsync<SomlParseException>(async () => await SomlValue.ParseAsync(stream, TestContext.Current.CancellationToken));
		}
		else
		{
			Assert.Throws<SomlParseException>(() => SomlValue.Parse(stream));
		}

		Assert.Equal(100, stream.Position);
	}

	[Fact]
	public async Task CanceledExposedMemoryStreamsAreNotConsumed()
	{
		using var stream = new MemoryStream();
		stream.Write("a: 1"u8);
		stream.Position = 0;
		using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
		cancellation.Cancel();

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await SomlValue.ParseAsync(stream, cancellation.Token));
		Assert.Equal(0, stream.Position);
	}

	[Fact]
	public void LargeSeparatedFloatParsingReusesTemporaryStorage()
	{
		var text = "[0." + string.Join('_', Enumerable.Repeat("0", 10_000)) + "_1e10000]";
		Assert.Equal(0.1, SomlValue.Parse(text)[0].GetDouble());
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var parsed = SomlValue.Parse(text);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Equal(0.1, parsed[0].GetDouble());
		Assert.True(allocated < 4096, $"Parsing a separated float allocated {allocated} bytes");
	}

	[Fact]
	public void DecimalReadingDoesNotAllocateIntermediateFloatSpellings()
	{
		SomlValue[] values = [0.1, -0.25, 1e-28, 7.5e20];
		decimal[] expected = [0.1m, -0.25m, 0.0000000000000000000000000001m, 750000000000000000000m];

		for (var iteration = 0; iteration < 100; iteration++)
		{
			foreach (var value in values)
			{
				_ = (decimal)value;
			}
		}

		Span<decimal> actual = stackalloc decimal[values.Length];
		var allocated = GC.GetAllocatedBytesForCurrentThread();

		for (var index = 0; index < values.Length; index++)
		{
			actual[index] = (decimal)values[index];
		}

		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal(0, allocated);
		Assert.Equal(expected, actual.ToArray());
	}

	[Fact]
	public void GeneralIntegerRadicesAgreeAtSignedRangeBoundaries()
	{
		var random = new Random(528);

		foreach (var value in (long[])[0, 1, long.MaxValue, long.MinValue, .. Enumerable.Range(0, 100).Select(_ => random.NextInt64(long.MinValue, long.MaxValue))])
		{
			var decimalText = value.ToString(CultureInfo.InvariantCulture);
			var separated = string.Join('_', decimalText.TrimStart('-').Select(character => character.ToString()));
			Assert.Equal(value, SomlValue.Parse($"[{(value < 0 ? "-" : "")}{separated}]")[0].GetInt64());

			if (value >= 0)
			{
				foreach (var radix in new[] { 2, 8, 16 })
				{
					var digits = Convert.ToString(value, radix).ToUpperInvariant();
					var prefix = radix switch { 2 => "0b", 8 => "0o", _ => "0x" };
					Assert.Equal(value, SomlValue.Parse($"[{prefix}{string.Join('_', digits.Select(character => character.ToString()))}]")[0].GetInt64());
				}
			}
		}
	}

	[Theory]
	[InlineData("0x8000_0000_0000_0000")]
	[InlineData("0o1000_0000_0000_0000_0000_00")]
	[InlineData("0b1_000000000000000000000000000000000000000000000000000000000000000")]
	[InlineData("9_223_372_036_854_775_808")]
	[InlineData("-9_223_372_036_854_775_809")]
	public void GeneralIntegersRejectOverflowBeforeUnsignedArithmeticWraps(string text)
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse($"[{text}]"));
		Assert.Contains("outside the 64-bit range", error.Reason, StringComparison.Ordinal);
		Assert.Equal(1, error.Offset);
	}

	[Theory]
	[InlineData("1.2345678901234567e-100")]
	[InlineData("-1.2345678901234567e100")]
	[InlineData("100000000000000020000.0")]
	[InlineData("-0.0000012345678901234567")]
	public void DirectFloatDisplayAndDocumentWritingUseTheSameCanonicalDigits(string text)
	{
		var value = SomlValue.Parse($"[{text}]")[0];
		Assert.Equal(text, value.ToString());
		Assert.Equal($"[\n\t{text}\n]\n", new SomlArray { value }.ToDocumentString());
		Assert.Equal(value, SomlValue.Parse($"[{value}]")[0]);
	}

	[Theory]
	[InlineData("name: ", "word", "")]
	[InlineData("value: 1 ", "s", "")]
	[InlineData("value: 2026-09-19 14:00:00", "0", "Z")]
	[InlineData("value: 0x", "0", "a")]
	[InlineData("value: 0", "1", "")]
	[InlineData("value: 1.", "0", "µs")]
	public void InvalidValueDiagnosticsDoNotCopyUnboundedTokens(string prefix, string repeated, string suffix)
	{
		var text = prefix + new string(repeated[0], 1_000_000) + suffix;
		Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.True(error.Message.Length < 2000);
		Assert.True(allocated < 100_000, $"A bounded parse diagnostic allocated {allocated} bytes");
	}

	[Fact]
	public void ThousandsSeparatorDiagnosticsDoNotCopyAllPrecedingArrayItems()
	{
		var groups = string.Join(',', Enumerable.Repeat("111", 32_768));
		var valid = $"[{groups}]";
		var invalid = $"[{groups},000]";
		_ = SomlValue.Parse(valid);
		Assert.Throws<SomlParseException>(() => SomlValue.Parse(invalid));

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		_ = SomlValue.Parse(valid);
		var validAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated;
		allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(invalid));
		var diagnosticAllocation = GC.GetAllocatedBytesForCurrentThread() - allocated - validAllocation;

		Assert.Contains("A comma separates items", error.Reason, StringComparison.Ordinal);
		Assert.True(diagnosticAllocation < 100_000, $"A bounded parse diagnostic allocated {diagnosticAllocation} bytes beyond its preceding valid array items");
	}

	[Fact]
	public void RootTypeErrorsLocateTheCollectionWithoutBuildingItsSyntaxTree()
	{
		var text = "# leading\n\n\t[" + string.Join(',', Enumerable.Repeat("1", 10_000)) + "]";
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<int>(text));

		var parsingAllocated = GC.GetAllocatedBytesForCurrentThread();
		_ = SomlValue.Parse(text);
		parsingAllocated = GC.GetAllocatedBytesForCurrentThread() - parsingAllocated;

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<int>(text));
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Null(error.Path);
		Assert.Contains("at line 3, column 2", error.Message, StringComparison.Ordinal);
		Assert.True(allocated - parsingAllocated < 100_000, $"Locating the collection allocated {allocated - parsingAllocated} additional bytes");
	}
}
