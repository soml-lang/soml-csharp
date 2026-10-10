namespace Soml.Tests;

public sealed class ParsingSecurityRoundTwoTests
{
	[Fact]
	public void IndentedObjectHintsDoNotFormatAnEntireUntrustedKey()
	{
		var key = "a'" + new string('x', 100_000);
		var text = "\"" + key + "\":\n\tchild: 1";
		Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Contains("Indentation does not nest objects", error.Reason, StringComparison.Ordinal);
		Assert.True(error.Message.Length < 1000);
		Assert.True(allocated < 700_000, $"An indentation hint allocated {allocated} bytes");
	}

	[Fact]
	public void InlineBlockDelimiterHintsHaveBoundedSize()
	{
		var delimiter = new string('\'', 100_000);
		var text = "value: " + delimiter + "\ncontent" + delimiter;
		Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Contains("closing delimiter must start a line", error.Reason, StringComparison.Ordinal);
		Assert.True(error.Message.Length < 1000);
		Assert.True(allocated < 100_000, $"A block delimiter hint allocated {allocated} bytes");
	}
}
