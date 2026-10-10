using System.Globalization;
using Soml.Serialization;

namespace Soml.Tests;

[SomlSerializable(typeof(IEnumerable<DateOnly>), TypeInfoPropertyName = "Dates")]
internal partial class BuiltInConversionContext : SomlSerializerContext;

public sealed class BuiltInConversionTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void DateOnlyCalendarBoundariesKeepNativeDates(bool generated)
	{
		var previousCulture = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
			DateOnly[] dates = [DateOnly.MinValue, new(99, 12, 31), new(100, 1, 1), new(2000, 2, 29), DateOnly.MaxValue];
			const string text = "[\n\t0001-01-01\n\t0099-12-31\n\t0100-01-01\n\t2000-02-29\n\t9999-12-31\n]\n";
			Assert.Equal(text, generated ? SomlSerializer.Serialize(dates, BuiltInConversionContext.Default.Dates) : SomlSerializer.Serialize<IEnumerable<DateOnly>>(dates));
			Assert.Equal(dates, generated ? SomlSerializer.Deserialize(text, BuiltInConversionContext.Default.Dates) : SomlSerializer.Deserialize<IEnumerable<DateOnly>>(text));
		}
		finally
		{
			CultureInfo.CurrentCulture = previousCulture;
		}
	}

	[Theory]
	[InlineData("0000-01-01")]
	[InlineData("10000-01-01")]
	[InlineData("2024-2-01")]
	[InlineData("2024-02-1")]
	[InlineData(" 2024-02-01")]
	[InlineData("2024-02-01 ")]
	[InlineData("2023-02-29")]
	[InlineData("2024/02/01")]
	[InlineData("٢٠٢٤-٠٢-٠١")]
	[InlineData("2024-02-01T00:00:00")]
	public void DateOnlyStringsKeepTheirStrictGrammarAndNestedErrors(string text)
	{
		SomlValue value = new SomlArray { text };
		var reflection = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<IEnumerable<DateOnly>>(value));
		var generated = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize(value, BuiltInConversionContext.Default.Dates));

		Assert.Equal("[0]", generated.Path);
		Assert.Null(generated.InnerException);
		Assert.Contains("Expected a date", generated.Message, StringComparison.Ordinal);
		Assert.Equal(reflection.Message, generated.Message);
	}
}
