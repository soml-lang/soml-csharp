using System.Globalization;
using System.Text;

namespace Soml.Cli;

/// <summary>
/// The <c>soml</c> command: the decoder and encoder of the SOML conformance suite.
/// </summary>
public static class Program
{
	private static readonly System.Buffers.SearchValues<char> PrintableAsciiCharacters = System.Buffers.SearchValues.Create(" !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`abcdefghijklmnopqrstuvwxyz{|}~");

	private const string Usage = """
		Usage: soml <command> < input > output

		Commands:
		  decode  Convert a SOML document to the tagged JSON of the conformance suite
		  encode  Convert the tagged JSON of the conformance suite to a SOML document in canonical form

		Exit codes: 0 for success, 1 for an input that is not valid or an I/O error, and 64 for invalid arguments.
		""";

	/// <summary>
	/// Runs the command.
	/// </summary>
	public static int Main(string[] args)
	{
		using var input = Console.OpenStandardInput();
		using var output = Console.OpenStandardOutput();
		return Run(args, input, output, Console.Error);
	}

	/// <summary>
	/// Runs a command with the given input, output, and error writer, and returns the exit code.
	/// </summary>
	public static int Run(string[] args, Stream input, Stream output, TextWriter error)
	{
		ArgumentNullException.ThrowIfNull(args);
		ArgumentNullException.ThrowIfNull(input);
		ArgumentNullException.ThrowIfNull(output);
		ArgumentNullException.ThrowIfNull(error);

		// The command writes the same text on every platform, so it uses LF even where the writer would use CRLF.
		error.NewLine = "\n";

		try
		{
			if (args is ["--help" or "-h"])
			{
				using var writer = new StreamWriter(output, leaveOpen: true);
				writer.Write(Usage.Replace("\r", ""));
				writer.Write('\n');
				return 0;
			}

			if (args is not [var command] || command is not ("decode" or "encode"))
			{
				error.Write(Usage.Replace("\r", ""));
				error.Write('\n');
				return 64;
			}

			if (command == "decode")
			{
				TaggedJson.Write(SomlValue.Parse(input), output);
			}
			else
			{
				var text = TaggedJson.Read(input).ToDocumentString(canonical: true);
				using var writer = new StreamWriter(output, leaveOpen: true);
				writer.Write(text);
			}

			return 0;
		}
		catch (SomlParseException exception)
		{
			error.WriteLine($"{exception.Reason} at line {exception.Line}, column {exception.Column}");
			return 1;
		}
		catch (SomlException exception)
		{
			error.WriteLine(exception.Message);
			return 1;
		}
		// A failed read or write is an error, as in soml-cli. .NET ignores a closed standard output, so that case is not one.
		catch (Exception exception) when (exception is FormatException or IOException)
		{
			error.WriteLine(EscapeControlCharacters(exception.Message));
			return 1;
		}
	}

	/*
	Writes every control character, such as ESC, every format character, such as the bidirectional control U+202E, and the line and paragraph separators as an escape like `\u{1b}`, as soml-cli does. A tagged JSON error can quote the input, which must not send escape sequences to the terminal. The messages of `SomlException` are already escaped.
	*/
	private static string EscapeControlCharacters(string message)
	{
		if (message.AsSpan().IndexOfAnyExcept(PrintableAsciiCharacters) < 0)
		{
			return message;
		}

		StringBuilder? result = null;
		var offset = 0;

		// Note: Not using Unicode-category Regex.Replace because its UTF-16 character classification misses supplementary format controls.
		foreach (var character in message.EnumerateRunes())
		{
			if (Rune.GetUnicodeCategory(character) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
			{
				result ??= new StringBuilder(message, 0, offset, message.Length);
				result.Append(CultureInfo.InvariantCulture, $"\\u{{{character.Value:x}}}");
			}
			else
			{
				result?.Append(message.AsSpan(offset, character.Utf16SequenceLength));
			}

			offset += character.Utf16SequenceLength;
		}

		return result?.ToString() ?? message;
	}
}
