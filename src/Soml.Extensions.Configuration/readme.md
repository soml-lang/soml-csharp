# Soml.Extensions.Configuration

> [SOML](https://github.com/soml-lang/soml) configuration files for `Microsoft.Extensions.Configuration`

```sh
dotnet add package Soml.Extensions.Configuration
```

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddSomlFile("appsettings.soml", optional: false, reloadOnChange: true);
```

A document becomes configuration as JSON does with `AddJsonFile`: nested keys are joined with `:`, array items are keyed by their index (`servers:0:host`), an empty object is a key with a null value, and an empty array is a key with an empty string. Configuration keys are case-insensitive, so two flattened keys that differ only in case are an error. Keys containing `:` can also collide with nested paths.

Values are strings, as the binder expects: an int and a float in the invariant culture, a bool as `True` or `False`, an instant in canonical form, and a duration as `TimeSpan` writes itself, such as `01:30:00`, so that it binds to a `TimeSpan`. A duration finer than the 100 nanoseconds of a `TimeSpan` stays in SOML form, such as `1ns`, and does not bind to one. An instant finer than 100 nanoseconds binds to a `DateTimeOffset` rounded to 100 nanoseconds, except one that rounds past the end of year 9999, which does not bind.

Keys stay as they are written, as the SOML spec asks, so a kebab-case key such as `allowed-origins` does not bind to a property named `AllowedOrigins`. Name the key with `[ConfigurationKeyName("allowed-origins")]`, or read the file with `SomlSerializer` from the `Soml` package, which maps kebab-case keys by default.
