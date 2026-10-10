namespace Soml.Tests;

/*
The examples of the readme, so that they stay true.
*/
public sealed class ReadmeTests
{
	public sealed record Config(string Name, int Replicas, TimeSpan Timeout, DateTimeOffset DeployedAt, DateOnly LaunchDate, IReadOnlyList<string> Labels, Postgres Postgres);

	public sealed record Postgres(string Host, ushort Port);

	public sealed class Settings
	{
		public int Workers { get; init; } = 4;

		public required string Region { get; init; }

		public string? Owner { get; init; }
	}

	public sealed record Server(int Port, string[] Hosts);

	[Fact]
	public void ReadIntoAType()
	{
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

		Assert.Equal(new DateOnly(2027, 9, 19), config.LaunchDate);
		Assert.Equal(5432, config.Postgres.Port);
		Assert.Equal(90, config.Timeout.TotalSeconds);
		Assert.Equal(new DateTimeOffset(2026, 9, 19, 14, 0, 0, TimeSpan.Zero), config.DeployedAt);
	}

	[Fact]
	public void Defaults()
	{
		var settings = SomlSerializer.Deserialize<Settings>("region: 'eu'");

		Assert.Equal(4, settings.Workers);
		Assert.Null(settings.Owner);
		Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Settings>("workers: 2"));
	}

	[Fact]
	public void Write()
	{
		var server = new Server(8080, ["a", "b"]);

		Assert.Equal("port: 8080\nhosts: [\n\t'a'\n\t'b'\n]\n", SomlSerializer.Serialize(server));
		Assert.Equal("hosts: [\n\t'a'\n\t'b'\n]\nport: 8080\n", SomlSerializer.Serialize(server, new SomlSerializerOptions { Canonical = true }));
	}

	[Fact]
	public void ValuesOfUnknownShape()
	{
		var document = SomlValue.Parse("port: 8080\nhosts: ['a', 'b']");

		Assert.Equal(8080, document["port"].GetInt64());
		Assert.Equal("b", document["hosts"][1].GetString());
		Assert.Equal(SomlValueKind.Integer, document["port"].Kind);

		SomlArray ports = [80, 443];

		var server = new SomlObject
		{
			["host"] = "localhost",
			["ports"] = ports,
			["timeout"] = SomlDuration.Parse("30s"),
		};

		Assert.Equal(3, server.Count);
	}

	[Fact]
	public void ChangeADocument()
	{
		var text = "/* The edge service. */\nport: 0x1F90 # The default.\nserver: {host: 'a'}\n";

		text = SomlEditor.Set(text, ["port"], 8080);
		text = SomlEditor.Set(text, ["server", "port"], 443);
		text = SomlEditor.Set(text, ["replicas"], 3);

		Assert.Equal("/* The edge service. */\nport: 8080 # The default.\nserver: {host: 'a', port: 443}\nreplicas: 3\n", text);
	}

	[Fact]
	public void Format()
	{
		Assert.Equal("a: {b: 1, c: [2, 3]} # Note.\n", SomlFormatter.Format("a: {b: 1, c: [2,3]}  # Note."));
		Assert.Equal("a: {\n\tb: 1\n\tc: [2, 3]\n}\n", SomlFormatter.Format("a: {\nb: 1, c: [2,3]}"));
	}

	[Fact]
	public void Errors()
	{
		var error = Assert.Throws<SomlException>(() => SomlSerializer.Deserialize<Postgres>("host: 'a'\nport: 70000"));
		Assert.Equal("The int 70000 is outside the range of ushort (0 to 65535) (at port, line 2, column 7)", error.Message);

		var parseError = Assert.Throws<SomlParseException>(() => SomlValue.Parse("name: api-gateway"));
		Assert.Equal("Unexpected “api-gateway”. A string value must be quoted, as in 'api-gateway' at line 1, column 7\n\n> 1 | name: api-gateway\n    |       ^", parseError.Message);
	}
}
