using Microsoft.Extensions.FileProviders;
using Soml.Extensions.Configuration;

// In the namespace of `IConfigurationBuilder`, as `AddJsonFile` is, so that the methods are found without a `using`.
namespace Microsoft.Extensions.Configuration;

/// <summary>
/// Adds SOML files and streams to an <see cref="IConfigurationBuilder"/>.
/// </summary>
public static class SomlConfigurationExtensions
{
	/// <summary>
	/// Adds a SOML file to the configuration.
	/// </summary>
	/// <param name="builder">The builder.</param>
	/// <param name="path">The path of the file, relative to the base path of the builder.</param>
	public static IConfigurationBuilder AddSomlFile(this IConfigurationBuilder builder, string path) => builder.AddSomlFile(null, path, optional: false, reloadOnChange: false);

	/// <summary>
	/// Adds a SOML file to the configuration.
	/// </summary>
	/// <param name="builder">The builder.</param>
	/// <param name="path">The path of the file, relative to the base path of the builder.</param>
	/// <param name="optional">Whether a missing file is fine.</param>
	public static IConfigurationBuilder AddSomlFile(this IConfigurationBuilder builder, string path, bool optional) => builder.AddSomlFile(null, path, optional, reloadOnChange: false);

	/// <summary>
	/// Adds a SOML file to the configuration.
	/// </summary>
	/// <param name="builder">The builder.</param>
	/// <param name="path">The path of the file, relative to the base path of the builder.</param>
	/// <param name="optional">Whether a missing file is fine.</param>
	/// <param name="reloadOnChange">Whether to load the file again when it changes.</param>
	public static IConfigurationBuilder AddSomlFile(this IConfigurationBuilder builder, string path, bool optional, bool reloadOnChange) => builder.AddSomlFile(null, path, optional, reloadOnChange);

	/// <summary>
	/// Adds a SOML file to the configuration.
	/// </summary>
	/// <param name="builder">The builder.</param>
	/// <param name="provider">The file provider to read the file with, or <c>null</c> for the builder's.</param>
	/// <param name="path">The path of the file, relative to the base path of the file provider.</param>
	/// <param name="optional">Whether a missing file is fine.</param>
	/// <param name="reloadOnChange">Whether to load the file again when it changes.</param>
	public static IConfigurationBuilder AddSomlFile(this IConfigurationBuilder builder, IFileProvider? provider, string path, bool optional, bool reloadOnChange)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentException.ThrowIfNullOrEmpty(path);

		var source = new SomlConfigurationSource
		{
			FileProvider = provider,
			Path = path,
			Optional = optional,
			ReloadOnChange = reloadOnChange,
		};
		source.ResolveFileProvider();
		return builder.Add(source);
	}

	/// <summary>
	/// Adds a SOML file to the configuration, with a source that <paramref name="configureSource"/> sets up.
	/// </summary>
	public static IConfigurationBuilder AddSomlFile(this IConfigurationBuilder builder, Action<SomlConfigurationSource>? configureSource) => builder.Add(configureSource);

	/// <summary>
	/// Adds a stream of a SOML document, in UTF-8, to the configuration.
	/// </summary>
	public static IConfigurationBuilder AddSomlStream(this IConfigurationBuilder builder, Stream stream)
	{
		ArgumentNullException.ThrowIfNull(builder);
		ArgumentNullException.ThrowIfNull(stream);
		return builder.Add(new SomlStreamConfigurationSource { Stream = stream });
	}
}
