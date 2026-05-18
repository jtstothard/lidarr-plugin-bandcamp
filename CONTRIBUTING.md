# Contributing

## Building from source

You need the .NET 8 SDK (or newer).

```bash
# Clone with Lidarr references
git clone https://github.com/jtstothard/lidarr-plugin-bandcamp.git
cd lidarr-plugin-bandcamp
git clone --depth 1 --branch nightly https://github.com/Lidarr/Lidarr.git ext/Lidarr

# Build
dotnet restore Lidarr.Plugin.Bandcamp.slnx -p:TreatWarningsAsErrors=false
dotnet build Lidarr.Plugin.Bandcamp.slnx -c Release -p:TreatWarningsAsErrors=false

# Test
dotnet test Lidarr.Plugin.Bandcamp.slnx -c Release --filter "FullyQualifiedName~Bandcamp" -p:TreatWarningsAsErrors=false
```

The output DLL ends up at `src/Lidarr.Plugin.Bandcamp/bin/Release/net8.0/Lidarr.Plugin.Bandcamp.dll`.

## Pull requests

- PRs go to the `main` branch.
- Keep changes focused. One thing per PR is easier to review.
- Run the build and tests before pushing. CI will catch it anyway, but it's faster to fix locally.
- If you're changing behavior, add or update tests to cover it.

## Releases

Tagging a commit with `v*` (like `v1.0.19`) triggers CI to build, test, and publish a GitHub Release with the zipped plugin DLL.

## Reporting issues

Use the GitHub issue templates. Please include:

- Plugin version (check System → Plugins)
- Lidarr version and branch (must be nightly)
- What you expected to happen and what actually happened
- Trace logs if something is crashing or failing (Settings → General → Log Level → Trace)
