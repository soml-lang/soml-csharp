using System.Globalization;

namespace Soml.Tests;

public sealed class BuiltInParsingTests
{
	[Fact]
	public void AsciiHexChecksPreserveEscapeValidationOrder()
	{
		foreach (var codePoint in new[] { 0, 9, 10, 31, 0x7F, 0xAB, 0xFFFF, 0x1F600, 0x10FFFF })
		{
			var digits = codePoint.ToString("x", CultureInfo.InvariantCulture);
			var source = $"[\"\\u{{{digits}}}\"]";
			Assert.Equal(char.ConvertFromUtf32(codePoint), SomlValue.Parse(source)[0].GetString());
			Assert.Equal(source + "\n", SomlFormatter.Format(source));
		}

		foreach (var digits in new[] { "/", ":", "@", "G", "`", "g", "0g", "１", "ａ", "\uffff", "" })
		{
			Assert.Throws<SomlParseException>(() => SomlValue.Parse($"[\"\\u{{{digits}}}\"]"));
		}

		Assert.Contains("lowercase", Assert.Throws<SomlParseException>(() => SomlValue.Parse("[\"\\u{AB}\"]")).Reason, StringComparison.Ordinal);
		Assert.Contains("surrogate", Assert.Throws<SomlParseException>(() => SomlValue.Parse("[\"\\u{D800}\"]")).Reason, StringComparison.Ordinal);
		Assert.Throws<SomlParseException>(() => SomlValue.Parse("[\"\\u{"));
	}

	[Fact]
	public void InstantDigitScansRejectNonAsciiDigitsAndTruncatedInput()
	{
		const string text = "2024-02-29T12:34:56.123456789+23:59";
		Assert.True(SomlInstant.TryParse(text.AsSpan(), null, out _));

		for (var index = 0; index < text.Length; index++)
		{
			Assert.False(SomlInstant.TryParse(text.AsSpan(0, index), null, out _));

			if (char.IsAsciiDigit(text[index]))
			{
				var characters = text.ToCharArray();
				characters[index] = '１';
				Assert.False(SomlInstant.TryParse(characters.AsSpan(), null, out _));
			}
		}
	}

	[Theory]
	[InlineData("0001-01-01T00:00:00Z", -62_135_596_800L)]
	[InlineData("1970-01-01T00:00:00Z", 0L)]
	[InlineData("2000-02-29T00:00:00Z", 951_782_400L)]
	[InlineData("9999-12-31T23:59:59Z", 253_402_300_799L)]
	public void GregorianDayNumbersPreserveUnixSeconds(string text, long seconds)
	{
		Assert.Equal(seconds, SomlInstant.Parse(text).UnixSeconds);
		Assert.Equal(seconds, SomlValue.Parse($"[{text}]")[0].GetInstant().UnixSeconds);
	}
}
