using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;

namespace Soml;

/// <summary>
/// A SOML duration: a signed length of time in whole nanoseconds, from about −292 to +292 years.
/// </summary>
/// <remarks>
/// <para>It exists because <see cref="TimeSpan"/> counts in ticks of 100 nanoseconds, and a SOML duration is exact to the nanosecond. Convert with <see cref="FromTimeSpan(TimeSpan)"/> and <see cref="ToTimeSpan()"/>.</para>
/// <para><c>ToString()</c> gives the canonical form, such as <c>1h30m</c> or <c>0.25s</c>, and <c>Parse()</c> reads any SOML spelling, such as <c>90m</c> or <c>1.5h</c>.</para>
/// </remarks>
public readonly struct SomlDuration :
	IEquatable<SomlDuration>,
	IComparable<SomlDuration>,
	IComparable,
	ISpanFormattable,
	IUtf8SpanFormattable,
	ISpanParsable<SomlDuration>,
	IComparisonOperators<SomlDuration, SomlDuration, bool>,
	IAdditionOperators<SomlDuration, SomlDuration, SomlDuration>,
	ISubtractionOperators<SomlDuration, SomlDuration, SomlDuration>,
	IUnaryNegationOperators<SomlDuration, SomlDuration>
{
	internal const long NanosecondsPerSecond = 1_000_000_000;
	private const long NanosecondsPerMinute = 60 * NanosecondsPerSecond;
	private const long NanosecondsPerHour = 60 * NanosecondsPerMinute;
	private const long NanosecondsPerTick = 100;
	private const int MaximumFormattedLength = 25;

	/// <summary>
	/// Creates a duration from a count of nanoseconds.
	/// </summary>
	public SomlDuration(long nanoseconds)
	{
		Nanoseconds = nanoseconds;
	}

	/// <summary>
	/// The zero duration, written <c>0s</c>.
	/// </summary>
	public static SomlDuration Zero => default;

	/// <summary>
	/// The shortest duration, <c>-9223372036854775808ns</c>.
	/// </summary>
	public static SomlDuration MinValue => new(long.MinValue);

	/// <summary>
	/// The longest duration, <c>9223372036854775807ns</c>.
	/// </summary>
	public static SomlDuration MaxValue => new(long.MaxValue);

	/// <summary>
	/// The length in nanoseconds.
	/// </summary>
	public long Nanoseconds { get; }

	/// <summary>
	/// Creates a duration from a <see cref="TimeSpan"/>.
	/// </summary>
	/// <exception cref="OverflowException">When the time span is longer than about 292 years either way, which a duration cannot hold.</exception>
	public static SomlDuration FromTimeSpan(TimeSpan timeSpan) => new(checked(timeSpan.Ticks * NanosecondsPerTick));

	/// <summary>
	/// Converts the duration to a <see cref="TimeSpan"/>.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the duration is not a whole number of ticks (100 nanoseconds), so the conversion would lose precision.</exception>
	public TimeSpan ToTimeSpan()
	{
		if (Nanoseconds % NanosecondsPerTick != 0)
		{
			throw new InvalidOperationException($"The duration {this} is not a whole number of 100-nanosecond ticks, so a TimeSpan cannot hold it. Use SomlDuration to keep the nanoseconds");
		}

		return TimeSpan.FromTicks(Nanoseconds / NanosecondsPerTick);
	}

	/// <summary>
	/// Reads a duration in any SOML spelling, such as <c>1h30m</c>, <c>90m</c>, <c>1.5h</c>, or <c>-250ms</c>.
	/// </summary>
	/// <exception cref="FormatException">When the text is not a valid SOML duration.</exception>
	public static SomlDuration Parse(string text) => Parse(text, null);

	/// <summary>
	/// Reads a duration in any SOML spelling. The format provider is ignored, because a SOML duration has one syntax.
	/// </summary>
	/// <exception cref="FormatException">When the text is not a valid SOML duration.</exception>
	public static SomlDuration Parse(string text, IFormatProvider? provider)
	{
		ArgumentNullException.ThrowIfNull(text);
		return Parser.ParseScalar(text, SomlValueKind.Duration).GetDuration();
	}

	/// <summary>
	/// Reads a duration in any SOML spelling. The format provider is ignored.
	/// </summary>
	/// <exception cref="FormatException">When the text is not a valid SOML duration.</exception>
	/// <remarks>Invalid input throws a fixed-message <see cref="FormatException"/> without copying the span. Use the string overload for detailed syntax diagnostics.</remarks>
	public static SomlDuration Parse(ReadOnlySpan<char> text, IFormatProvider? provider)
	{
		return Parser.TryParseTime(text, SomlValueKind.Duration, out var value) ? value.GetDuration() : throw new FormatException("The text is not a valid SOML duration");
	}

	/// <summary>
	/// Tries to read a duration in any SOML spelling.
	/// </summary>
	public static bool TryParse([NotNullWhen(true)] string? text, out SomlDuration result) => TryParse(text, null, out result);

	/// <summary>
	/// Tries to read a duration in any SOML spelling. The format provider is ignored.
	/// </summary>
	public static bool TryParse([NotNullWhen(true)] string? text, IFormatProvider? provider, out SomlDuration result) => TryParse(text.AsSpan(), provider, out result);

	/// <summary>
	/// Tries to read a duration in any SOML spelling. The format provider is ignored.
	/// </summary>
	public static bool TryParse(ReadOnlySpan<char> text, IFormatProvider? provider, out SomlDuration result)
	{
		if (Parser.TryParseTime(text, SomlValueKind.Duration, out var value))
		{
			result = value.GetDuration();
			return true;
		}

		result = default;
		return false;
	}

	/// <summary>
	/// The canonical form, such as <c>1h30m</c>, <c>0.25s</c>, or <c>0s</c>.
	/// </summary>
	public override string ToString()
	{
		// Note: Not using string.Create interpolation because it rents a handler buffer and did not improve time-string creation on .NET 10 ARM64.
		Span<char> buffer = stackalloc char[MaximumFormattedLength];
		TryFormat(buffer, out var length);
		return buffer[..length].ToString();
	}

	/// <summary>
	/// The canonical form. The format and the format provider are ignored, because a duration has one canonical form.
	/// </summary>
	public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

	/// <summary>
	/// Writes the canonical form.
	/// </summary>
	public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
	{
		var isWritten = TryFormat(destination, out var written);
		charsWritten = isWritten ? written : 0;
		return isWritten;
	}

	/// <summary>
	/// Writes the canonical form as UTF-8. The format and the format provider are ignored.
	/// </summary>
	public bool TryFormat(Span<byte> utf8Destination, out int bytesWritten, ReadOnlySpan<char> format = default, IFormatProvider? provider = null)
	{
		Span<char> text = stackalloc char[MaximumFormattedLength];
		TryFormat(text, out var length);
		// Note: Not using Ascii.FromUtf16 because it did not improve the actual compiled formatter on .NET 10 ARM64, despite a faster composition probe.
		return System.Text.Encoding.UTF8.TryGetBytes(text[..length], utf8Destination, out bytesWritten);
	}

	private bool TryFormat(Span<char> destination, out int charsWritten)
	{
		charsWritten = 0;

		if (Nanoseconds == 0)
		{
			return Append(destination, ref charsWritten, "0s");
		}

		// The magnitude of `long.MinValue` does not fit a `long`.
		var magnitude = Nanoseconds < 0 ? (ulong)-(Nanoseconds + 1) + 1 : (ulong)Nanoseconds;
		var (hours, remainder) = ulong.DivRem(magnitude, NanosecondsPerHour);
		var (minutes, secondRemainder) = ulong.DivRem(remainder, NanosecondsPerMinute);
		var (seconds, nanoseconds) = ulong.DivRem(secondRemainder, NanosecondsPerSecond);
		var fraction = (int)nanoseconds;

		if (Nanoseconds < 0 && !Append(destination, ref charsWritten, "-"))
		{
			return false;
		}

		if (hours > 0 && !(AppendNumber(destination, ref charsWritten, hours) && Append(destination, ref charsWritten, "h")))
		{
			return false;
		}

		if (minutes > 0 && !(AppendNumber(destination, ref charsWritten, minutes) && Append(destination, ref charsWritten, "m")))
		{
			return false;
		}

		if (seconds > 0 || fraction > 0)
		{
			if (!AppendNumber(destination, ref charsWritten, seconds) || !AppendFraction(destination, ref charsWritten, fraction) || !Append(destination, ref charsWritten, "s"))
			{
				return false;
			}
		}

		return true;
	}

	private static bool Append(Span<char> destination, ref int charsWritten, ReadOnlySpan<char> text)
	{
		if (!text.TryCopyTo(destination[charsWritten..]))
		{
			return false;
		}

		charsWritten += text.Length;
		return true;
	}

	private static bool AppendNumber(Span<char> destination, ref int charsWritten, ulong number)
	{
		if (!number.TryFormat(destination[charsWritten..], out var written, default, CultureInfo.InvariantCulture))
		{
			return false;
		}

		charsWritten += written;
		return true;
	}

	/// <summary>
	/// Writes a fraction of a second as <c>.</c> and its nine digits without the trailing zeros, or nothing when it is zero. Shared with the canonical form of an instant.
	/// </summary>
	internal static bool AppendFraction(Span<char> destination, ref int charsWritten, int nanoseconds)
	{
		if (nanoseconds == 0)
		{
			return true;
		}

		// Note: Not using D9 formatting followed by trimming/copying because it was slower on .NET 10 ARM64, and counting decimal zeros avoids the extra buffer and copy.
		// Note: Not using decimal custom fractional formatting because it was slower than the integer digit path in .NET 10 ARM64 probes.
		var digits = 9;

		while (nanoseconds % 10 == 0)
		{
			nanoseconds /= 10;
			digits--;
		}

		if (destination.Length - charsWritten < digits + 1)
		{
			return false;
		}

		destination[charsWritten++] = '.';
		// The format stays on the stack, so that no format string is allocated for each call.
		ReadOnlySpan<char> format = ['D', (char)('0' + digits)];
		nanoseconds.TryFormat(destination.Slice(charsWritten, digits), out _, format, CultureInfo.InvariantCulture);
		charsWritten += digits;
		return true;
	}

	/// <inheritdoc/>
	public bool Equals(SomlDuration other) => Nanoseconds == other.Nanoseconds;

	/// <inheritdoc/>
	public override bool Equals([NotNullWhen(true)] object? obj) => obj is SomlDuration other && Equals(other);

	/// <inheritdoc/>
	public override int GetHashCode() => Nanoseconds.GetHashCode();

	/// <inheritdoc/>
	public int CompareTo(SomlDuration other) => Nanoseconds.CompareTo(other.Nanoseconds);

	/// <inheritdoc/>
	public int CompareTo(object? obj) => obj switch
	{
		null => 1,
		SomlDuration other => CompareTo(other),
		_ => throw new ArgumentException("The object is not a SomlDuration", nameof(obj)),
	};

	/// <inheritdoc/>
	public static bool operator ==(SomlDuration left, SomlDuration right) => left.Equals(right);

	/// <inheritdoc/>
	public static bool operator !=(SomlDuration left, SomlDuration right) => !left.Equals(right);

	/// <inheritdoc/>
	public static bool operator <(SomlDuration left, SomlDuration right) => left.Nanoseconds < right.Nanoseconds;

	/// <inheritdoc/>
	public static bool operator <=(SomlDuration left, SomlDuration right) => left.Nanoseconds <= right.Nanoseconds;

	/// <inheritdoc/>
	public static bool operator >(SomlDuration left, SomlDuration right) => left.Nanoseconds > right.Nanoseconds;

	/// <inheritdoc/>
	public static bool operator >=(SomlDuration left, SomlDuration right) => left.Nanoseconds >= right.Nanoseconds;

	/// <summary>
	/// Adds two durations.
	/// </summary>
	/// <exception cref="OverflowException">When the sum is outside the range of a duration.</exception>
	public static SomlDuration operator +(SomlDuration left, SomlDuration right) => new(checked(left.Nanoseconds + right.Nanoseconds));

	/// <summary>
	/// Subtracts a duration from another.
	/// </summary>
	/// <exception cref="OverflowException">When the difference is outside the range of a duration.</exception>
	public static SomlDuration operator -(SomlDuration left, SomlDuration right) => new(checked(left.Nanoseconds - right.Nanoseconds));

	/// <summary>
	/// Negates a duration.
	/// </summary>
	/// <exception cref="OverflowException">For <see cref="MinValue"/>, whose negation is outside the range of a duration.</exception>
	public static SomlDuration operator -(SomlDuration value) => new(checked(-value.Nanoseconds));

	/// <summary>
	/// Converts a <see cref="TimeSpan"/>, as <see cref="FromTimeSpan(TimeSpan)"/> does.
	/// </summary>
	/// <exception cref="OverflowException">When the time span is longer than about 292 years either way.</exception>
	public static explicit operator SomlDuration(TimeSpan timeSpan) => FromTimeSpan(timeSpan);

	/// <summary>
	/// Converts to a <see cref="TimeSpan"/>, as <see cref="ToTimeSpan()"/> does.
	/// </summary>
	/// <exception cref="InvalidOperationException">When the duration is not a whole number of ticks.</exception>
	public static explicit operator TimeSpan(SomlDuration duration) => duration.ToTimeSpan();
}
