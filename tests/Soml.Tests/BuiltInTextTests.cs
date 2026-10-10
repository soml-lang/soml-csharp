using System.Globalization;
using System.Text;

namespace Soml.Tests;

public sealed class BuiltInTextTests
{
	[Theory]
	[InlineData(-1)]
	[InlineData(1)]
	public void Utf8DurationFractionsPreserveSignsAndDestinationSlicesAcrossCultures(int sign)
	{
		var previousCulture = CultureInfo.CurrentCulture;

		try
		{
			CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
			(int Nanoseconds, string Fraction)[] fractions = [(1, "000000001"), (10, "00000001"), (100, "0000001"), (1000, "000001"), (10000, "00001"), (100000, "0001"), (1000000, "001"), (10000000, "01"), (100000000, "1")];

			foreach (var (nanoseconds, fraction) in fractions)
			{
				var duration = new SomlDuration(sign * (1_000_000_000L + nanoseconds));
				var expected = $"{(sign < 0 ? "-" : "")}1.{fraction}s";

				for (var capacity = 0; capacity <= expected.Length + 1; capacity++)
				{
					var bytes = Enumerable.Repeat((byte)0xCC, capacity + 4).ToArray();
					var destination = bytes.AsSpan(2, capacity);
					var success = duration.TryFormat(destination, out var written, "ignored", new NumberFormatInfo { NumberDecimalSeparator = ",", NegativeSign = "~" });

					Assert.Equal(capacity >= expected.Length, success);
					Assert.Equal(success ? expected.Length : 0, written);
					Assert.Equal([0xCC, 0xCC], bytes[..2]);
					Assert.Equal([0xCC, 0xCC], bytes[^2..]);

					if (success)
					{
						Assert.Equal(expected, Encoding.UTF8.GetString(destination[..written]));
						Assert.All(destination[written..].ToArray(), value => Assert.Equal(0xCC, value));
					}
				}
			}
		}
		finally
		{
			CultureInfo.CurrentCulture = previousCulture;
		}
	}
}
