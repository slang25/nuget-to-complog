using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuGetToCompLog.Services;

/// <summary>
/// Reads the informational version (e.g. "5.6.0-2.26270.133+96856fd7...") from a compiler
/// assembly, matching the "compiler-version" value Roslyn records in PDB compilation options.
/// </summary>
public static class CompilerVersionReader
{
    private static readonly ConcurrentDictionary<string, string?> Cache = new();

    public static string? TryGetInformationalVersion(string assemblyPath) =>
        Cache.GetOrAdd(assemblyPath, static path =>
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var peReader = new PEReader(stream);
                var reader = peReader.GetMetadataReader();
                var assembly = reader.GetAssemblyDefinition();

                foreach (var handle in assembly.GetCustomAttributes())
                {
                    var attribute = reader.GetCustomAttribute(handle);
                    if (attribute.Constructor.Kind != HandleKind.MemberReference)
                    {
                        continue;
                    }

                    var ctor = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                    if (ctor.Parent.Kind != HandleKind.TypeReference)
                    {
                        continue;
                    }

                    var type = reader.GetTypeReference((TypeReferenceHandle)ctor.Parent);
                    if (reader.GetString(type.Name) != "AssemblyInformationalVersionAttribute")
                    {
                        continue;
                    }

                    // Attribute value blob: prolog (0x0001) followed by a SerString.
                    var blobReader = reader.GetBlobReader(attribute.Value);
                    if (blobReader.ReadUInt16() != 0x0001)
                    {
                        return null;
                    }
                    return blobReader.ReadSerializedString();
                }
            }
            catch
            {
            }

            return null;
        });
}
