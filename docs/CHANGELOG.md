# Changelog

All notable changes to the NuGet to CompLog tool.

## [Unreleased]

### Major Refactoring (2025-10-13)

#### Architecture Improvements
- **Complete code refactoring** from monolithic classes to clean architecture with SOLID principles
- Reduced largest class from 1,340 lines to 315 lines (76% reduction)
- Extracted 32 new files organized into 6 clear architectural layers
- Implemented dependency injection using Microsoft.Extensions.DependencyInjection
- All services now interface-based and fully testable

#### New Architecture Layers
1. **Domain** (6 files) - Immutable value objects: PackageIdentity, CompilationInfo, PdbMetadata, SourceFileInfo, TargetFrameworkInfo, ReferenceAssemblyInfo
2. **Abstractions** (7 files) - Service interfaces: INuGetClient, IFileSystemService, IConsoleWriter, IPdbReader, ISourceFileDownloader, ITargetFrameworkSelector, IReferenceResolver
3. **Exceptions** (5 files) - Custom exceptions with rich context
4. **Infrastructure** (3 files) - External concerns: FileSystemService, SpectreConsoleWriter, HttpSourceFileDownloader
5. **Services** (12 files) - Business logic split into focused services (NuGet, PDB, References, CompLog)
6. **Commands** (2 files) - Command pattern: ProcessPackageCommand and ProcessPackageCommandHandler

#### Removed Dead Code
- Removed `CompilerArgumentsExtractor.cs` (434 lines) - replaced by ProcessPackageCommandHandler
- Removed `PdbCompilerArgumentsExtractor.cs` (1,340 lines) - replaced by 4 focused PDB services
- Removed `CompLogCreator.cs` (179 lines) - replaced by CompLogStructureCreator
- Updated all tests to use new architecture

#### CLI Framework Integration
- Integrated **ConsoleAppFramework v5.6.1** for modern CLI handling
- Auto-generated help from XML documentation
- Source generator-based argument parsing (zero reflection, zero overhead)
- Support for both positional and named parameters
- Built-in cancellation token support

#### Enhanced Console Features
- Added ConEmu/Windows Terminal progress indicators using ANSI escape sequences
- Progress indicators appear in terminal title bar and Windows taskbar
- Automatic progress reporting in `ExecuteWithStatusAsync`
- Manual progress control methods: SetProgress, SetIndeterminateProgress, SetErrorProgress, ClearProgress
- Graceful degradation on terminals that don't support progress sequences

### Dependencies Added
- Microsoft.Extensions.DependencyInjection (9.0.9)
- ConsoleAppFramework (5.6.1)

### Build Status
- ✅ All tests passing (12 passed, 1 skipped)
- ✅ Zero compilation errors
- ✅ All functionality preserved

---

### Added - Enhanced Console UI
- **Spectre.Console Integration**: Beautiful, rich console output with colors and formatting
  - FIGlet ASCII art banner for professional branding
  - Colored, bordered tables for structured information
  - Tree views for hierarchical data (assemblies, source files)
  - Panels for grouping related content with headers
  - Animated progress spinners for async operations
  - Color-coded status messages (green=success, yellow=warning, cyan=info)
  - Much improved visual organization and readability

### Enhanced
- Help screen now uses formatted tables instead of plain text
- Package processing shows animated spinners during downloads
- Assembly list displayed as a tree showing framework targets
- PDB extraction results shown in attractive panels
- Metadata references displayed in formatted tables
- Source files shown as collapsible tree structures
- Next steps and TODOs presented in bordered panels

## [Initial Release]

### Added - Core Functionality
- Download NuGet packages (.nupkg) from nuget.org
- Download symbol packages (.snupkg) when available
- Extract package contents to temporary directories
- Discover PDB files (embedded, external, or in symbol packages)
- Parse portable PDB format using System.Reflection.Metadata
- Extract compiler command-line arguments
- Extract metadata references (assembly dependencies)
- Extract source file listings from PDBs
- Extract Source Link configuration for git mappings
- Display embedded source information

### Documentation
- Comprehensive README with usage examples
- ARCHITECTURE.md with deep technical details (16K words)
- EXAMPLES.md with practical usage scenarios
- TESTING.md with testing guidance
- PROJECT_SUMMARY.md with overview (8K words)
- QUICKSTART.md for rapid onboarding
- Extensive inline code comments
- TODO comments outlining future work

### Technical
- Built with .NET 10 (compatible with .NET 6+)
- Uses NuGet.Protocol for package management
- System.Reflection.Metadata for PDB parsing
- System.IO.Compression for package extraction
- Spectre.Console for rich terminal UI
- Clean architecture with separation of concerns
- Comprehensive error handling
- Async/await throughout

### Known Limitations
- Only supports portable PDB format (not Windows PDB)
- Requires symbols to be available (not all packages include them)
- No dependency resolution yet
- No framework assembly resolution yet
- No source code extraction from Source Link yet
- No CompLog file packaging yet
- Multi-targeting packages process all TFMs separately

## Future Plans

### Planned Features
1. **Dependency Resolution**
   - Parse .nuspec files
   - Recursive package downloads
   - Version conflict resolution

2. **Framework Assembly Resolution**
   - Identify target frameworks
   - Download reference assemblies
   - Handle multiple framework versions

3. **Source Code Extraction**
   - Extract embedded sources from PDBs
   - Download sources via Source Link
   - Preserve directory structures

4. **CompLog Packaging**
   - Define CompLog file format
   - Package all artifacts
   - Enable workspace recreation
   - Validation and testing

See inline TODO comments in the code for detailed implementation notes.
