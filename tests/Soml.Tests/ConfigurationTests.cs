using System.Text;
using Microsoft.Extensions.Configuration;

namespace Soml.Tests;

public sealed class ConfigurationTests
{
	private static IConfigurationRoot Load(string text) => new ConfigurationBuilder().AddSomlStream(new MemoryStream(Encoding.UTF8.GetBytes(text))).Build();

	[Fact]
	public void FlattensADocumentAsTheJsonProviderDoes()
	{
		var configuration = Load("""
			name: 'api'
			port: 8080
			ratio: 0.75
			debug: false
			owner: null
			timeout: 1m30s
			fine: 1ns
			released: 2026-09-19T16:00:00+02:00
			servers: [{host: 'a'}, {host: 'b'}]
			empty: {}
			none: []
			""");

		Assert.Equal("api", configuration["name"]);
		Assert.Equal("8080", configuration["port"]);
		Assert.Equal("0.75", configuration["ratio"]);
		Assert.Equal("False", configuration["debug"]);
		Assert.Null(configuration["owner"]);
		Assert.Equal("00:01:30", configuration["timeout"]);
		Assert.Equal("0.000000001s", configuration["fine"]);
		Assert.Equal("2026-09-19T14:00:00Z", configuration["released"]);
		Assert.Equal("b", configuration["servers:1:host"]);
		Assert.Equal("b", configuration["SERVERS:1:HOST"]);
		Assert.Null(configuration["empty"]);
		Assert.Equal("", configuration["none"]);
	}

	public sealed class Settings
	{
		public string Name { get; set; } = "";

		public int Port { get; set; }

		public TimeSpan Timeout { get; set; }

		public DateTimeOffset Released { get; set; }

		[ConfigurationKeyName("allowed-origins")]
		public List<string> AllowedOrigins { get; set; } = [];

		public double Limit { get; set; }
	}

	[Fact]
	public void BindsToAType()
	{
		var settings = Load("""
			name: 'api'
			port: 8080
			timeout: 30s
			released: 2026-09-19T14:00:00Z
			allowed-origins: ['https://example.com']
			limit: infinity
			""").Get<Settings>()!;

		Assert.Equal("api", settings.Name);
		Assert.Equal(8080, settings.Port);
		Assert.Equal(TimeSpan.FromSeconds(30), settings.Timeout);
		Assert.Equal(new DateTimeOffset(2026, 9, 19, 14, 0, 0, TimeSpan.Zero), settings.Released);
		Assert.Equal(["https://example.com"], settings.AllowedOrigins);
		Assert.Equal(double.PositiveInfinity, settings.Limit);

		// An instant finer than 100 nanoseconds is rounded, as the readme says.
		Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1_234_568), Load("released: 2026-01-01T00:00:00.123456789Z").Get<Settings>()!.Released);
	}

	[Fact]
	public void RejectsKeysThatDifferOnlyInCase()
	{
		var error = Assert.Throws<FormatException>(() => Load("port: 1\nPort: 2"));

		Assert.Equal("A duplicate key 'Port' was found. Configuration keys are case-insensitive", error.Message);
	}

	[Theory]
	[InlineData("\u001b")]
	[InlineData("\u009b")]
	[InlineData("\u202e")]
	[InlineData("\n")]
	public void CollisionErrorsEscapeCharactersThatAffectTerminals(string character)
	{
		var key = character + "Port";
		SomlValue document = new SomlObject { [key] = 1, [character + "port"] = 2 };
		var error = Assert.Throws<FormatException>(() => Load(document.ToDocumentString()));

		Assert.DoesNotContain(character, error.Message, StringComparison.Ordinal);
		Assert.Contains("port", error.Message, StringComparison.Ordinal);
	}

	[Theory]
	[InlineData("a: {b: 1}\nA: {b: 2}", "A:b")]
	[InlineData("'a:b': 1\na: {b: 2}", "a:b")]
	[InlineData("a: {b: {}}\n'A:B': 1", "A:B")]
	[InlineData("x: 1\nX: []", "X")]
	public void RejectsKeysThatCollideThroughDifferentPaths(string text, string key)
	{
		var error = Assert.Throws<FormatException>(() => Load(text));

		Assert.Equal($"A duplicate key '{key}' was found. Configuration keys are case-insensitive", error.Message);
	}

	[Fact]
	public void MergesObjectsWhoseKeysDifferOnlyInCase()
	{
		var configuration = Load("a: {b: 1}\nA: {c: 2}");

		Assert.Equal("1", configuration["A:b"]);
		Assert.Equal("2", configuration["a:c"]);
		Assert.Equal(["b", "c"], configuration.GetSection("a").GetChildren().Select(child => child.Key));
	}

	[Fact]
	public void FlattensNestedArraysAsTheJsonProviderDoes()
	{
		var configuration = Load("d: [[1, 2], [], [{}]]");

		Assert.Equal("2", configuration["d:0:1"]);
		Assert.Equal("", configuration["d:1"]);
		Assert.Null(configuration["d:2:0"]);
		Assert.Equal(["0", "1", "2"], configuration.GetSection("d").GetChildren().Select(child => child.Key));
	}

	[Fact]
	public void ReportsAnInvalidDocumentWithItsPosition()
	{
		var error = Assert.Throws<FormatException>(() => Load("port: 8080\nname: api"));

		Assert.Equal("Could not parse the SOML document: Unexpected “api”. A string value must be quoted, as in 'api' at line 2, column 7", error.Message);
		Assert.IsType<SomlParseException>(error.InnerException);
	}

	[Fact]
	public void ReloadingAFileReplacesItsPreviousValues()
	{
		var directory = Directory.CreateTempSubdirectory();

		try
		{
			var path = Path.Combine(directory.FullName, "app.soml");
			File.WriteAllText(path, "name: 'before'\nremoved: true\nservers: ['a', 'b']");
			using var configuration = Assert.IsType<ConfigurationRoot>(new ConfigurationBuilder().SetBasePath(directory.FullName).AddSomlFile("app.soml").Build());

			Assert.Equal("before", configuration["name"]);
			Assert.Equal("True", configuration["removed"]);
			Assert.Equal("b", configuration["servers:1"]);
			var reloadToken = configuration.GetReloadToken();

			File.WriteAllText(path, "name: 'after'\nservers: ['c']");
			configuration.Reload();

			Assert.True(reloadToken.HasChanged);
			Assert.Equal("after", configuration["name"]);
			Assert.Null(configuration["removed"]);
			Assert.Equal("c", configuration["servers:0"]);
			Assert.Null(configuration["servers:1"]);
			Assert.Equal(["0"], configuration.GetSection("servers").GetChildren().Select(child => child.Key));
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}

	[Fact]
	public void LoadsAFile()
	{
		var directory = Directory.CreateTempSubdirectory();

		try
		{
			File.WriteAllText(Path.Combine(directory.FullName, "app.soml"), "a: {b: 1}");

			var configuration = new ConfigurationBuilder().SetBasePath(directory.FullName).AddSomlFile("app.soml").AddSomlFile("missing.soml", optional: true).Build();

			Assert.Equal("1", configuration["a:b"]);
			Assert.Throws<FileNotFoundException>(() => new ConfigurationBuilder().SetBasePath(directory.FullName).AddSomlFile("missing.soml").Build());

			File.WriteAllText(Path.Combine(directory.FullName, "invalid.soml"), "a: b");
			var error = Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().SetBasePath(directory.FullName).AddSomlFile("invalid.soml").Build());
			Assert.Contains("invalid.soml", error.Message, StringComparison.Ordinal);
			Assert.IsType<FormatException>(error.InnerException);
		}
		finally
		{
			directory.Delete(recursive: true);
		}
	}
}
