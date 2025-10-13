# NuGet to CompLog - Refactoring Specification

## Overview
This document outlines the refactoring plan to transform the NuGet to CompLog codebase into a modern, clean, testable C# application without introducing additional dependencies.

## Goals
1. **Improve Testability**: Enable unit testing through dependency inversion and interface extraction
2. **Increase Maintainability**: Break down large classes into focused, single-responsibility components
3. **Enhance Readability**: Clear separation of concerns and consistent code organization
4. **Preserve Functionality**: All existing features must continue to work identically
5. **No New Dependencies**: Use only existing packages and built-in .NET features

## Constraints
- No additional NuGet package dependencies
- No DI container framework (use manual dependency injection)
- Preserve all existing functionality and behavior
- Maintain backward compatibility with existing tests

## Phase 1: Foundation - Interfaces and Value Objects

### 1.1 Create Domain Models (Value Objects)
**Location**: `src/NuGetToCompLog/Domain/`

#### New Record Types
- `PackageIdentity` - Immutable package identifier
- `CompilationInfo` - Compiler arguments and metadata
- `PdbMetadata` - Extracted PDB information
- `SourceFileInfo` - Source file details
- `TargetFrameworkInfo` - TFM with priority/version
- `ReferenceAssemblyInfo` - Reference assembly details

### 1.2 Extract Core Interfaces
**Location**: `src/NuGetToCompLog/Abstractions/`

#### Service Interfaces
- `INuGetClient` - Package download operations
- `IPdbReader` - PDB reading and parsing
- `IFileSystemService` - File/directory operations
- `IConsoleWriter` - Console output abstraction
- `ICompilerArgumentsParser` - Parse compiler arguments
- `IMetadataReferenceParser` - Parse metadata references
- `ISourceFileDownloader` - Download source files
- `IReferenceResolver` - Resolve reference assemblies
- `ITargetFrameworkSelector` - Select best TFM
- `ICompLogBuilder` - Build CompLog files

## Phase 2: Service Extraction

### 2.1 Break Up PdbCompilerArgumentsExtractor (1340 lines)
**Current responsibilities**: PDB parsing, source download, reference acquisition, console output

**New structure**:
```
Services/Pdb/
├── PdbReaderService.cs              # Read PDB files (embedded/external)
├── CompilationOptionsExtractor.cs   # Extract compiler options from PDB
├── MetadataReferenceExtractor.cs    # Extract metadata references
├── SourceLinkParser.cs              # Parse Source Link JSON
└── PdbDiscoveryService.cs           # Find PDB files
```

### 2.2 Break Up CompilerArgumentsExtractor (434 lines)
**Current responsibilities**: Package download, extraction, TFM selection, orchestration

**New structure**:
```
Services/NuGet/
├── NuGetClientService.cs            # Download packages
├── PackageExtractionService.cs      # Extract .nupkg files
└── TargetFrameworkSelector.cs       # TFM selection logic
```

### 2.3 Refactor ReferenceAssemblyAcquisitionService (935 lines)
**Current responsibilities**: Framework refs, NuGet deps, version resolution

**New structure**:
```
Services/References/
├── FrameworkReferenceResolver.cs    # Resolve framework refs
├── NuGetReferenceResolver.cs        # Resolve NuGet refs
├── DependencyResolver.cs            # Transitive dependencies
├── VersionResolver.cs               # Version range resolution
└── ReferenceAssemblyAcquisitionService.cs  # Orchestrator (reduced)
```

### 2.4 Create Infrastructure Services
**Location**: `src/NuGetToCompLog/Infrastructure/`

```
Infrastructure/
├── FileSystem/
│   └── FileSystemService.cs         # File/directory operations
├── Console/
│   └── SpectreConsoleWriter.cs      # Spectre.Console wrapper
└── SourceDownload/
    └── HttpSourceFileDownloader.cs  # HTTP download for sources
```

### 2.5 Create Orchestration Layer
**Location**: `src/NuGetToCompLog/Services/Orchestration/`

```
Services/Orchestration/
└── PackageProcessingOrchestrator.cs  # Main workflow coordinator
```

## Phase 3: Static Methods to Instance Methods

### 3.1 CompLogCreator
- Convert static methods to instance methods
- Inject dependencies (IFileSystemService, IConsoleWriter)
- Return strongly-typed results instead of string paths

### 3.2 CompLogFileCreator
- Convert static methods to instance methods
- Extract compiler path finding logic
- Inject dependencies

### 3.3 MetadataReferenceParser
- Keep static Parse methods (pure functions)
- Extract to separate utility class if needed

## Phase 4: Improve Error Handling

### 4.1 Custom Exception Types
**Location**: `src/NuGetToCompLog/Exceptions/`

```csharp
public class NuGetPackageNotFoundException : Exception { }
public class PdbNotFoundException : Exception { }
public class PdbExtractionException : Exception { }
public class ReferenceResolutionException : Exception { }
public class CompLogCreationException : Exception { }
```

### 4.2 Result Pattern (without external dependencies)
Create a simple `Result<T>` type for expected failures:

```csharp
public class Result<T>
{
    public bool IsSuccess { get; }
    public T? Value { get; }
    public string? Error { get; }
    public Exception? Exception { get; }
}
```

## Phase 5: Refactor Program.cs

### 5.1 Command Pattern
**Location**: `src/NuGetToCompLog/Commands/`

```csharp
public record ProcessPackageCommand(string PackageId, string? Version);

public class ProcessPackageCommandHandler
{
    // Orchestrates entire workflow
    public async Task<Result<string>> HandleAsync(ProcessPackageCommand command);
}
```

### 5.2 Dependency Setup
Create a simple composition root in Program.cs:

```csharp
// Manual dependency injection
var fileSystem = new FileSystemService();
var console = new SpectreConsoleWriter();
var nugetClient = new NuGetClientService(fileSystem);
// ... etc
var handler = new ProcessPackageCommandHandler(/* dependencies */);
```

## Phase 6: Configuration Externalization

### 6.1 Configuration Class
**Location**: `src/NuGetToCompLog/Configuration/`

```csharp
public class AppConfiguration
{
    public string NuGetFeedUrl { get; init; }
    public string WorkingDirectoryRoot { get; init; }
    public TimeSpan HttpTimeout { get; init; }
    public int MaxConcurrentDownloads { get; init; }
    // ... etc
}
```

Load from environment variables or defaults (no config file dependencies needed).

## Phase 7: Code Organization

### Final Directory Structure
```
src/NuGetToCompLog/
├── Abstractions/              # Interfaces
│   ├── INuGetClient.cs
│   ├── IPdbReader.cs
│   ├── IFileSystemService.cs
│   ├── IConsoleWriter.cs
│   └── ... (other interfaces)
├── Commands/                  # Command pattern
│   ├── ProcessPackageCommand.cs
│   └── ProcessPackageCommandHandler.cs
├── Configuration/             # App configuration
│   └── AppConfiguration.cs
├── Domain/                    # Value objects and domain models
│   ├── PackageIdentity.cs
│   ├── CompilationInfo.cs
│   ├── PdbMetadata.cs
│   ├── SourceFileInfo.cs
│   ├── TargetFrameworkInfo.cs
│   └── ReferenceAssemblyInfo.cs
├── Exceptions/                # Custom exceptions
│   ├── NuGetPackageNotFoundException.cs
│   ├── PdbNotFoundException.cs
│   └── ... (other exceptions)
├── Extensions/                # Extension methods
│   └── PathExtensions.cs
├── Infrastructure/            # External concerns
│   ├── Console/
│   │   └── SpectreConsoleWriter.cs
│   ├── FileSystem/
│   │   └── FileSystemService.cs
│   └── SourceDownload/
│       └── HttpSourceFileDownloader.cs
├── Services/                  # Business logic
│   ├── CompLog/
│   │   ├── CompLogCreator.cs
│   │   └── CompLogFileCreator.cs
│   ├── NuGet/
│   │   ├── NuGetClientService.cs
│   │   ├── PackageExtractionService.cs
│   │   └── TargetFrameworkSelector.cs
│   ├── Orchestration/
│   │   └── PackageProcessingOrchestrator.cs
│   ├── Pdb/
│   │   ├── PdbReaderService.cs
│   │   ├── CompilationOptionsExtractor.cs
│   │   ├── MetadataReferenceExtractor.cs
│   │   ├── SourceLinkParser.cs
│   │   └── PdbDiscoveryService.cs
│   └── References/
│       ├── FrameworkReferenceResolver.cs
│       ├── NuGetReferenceResolver.cs
│       ├── DependencyResolver.cs
│       ├── VersionResolver.cs
│       └── ReferenceAssemblyAcquisitionService.cs
├── Utilities/                 # Pure utility functions
│   ├── MetadataReferenceParser.cs (existing)
│   └── CompilerArgumentsParser.cs
└── Program.cs                 # Entry point with composition root
```

## Implementation Strategy

### Order of Execution
1. **Phase 1**: Create interfaces and domain models (non-breaking)
2. **Phase 2**: Extract services one at a time, keeping old code working
3. **Phase 3**: Convert static methods (breaking but localized)
4. **Phase 4**: Add error handling improvements
5. **Phase 5**: Refactor Program.cs
6. **Phase 6**: Externalize configuration
7. **Phase 7**: Final reorganization and cleanup

### Testing Strategy
- Run existing tests after each phase
- Add new unit tests for new services as they're created
- Ensure all integration tests continue passing
- Add tests for edge cases uncovered during refactoring

### Migration Approach
For each large class being refactored:
1. Create new service interfaces
2. Create concrete implementations with injected dependencies
3. Add tests for new implementations
4. Update calling code to use new services
5. Remove old code once fully migrated
6. Verify all tests pass

## Key Principles

### Single Responsibility Principle
Each class should have one reason to change:
- PdbReaderService: Read PDB files only
- SourceLinkParser: Parse Source Link JSON only
- NuGetClientService: Download packages only

### Dependency Inversion
- High-level modules depend on abstractions (interfaces)
- Low-level modules implement abstractions
- No direct dependencies on infrastructure

### Open/Closed Principle
- Services open for extension (via interfaces)
- Closed for modification (stable contracts)

### Interface Segregation
- Small, focused interfaces
- No client should depend on methods it doesn't use

### Testability Requirements
Every service should be:
1. Constructible with mocked dependencies
2. Testable without file system access (use IFileSystemService)
3. Testable without network access (use INuGetClient)
4. Testable without console output (use IConsoleWriter)

## Success Criteria

### Code Quality Metrics
- No class over 300 lines
- No method over 50 lines
- All services have corresponding unit tests
- Test coverage > 70%
- No cyclic dependencies

### Functional Requirements
- All existing functionality preserved
- All existing tests pass
- No performance degradation
- Same console output experience

### Maintainability Improvements
- Clear separation of concerns
- Easy to add new features
- Easy to mock for testing
- Clear error messages
- Documented public APIs

## Non-Goals
- Performance optimization (unless regression)
- UI changes (preserve exact console output)
- Feature additions (pure refactoring)
- Breaking changes to public API

## Risks and Mitigations

### Risk: Breaking existing functionality
**Mitigation**: Run tests frequently, migrate incrementally, keep old code until fully verified

### Risk: Over-engineering
**Mitigation**: Follow YAGNI (You Aren't Gonna Need It), only extract what's needed

### Risk: Incomplete migration
**Mitigation**: Work in phases, each phase is a complete unit of work

### Risk: Merge conflicts during long refactoring
**Mitigation**: Work in small, focused PRs that can be merged quickly

## Timeline Estimate
- Phase 1: 2-3 hours
- Phase 2: 4-6 hours
- Phase 3: 2-3 hours
- Phase 4: 1-2 hours
- Phase 5: 1-2 hours
- Phase 6: 1 hour
- Phase 7: 1-2 hours

**Total**: 12-19 hours of focused work

## Approval and Sign-off
This specification should be reviewed and approved before implementation begins. Changes to the spec during implementation should be documented.

---

**Document Version**: 1.0  
**Date**: 2025-10-13  
**Status**: Draft - Awaiting Approval
