using System.Text;
using Microsoft.Extensions.Configuration;
using Soml.Cli;
using Soml.Extensions.Configuration;

namespace Soml.Tests;

/*
The text-processing edges that the conformance suite and the differential runs do not reach. Every expected message, position, and edit result comes from the JS reference implementation, and the configuration data from the JSON provider.
*/
public sealed class TextEdgeTests
{
	private static string Emoji(int count) => string.Concat(Enumerable.Repeat("😀", count));

	/*
	The caret line of a code frame pads one space for each code point before the error, as the column counts them, so a surrogate pair is one space, not two.
	*/
	public static TheoryData<string, string> CodeFramesAfterSurrogatePairs => new()
	{
		{
			"a: '😀' x",
			"Expected a line break before the next entry, but found “x” at line 1, column 8\n\n> 1 | a: '😀' x\n    |        ^"
		},
		{
			"a: '😀😀😀' 'é' x",
			"Expected a line break before the next entry, but found “'” at line 1, column 10\n\n> 1 | a: '😀😀😀' 'é' x\n    |          ^"
		},
		{
			"a: 1\nb: '😀'\nc: '😀' 2",
			"Expected a line break before the next entry, but found “2” at line 3, column 8\n\n  1 | a: 1\n  2 | b: '😀'\n> 3 | c: '😀' 2\n    |        ^"
		},
		{
			"a: '\t😀' x",
			"Expected a line break before the next entry, but found “x” at line 1, column 9\n\n> 1 | a: '\t😀' x\n    |     \t   ^"
		},
		{
			$"k: '{Emoji(53)}' x",
			$"Expected a line break before the next entry, but found “x” at line 1, column 60\n\n> 1 | …{Emoji(48)}' x\n    | {new string(' ', 51)}^"
		},
	};

	[Theory]
	[MemberData(nameof(CodeFramesAfterSurrogatePairs))]
	public void PointsTheCaretPastSurrogatePairsAsTheReferenceDoes(string text, string message)
	{
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(text));

		Assert.Equal(message, error.Message);
	}

	[Fact]
	public void PointsTheCaretPastASurrogatePairForInvalidUtf8()
	{
		byte[] bytes = [0x27, 0xF0, 0x9F, 0x98, 0x80, 0x27, 0x3A, 0x20, 0x31, 0xED, 0x0A];
		var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse(bytes));

		Assert.Equal("Invalid UTF-8 byte 0xED at line 1, column 7\n\n> 1 | '😀': 1\n    |       ^", error.Message);
	}

	[Fact]
	public void CodeFrameCaretMatchesTheColumn()
	{
		// Whatever the characters before the error, the caret is under the column: one space for each code point after the gutter.
		foreach (var prefix in new[] { "é", "😀", "😀😀", "日本", "a😀b" })
		{
			var error = Assert.Throws<SomlParseException>(() => SomlValue.Parse($"a: '{prefix}' x"));
			var caretLine = error.CodeFrame.Split('\n')[^1];

			Assert.Equal(error.Column - 1, caretLine.IndexOf('^', StringComparison.Ordinal) - "    | ".Length);
		}
	}

	public static TheoryData<byte[], string, int, int, int> InvalidUtf8 => new()
	{
		{ [0x61, 0x3A, 0x20, 0x27, 0xF0, 0x9F, 0x98, 0x80, 0xF0, 0x9F], "Incomplete UTF-8 sequence at the end of the input", 1, 6, 6 },
		{ [0x61, 0x3A, 0x20, 0x27, 0xED, 0xA0, 0x80, 0x27], "Invalid UTF-8 byte 0xED", 1, 5, 4 },
		{ [0x61, 0x3A, 0x20, 0x27, 0xF4, 0x90, 0x80, 0x80, 0x27], "Invalid UTF-8 byte 0xF4", 1, 5, 4 },
		{ [0x61, 0x3A, 0x20, 0x27, 0xE0, 0x80, 0x80, 0x27], "Invalid UTF-8 byte 0xE0", 1, 5, 4 },
		{ [0x61, 0x3A, 0x20, 0x27, 0xC3], "Incomplete UTF-8 sequence at the end of the input", 1, 5, 4 },
		{ [0x61, 0x3A, 0x20, 0x27, 0xF0, 0x9F, 0x98], "Incomplete UTF-8 sequence at the end of the input", 1, 5, 4 },
		{ [0x61, 0x3A, 0x20, 0x27, 0xED, 0xA0], "Invalid UTF-8 byte 0xED", 1, 5, 4 },
		{ [0x61, 0x3A, 0x20, 0x27, 0xF4, 0x90], "Invalid UTF-8 byte 0xF4", 1, 5, 4 },
		{ [0x61, 0x3A, 0x0A, 0x80], "Invalid UTF-8 byte 0x80", 2, 1, 3 },
		{ [0xEF, 0xBB, 0xBF, 0x61, 0x3A, 0x20, 0x31], "A byte order mark (BOM) is not allowed", 1, 1, 0 },
		{ [0x61, 0x3A, 0x20, 0x31, 0x0A, 0xEF, 0xBB, 0xBF], "Expected a key, but found U+FEFF (ZERO WIDTH NO-BREAK SPACE; only space, tab, and line feed are whitespace)", 2, 1, 5 },
		{ [0xEF, 0xBB], "Incomplete UTF-8 sequence at the end of the input", 1, 1, 0 },
		{ [], "A document must contain an object or an array, but this one is empty", 1, 1, 0 },
	};

	[Theory]
	[MemberData(nameof(InvalidUtf8))]
	public async Task RejectsInvalidUtf8AsTheReferenceDoes(byte[] bytes, string reason, int line, int column, int offset)
	{
		var fromSpan = Assert.Throws<SomlParseException>(() => SomlValue.Parse(bytes.AsSpan()));
		var fromStream = Assert.Throws<SomlParseException>(() => SomlValue.Parse(new MemoryStream(bytes)));
		var fromAsyncStream = await Assert.ThrowsAsync<SomlParseException>(async () => await SomlValue.ParseAsync(new MemoryStream(bytes), TestContext.Current.CancellationToken));

		foreach (var error in new[] { fromSpan, fromStream, fromAsyncStream })
		{
			Assert.Equal((reason, line, column, offset), (error.Reason, error.Line, error.Column, error.Offset));
		}
	}

	[Fact]
	public void ReadsUtf8AsItReadsText()
	{
		const string text = "a: 'é😀日本' # 😀\nb: [1, \"\\u{1f600}\"]\n";

		Assert.Equal(SomlValue.Parse(text), SomlValue.Parse(Encoding.UTF8.GetBytes(text)));
	}

	public static TheoryData<string, SomlPathSegment[], string> Removals => new()
	{
		{ "# top\na: 1 # c\n", ["a"], "# top\n{} # c\n" },
		{ "a: [1, /* note */ 2]\n", ["a", 1], "a: [1]\n" },
		{ "a: [1, 2, ]\n", ["a", 0], "a: [2, ]\n" },
		{ "a: [1]\n", ["a", 5], "a: [1]\n" },
		{ "a: 1\n", ["b", "c"], "a: 1\n" },
		{ "a: 1\n", ["b", 0], "a: 1\n" },
	};

	[Theory]
	[MemberData(nameof(Removals))]
	public void RemovesAsTheReferenceDoes(string text, SomlPathSegment[] path, string expected)
	{
		Assert.Equal(expected, SomlEditor.Remove(text, path, out var isRemoved));
		Assert.Equal(expected != text, isRemoved);
	}

	public static TheoryData<string, SomlPathSegment[], SomlValue, string> Sets => new()
	{
		{ "a: [1, 2] # c\n", ["a", 2], new SomlObject { ["x"] = "a\nb", ["y z"] = new SomlArray() }, "a: [1, 2, {x: \"a\\nb\", 'y z': []}] # c\n" },
		{ "a: [\n\t1 # one\n\t2 /* two */\n]\n", ["a", 2], "two\n\nlines\n", "a: [\n\t1 # one\n\t2 /* two */\n\t\"two\\n\\nlines\\n\"\n]\n" },
		{ "a: {\n\tb: 1\n} # c\n", ["a", "c", "d"], new SomlArray { new SomlArray(), new SomlArray { new SomlArray() } }, "a: {\n\tb: 1\n\tc: {\n\t\td: [\n\t\t\t[]\n\t\t\t[\n\t\t\t\t[]\n\t\t\t]\n\t\t]\n\t}\n} # c\n" },
		{ "{a: 1} # c\n", ["b"], "'''\n\"\"\"\n", "{a: 1, b: \"'''\\n\\\"\\\"\\\"\\n\"} # c\n" },
		{ "[\n\t# inside\n]\n", [0], SomlInstant.Parse("2026-09-19T14:00:00.5Z", null), "[\n\t# inside\n\t2026-09-19T14:00:00.5Z\n]\n" },
		{ "a:\n\t# comment\n\t1\n", ["a"], new SomlDuration(-5_400_000_000_001), "a:\n\t# comment\n\t-1h30m0.000000001s\n" },
		{ "a: '''\n\tblock\n\t'''\n", ["b"], "\ttab\n\tindent", "a: '''\n\tblock\n\t'''\nb: \"\\ttab\\n\\tindent\"\n" },
		{ "a: [1]\n", ["a", 1], 1, "a: [1, 1]\n" },
	};

	[Theory]
	[MemberData(nameof(Sets))]
	public void SetsAsTheReferenceDoes(string text, SomlPathSegment[] path, SomlValue value, string expected)
	{
		var edited = SomlEditor.Set(text, path, value);

		Assert.Equal(expected, edited);
		Assert.Equal(value, Lookup(SomlValue.Parse(edited), path));
	}

	private static SomlValue Lookup(SomlValue value, SomlPathSegment[] path)
	{
		foreach (var segment in path)
		{
			value = segment.IsKey ? value[segment.Key!] : value[segment.Index];
		}

		return value;
	}

	[Fact]
	public void RejectsEditsThatDoNotFitTheDocument()
	{
		Assert.Equal("Cannot edit a[2], because the array at a has 1 item. Add an item at index 1", Assert.Throws<SomlException>(() => SomlEditor.Set("a: [1]\n", ["a", 2], 1)).Message);
		Assert.Throws<SomlException>(() => SomlEditor.Remove("a: 1\n", ["a", "b"]));
		Assert.Throws<SomlException>(() => SomlEditor.Set("a: 1\n", ["b", 0], 1));
		Assert.Throws<SomlException>(() => SomlEditor.Set("a: 1\n", [0], 1));
		Assert.Throws<SomlException>(() => SomlEditor.Set("[1]\n", ["a"], 1));
		Assert.Throws<SomlException>(() => SomlEditor.Set("a: 1\n", ["x\ry"], 1));
		Assert.Throws<SomlException>(() => SomlEditor.Set("a: 1\n", [$"{(char)0xD800}"], 1));
		Assert.Throws<SomlException>(() => SomlEditor.Set("a: 1\n", ["a"], "x\ry"));
		Assert.Throws<SomlParseException>(() => SomlEditor.Set("", ["a"], 1));
		Assert.Throws<SomlParseException>(() => SomlEditor.Set("# only a comment", ["a"], 1));
		Assert.Throws<SomlParseException>(() => SomlEditor.Set("a: 1\r\n", ["a"], 2));
		Assert.Throws<ArgumentException>(() => SomlEditor.Set("a: 1\n", [], 1));
	}

	[Fact]
	public void SetsAValueAtTheDepthLimitOfADocumentWithoutBraces()
	{
		SomlValue deep = 1;

		// 99 arrays under the top-level object is 100 levels, the most a document may have.
		for (var level = 0; level < 99; level++)
		{
			deep = new SomlArray { deep };
		}

		var edited = SomlEditor.Set("a: 1\n", ["a"], deep);

		Assert.Equal(deep, SomlValue.Parse(edited)["a"]);
		Assert.Throws<SomlException>(() => SomlEditor.Set("a: 1\n", ["a"], new SomlArray { deep }));
	}

	private static (int ExitCode, byte[] Output, string Error) RunCli(string[] args, byte[] input)
	{
		using var output = new MemoryStream();
		using var error = new StringWriter();
		var exitCode = Program.Run(args, new MemoryStream(input), output, error);
		return (exitCode, output.ToArray(), error.ToString());
	}

	[Fact]
	public void ExitsWithTheDocumentedCodes()
	{
		Assert.Equal(0, RunCli(["--help"], []).ExitCode);
		Assert.Equal(0, RunCli(["decode"], "a: 1"u8.ToArray()).ExitCode);
		Assert.Equal(0, RunCli(["encode"], """{"a": {"type": "int", "value": "1"}}"""u8.ToArray()).ExitCode);
		Assert.Equal(1, RunCli(["decode"], [0xFF]).ExitCode);
		Assert.Equal(1, RunCli(["decode"], []).ExitCode);
		Assert.Equal(1, RunCli(["encode"], "not json"u8.ToArray()).ExitCode);
		Assert.Equal(1, RunCli(["encode"], """{"a": {"type": "string", "value": "x\ry"}}"""u8.ToArray()).ExitCode);
		Assert.Equal(64, RunCli([], []).ExitCode);
		Assert.Equal(64, RunCli(["decode", "extra"], []).ExitCode);
		Assert.Equal(64, RunCli(["format"], []).ExitCode);
	}

	/*
	What `AddJsonStream` gives for the same data in JSON, checked with Microsoft.Extensions.Configuration.Json 10.0.12.
	*/
	private static readonly Dictionary<string, string?> JsonProviderData = new(StringComparer.Ordinal)
	{
		[""] = "blank",
		["a:b"] = "1",
		["big"] = "1e21",
		["empty"] = null,
		["name"] = "api",
		["negative"] = "-1.25",
		["nested:deeper:empty"] = null,
		["nested:deeper:list:0"] = "",
		["nested:deeper:list:1"] = null,
		["nested:deeper:list:2:0"] = "1",
		["nested:deeper:list:2:1:0"] = "2",
		["nested:deeper:list:3"] = null,
		["nested:deeper:list:4"] = "",
		["nested:deeper:none"] = "",
		["none"] = "",
		["off"] = "False",
		["on"] = "True",
		["owner"] = null,
		["port"] = "8080",
		["ratio"] = "0.5",
		["x:"] = "2",
	};

	private static Dictionary<string, string?> ReadData(IConfigurationProvider provider)
	{
		var data = new Dictionary<string, string?>(StringComparer.Ordinal);
		Collect(null);
		return data;

		void Collect(string? parent)
		{
			foreach (var child in provider.GetChildKeys([], parent).Distinct())
			{
				var path = parent is null ? child : ConfigurationPath.Combine(parent, child);

				if (provider.TryGet(path, out var value))
				{
					data[path] = value;
				}

				Collect(path);
			}
		}
	}

	[Fact]
	public void FlattensEveryShapeAsTheJsonProviderDoes()
	{
		const string text = """
			name: 'api'
			port: 8080
			ratio: 0.5
			big: 1e21
			negative: -1.25
			on: true
			off: false
			owner: null
			empty: {}
			none: []
			nested: {deeper: {empty: {}, none: [], list: [[], {}, [1, [2]], null, '']}}
			'': 'blank'
			'a:b': 1
			x: {'': 2}
			""";
		var provider = new SomlStreamConfigurationProvider(new SomlStreamConfigurationSource());
		provider.Load(new MemoryStream(Encoding.UTF8.GetBytes(text)));

		Assert.Equal(JsonProviderData, ReadData(provider));
	}

	[Fact]
	public void ReloadsAFile()
	{
		var directory = Directory.CreateTempSubdirectory();

		try
		{
			var path = Path.Combine(directory.FullName, "app.soml");
			File.WriteAllText(path, "a: {b: 1}\nold: true");
			var configuration = new ConfigurationBuilder().SetBasePath(directory.FullName).AddSomlFile("app.soml", optional: false, reloadOnChange: false).Build();

			Assert.Equal("1", configuration["a:b"]);

			File.WriteAllText(path, "a: {b: 2}\nlist: [1h30m, 1ns, 2026-09-19T16:00:00+02:00]");
			configuration.Reload();

			Assert.Equal("2", configuration["a:b"]);
			Assert.Null(configuration["old"]);
			Assert.Equal(TimeSpan.FromMinutes(90), configuration.GetValue<TimeSpan>("list:0"));
			Assert.Equal("0.000000001s", configuration["list:1"]);
			Assert.Equal(new DateTimeOffset(2026, 9, 19, 14, 0, 0, TimeSpan.Zero), configuration.GetValue<DateTimeOffset>("list:2"));

			File.WriteAllText(path, "a: 3\nA: 1");
			var error = Assert.Throws<InvalidDataException>(configuration.Reload);

			Assert.IsType<FormatException>(error.InnerException);
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	/*
	An error 100 levels deep in objects passed through a `catch` and a `throw` at every level, and a `catch` runs on top of the frames of the throw, so the stack grew by a whole exception dispatch per level. That took 768 KB, so on a thread with a smaller stack, as some hosts and native threads have, the document crashed the process with a stack overflow, which cannot be caught.
	*/
	[Fact]
	public void ReportsAnErrorAtTheDepthLimitOnASmallStack()
	{
		static string Repeat(string text, int count) => string.Concat(Enumerable.Repeat(text, count));
		var documents = new[]
		{
			"a:\n" + Repeat("{\na:\n", 99) + "x\n" + Repeat("}\n", 99),
			"a: " + Repeat("{a: ", 99) + "x" + Repeat("}", 99),
			"a: " + Repeat("{a: ", 100) + "1" + Repeat("}", 100),
		};

		var reasons = new List<string>();
		Exception? unexpected = null;

		var thread = new Thread(() =>
		{
			try
			{
				foreach (var document in documents)
				{
					reasons.Add(Assert.Throws<SomlParseException>(() => SomlValue.Parse(document)).Reason);
					Assert.Throws<SomlParseException>(() => SomlFormatter.Format(document));
					Assert.Throws<SomlParseException>(() => SomlEditor.Set(document, ["a"], 1));
					Assert.Throws<SomlParseException>(() => SomlSerializer.Deserialize<SomlValue>(document));
				}
			}
			catch (Exception exception)
			{
				unexpected = exception;
			}
		}, maxStackSize: 512 * 1024);

		thread.Start();
		thread.Join();

		Assert.Null(unexpected);
		Assert.Equal(["Unexpected “x”. A string value must be quoted, as in 'x'", "Unexpected “x”. A string value must be quoted, as in 'x'", "The document is nested more than 100 levels deep"], reasons);
	}

	/*
	A block string is read in one pass, so these check that the indentation of every line is still checked before any escape, and that only the blank lines between content lines are kept.
	*/
	[Fact]
	public void ChecksABlockStringAsTheReferenceDoes()
	{
		Assert.Equal("This line does not start with the indentation of its block string's closing delimiter. Every line except a blank one must start with exactly the same spaces and tabs at line 3, column 1\n\n  1 | a: \"\"\"\n  2 | \t\\q\n> 3 |   x\n    | ^", Assert.Throws<SomlParseException>(() => SomlValue.Parse("a: \"\"\"\n\t\\q\n  x\n\t\"\"\"\n")).Message);
		Assert.Equal("Unknown escape “\\q”. The escapes are \\\\, \\\", \\n, \\t, and \\u{…}; use a '...' string for literal backslashes at line 3, column 2\n\n  1 | a: \"\"\"\n  2 |\n> 3 | \t\\q\n    | \t^", Assert.Throws<SomlParseException>(() => SomlFormatter.Format("a: \"\"\"\n\n\t\\q\n\n\t\\r\n\n\t\"\"\"\n")).Message);
		Assert.Equal("x\n\n\ny", SomlValue.Parse("a: '''\n\n\tx\n\n\n\ty\n\n\t'''\n")["a"].GetString());
	}

	/*
	The code frame sanitizes only the part of a line that it shows, so an error on a long line copies none of the line.
	*/
	[Fact]
	public void BuildsTheCodeFrameOfALongLineWithoutCopyingIt()
	{
		var text = $"x: 1\na: ['{(char)0x202E}', {string.Concat(Enumerable.Repeat("1, ", 300_000))}x]\ny: 2";
		// Warm up, so that one-time allocations are not counted.
		SomlFormatter.Format("a: 1");

		var allocated = GC.GetAllocatedBytesForCurrentThread();
		var error = Assert.Throws<SomlParseException>(() => SomlFormatter.Format(text));
		allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;

		Assert.Equal("Unexpected “x”. A string value must be quoted, as in 'x'", error.Reason);
		Assert.True(allocated < text.Length, $"Allocated {allocated} bytes");
	}

	[Fact]
	public void LoadsNothingForAMissingOptionalFile()
	{
		var directory = Directory.CreateTempSubdirectory();

		try
		{
			var configuration = new ConfigurationBuilder().SetBasePath(directory.FullName).AddSomlFile("missing.soml", optional: true, reloadOnChange: false).Build();

			Assert.Empty(configuration.AsEnumerable());
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}
}
