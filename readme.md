# Soml for C#

> [SOML](https://soml.sh) decoder and encoder

> [!NOTE]
> The format is a draft.

## Highlights

- Every rule in the spec is checked: duplicate keys, the int64 range, date, instant, and duration ranges, raw control characters, the nesting limit, and more
- Parse errors match the JavaScript reference implementation in wording, line, and column, with a code frame, and type errors have the path, line, and column of the value
- Reads into records and classes, with kebab-case keys by default, `required` members, and nullable annotations
- Writes members in the order of your type, or in canonical form, where the same value always gives the same bytes
- A formatter that changes layout and nothing else, and an editor that changes one value and keeps comments
- Native calendar dates with `DateOnly`, preserving year, month, and day without a timezone
- Exact to the nanosecond, with `SomlInstant` and `SomlDuration`, and conversions to `DateTimeOffset` and `TimeSpan` that never round silently
- Passes the language-neutral [conformance suite](https://github.com/soml-lang/soml/tree/main/conformance) of the spec, including its error positions
- No dependencies

## Install

The package is on [NuGet](https://www.nuget.org/packages/Soml/).

```sh
dotnet add package Soml
```

Requires .NET 10 or later.

## Usage

### Read into a type

```csharp
using Soml;

var config = SomlSerializer.Deserialize<Config>("""
	/* The edge service. */
	name: 'api-gateway'
	replicas: 3
	timeout: 1m30s # Including retries.
	deployed-at: 2026-09-19T14:00:00Z
	launch-date: 2027-09-19
	labels: ['prod', 'eu-west']
	postgres: {host: 'db.internal', port: 5432}
	""");

Console.WriteLine(config.Postgres.Port);
//=> 5432

Console.WriteLine(config.Timeout.TotalSeconds);
//=> 90

record Config(string Name, int Replicas, TimeSpan Timeout, DateTimeOffset DeployedAt, DateOnly LaunchDate, IReadOnlyList<string> Labels, Postgres Postgres);

record Postgres(string Host, ushort Port);
```

Keys are kebab-case by default, so the property `DeployedAt` reads the key `deployed-at`. Use `[SomlName("…")]` for another key, or set `PropertyNamingPolicy` in `SomlSerializerOptions`. Indentation never matters in SOML, so a document can be indented like the code around it.

A member that the document leaves out keeps the value your type gives it, so a property initializer is a default:

```csharp
class Settings
{
	public int Workers { get; init; } = 4;
	public required string Region { get; init; }
	public string? Owner { get; init; }
}
```

A `required` member must be present, even when its constructor supplies a default. An omitted constructor parameter uses its optional default, or `null` if nullable; otherwise it must be present.

Explicit `null` for a non-nullable member or parameter is an error. `[AllowNull]` permits null for reference types, and `[DisallowNull]` forbids it even for nullable value types.

Unknown keys are ignored unless you set `RejectUnknownKeys`, which also suggests likely corrections.

A default from a metadata attribute, such as `[DateTimeConstant]` or `[DefaultParameterValue]`, also needs `[Optional]`, and `[Optional]` alone gives no default.

When inherited members share a CLR name, constructor binding by that name is ambiguous. Give the parameter a different name and use `[SomlName]` to select its key.

### Write

```csharp
var server = new Server(8080, ["a", "b"]);

SomlSerializer.Serialize(server);
//=> "port: 8080\nhosts: [\n\t'a'\n\t'b'\n]\n"

SomlSerializer.Serialize(server, new SomlSerializerOptions {Canonical = true});
//=> "hosts: [\n\t'a'\n\t'b'\n]\nport: 8080\n"

record Server(int Port, string[] Hosts);
```

`Serialize` writes one member or item per line, with tab indentation and no commas. Members keep the order of your type: base class members first, and in each class, properties before fields, in declaration order. A dictionary keeps its enumeration order.

A value is written as its runtime type, so a property declared as a base class that holds a derived class is written with the derived class's members. A property declared as a collection interface, such as `IEnumerable<int>`, is written as an array, whatever its value's class is.

With `Canonical`, members are sorted by key, so the same value always gives the same bytes. Use it to hash, sign, or compare documents.

### Values of unknown shape

```csharp
var document = SomlValue.Parse("port: 8080\nhosts: ['a', 'b']");

document["port"].GetInt64();
//=> 8080

document["hosts"][1].GetString();
//=> "b"

document["port"].Kind;
//=> SomlValueKind.Integer
```

Use `TryGetValue` for a member that a document may leave out: `if (document.TryGetValue("timeout", out var timeout))`.

A `SomlValue` is a small struct, so it holds an int, a float, a bool, a date, an instant, or a duration without allocating. An object is a `SomlObject`, which keeps the order of its members, and an array is a `SomlArray`. An int and a float are different types, as in the spec, so `GetInt64()` does not read `3.0`. `GetDouble()` reads an int when a `double` holds it exactly.

Build values with conversions and collection expressions:

```csharp
SomlArray ports = [80, 443];

var server = new SomlObject
{
	["host"] = "localhost",
	["ports"] = ports,
	["timeout"] = SomlDuration.Parse("30s"),
};
```

Object collection expressions accept `KeyValuePair<string, SomlValue>` members: `SomlObject labels = [new("region", "eu"), new("tier", "edge")];`. `SomlArray.Create` and `SomlObject.Create` also copy span inputs into mutable storage.

Two values are equal when they are deeply equal, and the order of an object's members does not matter, because it is not part of the value.

### Change a document and keep its comments

```csharp
var text = "/* The edge service. */\nport: 0x1F90 # The default.\nserver: {host: 'a'}\n";

text = SomlEditor.Set(text, ["port"], 8080);
text = SomlEditor.Set(text, ["server", "port"], 443);
text = SomlEditor.Set(text, ["replicas"], 3);

Console.WriteLine(text);
//=> /* The edge service. */
//=> port: 8080 # The default.
//=> server: {host: 'a', port: 443}
//=> replicas: 3
```

Only the changed part of the text is rewritten. The comments, blank lines, indentation, member order, and the spelling of every other value stay as they are. Changes follow the spec's editing rules: a new member goes after the last member of its object, missing objects on the way are created, a removed member or item takes the comments it owns, such as one after it on its line, and a change to a formatted document leaves it formatted.

To set a value of your own type, convert it first with `SomlSerializer.SerializeToValue(value)`. `SomlEditor.Remove(text, path)` removes a value. Removing a value that does not exist changes nothing and is not an error, so removing the same path twice is safe, and an overload tells whether it removed something.

### Format

```csharp
SomlFormatter.Format("a: {b: 1, c: [2,3]}  # Note.");
//=> "a: {b: 1, c: [2, 3]} # Note.\n"

SomlFormatter.Format("a: {\nb: 1, c: [2,3]}");
//=> "a: {\n\tb: 1\n\tc: [2, 3]\n}\n"
```

The formatter changes layout and nothing else: one tab of indentation per level, every member and item on its own line, no commas, at most one blank line in a row, and no trailing spaces or tabs. An object or an array whose brackets are on one line stays on one line, with a comma and a space between its members or items. To give it one member or item per line, put a line break anywhere inside it. Comments stay where they are, and so do member order, block strings, and the spelling of every value. The spec's formatter rules are normative, so every conforming formatter gives the same text.

### Errors

```csharp
try
{
	SomlSerializer.Deserialize<Postgres>("host: 'a'\nport: 70000");
}
catch (SomlException error)
{
	Console.WriteLine(error.Message);
	//=> The int 70000 is outside the range of ushort (0 to 65535) (at port, line 2, column 7)
}
```

A document that is not valid throws a `SomlParseException`, which has the `Reason`, the 1-based `Line` and `Column` (in Unicode scalar values), and a `CodeFrame`:

```
Unexpected “api-gateway”. A string value must be quoted, as in 'api-gateway' at line 1, column 7

> 1 | name: api-gateway
    |       ^
```

A valid document that does not fit your type, a value that SOML cannot represent, such as NaN, and an edit whose path does not fit the document throw a `SomlException`. For a value that does not fit or cannot be written, its `Path` is the path of the value, such as `servers[0].port`, and the message also has the line and column when the value was read from text.

### Read files

```csharp
var config = SomlSerializer.Deserialize<Config>(File.ReadAllBytes("config.soml"));
```

Read SOML as bytes, or with the stream overloads. `File.ReadAllText` silently drops a byte order mark and replaces invalid UTF-8, both of which SOML requires to be errors. Stream overloads read from the current position to the end and leave the stream open. `DeserializeAsync` and `SerializeAsync` accept a cancellation token for stream I/O. A document is only valid as a whole, so deserialization reads everything before binding it to your type.

### Native AOT and trimming

`SomlSerializer` reads and writes types through reflection by default. For trimming and Native AOT, let the source generator in the package write that code at build time:

```csharp
using Soml;
using Soml.Serialization;

var config = SomlSerializer.Deserialize(File.ReadAllBytes("config.soml"), AppSomlContext.Default.Config);
var text = SomlSerializer.Serialize(config, AppSomlContext.Default.Config);

[SomlSerializable(typeof(Config))]
partial class AppSomlContext : SomlSerializerContext;
```

The context and its containing types must be partial and non-generic. It gets a property for each registered type and includes converters for the types they hold. Pass options to its constructor, such as `new AppSomlContext(new SomlSerializerOptions { RejectUnknownKeys = true })`. Reuse the context to reuse its converters.

Generated serialization follows reflection's rules and errors, with two differences:

- It writes values as their declared types. A property declared as a base class writes only that class's members, even when its value is a derived instance.
- Generic member nullability is best-effort because reflection exposes annotations inconsistently. Use `[AllowNull]` or `[DisallowNull]` to make the contract explicit.

Unsupported types produce compile errors: `object`, abstract classes, interfaces other than collection interfaces, non-generic collections, multidimensional arrays, tuples, and dictionaries with non-string keys. Use `SomlValue` for a value of any type.

The source generator also does not support:

- Selected constructors with by-reference parameters (`in`, `ref`, `ref readonly`, `out`). Reflection cannot read them either.
- Selected constructors whose parameter types match another accessible overload after generic substitution. Remove the overload or use reflection.
- Generic types whose members expand their type arguments without end, such as `Node<List<T>>` in `Node<T>`.
- Generic `CreateRange` factories whose overloads overlap after generic substitution. Every overload with the matching generic arity and an `IEnumerable<T>` parameter must permit the collection's type arguments in its constraints.
- User types with the same fully qualified name as a .NET or SOML type.

## Types

| .NET | SOML | Notes |
|---|---|---|
| `string`, `char` | string | A `char` is a string of one UTF-16 code unit. |
| `bool` | bool | |
| `sbyte`…`long`, `byte`…`ulong`, `nint`, `nuint`, `Int128`, `UInt128` | int | Range-checked both ways. Native integers follow the process's pointer width. An int is 64-bit, so a larger value cannot be written. |
| `double`, `float`, `Half` | float | An int reads into a float only when it converts exactly, and a float never reads into an integer type. A `float` and a `Half` are written with their own shortest digits, so `0.1f` is `0.1`. A finite float too large for a `float` or a `Half`, or a nonzero one that would become zero, is an error. NaN cannot be written. |
| `decimal` | float | Read from the shortest digits of the float, so `0.1` is `0.1m`. A decimal is written as the `double` closest to it, which must read back as the same decimal, so a decimal that no `double` holds exactly enough, such as `9007199254740993m`, cannot be written. |
| `SomlInstant`, `DateTimeOffset`, `DateTime` | instant | `SomlInstant` is exact to the nanosecond. The BCL types hold 100-nanosecond ticks, so a finer instant does not read into them. The offset is not part of an instant, so they read in UTC. A `DateTime` must have a `Kind` of `Utc` or `Local` to be written. |
| `SomlDuration`, `TimeSpan` | duration | `SomlDuration` is exact to the nanosecond. A finer duration does not read into a `TimeSpan`, and a `TimeSpan` longer than about 292 years cannot be written. |
| `DateOnly` | date | Exact proleptic Gregorian components, years 0001 through 9999, written as `YYYY-MM-DD`. A quoted date stays a string. |
| `TimeOnly` | string | `HH:mm:ss` with an optional fraction. A time is also read as `HH:mm`. |
| an enum | string | The member name, kebab-case by default. A `[Flags]` enum is an array of member names; writing it requires a named single-bit member for every set bit. |
| `Guid`, `Uri`, `Version`, and non-collection types that are `IParsable<T>` and `IFormattable` | string | In the invariant culture. |
| `byte[]` | array | An array of ints, as any array is. Write binary data as a string, such as base64, yourself. |
| arrays, `List<T>`, `HashSet<T>`, and their interfaces | array | Set items must be unique under the destination set's comparer. Duplicate errors point to the set. |
| classes, structs, records, `Dictionary<TKey, TValue>` | object | A dictionary key is a string, an enum, a date, an instant or a duration in canonical form, or a type that is `IParsable<T>` and `IFormattable`, such as `int` (`404: 'x'`). A string key is never renamed, an enum key is one member's name, as an enum value is, so a `[Flags]` value of two members cannot be a key, and a key must be written as the type writes it, so `007` is not the `int` key 7. |
| `SomlValue`, `SomlObject`, `SomlArray` | any value | |
| `object` | any value | Read as `string`, `long`, `double`, `bool`, `null`, `SomlInstant`, `SomlDuration`, `List<object?>`, and `Dictionary<string, object?>`. |

`SomlInstant.Parse` and `SomlDuration.Parse` provide detailed syntax diagnostics through their string overloads. Their span overloads throw a fixed-message `FormatException` for invalid input without copying the span.

`[SomlIgnore]` excludes an enum member's name. Another name for the same value remains usable.

Custom collections can be read through a public constructor that accepts their items, or through default construction and a public instance `Add` method. `Add` must be non-generic, take the exact item types by value, and return `void` or `bool`. Struct collections do not need an explicit parameterless constructor. Immutable-style collections can instead expose a compatible generic `CreateRange` factory on a same-named static class.

## Performance

Time to read a document into each library's document model, and to write that model back to text, for the [shared fixtures](https://github.com/soml-lang/soml/tree/main/benchmark) of the spec, which hold the same data in each format:

| | Soml | System.Text.Json | Newtonsoft.Json | Tomlyn |
|---|--:|--:|--:|--:|
| Read `service` (3 KB) | 7.2 µs | 4.8 µs | 17 µs | 38 µs |
| Read `platform` (35 KB) | 54 µs | 32 µs | 114 µs | 311 µs |
| Read `earthquakes` (733 KB) | 3.3 ms | 1.4 ms | 11.6 ms | 11.5 ms |
| Write `service` | 3.9 µs | 3.2 µs | 5.1 µs | 4.6 µs |
| Write `platform` | 19 µs | 21 µs | 31 µs | 24 µs |
| Write `earthquakes` | 3.5 ms | 3.3 ms | 4.5 ms | 3.3 ms |

Measured on 2026-10-20 with an Apple M4 Pro and .NET 10.0.12, using [BenchmarkDotNet](bench/Soml.Benchmarks) with CPU-time measurements. System.Text.Json reads into `JsonDocument`, which creates a value only when it is read, and writes the text of each value as it was read, while the others create every value and format it.

## Related

- [Soml.Extensions.Configuration](src/Soml.Extensions.Configuration): SOML files for `Microsoft.Extensions.Configuration`, with `AddSomlFile()`
- [soml CLI](tools/Soml.Cli): Converts between SOML and the tagged JSON of the conformance suite, for test harnesses

## Format

Documents follow the [SOML specification](https://github.com/soml-lang/soml/blob/main/spec.md).

- File extension: `.soml`
- Encoding: UTF-8 without a byte order mark, with LF line endings
- Media type: `application/soml`
- Nesting is limited to 100 levels, as the spec requires. Every object and array counts, including the document's own.

## Limitations

- `SomlSerializer` uses reflection unless you give it a [source-generated](#native-aot-and-trimming) `SomlTypeInfo<T>`. The rest of the API never does.
- Nullable annotations are not checked for collection items.
- The keys of a member's declared type must be unique, even if a derived value overrides or ignores the conflicting members.
- `SerializeToValue()` does not validate strings or SOML containers. Writing to text does.
- A `float` or `Half` is read through a `double`, so a number with more digits than a `double` holds, very near halfway between two `float` values, can round differently than `float.Parse`. Negative zero reads as zero.
- A property without a public setter is not read, so `public List<int> Items { get; } = []` keeps its items. Its key still counts as known for `RejectUnknownKeys`.
- Errors from `Deserialize(SomlValue)` have no line and column.
- `SomlValue` equality does not support a value that contains itself.
- `SomlSerializer` sets any public property a document names, so read untrusted documents only into your own types. For example, `capacity: 100000000` on a `StringBuilder` allocates hundreds of megabytes.
