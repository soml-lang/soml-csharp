using System.Text;
using Microsoft.Extensions.Configuration;
using Soml.Cli;

namespace Soml.Tests;

public sealed class TextProcessingTests
{
	[Fact]
	public void ConfigurationCollisionDiagnosticsBoundUntrustedKeys()
	{
		var prefix = new string('x', 100_000);
		using var input = new MemoryStream(Encoding.UTF8.GetBytes($"'{prefix}A': 1\n'{prefix}a': 2"));
		var error = Assert.Throws<FormatException>(() => new ConfigurationBuilder().AddSomlStream(input).Build());

		Assert.True(error.Message.Length < 1000);
		Assert.Contains("…", error.Message, StringComparison.Ordinal);
	}

	private sealed class FailedInput(string message = "bad\u001b[31m\u202einput") : MemoryStream
	{
		public override void CopyTo(Stream destination, int bufferSize)
		{
			throw new IOException(message);
		}

		public override int Read(byte[] buffer, int offset, int count) => throw new IOException(message);

		public override int Read(Span<byte> buffer) => throw new IOException(message);
	}

	[Fact]
	public void IoDiagnosticsEscapeTerminalControlCharacters()
	{
		using var input = new FailedInput();
		using var output = new MemoryStream();
		// A writer whose default newline is CRLF, as on Windows, so that the output must use LF itself.
		using var error = new StringWriter { NewLine = "\r\n" };

		Assert.Equal(1, Program.Run(["encode"], input, output, error));
		Assert.Equal("bad\\u{1b}[31m\\u{202e}input\n", error.ToString());
	}

	[Fact]
	public void AlreadyFormattedDocumentsReuseTheirText()
	{
		var text = "message: '" + new string('x', 100_000) + "'\n";

		Assert.Same(text, SomlFormatter.Format(text));
		Assert.True(SomlFormatter.IsFormatted(text));
		Assert.False(SomlFormatter.IsFormatted(text[..^1]));
	}

	[Fact]
	public void TaggedJsonStreamsReadFromTheirPositionAndStayOpen()
	{
		using var input = new MemoryStream("junk{\"a\": {\"type\": \"int\", \"value\": \"7\"}}"u8.ToArray());
		input.Position = 4;

		Assert.Equal(7, TaggedJson.Read(input)["a"].GetInt64());
		Assert.Equal(input.Length, input.Position);
		Assert.True(input.CanRead);
	}

	[Fact]
	public void TaggedJsonWritingBoundsCyclicContainers()
	{
		var array = new SomlArray();
		array.Add(array);
		using var output = new MemoryStream();

		Assert.Throws<InvalidOperationException>(() => TaggedJson.Write(array, output));
		Assert.True(output.Length < 20_000);
	}

	[Theory]
	[InlineData("0001-01-01T00:00:00Z")]
	[InlineData("9999-12-31T23:59:59.999999999Z")]
	[InlineData("-2562047h47m16.854775808s")]
	[InlineData("0s")]
	public void TimeValuesFormatAsUtf8IntoEveryDestinationSize(string text)
	{
		IUtf8SpanFormattable value = text.Contains('T') ? SomlInstant.Parse(text) : SomlDuration.Parse(text);

		for (var length = 0; length <= text.Length + 1; length++)
		{
			var destination = new byte[length];
			var success = value.TryFormat(destination, out var written, "ignored", new System.Globalization.CultureInfo("ar-EG"));

			Assert.Equal(length >= text.Length, success);
			Assert.Equal(success ? text.Length : 0, written);

			if (success)
			{
				Assert.Equal(text, Encoding.UTF8.GetString(destination.AsSpan(0, written)));
			}
		}
	}

	[Fact]
	public void SettingAnUnchangedValueReusesTheDocument()
	{
		var text = "message: '" + new string('x', 100_000) + "'\nport: 80 # keep\n";

		Assert.Same(text, SomlEditor.Set(text, ["port"], 80));
		Assert.Same(text, SomlEditor.Remove(text, ["missing"]));
	}

	[Fact]
	public void UnicodeIoDiagnosticsDoNotAllocatePerCharacter()
	{
		var message = string.Concat(Enumerable.Repeat("😀é", 10_000));
		using var warmInput = new FailedInput("é");
		using var output = new MemoryStream();
		// A writer whose default newline is CRLF, as on Windows, so that the output must use LF itself.
		using var error = new StringWriter { NewLine = "\r\n" };
		Assert.Equal(1, Program.Run(["encode"], warmInput, output, error));
		error.GetStringBuilder().Clear();
		using var input = new FailedInput(message);
		var allocated = GC.GetAllocatedBytesForCurrentThread();

		Assert.Equal(1, Program.Run(["encode"], input, output, error));

		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
		Assert.Equal(message + "\n", error.ToString());
		Assert.True(allocated < message.Length * 8, $"An I/O diagnostic allocated {allocated} bytes");
	}

	private sealed class ShortInput(byte[] bytes) : Stream
	{
		private readonly MemoryStream _input = new(bytes);

		public override bool CanRead => true;

		public override bool CanSeek => false;

		public override bool CanWrite => false;

		public override long Length => throw new NotSupportedException();

		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, Math.Min(1, count));

		public override int Read(Span<byte> buffer) => _input.Read(buffer[..Math.Min(1, buffer.Length)]);

		public override void Flush() => throw new NotSupportedException();

		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

		public override void SetLength(long value) => throw new NotSupportedException();

		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_input.Dispose();
			}

			base.Dispose(disposing);
		}
	}

	[Theory]
	[InlineData("encode", "{\"a\": {\"type\": \"string\", \"value\": \"😀\"}}", "a: '😀'\n")]
	[InlineData("decode", "a: '😀'", "😀")]
	public void CliReadsNonSeekableStreamsAcrossUtf8SequenceBoundaries(string command, string text, string expected)
	{
		using var input = new ShortInput(Encoding.UTF8.GetBytes(text));
		using var output = new MemoryStream();
		using var error = new StringWriter();

		Assert.Equal(0, Program.Run([command], input, output, error));
		if (command == "decode")
		{
			Assert.Equal(expected, TaggedJson.Read(output.ToArray())["a"].GetString());
		}
		else
		{
			Assert.Equal(expected, Encoding.UTF8.GetString(output.ToArray()));
		}
		Assert.True(input.CanRead);
		Assert.True(output.CanWrite);
		Assert.Equal("", error.ToString());
	}

	[Fact]
	public void ConfigurationDiagnosticTruncationDoesNotSplitSurrogatePairs()
	{
		var prefix = new string('x', 199);
		using var input = new MemoryStream(Encoding.UTF8.GetBytes($"'{prefix}😀A': 1\n'{prefix}😀a': 2"));
		var error = Assert.Throws<FormatException>(() => new ConfigurationBuilder().AddSomlStream(input).Build());

		Assert.Equal($"A duplicate key '{prefix}…' was found. Configuration keys are case-insensitive", error.Message);
	}

	[Fact]
	public void Utf8InterpolationUsesCanonicalTimeValuesWithoutStrings()
	{
		var instant = SomlInstant.MaxValue;
		var duration = SomlDuration.MinValue;
		Span<byte> destination = stackalloc byte[100];

		Assert.True(System.Text.Unicode.Utf8.TryWrite(destination, $"{instant}/{duration}", out var written));
		Assert.Equal("9999-12-31T23:59:59.999999999Z/-2562047h47m16.854775808s", Encoding.UTF8.GetString(destination[..written]));
	}

	[Fact]
	public void NullConfigurationStreamsAreRejectedWhenRegistered()
	{
		var builder = new ConfigurationBuilder();
		var error = Assert.Throws<ArgumentNullException>(() => builder.AddSomlStream(null!));

		Assert.Equal("stream", error.ParamName);
		Assert.Empty(builder.Sources);
	}

	private sealed class OverstatedInput() : MemoryStream("{}"u8.ToArray())
	{
		public override long Length => 32 * 1024 * 1024;
	}

	[Fact]
	public void TaggedJsonStreamAllocationFollowsTheBytesRead()
	{
		using var warmInput = new MemoryStream("{}"u8.ToArray());
		Assert.Empty(TaggedJson.Read(warmInput).GetObject());
		using var input = new OverstatedInput();
		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var value = TaggedJson.Read(input);
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Empty(value.GetObject());
		Assert.True(allocated < 100_000, $"A two-byte JSON stream allocated {allocated} bytes");
	}
}
