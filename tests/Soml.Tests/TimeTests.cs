namespace Soml.Tests;

public sealed class TimeTests
{
	[Theory]
	[InlineData("90m", "1h30m")]
	[InlineData("1500ms", "1.5s")]
	[InlineData("250us", "0.00025s")]
	[InlineData("0s", "0s")]
	[InlineData("1.5h", "1h30m")]
	[InlineData("0.0000000001h", "0.00000036s")]
	[InlineData("-5m", "-5m")]
	[InlineData("1h2m3s4ms5us6ns", "1h2m3.004005006s")]
	[InlineData("-9223372036854775808ns", "-2562047h47m16.854775808s")]
	[InlineData("9223372036854775807ns", "2562047h47m16.854775807s")]
	public void WritesDurationsInCanonicalForm(string text, string canonical)
	{
		var duration = SomlDuration.Parse(text);

		Assert.Equal(canonical, duration.ToString());
		Assert.Equal(duration, SomlDuration.Parse(canonical));
	}

	[Fact]
	public void RejectsInvalidDurations()
	{
		var error = Assert.Throws<FormatException>(() => SomlDuration.Parse("1.5h30m"));

		Assert.Equal("Invalid duration 1.5h30m: only the last part may have a fraction", error.Message);
		Assert.False(SomlDuration.TryParse("1d", out _));
		Assert.False(SomlDuration.TryParse("30", out _));
		Assert.False(SomlDuration.TryParse(" 30s", out _));
		Assert.False(SomlDuration.TryParse("30s ", out _));
	}

	[Fact]
	public void ConvertsDurationsToAndFromTimeSpan()
	{
		Assert.Equal(TimeSpan.FromMinutes(90), SomlDuration.Parse("1h30m").ToTimeSpan());
		Assert.Equal(SomlDuration.Parse("1.5s"), SomlDuration.FromTimeSpan(TimeSpan.FromSeconds(1.5)));
		Assert.Equal(SomlDuration.Parse("100ns"), (SomlDuration)TimeSpan.FromTicks(1));
		Assert.Throws<InvalidOperationException>(() => SomlDuration.Parse("1ns").ToTimeSpan());
		Assert.Throws<OverflowException>(() => SomlDuration.FromTimeSpan(TimeSpan.MaxValue));
	}

	[Fact]
	public void DoesDurationArithmetic()
	{
		Assert.Equal(SomlDuration.Parse("1h30m"), SomlDuration.Parse("1h") + SomlDuration.Parse("30m"));
		Assert.Equal(SomlDuration.Parse("-30m"), -SomlDuration.Parse("30m"));
		Assert.True(SomlDuration.Parse("1s") > SomlDuration.Parse("999ms"));
		Assert.Throws<OverflowException>(() => SomlDuration.MaxValue + SomlDuration.Parse("1ns"));
		Assert.Throws<OverflowException>(() => -SomlDuration.MinValue);
	}

	[Theory]
	[InlineData("2026-09-19T14:00:00Z", "2026-09-19T14:00:00Z")]
	[InlineData("2026-09-19T16:00:00+02:00", "2026-09-19T14:00:00Z")]
	[InlineData("2026-09-19T14:00:00.500Z", "2026-09-19T14:00:00.5Z")]
	[InlineData("2026-09-19T14:00:00.123456789-23:59", "2026-09-20T13:59:00.123456789Z")]
	[InlineData("0001-01-01T00:00:00Z", "0001-01-01T00:00:00Z")]
	[InlineData("9999-12-31T23:59:59.999999999Z", "9999-12-31T23:59:59.999999999Z")]
	[InlineData("1969-12-31T23:59:59.5Z", "1969-12-31T23:59:59.5Z")]
	[InlineData("2024-02-29T00:00:00Z", "2024-02-29T00:00:00Z")]
	public void WritesInstantsInCanonicalForm(string text, string canonical)
	{
		var instant = SomlInstant.Parse(text);

		Assert.Equal(canonical, instant.ToString());
		Assert.Equal(instant, SomlInstant.Parse(canonical));
	}

	[Fact]
	public void RejectsInvalidInstants()
	{
		var error = Assert.Throws<FormatException>(() => SomlInstant.Parse("0001-01-01T00:00:00+00:01"));

		Assert.Equal("Invalid instant 0001-01-01T00:00:00+00:01: in UTC it falls outside the years 0001 to 9999", error.Message);
		Assert.False(SomlInstant.TryParse("2026-02-29T00:00:00Z", out _));
		Assert.False(SomlInstant.TryParse("2026-09-19", out _));
		Assert.False(SomlInstant.TryParse("2026-09-19T14:00:00", out _));
	}

	[Fact]
	public void ConvertsInstantsToAndFromTheBclTypes()
	{
		var dateTimeOffset = new DateTimeOffset(2026, 9, 19, 16, 0, 0, TimeSpan.FromHours(2));
		var instant = SomlInstant.FromDateTimeOffset(dateTimeOffset);

		Assert.Equal("2026-09-19T14:00:00Z", instant.ToString());
		Assert.Equal(dateTimeOffset, instant.ToDateTimeOffset());
		Assert.Equal(TimeSpan.Zero, instant.ToDateTimeOffset().Offset);
		Assert.Equal(DateTimeKind.Utc, instant.ToDateTime().Kind);
		Assert.Equal(SomlInstant.MinValue, SomlInstant.FromDateTimeOffset(DateTimeOffset.MinValue));
		Assert.Throws<ArgumentException>(() => SomlInstant.FromDateTime(new DateTime(2026, 9, 19)));
		Assert.Throws<InvalidOperationException>(() => SomlInstant.Parse("2026-09-19T14:00:00.000000001Z").ToDateTimeOffset());
		Assert.Throws<ArgumentOutOfRangeException>(() => new SomlInstant(253_402_300_800, 0));
	}

	[Fact]
	public void OrdersInstants()
	{
		Assert.True(SomlInstant.Parse("2026-09-19T14:00:00Z") < SomlInstant.Parse("2026-09-19T14:00:00.000000001Z"));
		Assert.True(SomlInstant.Parse("1969-12-31T23:59:59.999999999Z") < SomlInstant.UnixEpoch);
		Assert.Equal(-1, SomlInstant.Parse("1969-12-31T23:59:59.5Z").UnixSeconds);
		Assert.Equal(500_000_000, SomlInstant.Parse("1969-12-31T23:59:59.5Z").Nanosecond);
	}

	[Theory]
	[InlineData("0001-01-01T00:00:00Z")]
	[InlineData("1969-12-31T23:59:59.000000001Z")]
	[InlineData("2026-09-19T14:00:00.5Z")]
	[InlineData("9999-12-31T23:59:59.999999999Z")]
	public void FormatsInstantsIntoSpansOfEverySize(string text)
	{
		AssertSpanFormatting(SomlInstant.Parse(text), text);
	}

	[Theory]
	[InlineData("0s")]
	[InlineData("-0.000000001s")]
	[InlineData("1h2m3.004005006s")]
	[InlineData("-2562047h47m16.854775808s")]
	public void FormatsDurationsIntoSpansOfEverySize(string text)
	{
		AssertSpanFormatting(SomlDuration.Parse(text), text);
	}

	[Fact]
	public void FormatsEveryFractionWidthAcrossTheFirstCenturyWithAnInvariantCalendar()
	{
		var previousCulture = System.Globalization.CultureInfo.CurrentCulture;

		try
		{
			System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-SA");
			(int Nanoseconds, string Fraction)[] fractions = [(1, "000000001"), (10, "00000001"), (100, "0000001"), (1000, "000001"), (10000, "00001"), (100000, "0001"), (1000000, "001"), (10000000, "01"), (100000000, "1")];

			foreach (var prefix in new[] { "0099-12-31T23:59:59", "0100-01-01T00:00:00" })
			{
				var whole = SomlInstant.Parse(prefix + "Z");

				foreach (var (nanoseconds, fraction) in fractions)
				{
					AssertSpanFormatting(new SomlInstant(whole.UnixSeconds, nanoseconds), $"{prefix}.{fraction}Z");
				}
			}
		}
		finally
		{
			System.Globalization.CultureInfo.CurrentCulture = previousCulture;
		}
	}

	private static void AssertSpanFormatting(ISpanFormattable value, string text)
	{
		var provider = new System.Globalization.NumberFormatInfo { NumberDecimalSeparator = ",", NegativeSign = "~" };

		for (var size = 0; size <= text.Length + 1; size++)
		{
			var buffer = new char[size];
			var success = value.TryFormat(buffer, out var written, "ignored", provider);

			Assert.Equal(size >= text.Length, success);
			Assert.Equal(success ? text.Length : 0, written);

			if (success)
			{
				Assert.Equal(text, new string(buffer, 0, written));
			}
		}
	}

	[Fact]
	public void BorrowsAndCarriesNanosecondsAcrossTheUnixEpoch()
	{
		var nanosecond = new SomlDuration(1);
		var before = SomlInstant.Parse("1969-12-31T23:59:59.999999999Z");
		var after = SomlInstant.Parse("1970-01-01T00:00:00.000000001Z");

		Assert.Equal(SomlInstant.UnixEpoch, before + nanosecond);
		Assert.Equal(before, SomlInstant.UnixEpoch - nanosecond);
		Assert.Equal(after, SomlInstant.UnixEpoch + nanosecond);
		Assert.Equal(new SomlDuration(2), after - before);
		Assert.Equal(new SomlDuration(-2), before - after);
		Assert.Equal(before, after - new SomlDuration(2));
	}

	[Fact]
	public void RejectsArithmeticOutsideTheInstantAndDurationRanges()
	{
		var nanosecond = new SomlDuration(1);

		Assert.Throws<OverflowException>(() => SomlInstant.MinValue - nanosecond);
		Assert.Throws<OverflowException>(() => SomlInstant.MaxValue + nanosecond);
		Assert.Throws<OverflowException>(() => SomlDuration.MinValue - nanosecond);
		Assert.Throws<OverflowException>(() => SomlDuration.MaxValue + nanosecond);
		Assert.Throws<OverflowException>(() => SomlInstant.MinValue - SomlInstant.MaxValue);
	}

	[Fact]
	public void ConvertsTheLargestWholeTicksWithinTheDurationRange()
	{
		var maximum = new SomlDuration(long.MaxValue - (long.MaxValue % 100));
		var minimum = new SomlDuration(long.MinValue - (long.MinValue % 100));

		Assert.Equal(maximum, SomlDuration.FromTimeSpan(maximum.ToTimeSpan()));
		Assert.Equal(minimum, SomlDuration.FromTimeSpan(minimum.ToTimeSpan()));
		Assert.Throws<OverflowException>(() => SomlDuration.FromTimeSpan(maximum.ToTimeSpan() + TimeSpan.FromTicks(1)));
		Assert.Throws<OverflowException>(() => SomlDuration.FromTimeSpan(minimum.ToTimeSpan() - TimeSpan.FromTicks(1)));
	}
}
