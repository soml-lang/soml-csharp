using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Soml.Cli;

namespace Soml.Tests;

/*
The `soml` command, as a conformance runner of another implementation would run it.
*/
public sealed class CliTests
{
	public static TheoryData<string> ValidCases => Conformance.Cases("valid", ".soml");

	public static TheoryData<string> InvalidCases => Conformance.Cases("invalid", ".soml");

	private static (int ExitCode, string Output, string Error) Run(string command, byte[] input)
	{
		using var output = new MemoryStream();
		// A writer whose default newline is CRLF, as on Windows, so that the output must use LF itself.
		using var error = new StringWriter { NewLine = "\r\n" };
		var exitCode = Program.Run([command], new MemoryStream(input), output, error);
		return (exitCode, Encoding.UTF8.GetString(output.ToArray()), error.ToString());
	}

	[Theory]
	[MemberData(nameof(ValidCases))]
	public void DecodesAValidCaseToItsTaggedJson(string name)
	{
		var (exitCode, output, error) = Run("decode", Conformance.ReadBytes($"valid/{name}.soml"));
		var expected = JsonNode.Parse(Conformance.ReadText($"valid/{name}.json"), documentOptions: new() { MaxDepth = 200 });

		Assert.Equal(0, exitCode);
		Assert.Empty(error);
		Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(output, documentOptions: new() { MaxDepth = 200 })), output);
	}

	[Theory]
	[MemberData(nameof(ValidCases))]
	public void EncodesTheTaggedJsonOfAValidCaseToItsCanonicalForm(string name)
	{
		var (exitCode, output, error) = Run("encode", Conformance.ReadBytes($"valid/{name}.json"));

		Assert.Equal(0, exitCode);
		Assert.Empty(error);
		Assert.Equal(Conformance.ReadText($"valid/{name}.canonical.soml"), output);
	}

	[Theory]
	[MemberData(nameof(InvalidCases))]
	public void RejectsAnInvalidCaseWithItsPosition(string name)
	{
		var expected = Conformance.InvalidReasons[name]!;
		var (exitCode, output, error) = Run("decode", Conformance.ReadBytes($"invalid/{name}.soml"));

		Assert.Equal(1, exitCode);
		Assert.Empty(output);
		Assert.EndsWith($" at line {expected["line"]}, column {expected["column"]}\n", error, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("""{"a": {"type": "int", "value": "+5"}}""", "The int value “+5” is not in canonical form")]
	[InlineData("""{"a": {"type": "float", "value": "1.50"}}""", "The float value “1.50” is not in canonical form")]
	[InlineData("""{"a": {"type": "instant", "value": "2026-09-19T16:00:00+02:00"}}""", "The instant value “2026-09-19T16:00:00+02:00” is not in canonical form")]
	[InlineData("""{"a": {"type": "int", "value": "9223372036854775808"}}""", "The int value “9223372036854775808” is out of range")]
	[InlineData("""{"a": {"type": "duration", "value": "-9223372036854775809"}}""", "The duration value “-9223372036854775809” is out of range")]
	[InlineData("""{"a": {"type": "unsupported", "value": "2026-09-19"}}""", "Unknown tagged scalar type.")]
	[InlineData("""{"a": 1}""", "Expected an object, an array, or a tagged scalar, but found a number")]
	[InlineData("""{"a": {"type": "int", "value": "1"}, "a": {"type": "int", "value": "2"}}""", "The input is not valid JSON")]
	[InlineData("""{"type": "int", "value": "1"}""", "The top-level value must be an object or an array")]
	[InlineData("""{"a": {"type": "string", "value": "\ud800"}}""", "Cannot read incomplete UTF-16 JSON text")]
	[InlineData("""{"\ud800": {"type": "string", "value": "x"}}""", "The input is not valid JSON: Cannot read incomplete UTF-16 JSON text")]
	public void RejectsTaggedJsonThatIsNotCanonical(string input, string message)
	{
		var (exitCode, _, error) = Run("encode", Encoding.UTF8.GetBytes(input));

		Assert.Equal(1, exitCode);
		Assert.StartsWith(message, error, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("""{"a": {"type": "\u001b[31m\u202e", "value": "1"}}""", "Unknown tagged scalar type.")]
	[InlineData("""{"a": {"type": "\u2028\u200b", "value": "1"}}""", "Unknown tagged scalar type.")]
	[InlineData("""{"a": {"type": "\udb40\udc01😀", "value": "1"}}""", "Unknown tagged scalar type.")]
	// System.Text.Json quotes the key in its own error as it is.
	[InlineData("""{"\u001b[31m": {"type": "int", "value": "1"}, "\u001b[31m": {"type": "int", "value": "2"}}""", "The input is not valid JSON: Duplicate property '\\u{1b}[31m'")]
	public void EscapesControlCharactersInAnError(string input, string message)
	{
		var (exitCode, _, error) = Run("encode", Encoding.UTF8.GetBytes(input));

		Assert.Equal(1, exitCode);
		Assert.StartsWith(message, error, StringComparison.Ordinal);
	}

	[Fact]
	public void RejectsAMillionDigitIntWithoutQuotingIt()
	{
		var digits = new string('9', 1_000_000);
		var (exitCode, _, error) = Run("encode", Encoding.UTF8.GetBytes($$$"""{"a": {"type": "int", "value": "{{{digits}}}"}}"""));

		Assert.Equal(1, exitCode);
		Assert.Equal("The int value is too long to be in canonical form\n", error);
	}

	[Theory]
	[InlineData("decode")]
	[InlineData("encode")]
	[InlineData("--help")]
	public void ExitsWithOneOnAnIOError(string command)
	{
		using var output = new AnonymousPipeServerStream(PipeDirection.Out);
		output.ClientSafePipeHandle.Dispose();
		using var error = new StringWriter();

		var input = command == "encode" ? "{}" : "a: 1\n";
		Assert.Equal(1, Program.Run([command], new MemoryStream(Encoding.UTF8.GetBytes(input)), output, error));
		Assert.NotEmpty(error.ToString());
	}

	[Fact]
	public void RejectsInvalidArguments()
	{
		using var error = new StringWriter();

		Assert.Equal(64, Program.Run([], new MemoryStream(), new MemoryStream(), error));
		Assert.Equal(64, Program.Run(["fmt"], new MemoryStream(), new MemoryStream(), error));
		Assert.Equal(64, Program.Run(["decode", "extra"], new MemoryStream(), new MemoryStream(), error));
		Assert.Equal(0, Program.Run(["--help"], new MemoryStream(), new MemoryStream(), error));
	}
}
