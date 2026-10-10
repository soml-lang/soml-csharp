using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Soml;

/// <summary>
/// A SOML instant: a point in time, exact to the nanosecond, from <c>0001-01-01T00:00:00Z</c> to <c>9999-12-31T23:59:59.999999999Z</c>.
/// </summary>
/// <remarks>
/// <para>It exists because <see cref="DateTimeOffset"/> and <see cref="DateTime"/> count in ticks of 100 nanoseconds, and a SOML instant is exact to the nanosecond. Convert with <see cref="FromDateTimeOffset(DateTimeOffset)"/> and <see cref="ToDateTimeOffset()"/>.</para>
/// <para>The offset that a document writes an instant with is not part of its value, so <c>2026-09-19T16:00:00+02:00</c> and <c>2026-09-19T14:00:00Z</c> are the same instant. <c>ToString()</c> gives the canonical form, in UTC.</para>
/// </remarks>
public readonly struct SomlInstant :
	IEquatable<SomlInstant>,
	IComparable<SomlInstant>,
	IComparable,
	ISpanFormattable,
	IUtf8SpanFormattable,
	ISpanParsable<SomlInstant>,
	IComparisonOperators<SomlInstant, SomlInstant, bool>,
	IAdditionOperators<SomlInstant, SomlDuration, SomlInstant>,
	ISubtractionOperators<SomlInstant, SomlDuration, SomlInstant>
{
	private const long SecondsPerDay = 86_400;
	private const long MinUnixSeconds = -62_135_596_800; // 0001-01-01T00:00:00Z
	private const long MaxUnixSeconds = 253_402_300_799; // 9999-12-31T23:59:59Z
	private const int NanosecondsPerTick = 100;
	private const int MaximumFormattedLength = 30;

	private readonly long _unixSeconds;
	private readonly int _nanosecond;

	/// <summary>
	/// Creates an instant from the seconds since the Unix epoch, <c>1970-01-01T00:00:00Z</c>, and the nanoseconds within that second.
	/// </summary>
	/// <exception cref="ArgumentOutOfRangeException">When the nanoseconds are not 0 to 999,999,999, or the instant is outside the years 0001 to 9999.</exception>
	public SomlInstant(long unixSeconds, int nanosecond)
	{
		ArgumentOutOfRangeException.ThrowIfNegative(nanosecond);
		ArgumentOutOfRangeException.ThrowIfGreaterThan(nanosecond, 999_999_999);

		if (unixSeconds is < MinUnixSeconds or > MaxUnixSeconds)
		{
			throw new ArgumentOutOfRangeException(nameof(unixSeconds), unixSeconds, "A SOML instant is in the years 0001 to 9999, in UTC");
		}

		_unixSeconds = unixSeconds;
		_nanosecond = nanosecond;
	}

	/// <summary>
	/// The earliest instant, <c>0001-01-01T00:00:00Z</c>.
	/// </summary>
	public static SomlInstant MinValue => new(MinUnixSeconds, 0);

	/// <summary>
	/// The latest instant, <c>9999-12-31T23:59:59.999999999Z</c>.
	/// </summary>
	public static SomlInstant MaxValue => new(MaxUnixSeconds, 999_999_999);

	/// <summary>
	/// The Unix epoch, <c>1970-01-01T00:00:00Z</c>, which is also the default value.
	/// </summary>
	public static SomlInstant UnixEpoch => default;

	/// <summary>
	/// The whole seconds since the Unix epoch, <c>1970-01-01T00:00:00Z</c>. Negative before it.
	/// </summary>
	public long UnixSeconds => _unixSeconds;

	/// <summary>
	/// The nanoseconds within the second, from 0 to 999,999,999.
	/// </summary>
	public int Nanosecond => _nanosecond;

	/// <summary>
	/// Creates an instant from a <see cref="DateTimeOffset"/>. Its offset is not kept, because it is not part of the value. Every <see cref="DateTimeOffset"/> is in the range of an instant.
	/// </summary>
	public static SomlInstant FromDateTimeOffset(DateTimeOffset dateTimeOffset) => FromUtcTicks(dateTimeOffset.UtcTicks);

	/// <summary>
	/// Creates an instant from a <see cref="DateTime"/> whose <see cref="DateTime.Kind"/> is <see cref="DateTimeKind.Utc"/> or <see cref="DateTimeKind.Local"/>.
	/// </summary>
	/// <exception cref="ArgumentException">When the kind is <see cref="DateTimeKind.Unspecified"/>, because then the time does not say which instant it is.</exception>
	/// <exception cref="ArgumentOutOfRangeException">When the instant is outside the years 0001 to 9999 in UTC.</exception>
	public static SomlInstant FromDateTime(DateTime dateTime)
	{
		if (dateTime.Kind == DateTimeKind.Unspecified)
		{
			throw new ArgumentException($"The DateTime {dateTime.ToString("O", CultureInfo.InvariantCulture)} has an unspecified kind, so it is not an instant. Set its kind to Utc or Local, or use a DateTimeOffset", nameof(dateTime));
		}

		// Note: Not using DateTime.ToUniversalTime for local values because it clamps a UTC value outside the range, while DateTimeOffset refuses it.
		return dateTime.Kind == DateTimeKind.Utc ? FromUtcTicks(dateTime.Ticks) : FromDateTimeOffset(new DateTimeOffset(dateTime));
	}

	private static SomlInstant FromUtcTicks(long utcTicks)
	{
		// Note: Not using a DateTimeOffset for ToUnixTimeSeconds because constructing it was slower than this exact floor/remainder conversion in .NET 10 ARM64 probes.
		var ticksSinceEpoch = utcTicks - DateTime.UnixEpoch.Ticks;
		var (unixSeconds, ticks) = Math.DivRem(ticksSinceEpoch, TimeSpan.TicksPerSecond);

		// The division truncates toward zero, and the fraction of a second is never negative, so an instant before 1970 is floored.
		if (ticks < 0)
		{
			unixSeconds--;
			ticks += TimeSpan.TicksPerSecond;
		}

		return new SomlInstant(unixSeconds, (int)ticks * NanosecondsPerTick);
	}

	/// <summary>
	/// Converts the instant to a <see cref="DateTimeOffset"/> in UTC.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the instant is not a whole number of ticks (100 nanoseconds), so the conversion would lose precision.</exception>
	public DateTimeOffset ToDateTimeOffset() => new(ToUtcTicks(), TimeSpan.Zero);

	/// <summary>
	/// Converts the instant to a <see cref="DateTime"/> whose kind is <see cref="DateTimeKind.Utc"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the instant is not a whole number of ticks (100 nanoseconds), so the conversion would lose precision.</exception>
	public DateTime ToDateTime() => new(ToUtcTicks(), DateTimeKind.Utc);

	private long ToUtcTicks()
	{
		if (_nanosecond % NanosecondsPerTick != 0)
		{
			throw new InvalidOperationException($"The instant {this} is not a whole number of 100-nanosecond ticks, so a DateTimeOffset or DateTime cannot hold it. Use SomlInstant to keep the nanoseconds");
		}

		return DateTime.UnixEpoch.Ticks + (_unixSeconds * TimeSpan.TicksPerSecond) + (_nanosecond / NanosecondsPerTick);
	}

	/// <summary>
	/// The instant of a date and time with an offset, or <c>null</c> when it is outside the years 0001 to 9999 in UTC. The components must already be in range.
	/// </summary>
	internal static SomlInstant? FromComponents(int year, int month, int day, int hour, int minute, int second, int nanosecond, int offsetMinutes)
	{
		// Note: Not using DateTimeOffset's component constructor because it limits offsets to ±14 hours, while SOML accepts them through ±23:59.
		var unixSeconds = (DaysFromCivil(year, month, day) * SecondsPerDay) + (hour * 3600) + (minute * 60) + second - (offsetMinutes * 60L);
		return unixSeconds is < MinUnixSeconds or > MaxUnixSeconds ? null : new SomlInstant(unixSeconds, nanosecond);
	}

	/*
	The days since the Unix epoch of a date in the proleptic Gregorian calendar.
	*/
	private static long DaysFromCivil(int year, int month, int day)
	{
		return new DateOnly(year, month, day).DayNumber - new DateOnly(1970, 1, 1).DayNumber;
	}

	/// <summary>
	/// Reads an instant in SOML syntax, such as <c>2026-09-19T14:00:00Z</c> or <c>2026-09-19T16:00:00.5+02:00</c>.
	/// </summary>
	/// <exception cref="FormatException">When the text is not a valid SOML instant.</exception>
	public static SomlInstant Parse(string text) => Parse(text, null);

	/// <summary>
	/// Reads an instant in SOML syntax. The format provider is ignored, because a SOML instant has one syntax.
	/// </summary>
	/// <exception cref="FormatException">When the text is not a valid SOML instant.</exception>
	public static SomlInstant Parse(string text, IFormatProvider? provider)
	{
		ArgumentNullException.ThrowIfNull(text);
		return Parser.ParseScalar(text, SomlValueKind.Instant).GetInstant();
	}

	/// <inheritdoc cref="Parse(string, IFormatProvider?)"/>
	/// <remarks>Invalid input throws a fixed-message <see cref="FormatException"/> without copying the span. Use the string overload for detailed syntax diagnostics.</remarks>
	public static SomlInstant Parse(ReadOnlySpan<char> text, IFormatProvider? provider)
	{
		return Parser.TryParseTime(text, SomlValueKind.Instant, out var value) ? value.GetInstant() : throw new FormatException("The text is not a valid SOML instant");
	}

	/// <summary>
	/// Tries to read an instant in SOML syntax.
	/// </summary>
	public static bool TryParse([NotNullWhen(true)] string? text, out SomlInstant result) => TryParse(text, null, out result);

	/// <summary>
	/// Tries to read an instant in SOML syntax. The format provider is ignored.
	/// </summary>
	public static bool TryParse([NotNullWhen(true)] string? text, IFormatProvider? provider, out SomlInstant result) => TryParse(text.AsSpan(), provider, out result);

	/// <inheritdoc cref="TryParse(string?, IFormatProvider?, out SomlInstant)"/>
	public static bool TryParse(ReadOnlySpan<char> text, IFormatProvider? provider, out SomlInstant result)
	{
		if (Parser.TryParseTime(text, SomlValueKind.Instant, out var value))
		{
			result = value.GetInstant();
			return true;
		}

		result = default;
		return false;
	}

	/// <summary>
	/// The canonical form: UTC with <c>Z</c>, and the fraction of a second without trailing zeros, or without a fraction when it is zero, such as <c>2026-09-19T14:00:00.5Z</c>.
	/// </summary>
	public override string ToString()
	{
		// Note: Not using string.Create interpolation because it rents a handler buffer and did not improve time-string creation on .NET 10 ARM64.
		// The longest form, with nine fraction digits, is 30 characters.
		Span<char> buffer = stackalloc char[MaximumFormattedLength];
		TryFormat(buffer, out var length);
		return buffer[..length].ToString();
	}

	/// <summary>
	/// The canonical form. The format and the format provider are ignored, because an instant has one canonical form.
	/// </summary>
	public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

	/// <summary>
	/// Writes the canonical form. The format and the format provider are ignored.
	/// </summary>
	public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
	{
		/*
		The date and time of the whole seconds since the Unix epoch, using the framework's proleptic Gregorian calendar and sortable format. The nanosecond fraction is appended separately.
		*/
		// Converting the whole seconds to ticks before adding the epoch keeps times before 1970 on the day they fall in.
		// Note: Not using UnixEpoch.AddSeconds because it repeated floating-point and range work and was slower for this construction in .NET 10 ARM64 probes.
		var dateTime = new DateTime(DateTime.UnixEpoch.Ticks + (_unixSeconds * TimeSpan.TicksPerSecond), DateTimeKind.Utc);

		if (!dateTime.TryFormat(destination, out charsWritten, "s", CultureInfo.InvariantCulture))
		{
			return false;
		}

		if (!SomlDuration.AppendFraction(destination, ref charsWritten, _nanosecond) || charsWritten >= destination.Length)
		{
			charsWritten = 0;
			return false;
		}

		destination[charsWritten++] = 'Z';
		return true;
	}

	/// <summary>
	/// Writes the canonical form as UTF-8. The format and the format provider are ignored.
	/// </summary>
	public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
	{
		Span<char> text = stackalloc char[MaximumFormattedLength];
		TryFormat(text, out var length);
		// Note: Not using Ascii.FromUtf16 because formatter timings overlapped on .NET 10 ARM64 and it needs extra handling to preserve zero bytesWritten on failure.
		return System.Text.Encoding.UTF8.TryGetBytes(text[..length], utf8Destination, out bytesWritten);
	}

	/// <inheritdoc/>
	public bool Equals(SomlInstant other) => _unixSeconds == other._unixSeconds && _nanosecond == other._nanosecond;

	/// <inheritdoc/>
	public override bool Equals([NotNullWhen(true)] object? obj) => obj is SomlInstant other && Equals(other);

	/// <inheritdoc/>
	public override int GetHashCode() => HashCode.Combine(_unixSeconds, _nanosecond);

	/// <inheritdoc/>
	public int CompareTo(SomlInstant other)
	{
		// Note: Not using ValueTuple.CompareTo because it preserved this order but was slower in .NET 10 ARM64 comparison probes.
		var comparison = _unixSeconds.CompareTo(other._unixSeconds);
		return comparison != 0 ? comparison : _nanosecond.CompareTo(other._nanosecond);
	}

	/// <inheritdoc/>
	public int CompareTo(object? obj) => obj switch
	{
		null => 1,
		SomlInstant other => CompareTo(other),
		_ => throw new ArgumentException("The object is not a SomlInstant", nameof(obj)),
	};

	/// <inheritdoc/>
	public static bool operator ==(SomlInstant left, SomlInstant right) => left.Equals(right);

	/// <inheritdoc/>
	public static bool operator !=(SomlInstant left, SomlInstant right) => !left.Equals(right);

	/// <inheritdoc/>
	public static bool operator <(SomlInstant left, SomlInstant right) => left.CompareTo(right) < 0;

	/// <inheritdoc/>
	public static bool operator <=(SomlInstant left, SomlInstant right) => left.CompareTo(right) <= 0;

	/// <inheritdoc/>
	public static bool operator >(SomlInstant left, SomlInstant right) => left.CompareTo(right) > 0;

	/// <inheritdoc/>
	public static bool operator >=(SomlInstant left, SomlInstant right) => left.CompareTo(right) >= 0;

	/// <summary>
	/// The instant a duration later, or earlier for a negative duration.
	/// </summary>
	/// <exception cref="OverflowException">When the result is outside the years 0001 to 9999.</exception>
	public static SomlInstant operator +(SomlInstant instant, SomlDuration duration)
	{
		var (seconds, nanosecond) = Math.DivRem(duration.Nanoseconds, SomlDuration.NanosecondsPerSecond);
		return instant.Add(seconds, (int)nanosecond);
	}

	/// <summary>
	/// The instant a duration earlier, or later for a negative duration.
	/// </summary>
	/// <exception cref="OverflowException">When the result is outside the years 0001 to 9999.</exception>
	public static SomlInstant operator -(SomlInstant instant, SomlDuration duration)
	{
		var (seconds, nanosecond) = Math.DivRem(duration.Nanoseconds, SomlDuration.NanosecondsPerSecond);
		return instant.Add(-seconds, -(int)nanosecond);
	}

	/// <summary>
	/// The duration from <paramref name="right"/> to <paramref name="left"/>.
	/// </summary>
	/// <exception cref="OverflowException">When the difference is longer than about 292 years either way, which a duration cannot hold.</exception>
	public static SomlDuration operator -(SomlInstant left, SomlInstant right)
	{
		var difference = ((Int128)(left._unixSeconds - right._unixSeconds) * SomlDuration.NanosecondsPerSecond) + left._nanosecond - right._nanosecond;
		return difference < long.MinValue || difference > long.MaxValue ? throw new OverflowException("The difference between the instants is outside the range of a SOML duration, about 292 years either way") : new SomlDuration((long)difference);
	}

	private SomlInstant Add(long seconds, int nanosecond)
	{
		seconds += _unixSeconds;
		nanosecond += _nanosecond;

		// Floored, so that the nanoseconds within the second are never negative.
		if (nanosecond < 0)
		{
			seconds--;
			nanosecond += (int)SomlDuration.NanosecondsPerSecond;
		}
		else if (nanosecond >= SomlDuration.NanosecondsPerSecond)
		{
			seconds++;
			nanosecond -= (int)SomlDuration.NanosecondsPerSecond;
		}

		if (seconds < MinUnixSeconds || seconds > MaxUnixSeconds)
		{
			throw new OverflowException("The instant is outside the years 0001 to 9999");
		}

		return new SomlInstant(seconds, nanosecond);
	}

	/// <summary>
	/// Converts a <see cref="DateTimeOffset"/>, as <see cref="FromDateTimeOffset(DateTimeOffset)"/> does.
	/// </summary>
	public static explicit operator SomlInstant(DateTimeOffset dateTimeOffset) => FromDateTimeOffset(dateTimeOffset);

	/// <summary>
	/// Converts to a <see cref="DateTimeOffset"/>, as <see cref="ToDateTimeOffset()"/> does.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the instant is not a whole number of ticks.</exception>
	public static explicit operator DateTimeOffset(SomlInstant instant) => instant.ToDateTimeOffset();
}
