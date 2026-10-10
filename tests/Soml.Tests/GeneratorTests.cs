using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using Soml.Serialization;

namespace Soml.Tests;

[SomlSerializable(typeof(SerializerTests.Server))]
[SomlSerializable(typeof(SerializerTests.Settings))]
[SomlSerializable(typeof(SerializerTests.RequiredSettings))]
[SomlSerializable(typeof(SerializerTests.Renamed))]
[SomlSerializable(typeof(SerializerTests.Node))]
[SomlSerializable(typeof(SerializerTests.Point))]
[SomlSerializable(typeof(RegressionTests.Collections))]
[SomlSerializable(typeof(RegressionTests.WithDefaultArray))]
[SomlSerializable(typeof(RegressionTests.WithClashingKeys))]
[SomlSerializable(typeof(RegressionTests.ThrowingSetter))]
[SomlSerializable(typeof(RegressionTests.Outer))]
[SomlSerializable(typeof(RegressionTests.WithReadOnlyMembers))]
[SomlSerializable(typeof(RegressionTests.WithAnyValue))]
[SomlSerializable(typeof(RegressionTests.WithIgnoredParameter))]
[SomlSerializable(typeof(RegressionTests.WithIgnoredRequiredParameter))]
[SomlSerializable(typeof(RegressionTests.Numbers))]
[SomlSerializable(typeof(RegressionTests.WithSequence))]
[SomlSerializable(typeof(RegressionTests.WithLazySequence))]
[SomlSerializable(typeof(Dictionary<string, RegressionTests.LargeFlags>))]
[SomlSerializable(typeof(Dictionary<string, RegressionTests.Throwing>))]
[SomlSerializable(typeof(ReadmeTests.Config))]
[SomlSerializable(typeof(GeneratorTests.Everything))]
[SomlSerializable(typeof(GeneratorTests.Box<int>))]
[SomlSerializable(typeof(GeneratorTests.Box<string>), TypeInfoPropertyName = "StringBox")]
[SomlSerializable(typeof(GeneratorTests.InitStruct))]
[SomlSerializable(typeof(GeneratorTests.Derived))]
[SomlSerializable(typeof(GeneratorTests.WithDefaults))]
[SomlSerializable(typeof(GeneratorTests.RequiredWithConstructor))]
[SomlSerializable(typeof(GeneratorTests.SetsRequired))]
#pragma warning disable SOML004
[SomlSerializable(typeof(GeneratorTests.Ambiguous))]
#pragma warning restore SOML004
[SomlSerializable(typeof(GeneratorTests.Position))]
[SomlSerializable(typeof(GeneratorTests.Everything[]))]
[SomlSerializable(typeof(List<int>))]
[SomlSerializable(typeof(GeneratorTests.ChildInit))]
[SomlSerializable(typeof(GeneratorTests.GenericChild))]
[SomlSerializable(typeof(GeneratorTests.RequiredDerived))]
[SomlSerializable(typeof(GeneratorTests.WithInheritedAdd))]
[SomlSerializable(typeof(GeneratorTests.InternalConfig))]
[SomlSerializable(typeof(FactoryItems<int>))]
[SomlSerializable(typeof(SomlValue))]
public partial class GeneratorContext : SomlSerializerContext;

/*
Each case runs through the reflection path and the generated path, which must give the same text, or the same error at the same place.
*/
public sealed class GeneratorTests
{
	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void UsesTheGenericCollectionFactoryInsteadOfANongenericOverload(bool generated)
	{
		var value = generated ? SomlSerializer.Deserialize("[1, 2, 3]", GeneratorContext.Default.FactoryItemsInt32) : SomlSerializer.Deserialize<FactoryItems<int>>("[1, 2, 3]");

		Assert.Equal([1, 2, 3], value);
		Assert.Equal("[\n\t1\n\t2\n\t3\n]\n", generated ? SomlSerializer.Serialize(value, GeneratorContext.Default.FactoryItemsInt32) : SomlSerializer.Serialize(value));
	}

	public enum Color
	{
		Red,
		DarkBlue,
		[SomlName("lime")]
		Green,
	}

	public sealed class Everything
	{
		public string Text { get; set; } = "";

		public string? MaybeText { get; set; }

		public bool Flag { get; set; }

		public char Letter { get; set; } = 'a';

		public byte Byte { get; set; }

		public short Short { get; set; }

		public uint UInt { get; set; }

		public long Long { get; set; }

		public ulong ULong { get; set; }

		public Int128 Huge { get; set; }

		public float Single { get; set; }

		public double Double { get; set; }

		public decimal Decimal { get; set; }

		public Half Half { get; set; }

		public int? MaybeNumber { get; set; }

		public Color Color { get; set; }

		public Color? MaybeColor { get; set; }

		public SerializerTests.Permissions Permissions { get; set; }

		public DateTimeOffset When { get; set; }

		public DateTime Local { get; set; }

		public DateOnly Day { get; set; }

		public TimeOnly Time { get; set; }

		public TimeSpan Span { get; set; }

		public SomlInstant Instant { get; set; }

		public SomlDuration Duration { get; set; }

		public Uri? Link { get; set; }

		public Version? Version { get; set; }

		public Guid Id { get; set; }

		public int[][] Jagged { get; set; } = [];

		public List<int?> Numbers { get; set; } = [];

		public HashSet<string> Tags { get; set; } = [];

		public ISet<int>? Set { get; set; }

		public IReadOnlySet<int>? ReadOnlySet { get; set; }

		public SortedSet<int>? Sorted { get; set; }

		public LinkedList<int>? Linked { get; set; }

		public ICollection<string>? Collection { get; set; }

		public IReadOnlyDictionary<string, List<string>>? Groups { get; set; }

		public IDictionary<string, double>? Weights { get; set; }

		public SortedList<string, int>? SortedList { get; set; }

		public ReadOnlyDictionary<string, int>? ReadOnlyDictionary { get; set; }

		public ImmutableSortedSet<string>? ImmutableSorted { get; set; }

		public FrozenSet<int>? Frozen { get; set; }

		public SomlObject? Raw { get; set; }

		public SomlArray? RawArray { get; set; }

		public Everything? Next;

		public readonly int ReadOnlyField = 3;

		public const int Constant = 4;

		public static int Static { get; set; }
	}

	public sealed class Box<T>
	{
		public required T Value { get; init; }

		public T? Other { get; init; }
	}

	public struct InitStruct
	{
		public int A { get; init; }

		public string? B { get; init; }
	}

	public class Base
	{
		public int Id { get; set; }

		public virtual string Name { get; set; } = "";
	}

	public sealed class Derived : Base
	{
		public override string Name { get; set; } = "derived";

		public int Extra { get; set; }
	}

	public sealed class WithDefaults
	{
		[SomlConstructor]
		public WithDefaults(Color color = Color.DarkBlue, decimal price = 1.5m, double ratio = double.NaN, float scale = 0.25f, string name = "a\t\"b\"", char letter = '\'', long big = long.MinValue, ulong positive = ulong.MaxValue, int? maybe = null, string? none = null, bool flag = true, Color? maybeColor = Color.Green)
		{
			Color = color;
			Price = price;
			Ratio = ratio;
			Scale = scale;
			Name = name;
			Letter = letter;
			Big = big;
			Positive = positive;
			Maybe = maybe;
			None = none;
			Flag = flag;
			MaybeColor = maybeColor;
		}

		public WithDefaults(int unused)
		{
			Name = unused.ToString(System.Globalization.CultureInfo.InvariantCulture);
		}

		public Color Color { get; }

		public decimal Price { get; }

		public double Ratio { get; }

		public float Scale { get; }

		public string Name { get; }

		public char Letter { get; }

		public long Big { get; }

		public ulong Positive { get; }

		public int? Maybe { get; }

		public string? None { get; }

		public bool Flag { get; }

		public Color? MaybeColor { get; }
	}

	public sealed class RequiredWithConstructor(string host)
	{
		public string Host { get; } = host;

		public required int Port { get; init; }
	}

	public sealed class SetsRequired
	{
		[SetsRequiredMembers]
		public SetsRequired()
		{
			Name = "set";
		}

		public required string Name { get; set; }
	}

	public sealed class Ambiguous
	{
		public Ambiguous(int a)
		{
			A = a;
		}

		public Ambiguous(string b)
		{
			A = b.Length;
		}

		public int A { get; set; }
	}

	public readonly record struct Position(int Line, int Column, string? File = null);

	public class InitBase
	{
		public int X { get; init; }

		public required string Name { get; init; }
	}

	public sealed class ChildInit : InitBase
	{
		public int Y { get; init; }
	}

	public class GenericBase<T>
	{
		public T? Value { get; init; }
	}

	public sealed class GenericChild : GenericBase<string>
	{
		public int Count { get; init; }
	}

	public class RequiredBase
	{
		public required int A { get; set; }
	}

	public sealed class RequiredDerived(string s) : RequiredBase
	{
		public string S { get; } = s;
	}

	public sealed class Tags : List<string>;

	public sealed class Counts : Dictionary<string, int>;

	public sealed class WithInheritedAdd
	{
		public Tags Tags { get; set; } = [];

		public Counts Counts { get; set; } = [];
	}

	internal sealed class InternalConfig
	{
		public int A { get; set; }
	}

	private static string Outcome(Func<string> action)
	{
		try
		{
			return action();
		}
		catch (SomlException exception)
		{
			return $"error: {exception.Message} [{exception.Path}] {exception.InnerException?.GetType().Name}";
		}
	}

	private static void AssertReadsTheSame<T>(Func<GeneratorContext, SomlTypeInfo<T>> select, string text, SomlSerializerOptions? options = null)
	{
		var context = options is null ? GeneratorContext.Default : new GeneratorContext(options);
		var reflection = Outcome(() => SomlSerializer.Serialize(SomlSerializer.Deserialize<T>(text, options), options));
		var generated = Outcome(() => SomlSerializer.Serialize(SomlSerializer.Deserialize(text, select(context)), select(context)));
		Assert.Equal(reflection, generated);
	}

	private static void AssertWritesTheSame<T>(Func<GeneratorContext, SomlTypeInfo<T>> select, T value, SomlSerializerOptions? options = null)
	{
		var context = options is null ? GeneratorContext.Default : new GeneratorContext(options);
		var reflection = Outcome(() => SomlSerializer.Serialize(value, options));
		var generated = Outcome(() => SomlSerializer.Serialize(value, select(context)));
		Assert.Equal(reflection, generated);
	}

	private static readonly SomlSerializerOptions Strict = new() { RejectUnknownKeys = true };

	private static readonly SomlSerializerOptions OmitNull = new() { OmitNullMembers = true };

	private static readonly SomlSerializerOptions SnakeCase = new() { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.SnakeCaseLower };

	public static TheoryData<string> EverythingDocuments =>
	[
		"",
		"{}",
		"text: 'a'\nmaybe-text: null\nflag: true\nletter: 'z'\nbyte: 255\nshort: -3\nu-int: 7\nlong: -9\nu-long: 18446744073709551615\nhuge: 170141183460469231731687303715884105727\nsingle: 0.1\ndouble: 1e300\ndecimal: 0.30\nhalf: 0.5\nmaybe-number: 3\ncolor: 'dark-blue'\nmaybe-color: 'lime'\npermissions: ['read', 'write']",
		"when: 2026-10-10T12:00:00+02:00\nlocal: 2026-10-10T12:00:00Z\nday: '2026-10-10'\ntime: '12:30:00'\nspan: 1h2m\ninstant: 2026-10-10T00:00:00.123456789Z\nduration: 3ns\nlink: 'https://example.com/a'\nversion: '1.2.3'\nid: '7d444840-9dc0-11d1-b245-5ffdce74fad2'",
		"jagged: [[1, 2], [], [3]]\nnumbers: [1, null, 3]\ntags: ['a', 'b']\nset: [1, 2]\nread-only-set: [3]\nsorted: [9, 1]\nlinked: [1, 2]\ncollection: ['x']\ngroups: {a: ['b']}\nweights: {w: 0.5}\nsorted-list: {b: 2, a: 1}\nread-only-dictionary: {k: 1}\nimmutable-sorted: ['z', 'y']\nfrozen: [5]\nraw: {a: [1, {b: null}]}\nraw-array: [1, 'a']\nnext: {text: 'inner', next: {flag: true}}",
		"tags: ['a', 'a']",
		"set: [1, 1]",
		"numbers: [1, 'x']",
		"byte: 256",
		"letter: 'ab'",
		"color: 'purple'",
		"color: 'DarkBlue'",
		"permissions: ['read', 'nope']",
		"text: null",
		"maybe-number: null\nmaybe-color: null\nlink: null",
		"jagged: [[1], null]",
		"groups: {a: null}",
		"next: 1",
		"day: 'not a day'",
		"single: 1e300",
		"decimal: 1e-30",
		"huge: 1e3",
		"read-only-field: 5\nconstant: 1\nstatic: 2",
		"version: 3",
		"raw: []",
		"[1]",
		"1",
	];

	[Theory]
	[MemberData(nameof(EverythingDocuments))]
	public void ReadsEverythingLikeTheReflectionPath(string text)
	{
		AssertReadsTheSame(context => context.Everything, text);
		AssertReadsTheSame(context => context.Everything, text, Strict);
		AssertReadsTheSame(context => context.Everything, text, OmitNull);
		AssertReadsTheSame(context => context.Everything, text, SnakeCase);
		AssertReadsTheSame(context => context.EverythingArray, $"[{{{text.Replace("\n", ", ", StringComparison.Ordinal)}}}]");
	}

	public static TheoryData<string> ModelDocuments =>
	[
		"host: 'a'\nport: 1\ntimeout: 1s\nallowed-origins: []",
		"host: 'a'\nport: 1\ntimeout: 1s",
		"host: 'a'\nport: 'x'\ntimeout: 1s\nallowed-origins: []",
		"host: 'a'\nprt: 1\ntimeout: 1s\nallowed-origins: []",
	];

	[Theory]
	[MemberData(nameof(ModelDocuments))]
	public void ReadsARecordLikeTheReflectionPath(string text)
	{
		AssertReadsTheSame(context => context.Server, text);
		AssertReadsTheSame(context => context.Server, text, Strict);
	}

	[Fact]
	public void ReadsTheModelsOfTheOtherTestsLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.Settings, "name: 'x'\nreplicas: 2\nratio: 0.5\nowner: null\nlevel: 'very-verbose'\npermissions: ['read', 'execute']\nlimits: {a: 1}");
		AssertReadsTheSame(context => context.Settings, "level: 'loud'");
		AssertReadsTheSame(context => context.Settings, "nme: 'x'", Strict);
		AssertReadsTheSame(context => context.RequiredSettings, "name: 'x'");
		AssertReadsTheSame(context => context.RequiredSettings, "port: 1");
		AssertReadsTheSame(context => context.Renamed, "listen-on: 1\nthe host: 'h'");
		AssertReadsTheSame(context => context.Renamed, "port: 1\nhost: 'h'", Strict);
		AssertReadsTheSame(context => context.Node, "name: 'a'\nchildren: [{name: 'b', children: [{name: 'c'}]}]\nparent: {name: 'p'}");
		AssertReadsTheSame(context => context.Point, "x: 1\ny: 2");
		AssertReadsTheSame(context => context.Collections, "array: [1, 2]\nlist: ['a']\nset: [3]\ndictionary: {a: 1}\nread-only: [4, 5]\nfrozen: ['x']\nfrozen-dictionary: {b: 2}\nsorted: {d: 4, c: 3}\nqueue: [6, 7]");
		AssertReadsTheSame(context => context.WithDefaultArray, "{}");
		AssertReadsTheSame(context => context.WithClashingKeys, "url: 'a'");
		AssertReadsTheSame(context => context.ThrowingSetter, "port: 1");
		AssertReadsTheSame(context => context.Outer, "inner: 'x'");
		AssertReadsTheSame(context => context.Outer, "items: 'x'");
		AssertReadsTheSame(context => context.WithReadOnlyMembers, "a: 3\ndouble: 100\nprivate: 9");
		AssertReadsTheSame(context => context.WithReadOnlyMembers, "a: 3\ndouble: 100\nprivate: 9", Strict);
		AssertReadsTheSame(context => context.WithAnyValue, "value: null\nitems: [null, 1]");
		AssertReadsTheSame(context => context.WithIgnoredParameter, "a: 1\nb: 5");
		AssertReadsTheSame(context => context.WithIgnoredParameter, "a: 1\nb: 5", Strict);
		AssertReadsTheSame(context => context.WithIgnoredRequiredParameter, "a: 1");
		AssertReadsTheSame(context => context.Numbers, "integer: 1\nfloat: 2.5\nduration: 1h");
		AssertReadsTheSame(context => context.WithSequence, "items: [1, 2]");
		AssertReadsTheSame(context => context.DictionaryStringLargeFlags, "a: ['small', 'big']");
		AssertReadsTheSame(context => context.DictionaryStringThrowing, "a: 'x'");
		AssertReadsTheSame(context => context.Config, "name: 'web'\nreplicas: 3\ntimeout: 30s\ndeployed-at: 2026-01-01T00:00:00Z\nlabels: ['a']\npostgres: {host: 'db', port: 5432}");
		AssertReadsTheSame(context => context.Config, "name: 'web'\nreplicas: 3\ntimeout: 30s\ndeployed-at: 2026-01-01T00:00:00Z\nlabels: ['a']\npostgres: {host: 'db', port: 70000}");
		AssertReadsTheSame(context => context.ListInt32, "[1, 2]");
		AssertReadsTheSame(context => context.SomlValue, "a: {b: [1]}");
	}

	[Fact]
	public void ReadsTheGeneratorCasesLikeTheReflectionPath()
	{
		AssertReadsTheSame(context => context.BoxInt32, "value: 1\nother: 2");
		AssertReadsTheSame(context => context.BoxInt32, "other: 2");
		AssertReadsTheSame(context => context.StringBox, "value: 'a'");
		AssertReadsTheSame(context => context.StringBox, "value: null");
		AssertReadsTheSame(context => context.InitStruct, "a: 1\nb: 'x'");
		AssertReadsTheSame(context => context.Derived, "id: 1\nname: 'n'\nextra: 2");
		AssertReadsTheSame(context => context.Derived, "{}");
		AssertReadsTheSame(context => context.WithDefaults, "{}");
		AssertReadsTheSame(context => context.WithDefaults, "color: 'red'\nprice: 2\nname: 'x'\nmaybe: 1\nmaybe-color: null");
		AssertReadsTheSame(context => context.WithDefaults, "unused: 1", Strict);
		AssertReadsTheSame(context => context.RequiredWithConstructor, "host: 'h'\nport: 1");
		AssertReadsTheSame(context => context.RequiredWithConstructor, "host: 'h'");
		AssertReadsTheSame(context => context.RequiredWithConstructor, "port: 1");
		AssertReadsTheSame(context => context.SetsRequired, "{}");
		AssertReadsTheSame(context => context.SetsRequired, "name: 'x'");
		AssertReadsTheSame(context => context.Ambiguous, "a: 1");
		AssertReadsTheSame(context => context.Position, "line: 1\ncolumn: 2");
		AssertReadsTheSame(context => context.Position, "line: 1");
		AssertReadsTheSame(context => context.Position, "line: 1\ncolumn: 2\nfile: null", OmitNull);
		AssertReadsTheSame(context => context.ChildInit, "x: 1\ny: 2\nname: 'n'");
		AssertReadsTheSame(context => context.ChildInit, "x: 1\ny: 2");
		AssertReadsTheSame(context => context.GenericChild, "value: 'v'\ncount: 2");
		AssertReadsTheSame(context => context.RequiredDerived, "s: 'x'");
		AssertReadsTheSame(context => context.RequiredDerived, "s: 'x'\na: 1");
		AssertReadsTheSame(context => context.WithInheritedAdd, "tags: ['a', 'b']\ncounts: {a: 1}");
		AssertReadsTheSame(context => context.InternalConfig, "a: 1");
	}

	[Fact]
	public void WritesLikeTheReflectionPath()
	{
		var node = new SerializerTests.Node { Name = "a" };
		node.Children.Add(new SerializerTests.Node { Name = "b", Parent = node });
		AssertWritesTheSame(context => context.Node, node);
		AssertWritesTheSame(context => context.Settings, new SerializerTests.Settings(), OmitNull);
		AssertWritesTheSame(context => context.Settings, new SerializerTests.Settings { Permissions = (SerializerTests.Permissions)64 });
		AssertWritesTheSame(context => context.Settings, new SerializerTests.Settings { Level = (SerializerTests.LogLevel)9 });
		AssertWritesTheSame(context => context.WithDefaultArray, new RegressionTests.WithDefaultArray());
		AssertWritesTheSame(context => context.WithClashingKeys, new RegressionTests.WithClashingKeys());
		AssertWritesTheSame(context => context.WithLazySequence, new RegressionTests.WithLazySequence());
		AssertWritesTheSame(context => context.WithSequence, new RegressionTests.WithSequence(new[] { 1, 2, 3 }.Where(item => item > 1)));
		AssertWritesTheSame(context => context.Everything, new Everything { Double = double.NaN });
		AssertWritesTheSame(context => context.Everything, new Everything { Single = float.PositiveInfinity, MaybeText = "x", Numbers = [null] }, OmitNull);
		AssertWritesTheSame(context => context.Everything, new Everything { Local = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) });
		AssertWritesTheSame(context => context.Everything, new Everything { Collection = new List<string> { "a" }, Set = new SortedSet<int> { 2, 1 } });
		AssertWritesTheSame(context => context.Everything, new Everything { Jagged = [null!] });
		AssertWritesTheSame(context => context.Everything, new Everything { Text = null! });
		AssertWritesTheSame(context => context.WithDefaults, new WithDefaults());
		AssertWritesTheSame(context => context.Position, new Position(1, 2), OmitNull);
		AssertWritesTheSame(context => context.Position, new Position(1, 2), SnakeCase);
		AssertWritesTheSame(context => context.InitStruct, new InitStruct { A = 1 });
		AssertWritesTheSame(context => context.SomlValue, SomlValue.Null);
		AssertWritesTheSame(context => context.EverythingArray, [new Everything(), null!]);
	}

	[Fact]
	public void RequiresAMemberThatABaseClassMarksRequired()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<RequiredDerived>("s: 'x'"));

		Assert.StartsWith("Missing the key a, which RequiredDerived requires", error.Message, StringComparison.Ordinal);
		Assert.Equal(2, SomlSerializer.Deserialize("x: 1\ny: 2\nname: 'n'", GeneratorContext.Default.ChildInit).Y);
		Assert.Equal(1, SomlSerializer.Deserialize("x: 1\ny: 2\nname: 'n'", GeneratorContext.Default.ChildInit).X);
	}

	[Fact]
	public void ReadsAndWritesThroughEveryOverload()
	{
		var typeInfo = GeneratorContext.Default.Server;
		var server = new SerializerTests.Server("example.com", 8080, TimeSpan.FromSeconds(90), ["a"]);
		var text = SomlSerializer.Serialize(server, typeInfo);
		var bytes = SomlSerializer.SerializeToUtf8Bytes(server, typeInfo);

		Assert.Equal(text, System.Text.Encoding.UTF8.GetString(bytes));
		Assert.Equal(server.Host, SomlSerializer.Deserialize(bytes, typeInfo).Host);
		Assert.Equal(server.Port, SomlSerializer.Deserialize(new MemoryStream(bytes), typeInfo).Port);
		Assert.Equal(server.Timeout, SomlSerializer.Deserialize(SomlSerializer.SerializeToValue(server, typeInfo), typeInfo).Timeout);

		using var stream = new MemoryStream();
		SomlSerializer.Serialize(stream, server, typeInfo);
		Assert.Equal(bytes, stream.ToArray());
		Assert.Same(GeneratorContext.Default, GeneratorContext.Default);
		Assert.Same(typeInfo, GeneratorContext.Default.Server);
		Assert.Same(typeInfo.Context, GeneratorContext.Default);
	}

	[Fact]
	public void PointsAtTheErrorInTheDocument()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize("host: 'a'\nport: 'x'\ntimeout: 1s\nallowed-origins: []", GeneratorContext.Default.Server));

		Assert.Equal("port", error.Path);
		Assert.EndsWith("(at port, line 2, column 7)", error.Message, StringComparison.Ordinal);
	}
}

public sealed class FactoryItems<T> : List<T>
{
	internal FactoryItems(IEnumerable<T> items) : base(items)
	{
	}
}

public static class FactoryItems
{
	public static FactoryItems<T> CreateRange<T>(IEnumerable<T> items) => new(items);

	public static FactoryItems<int> CreateRange(IEnumerable<int> items) => new(items.Select(item => -item));
}
