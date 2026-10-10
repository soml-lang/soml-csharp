using System.Net;
using System.Text;
using Soml.Serialization;

namespace Soml.Tests;

public sealed class SerializerTests
{
	public sealed record Server(string Host, int Port, TimeSpan Timeout, IReadOnlyList<string> AllowedOrigins);

	public sealed class Settings
	{
		public string Name { get; set; } = "default";

		public int Replicas { get; set; } = 1;

		public double Ratio { get; set; }

		public string? Owner { get; set; }

		public LogLevel Level { get; set; }

		public Permissions Permissions { get; set; }

		public Dictionary<string, int> Limits { get; set; } = [];
	}

	public enum LogLevel
	{
		Info,
		Warning,
		VeryVerbose,
	}

	[Flags]
	public enum Permissions
	{
		None = 0,
		Read = 1,
		Write = 2,
		Execute = 4,
	}

	public enum AliasedStatus
	{
		First = 1,
		Alias = First,
	}

	[Flags]
	public enum AliasedPermissions
	{
		Read = 1,
		Alias = Read,
	}

	public enum IgnoredStatus
	{
		Visible,
		[SomlIgnore]
		Hidden,
	}

	public enum IgnoredNameCollision
	{
		[SomlName("same")]
		Visible,
		[SomlIgnore, SomlName("same")]
		Hidden,
	}

	public enum IgnoredAlias
	{
		[SomlIgnore]
		Hidden = 1,
		Visible = Hidden,
	}

	[Flags]
	public enum IgnoredPermissions
	{
		Read = 1,
		[SomlIgnore]
		Hidden = 2,
		Write = 4,
	}

	public sealed class RequiredSettings
	{
		public required string Name { get; init; }

		public int Port { get; init; } = 80;
	}

	public sealed record Renamed([SomlName("listen-on")] int Port, [property: SomlName("the host")] string Host);

	public sealed class Node
	{
		public string Name { get; set; } = "";

		public List<Node> Children { get; set; } = [];

		public Node? Parent { get; set; }
	}

	public struct Point
	{
		public int X { get; set; }

		public int Y;
	}

	[Fact]
	public void ReadsRecordWithKebabCaseKeys()
	{
		var server = SomlSerializer.Deserialize<Server>("""
			host: 'example.com'
			port: 8080
			timeout: 1m30s
			allowed-origins: ['https://example.com', 'https://example.org']
			""");

		Assert.Equal("example.com", server.Host);
		Assert.Equal(8080, server.Port);
		Assert.Equal(TimeSpan.FromSeconds(90), server.Timeout);
		Assert.Equal(["https://example.com", "https://example.org"], server.AllowedOrigins);
	}

	[Fact]
	public void WritesMembersInTheirOrder()
	{
		var server = new Server("example.com", 8080, TimeSpan.FromMilliseconds(1500), ["a"]);

		Assert.Equal("""
			host: 'example.com'
			port: 8080
			timeout: 1.5s
			allowed-origins: [
				'a'
			]

			""".Replace("\r", "", StringComparison.Ordinal), SomlSerializer.Serialize(server));
	}

	[Fact]
	public void WritesCanonicalFormSorted()
	{
		var server = new Server("example.com", 8080, TimeSpan.Zero, []);

		Assert.Equal("allowed-origins: []\nhost: 'example.com'\nport: 8080\ntimeout: 0s\n", SomlSerializer.Serialize(server, new SomlSerializerOptions { Canonical = true }));
	}

	[Fact]
	public void MissingMembersKeepTheirDefaults()
	{
		var settings = SomlSerializer.Deserialize<Settings>("replicas: 3");

		Assert.Equal("default", settings.Name);
		Assert.Equal(3, settings.Replicas);
		Assert.Null(settings.Owner);
	}

	[Fact]
	public void ReadsEnumsByMemberName()
	{
		var settings = SomlSerializer.Deserialize<Settings>("level: 'very-verbose'\npermissions: ['read', 'execute']");

		Assert.Equal(LogLevel.VeryVerbose, settings.Level);
		Assert.Equal(Permissions.Read | Permissions.Execute, settings.Permissions);
		Assert.Contains("permissions: [\n\t'read'\n\t'execute'\n]", SomlSerializer.Serialize(settings), StringComparison.Ordinal);
	}

	[Fact]
	public void RejectsAnUnknownEnumMember()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Settings>("level: 'loud'"));

		Assert.Equal("'loud' is not a member of LogLevel. The members are 'info', 'warning', 'very-verbose' (at level, line 1, column 8)", error.Message);
		Assert.Equal("level", error.Path);
	}

	[Fact]
	public void ReadsAnIntIntoADoubleButNotAFloatIntoAnInt()
	{
		Assert.Equal(2.0, SomlSerializer.Deserialize<Settings>("ratio: 2").Ratio);

		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Settings>("replicas: 3.0"));
		Assert.Equal("Expected an int for int, but found the float 3.0. A float does not convert to an integer type, so write an int without a fraction, as in 3 (at replicas, line 1, column 11)", error.Message);
	}

	[Fact]
	public void ChecksTheRangeOfIntegerTypes()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, byte>>("a: 300"));

		Assert.Equal("The int 300 is outside the range of byte (0 to 255) (at a, line 1, column 4)", error.Message);
	}

	[Fact]
	public void RejectsNullForANonNullableMember()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Settings>("name: null"));

		Assert.Equal("Expected a string for string, but found null (at name, line 1, column 7)", error.Message);
		Assert.Null(SomlSerializer.Deserialize<Settings>("owner: null").Owner);
	}

	[Fact]
	public void RequiresRequiredMembersAndConstructorParameters()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<RequiredSettings>("port: 1"));
		Assert.Equal("Missing the key name, which RequiredSettings requires (at line 1, column 1)", error.Message);

		var recordError = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Server>("host: 'a'"));
		Assert.Equal("Missing the key port, which Server requires (at line 1, column 1)", recordError.Message);
	}

	[Fact]
	public void RejectsUnknownKeysWhenAsked()
	{
		var options = new SomlSerializerOptions { RejectUnknownKeys = true };
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Settings>("replica: 2", options));

		Assert.Equal("Unknown key replica, which Settings does not read. Did you mean replicas? (at replica, line 1, column 1)", error.Message);
		Assert.Equal(1, SomlSerializer.Deserialize<Settings>("replica: 2").Replicas);
	}

	[Fact]
	public void UsesSomlNameOnParametersAndProperties()
	{
		var value = SomlSerializer.Deserialize<Renamed>("listen-on: 80\n'the host': 'a'");

		Assert.Equal(new Renamed(80, "a"), value);
		Assert.Equal("listen-on: 80\n'the host': 'a'\n", SomlSerializer.Serialize(value));
	}

	[Fact]
	public void ReadsDictionariesWithParsableKeys()
	{
		var value = SomlSerializer.Deserialize<Dictionary<int, string>>("404: 'not found'\n500: 'error'");

		Assert.Equal("not found", value[404]);
		Assert.Equal("404: 'not found'\n500: 'error'\n", SomlSerializer.Serialize(value));

		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<int, string>>("007: 'bond'"));
		Assert.Equal("The key 007 is not how int writes it, which is 7 (at 007, line 1, column 1)", error.Message);
	}

	[Theory]
	[InlineData(typeof(Dictionary<AliasedStatus, int>), "alias", "first")]
	[InlineData(typeof(Dictionary<AliasedPermissions, int>), "alias", "read")]
	[InlineData(typeof(Dictionary<HttpStatusCode, int>), "redirect-method", "see-other")]
	public void RequiresAnEnumDictionaryKeyToUseItsWrittenName(Type dictionaryType, string alias, string canonical)
	{
		var type = typeof(Dictionary<,>).MakeGenericType(typeof(string), dictionaryType);
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize($"map: {{\n\t{alias}: 1\n}}", type));

		Assert.Equal($"map.{alias}", error.Path);
		Assert.Contains($"writes it, which is {canonical}", error.Message, StringComparison.Ordinal);
		Assert.EndsWith($"(at map.{alias}, line 2, column 2)", error.Message, StringComparison.Ordinal);

		var value = SomlSerializer.Deserialize($"map: {{{canonical}: 1}}", type);

		Assert.Equal($"map: {{\n\t{canonical}: 1\n}}\n", SomlSerializer.Serialize(value));
	}

	[Fact]
	public void ReadsEnumAliasesAsValues()
	{
		Assert.Equal(AliasedStatus.First, SomlSerializer.Deserialize<Dictionary<string, AliasedStatus>>("a: 'alias'")["a"]);
		Assert.Equal(AliasedPermissions.Read, SomlSerializer.Deserialize<Dictionary<string, AliasedPermissions>>("a: ['alias']")["a"]);
		Assert.Equal(HttpStatusCode.SeeOther, SomlSerializer.Deserialize<Dictionary<string, HttpStatusCode>>("a: 'redirect-method'")["a"]);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void RejectsIgnoredEnumNamesWhenReading(bool generated, bool flags)
	{
		var error = flags
			? Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("value: ['read', 'hidden']", IgnoredEnumContext.Default.Permissions) : SomlSerializer.Deserialize<Dictionary<string, IgnoredPermissions>>("value: ['read', 'hidden']"))
			: Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("value: 'hidden'", IgnoredEnumContext.Default.Status) : SomlSerializer.Deserialize<Dictionary<string, IgnoredStatus>>("value: 'hidden'"));

		Assert.Equal(flags ? "value[1]" : "value", error.Path);
		Assert.Equal(flags ? "'hidden' is not a member of IgnoredPermissions. The members are 'read', 'write' (at value[1], line 1, column 17)" : "'hidden' is not a member of IgnoredStatus. The members are 'visible' (at value, line 1, column 8)", error.Message);
	}

	[Theory]
	[InlineData(false, false)]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(true, true)]
	public void RejectsEnumValuesWithOnlyAnIgnoredNameWhenWriting(bool generated, bool flags)
	{
		var status = new Dictionary<string, IgnoredStatus> { ["value"] = IgnoredStatus.Hidden };
		var permissions = new Dictionary<string, IgnoredPermissions> { ["value"] = IgnoredPermissions.Read | IgnoredPermissions.Hidden };
		var error = flags
			? Assert.Throws<SomlException>(() => generated ? SomlSerializer.Serialize(permissions, IgnoredEnumContext.Default.Permissions) : SomlSerializer.Serialize(permissions))
			: Assert.Throws<SomlException>(() => generated ? SomlSerializer.Serialize(status, IgnoredEnumContext.Default.Status) : SomlSerializer.Serialize(status));

		Assert.Equal("value", error.Path);
		Assert.Equal(flags ? "The value 3 of IgnoredPermissions has bits that no single-bit member names (at value)" : "The value 1 is not a member of IgnoredStatus (at value)", error.Message);

		status["value"] = IgnoredStatus.Visible;
		permissions["value"] = IgnoredPermissions.Read | IgnoredPermissions.Write;
		var written = flags
			? generated ? SomlSerializer.Serialize(permissions, IgnoredEnumContext.Default.Permissions) : SomlSerializer.Serialize(permissions)
			: generated ? SomlSerializer.Serialize(status, IgnoredEnumContext.Default.Status) : SomlSerializer.Serialize(status);

		Assert.Equal(flags ? "value: [\n\t'read'\n\t'write'\n]\n" : "value: 'visible'\n", written);

		if (flags)
		{
			Assert.Equal(permissions, generated ? SomlSerializer.Deserialize(written, IgnoredEnumContext.Default.Permissions) : SomlSerializer.Deserialize<Dictionary<string, IgnoredPermissions>>(written));
		}
		else
		{
			Assert.Equal(status, generated ? SomlSerializer.Deserialize(written, IgnoredEnumContext.Default.Status) : SomlSerializer.Deserialize<Dictionary<string, IgnoredStatus>>(written));
		}
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void IgnoresExcludedEnumFieldsWhenCheckingNames(bool generated)
	{
		const string text = "value: 'same'\n";
		var result = generated ? SomlSerializer.Deserialize(text, IgnoredEnumContext.Default.Collision) : SomlSerializer.Deserialize<Dictionary<string, IgnoredNameCollision>>(text);

		Assert.Equal(IgnoredNameCollision.Visible, result["value"]);
		Assert.Equal(text, generated ? SomlSerializer.Serialize(result, IgnoredEnumContext.Default.Collision) : SomlSerializer.Serialize(result));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void WritesAnEnumValueWithItsNonignoredAlias(bool generated)
	{
		var value = new Dictionary<string, IgnoredAlias> { ["value"] = IgnoredAlias.Hidden };
		var text = generated ? SomlSerializer.Serialize(value, IgnoredEnumContext.Default.Alias) : SomlSerializer.Serialize(value);

		Assert.Equal("value: 'visible'\n", text);
		Assert.Equal(value, generated ? SomlSerializer.Deserialize(text, IgnoredEnumContext.Default.Alias) : SomlSerializer.Deserialize<Dictionary<string, IgnoredAlias>>(text));
		var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Deserialize("value: 'hidden'", IgnoredEnumContext.Default.Alias) : SomlSerializer.Deserialize<Dictionary<string, IgnoredAlias>>("value: 'hidden'"));

		Assert.Equal("value", error.Path);
		Assert.Contains("The members are 'visible'", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ReadsCollections()
	{
		var value = SomlSerializer.Deserialize<Dictionary<string, object>>("a: [1, 2]");
		Assert.Equal(new List<object?> { 1L, 2L }, value["a"]);

		Assert.Equal([1, 2], SomlSerializer.Deserialize<int[]>("[1, 2]"));
		Assert.Equal([1, 2], SomlSerializer.Deserialize<HashSet<int>>("[1, 2]"));
		Assert.Equal([[1], [2, 3]], SomlSerializer.Deserialize<List<List<int>>>("[[1], [2, 3]]"));

		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<HashSet<int>>("[1, 1]"));
		Assert.Equal("Duplicate item in HashSet<int> (at line 1, column 1)", error.Message);
	}

	[Fact]
	public void ReadsRecursiveTypes()
	{
		var node = SomlSerializer.Deserialize<Node>("name: 'root'\nchildren: [{name: 'leaf', children: []}]");

		Assert.Equal("leaf", Assert.Single(node.Children).Name);
	}

	[Fact]
	public void RefusesAReferenceCycle()
	{
		var node = new Node { Name = "root" };
		node.Parent = node;

		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(node));
		Assert.StartsWith("Cannot serialize a value nested more than 100 levels deep", error.Message, StringComparison.Ordinal);
	}

	[Fact]
	public void ReadsAndWritesStructs()
	{
		var point = SomlSerializer.Deserialize<Point>("x: 1\ny: 2");

		Assert.Equal((1, 2), (point.X, point.Y));
		Assert.Equal("x: 1\ny: 2\n", SomlSerializer.Serialize(point));
	}

	[Fact]
	public void WritesFloatsWithTheirOwnDigits()
	{
		Assert.Equal("a: 0.1\n", SomlSerializer.Serialize(new Dictionary<string, float> { ["a"] = 0.1f }));
		Assert.Equal("a: 0.1\n", SomlSerializer.Serialize(new Dictionary<string, decimal> { ["a"] = 0.1m }));
		Assert.Equal(0.1m, SomlSerializer.Deserialize<Dictionary<string, decimal>>("a: 0.1")["a"]);

		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(new Dictionary<string, double> { ["a"] = double.NaN }));
		Assert.Equal("NaN is not a SOML value. Use null for a missing value (at a)", error.Message);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void WritesDecimalsOnlyWhenTheyReadBackWithoutRounding(bool generated)
	{
		decimal[] exact = [0m, 0.1m, 0.0000000000000000000000000001m, 9_007_199_254_740_992m, 1_000_000_000_000_000_000_000_000m];

		foreach (var number in exact)
		{
			var value = new SerializerParityTests.Numeric { Decimal = number };
			var text = generated ? SomlSerializer.Serialize(value, ParityContext.Default.Numeric) : SomlSerializer.Serialize(value);
			var readBack = generated ? SomlSerializer.Deserialize(text, ParityContext.Default.Numeric) : SomlSerializer.Deserialize<SerializerParityTests.Numeric>(text);
			var written = SomlValue.Parse(text)["decimal"];

			Assert.Equal(number, readBack.Decimal);
			Assert.Equal(SomlValueKind.Float, written.Kind);
			Assert.Equal(number, (decimal)written);
		}

		decimal[] inexact = [9_007_199_254_740_993m, -9_007_199_254_740_993m, 1.0000000000000000000000000001m, decimal.MinValue, decimal.MaxValue];

		foreach (var number in inexact)
		{
			var value = new SerializerParityTests.Numeric { Decimal = number };
			var error = Assert.Throws<SomlException>(() => generated ? SomlSerializer.Serialize(value, ParityContext.Default.Numeric) : SomlSerializer.Serialize(value));

			Assert.Equal("decimal", error.Path);
			Assert.Contains("cannot be written exactly as a float", error.Message, StringComparison.Ordinal);
		}
	}

	[Fact]
	public void ReadsTimeTypesExactly()
	{
		var value = SomlSerializer.Deserialize<Dictionary<string, DateTimeOffset>>("a: 2026-09-19T16:00:00+02:00");
		Assert.Equal(new DateTimeOffset(2026, 9, 19, 14, 0, 0, TimeSpan.Zero), value["a"]);

		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Dictionary<string, TimeSpan>>("a: 1ns"));
		Assert.Equal("The duration 0.000000001s is not a whole number of 100-nanosecond ticks, so a TimeSpan cannot hold it. Use SomlDuration to keep the nanoseconds (at a, line 1, column 4)", error.Message);

		Assert.Equal(new SomlDuration(1), SomlSerializer.Deserialize<Dictionary<string, SomlDuration>>("a: 1ns")["a"]);
	}

	[Fact]
	public void MapsParsableTypesToStrings()
	{
		var id = Guid.NewGuid();
		var text = SomlSerializer.Serialize(new Dictionary<string, object> { ["id"] = id, ["version"] = new Version(1, 2, 3), ["date"] = new DateOnly(2026, 9, 19) });

		Assert.Equal($"id: '{id}'\nversion: '1.2.3'\ndate: 2026-09-19\n", text);
		Assert.Equal(id, SomlSerializer.Deserialize<Dictionary<string, Guid>>($"id: '{id}'")["id"]);
	}

	[Fact]
	public void ReadsObjectAsPlainValues()
	{
		var value = SomlSerializer.Deserialize<Dictionary<string, object?>>("a: 1\nb: 1.5\nc: 'x'\nd: null\ne: {f: true}");

		Assert.Equal(1L, value["a"]);
		Assert.Equal(1.5, value["b"]);
		Assert.Equal("x", value["c"]);
		Assert.Null(value["d"]);
		Assert.Equal(true, ((Dictionary<string, object?>)value["e"]!)["f"]);
	}

	[Fact]
	public void RoundTripsThroughText()
	{
		var settings = new Settings { Name = "api", Replicas = 3, Ratio = 0.75, Owner = null, Level = LogLevel.Warning, Permissions = Permissions.Read, Limits = new() { ["cpu"] = 2 } };
		var text = SomlSerializer.Serialize(settings);
		var readBack = SomlSerializer.Deserialize<Settings>(text);

		Assert.Equal(text, SomlSerializer.Serialize(readBack));
		Assert.Contains("owner: null", text, StringComparison.Ordinal);
		Assert.DoesNotContain("owner", SomlSerializer.Serialize(settings, new SomlSerializerOptions { OmitNullMembers = true }), StringComparison.Ordinal);
	}

	[Fact]
	public void RefusesAScalarAsADocument()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Serialize(5));

		Assert.Equal("The top-level value must be an object or an array, because a document is a collection, but it is an int", error.Message);
		Assert.Equal(5, SomlSerializer.SerializeToValue(5).GetInt64());
	}

	[Fact]
	public void UsesNamesAsTheyAreWithoutANamingPolicy()
	{
		var options = new SomlSerializerOptions { PropertyNamingPolicy = null };

		Assert.Equal(3, SomlSerializer.Deserialize<Settings>("Replicas: 3", options).Replicas);
	}

	// A non-seekable source that splits every multibyte UTF-8 character across reads.
	private sealed class ChunkedStream(byte[] bytes) : Stream
	{
		private readonly MemoryStream _source = new(bytes);

		public override bool CanRead => _source.CanRead;
		public override bool CanSeek => false;
		public override bool CanWrite => false;
		public override long Length => throw new NotSupportedException();
		public override long Position
		{
			get => throw new NotSupportedException();
			set => throw new NotSupportedException();
		}

		public override int Read(byte[] buffer, int offset, int count) => _source.Read(buffer, offset, Math.Min(count, 1));

		public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _source.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);

		public override void Flush() => throw new NotSupportedException();
		public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
		public override void SetLength(long value) => throw new NotSupportedException();
		public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

		protected override void Dispose(bool disposing)
		{
			if (disposing)
			{
				_source.Dispose();
			}

			base.Dispose(disposing);
		}
	}

	private static ValueTask<Server> ReadServerAsync(Stream input, bool generated, CancellationToken cancellationToken) => generated ? SomlSerializer.DeserializeAsync(input, GeneratorContext.Default.Server, cancellationToken) : SomlSerializer.DeserializeAsync<Server>(input, cancellationToken: cancellationToken);

	private static Task WriteServerAsync(Stream output, Server server, bool generated, CancellationToken cancellationToken) => generated ? SomlSerializer.SerializeAsync(output, server, GeneratorContext.Default.Server, cancellationToken) : SomlSerializer.SerializeAsync(output, server, cancellationToken: cancellationToken);

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task ReadsAndWritesAsyncWithoutClosingTheStream(bool generated)
	{
		var server = new Server("é😀日本", 8080, TimeSpan.FromSeconds(90), ["a"]);
		var bytes = SomlSerializer.SerializeToUtf8Bytes(server);
		using var input = new ChunkedStream([0xFF, .. bytes]);
		Assert.Equal(0xFF, input.ReadByte());
		var token = TestContext.Current.CancellationToken;
		var result = await ReadServerAsync(input, generated, token);

		Assert.Equal(server.Host, result.Host);
		Assert.Equal(server.Port, result.Port);
		Assert.Equal(server.Timeout, result.Timeout);
		Assert.Equal(server.AllowedOrigins, result.AllowedOrigins);
		Assert.True(input.CanRead);

		using var output = new MemoryStream();

		await WriteServerAsync(output, result, generated, token);

		Assert.True(output.CanWrite);
		Assert.Equal(bytes, output.ToArray());
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AsyncStreamsHonorCancellation(bool generated)
	{
		using var cancellation = new CancellationTokenSource();
		cancellation.Cancel();
		using var input = new ChunkedStream(Encoding.UTF8.GetBytes("host: 'a'"));

		await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await ReadServerAsync(input, generated, cancellation.Token));

		using var output = new MemoryStream();
		var server = new Server("a", 80, TimeSpan.Zero, []);

		await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WriteServerAsync(output, server, generated, cancellation.Token));

		Assert.True(input.CanRead);
		Assert.True(output.CanWrite);
		Assert.Equal(0, output.Length);
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public async Task AsyncStreamsReportErrorsAfterTheWholeDocumentIsRead(bool generated)
	{
		const string text = "host: 'é😀'\nport: 'x'\ntimeout: 1s\nallowed-origins: []";
		using var input = new ChunkedStream(Encoding.UTF8.GetBytes(text));
		var error = await Assert.ThrowsAsync<SomlException>(async () => await ReadServerAsync(input, generated, TestContext.Current.CancellationToken));

		Assert.Equal("port", error.Path);
		Assert.EndsWith("(at port, line 2, column 7)", error.Message, StringComparison.Ordinal);
		Assert.True(input.CanRead);

		// Invalid encoding at the end takes precedence over the earlier type mismatch.
		using var invalidInput = new ChunkedStream([.. Encoding.UTF8.GetBytes(text), 0xFF]);
		var parseError = await Assert.ThrowsAsync<SomlParseException>(async () => await ReadServerAsync(invalidInput, generated, TestContext.Current.CancellationToken));
		Assert.Equal("Invalid UTF-8 byte 0xFF", parseError.Reason);
		Assert.True(invalidInput.CanRead);
	}
}

[SomlSerializable(typeof(Dictionary<string, SerializerTests.IgnoredStatus>), TypeInfoPropertyName = "Status")]
[SomlSerializable(typeof(Dictionary<string, SerializerTests.IgnoredNameCollision>), TypeInfoPropertyName = "Collision")]
[SomlSerializable(typeof(Dictionary<string, SerializerTests.IgnoredAlias>), TypeInfoPropertyName = "Alias")]
[SomlSerializable(typeof(Dictionary<string, SerializerTests.IgnoredPermissions>), TypeInfoPropertyName = "Permissions")]
internal partial class IgnoredEnumContext : SomlSerializerContext;
