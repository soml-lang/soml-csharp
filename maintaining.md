# Maintaining

## Publish a new version

The [Release workflow](.github/workflows/release.yml) publishes `Soml`, `Soml.Extensions.Configuration`, and `Soml.Cli` with [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), so there is no API key to keep. The trusted publishing policy on nuget.org is already set up.

For each version:

1. Run the Release workflow on `main` from the Actions tab, or with `gh workflow run release.yml -f version=1.2.3`. It tests, publishes the three packages with that version, and pushes the `v1.2.3` tag. The project files have no version, so there is nothing to bump.
2. Create a release on GitHub.

A published version cannot be deleted or reused. It can only be unlisted on nuget.org.
