# Testing the Tool

To test the PDB extraction capabilities with a package that has embedded symbols, you can create a test package:

## Create a Test Package with Embedded Symbols

```bash
# Create a test library
mkdir TestLibrary
cd TestLibrary

dotnet new classlib
# Edit the .csproj to enable deterministic builds and embedded symbols

dotnet pack -c Release
# This creates a .nupkg with embedded PDBs

cd ..
dotnet run -- TestLibrary 1.0.0 --source ./TestLibrary/bin/Release
```

## Edit TestLibrary.csproj

Add these properties to enable full symbol embedding:

```xml
<PropertyGroup>
  <Deterministic>true</Deterministic>
  <EmbedAllSources>true</EmbedAllSources>
  <DebugType>embedded</DebugType>
  <ContinuousIntegrationBuild>true</ContinuousIntegrationBuild>
  <PublishRepositoryUrl>true</PublishRepositoryUrl>
</PropertyGroup>
```

## Known Packages with Good Symbol Support

Some packages that typically include symbols (check nuget.org for .snupkg availability):

- Recent Microsoft.* packages often have symbol packages
- Roslyn.* packages
- Many modern OSS packages on GitHub with proper CI/CD

However, many packages on nuget.org still don't include portable PDBs or symbols packages, which limits what can be extracted.
