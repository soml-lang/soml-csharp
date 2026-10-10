using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Perfolizer.Horology;
using Soml;
using Tomlyn;
using Tomlyn.Model;

/*
Reads and writes the shared fixtures of the SOML repository (`Fixtures`, a copy of `soml/benchmark`), which hold the same data in SOML, JSON, and TOML. Each library parses the text into its own document model, and writes that model back to text, so the formats are compared on the same data without a typed model, whose shape would differ between them.

`--cpu` measures the CPU time of the process instead of the wall-clock time, for a machine that is busy with other work.
*/
var config = DefaultConfig.Instance.WithOptions(ConfigOptions.JoinSummary);

// `--check` runs each case once, to see that every library reads and writes every fixture.
if (args.Contains("--check"))
{
	foreach (var fixture in Fixture.All)
	{
		var read = new Read { Fixture = fixture };
		read.Setup();
		var write = new Write { Fixture = fixture };
		write.Setup();
		Console.WriteLine($"{fixture}: read {read.Soml().Kind}, {read.SystemTextJson()}, {read.NewtonsoftJson().Type}, {read.Tomlyn().Count} members; wrote {write.Soml().Length}, {write.SystemTextJson().Length}, {write.NewtonsoftJson().Length}, {write.Tomlyn().Length} characters");
		write.Cleanup();
	}

	return;
}

if (args.Contains("--cpu"))
{
	// A clock instance cannot be passed to a child process, so the benchmarks run in this one.
	config = config.AddJob(Job.Default.WithToolchain(InProcessEmitToolchain.Instance).WithClock(new ProcessCpuClock()).WithWarmupCount(5).WithIterationCount(15));
	args = [.. args.Where(argument => argument != "--cpu")];
}

BenchmarkSwitcher.FromAssembly(typeof(Fixture).Assembly).Run(args, config);

public sealed record Fixture(string Name)
{
	public static IEnumerable<Fixture> All { get; } = [new("service"), new("platform"), new("earthquakes")];

	public string Read(string extension) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", $"{Name}.{extension}"));

	public override string ToString() => Name;
}

[MemoryDiagnoser]
public class Read
{
	private string _soml = "";
	private string _json = "";
	private string _toml = "";

	[ParamsSource(nameof(Fixtures))]
	public Fixture Fixture { get; set; } = Fixture.All.First();

	public static IEnumerable<Fixture> Fixtures => Fixture.All;

	[GlobalSetup]
	public void Setup()
	{
		_soml = Fixture.Read("soml");
		_json = Fixture.Read("json");
		_toml = Fixture.Read("toml");
	}

	[Benchmark(Baseline = true)]
	public SomlValue Soml() => SomlValue.Parse(_soml);

	// `JsonDocument` validates the whole document, but creates a string only when it is read, which makes it faster than a model that holds its values.
	[Benchmark]
	public JsonValueKind SystemTextJson()
	{
		using var document = JsonDocument.Parse(_json);
		return document.RootElement.ValueKind;
	}

	[Benchmark]
	public JToken NewtonsoftJson() => JToken.Parse(_json);

	[Benchmark]
	public TomlTable Tomlyn() => TomlSerializer.Deserialize<TomlTable>(_toml)!;
}

[MemoryDiagnoser]
public class Write
{
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	private SomlValue _soml;
	private JsonDocument? _json;
	private JToken? _newtonsoft;
	private TomlTable? _toml;

	[ParamsSource(nameof(Fixtures))]
	public Fixture Fixture { get; set; } = Fixture.All.First();

	public static IEnumerable<Fixture> Fixtures => Fixture.All;

	[GlobalSetup]
	public void Setup()
	{
		_soml = SomlValue.Parse(Fixture.Read("soml"));
		_json = JsonDocument.Parse(Fixture.Read("json"));
		_newtonsoft = JToken.Parse(Fixture.Read("json"));
		_toml = TomlSerializer.Deserialize<TomlTable>(Fixture.Read("toml"));
	}

	[GlobalCleanup]
	public void Cleanup() => _json?.Dispose();

	[Benchmark(Baseline = true)]
	public string Soml() => _soml.ToDocumentString();

	[Benchmark]
	public string SystemTextJson() => System.Text.Json.JsonSerializer.Serialize(_json!.RootElement, Indented);

	[Benchmark]
	public string NewtonsoftJson() => _newtonsoft!.ToString(Formatting.Indented);

	[Benchmark]
	public string Tomlyn() => TomlSerializer.Serialize(_toml!);
}

[MemoryDiagnoser]
public class ObjectLookup
{
	private const int LookupsPerInvoke = 64;
	private SomlObject _object = new();
	private string _key = "";

	[Params(4, 16, 64)]
	public int Count { get; set; }

	[Params("first", "last", "missing")]
	public string Position { get; set; } = "";

	[GlobalSetup]
	public void Setup()
	{
		_object = new SomlObject(Count);

		for (var index = 0; index < Count; index++)
		{
			_object.Add($"key{index}", index);
		}

		_key = Position switch
		{
			"first" => "key0",
			"last" => $"key{Count - 1}",
			_ => "missing",
		};

		// Build the lazy index before measuring lookups.
		_object.IndexOf(_key);
	}

	[Benchmark(OperationsPerInvoke = LookupsPerInvoke)]
	public int Find()
	{
		// Batch lookups so the CPU clock can measure the first pilot iteration.
		var result = 0;

		for (var index = 0; index < LookupsPerInvoke; index++)
		{
			result += _object.IndexOf(_key);
		}

		return result;
	}
}

[MemoryDiagnoser]
public class ScalarReading
{
	private string _document = "";

	[Params("2024-02-29T12:30:59.123456789-23:59", "1h2m3s4ms5us6ns", "true,false,null,infinity,-infinity", "0x1F90")]
	public string Text { get; set; } = "";

	[GlobalSetup]
	public void Setup() => _document = "[" + string.Join(',', Enumerable.Repeat(Text, 32)) + "]";

	[Benchmark]
	public SomlValue Read() => SomlValue.Parse(_document);
}

[MemoryDiagnoser]
public class ConverterCreation
{
	private static readonly SomlValue Items = SomlValue.Parse("[1,2,3,4,5,6,7,8]");
	private static readonly SomlValue Members = SomlValue.Parse("name: 'test'\ncount: 3\nactive: true");

	[Benchmark]
	public List<int> List() => SomlSerializer.Deserialize<List<int>>(Items, new SomlSerializerOptions());

	[Benchmark]
	public Settings Object() => SomlSerializer.Deserialize<Settings>(Members, new SomlSerializerOptions { RejectUnknownKeys = true });

	public sealed record Settings(string Name, int Count, bool Active);
}

[MemoryDiagnoser]
public class StringReading
{
	private string _document = "";

	[Params("'abc'", "\"a\\nb\"", "\"\\u{ab}\\u{1f600}\"")]
	public string Text { get; set; } = "";

	[Params(1, 64)]
	public int Count { get; set; }

	[GlobalSetup]
	public void Setup() => _document = "[" + string.Join(',', Enumerable.Repeat(Text, Count)) + "]";

	[Benchmark]
	public SomlValue Read() => SomlValue.Parse(_document);
}

[MemoryDiagnoser]
public class StringWriting
{
	private SomlValue _value;

	[Params("abc", "a'b", "a\0b", "a😀b")]
	public string Text { get; set; } = "";

	[GlobalSetup]
	public void Setup() => _value = string.Concat(Enumerable.Repeat(Text, 256));

	[Benchmark]
	public string Write() => _value.ToString();
}

[MemoryDiagnoser]
public class ObjectEdits
{
	private SomlObject _members = new();

	[Params(16, 256, 4096)]
	public int Count { get; set; }

	[GlobalSetup]
	public void Setup()
	{
		_members = new SomlObject(Count + 1);

		for (var index = 0; index < Count; index++)
		{
			_members.Add($"key{index}", index);
		}
	}

	[Benchmark]
	public void First() => InsertAndRemove(0);

	[Benchmark]
	public void Middle() => InsertAndRemove(Count / 2);

	[Benchmark]
	public void Last() => InsertAndRemove(Count);

	private void InsertAndRemove(int index)
	{
		_members.Insert(index, "temporary", 42);
		_members.RemoveAt(index);
	}
}

/*
The CPU time of all threads of the process, including the garbage collector, in 100-nanosecond ticks.
*/
internal sealed class ProcessCpuClock : IClock
{
	public string Title => "Process CPU time";

	public bool IsAvailable => true;

	public Frequency Frequency => new(TimeSpan.TicksPerSecond);

	public long GetTimestamp() => Environment.CpuUsage.TotalTime.Ticks;
}
