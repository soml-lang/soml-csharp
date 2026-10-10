using System.Globalization;
using System.Text;
using Microsoft.Extensions.Configuration;
using Soml.Cli;

namespace Soml.Tests;

public sealed class CalendarDateTests
{
	[Theory]
	[InlineData("0001-01-01", 1, 1, 1)]
	[InlineData("9999-12-31", 9999, 12, 31)]
	[InlineData("1582-10-10", 1582, 10, 10)]
	[InlineData("2011-12-30", 2011, 12, 30)]
	[InlineData("2000-02-29", 2000, 2, 29)]
	public void PreservesNativeCalendarComponents(string text, int year, int month, int day)
	{
		var date = new DateOnly(year, month, day);
		SomlValue value = date;
		Assert.Equal(SomlValueKind.Date, value.Kind);
		Assert.Equal(date, value.GetDate());
		Assert.True(value.TryGetDate(out var actual));
		Assert.Equal(date, actual);
		Assert.Equal(text, value.ToString());
		Assert.Equal(value, SomlValue.Parse($"day: {text}")["day"]);
		Assert.Equal(value, SomlValue.Parse(Encoding.UTF8.GetBytes($"day: {text}"))["day"]);
		Assert.Equal($"day: {text}\n", new SomlObject { ["day"] = value }.ToDocumentString(canonical: true));
		Assert.NotEqual<SomlValue>(text, value);
		Assert.NotEqual<SomlValue>(SomlInstant.Parse($"{text}T00:00:00Z"), value);
		Assert.Equal(date, (DateOnly)value);
	}

	[Theory]
	[InlineData("1900-02-29")]
	[InlineData("2026-02-30")]
	[InlineData("0000-01-01")]
	[InlineData("10000-01-01")]
	[InlineData("2026-1-01")]
	[InlineData("2026-11-01T")]
	[InlineData("2026-11-01Z")]
	[InlineData("2026-11-01foo")]
	[InlineData("2026-11-01T12:00:00")]
	[InlineData("２０２６-11-01")]
	public void RejectsWholeInvalidTokens(string text)
	{
		Assert.Throws<SomlParseException>(() => SomlValue.Parse($"day: {text}"));
		Assert.Throws<SomlParseException>(() => SomlFormatter.Format($"day: {text}"));
	}

	[Fact]
	public void KeepsTypedDatesSeparateFromStringsAndInstants()
	{
		DateOnly[] dates = [new(1582, 10, 10), DateOnly.MinValue, DateOnly.MaxValue];
		const string text = "[\n\t1582-10-10\n\t0001-01-01\n\t9999-12-31\n]\n";
		Assert.Equal(text, SomlSerializer.Serialize(dates));
		Assert.Equal(dates, SomlSerializer.Deserialize<DateOnly[]>(text));
		Assert.Equal(text, SomlSerializer.Serialize<IEnumerable<DateOnly>>(dates, BuiltInConversionContext.Default.Dates));
		Assert.Equal(dates, SomlSerializer.Deserialize(text, BuiltInConversionContext.Default.Dates));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<DateOnly[]>("['2026-11-01']"));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<string[]>("[2026-11-01]"));
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<SomlInstant[]>("[2026-11-01]"));
		var untyped = SomlSerializer.Deserialize<Dictionary<string, object>>("day: 1582-10-10");
		Assert.Equal(dates[0], Assert.IsType<DateOnly>(untyped["day"]));
	}

	[Fact]
	public void PreservesDatesThroughCommentsEditingTaggedJsonAndConfiguration()
	{
		const string source = "day: 1582-10-10#Calendar\ndates:[2011-12-30,2000-02-29]";
		const string formatted = "day: 1582-10-10 #Calendar\ndates: [2011-12-30, 2000-02-29]\n";
		Assert.Equal(formatted, SomlFormatter.Format(source));
		Assert.Equal("day: 2027-09-19 #Calendar\ndates: [2011-12-30, 2000-02-29]\n", SomlEditor.Set(formatted, ["day"], new DateOnly(2027, 9, 19)));
		using var stream = new MemoryStream();
		TaggedJson.Write(SomlValue.Parse(source), stream);
		Assert.Contains("\"type\": \"date\"", Encoding.UTF8.GetString(stream.ToArray()), StringComparison.Ordinal);
		Assert.Equal(SomlValue.Parse(source), TaggedJson.Read(stream.ToArray()));
		using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
		var config = new ConfigurationBuilder().AddSomlStream(input).Build();
		Assert.Equal("1582-10-10", config["day"]);
	}
	[Fact]
	public void ValidatesEveryDaySlotAcrossTheGregorianCycle()
	{
		for (var year = 1; year <= 400; year++)
		{
			for (var month = 1; month <= 12; month++)
			{
				var lastDay = System.DateTime.DaysInMonth(year, month);
				for (var day = 0; day <= 32; day++)
				{
					var text = string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{month:D2}-{day:D2}");
					if (day >= 1 && day <= lastDay)
					{
						var value = SomlValue.Parse($"[{text}]")[0];
						Assert.Equal(new DateOnly(year, month, day), value.GetDate());
						Assert.Equal(text, value.ToString());
					}
					else
					{
						Assert.Throws<SomlParseException>(() => SomlValue.Parse($"[{text}]"));
					}
				}
			}
		}
	}

	[Fact]
	public void UsesInvariantAsciiWithADifferentDefaultCalendar()
	{
		var previous = CultureInfo.CurrentCulture;
		try
		{
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
			var date = new DateOnly(1582, 10, 10);
			SomlValue value = date;
			Assert.Equal("1582-10-10", value.ToString());
			Assert.Equal("[\n\t1582-10-10\n]\n", SomlSerializer.Serialize(new[] { date }));
			var dictionary = new Dictionary<DateOnly, int> { [date] = 1 };
			Assert.Equal("1582-10-10: 1\n", SomlSerializer.Serialize(dictionary));
			Assert.Equal(1, SomlSerializer.Deserialize<Dictionary<DateOnly, int>>("1582-10-10: 1")[date]);
		}
		finally
		{
			CultureInfo.CurrentCulture = previous;
		}
	}

	[Theory]
	[InlineData("2026-1-01")]
	[InlineData("1900-02-29")]
	[InlineData("2026-11-01T00:00:00Z")]
	[InlineData("2026-11-01foo")]
	[InlineData("２０２６-11-01")]
	public void RejectsInvalidTaggedDateText(string text)
	{
		var json = System.Text.Json.JsonSerializer.Serialize(new[] { new { type = "date", value = text } });
		Assert.Throws<FormatException>(() => TaggedJson.Read(Encoding.UTF8.GetBytes(json)));
	}

	[Fact]
	public void BoundsUntrustedSuffixDiagnosticsAndPreservesEncodingErrorPriority()
	{
		var source = "day: 2026-11-01" + new string('x', 100_000);
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(source));
		Assert.True(error.Reason.Length < 250);
		Assert.Equal(6, error.Column);
		var control = Assert.Throws<SomlParseException>(() => SomlValue.Parse(source + "\0"));
		Assert.Contains("U+0000", control.Reason, StringComparison.Ordinal);
	}

}
