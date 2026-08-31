using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace NuGetToCompLog.Services.SourceBuild;

/// <summary>
/// What comparing two assemblies' public surfaces found.
/// </summary>
public record SurfaceComparison(
    bool Matches,
    IReadOnlyList<string> MissingFromRebuild,
    IReadOnlyList<string> AddedByRebuild,
    IReadOnlyList<string> ReferenceDifferences,
    int SurfaceSize);

/// <summary>
/// Compares the publicly visible surface of two assemblies - every public and protected type,
/// member and signature - plus the identities of the assemblies they reference.
///
/// This exists because a byte comparison stops meaning anything once a different compiler is
/// involved. Two Roslyn versions compiling identical source produce different bytes as a matter
/// of course, so "the bytes differ" cannot distinguish a harmless codegen change from a rebuild
/// that dropped a type because a source file went missing. The surface can: it is unchanged by
/// codegen and changes precisely when the rebuild is no longer the same library to anything that
/// consumes it.
///
/// It is not a proof of identical behaviour - two assemblies can share a surface and differ
/// inside. It is the strongest compiler-independent evidence available, and it is checked
/// alongside the reconstruction ledger, which separately accounts for every input that went in.
/// </summary>
public static class AssemblySurfaceComparer
{
    public static SurfaceComparison Compare(string originalPath, string rebuiltPath)
    {
        var original = ReadSurface(originalPath);
        var rebuilt = ReadSurface(rebuiltPath);

        var missing = original.Except(rebuilt).Order(StringComparer.Ordinal).ToList();
        var added = rebuilt.Except(original).Order(StringComparer.Ordinal).ToList();

        // A rebuild that binds to a different version of a dependency is a different library even
        // when its own surface is untouched, because the types flowing across that boundary are
        // not the same types. Differing MVIDs at the same identity are fine - that is one
        // dependency rebuilt, not a different dependency.
        var originalReferences = ReadAssemblyReferences(originalPath);
        var rebuiltReferences = ReadAssemblyReferences(rebuiltPath);
        var referenceDifferences = originalReferences.Except(rebuiltReferences)
            .Select(r => $"only the original references {r}")
            .Concat(rebuiltReferences.Except(originalReferences).Select(r => $"only the rebuild references {r}"))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new SurfaceComparison(
            missing.Count == 0 && added.Count == 0 && referenceDifferences.Count == 0,
            missing, added, referenceDifferences, original.Count);
    }

    /// <summary>
    /// Every publicly visible type and member, rendered so that two assemblies agree exactly when
    /// a consumer could not tell them apart at compile time.
    /// </summary>
    public static SortedSet<string> ReadSurface(string assemblyPath)
    {
        var surface = new SortedSet<string>(StringComparer.Ordinal);

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();
        var provider = new TypeNameProvider();

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!IsVisible(reader, type))
            {
                continue;
            }

            var typeName = FullName(reader, handle);
            var baseType = type.BaseType.IsNil ? "" : $" : {TypeName(reader, type.BaseType, provider)}";
            var interfaces = type.GetInterfaceImplementations()
                .Select(i => TypeName(reader, reader.GetInterfaceImplementation(i).Interface, provider))
                .Order(StringComparer.Ordinal)
                .ToList();
            var implements = interfaces.Count == 0 ? "" : $" implements {string.Join(",", interfaces)}";
            surface.Add($"T:{typeName}{Modifiers(type.Attributes)}{baseType}{implements}");

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (!IsVisible(method.Attributes))
                {
                    continue;
                }
                var signature = method.DecodeSignature(provider, null);
                var parameters = string.Join(", ", signature.ParameterTypes);
                var generics = signature.GenericParameterCount > 0 ? $"`{signature.GenericParameterCount}" : "";
                surface.Add($"M:{typeName}.{reader.GetString(method.Name)}{generics}({parameters}) : {signature.ReturnType}");
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (!IsVisible(field.Attributes))
                {
                    continue;
                }
                surface.Add($"F:{typeName}.{reader.GetString(field.Name)} : {field.DecodeSignature(provider, null)}");
            }

            // Properties and events are accessor pairs at the IL level, but a consumer binds to
            // them by name, so a rebuild that turned one into a field would otherwise look fine.
            foreach (var propertyHandle in type.GetProperties())
            {
                var property = reader.GetPropertyDefinition(propertyHandle);
                var accessors = property.GetAccessors();
                if (!HasVisibleAccessor(reader, accessors.Getter, accessors.Setter))
                {
                    continue;
                }
                var signature = property.DecodeSignature(provider, null);
                surface.Add($"P:{typeName}.{reader.GetString(property.Name)}({string.Join(", ", signature.ParameterTypes)}) : {signature.ReturnType}");
            }

            foreach (var eventHandle in type.GetEvents())
            {
                var @event = reader.GetEventDefinition(eventHandle);
                var accessors = @event.GetAccessors();
                if (!HasVisibleAccessor(reader, accessors.Adder, accessors.Remover))
                {
                    continue;
                }
                surface.Add($"E:{typeName}.{reader.GetString(@event.Name)} : {TypeName(reader, @event.Type, provider)}");
            }
        }

        return surface;
    }

    /// <summary>
    /// The assembly version this assembly was compiled against, per referenced assembly name.
    ///
    /// This is the only precise record of which build of a dependency took part in a compilation.
    /// A PDB's metadata references carry file names and MVIDs but no versions, and a nuspec states
    /// a range rather than what was resolved - so when a reference has to be found on nuget.org,
    /// this is what says which version to look for.
    /// </summary>
    public static Dictionary<string, Version> ReadReferencedAssemblyVersions(string assemblyPath)
    {
        var versions = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        if (!peReader.HasMetadata)
        {
            return versions;
        }

        var reader = peReader.GetMetadataReader();
        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            versions[reader.GetString(reference.Name)] = reference.Version;
        }

        return versions;
    }

    /// <summary>The identity of every assembly this one references, without the MVID.</summary>
    public static SortedSet<string> ReadAssemblyReferences(string assemblyPath)
    {
        var references = new SortedSet<string>(StringComparer.Ordinal);

        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        foreach (var handle in reader.AssemblyReferences)
        {
            var reference = reader.GetAssemblyReference(handle);
            var token = reference.PublicKeyOrToken.IsNil
                ? "null"
                : Convert.ToHexStringLower(reader.GetBlobBytes(reference.PublicKeyOrToken));
            references.Add($"{reader.GetString(reference.Name)}, {reference.Version}, {token}");
        }

        return references;
    }

    private static bool HasVisibleAccessor(MetadataReader reader, params MethodDefinitionHandle[] accessors) =>
        accessors.Any(a => !a.IsNil && IsVisible(reader.GetMethodDefinition(a).Attributes));

    private static bool IsVisible(MethodAttributes attributes) =>
        (attributes & MethodAttributes.MemberAccessMask) is
            MethodAttributes.Public or MethodAttributes.Family or MethodAttributes.FamORAssem;

    private static bool IsVisible(FieldAttributes attributes) =>
        (attributes & FieldAttributes.FieldAccessMask) is
            FieldAttributes.Public or FieldAttributes.Family or FieldAttributes.FamORAssem;

    /// <summary>
    /// A nested type is only reachable if everything containing it is, so visibility walks all
    /// the way out rather than looking at the type alone.
    /// </summary>
    private static bool IsVisible(MetadataReader reader, TypeDefinition type)
    {
        while (true)
        {
            switch (type.Attributes & TypeAttributes.VisibilityMask)
            {
                case TypeAttributes.Public:
                    return true;
                case TypeAttributes.NestedPublic:
                case TypeAttributes.NestedFamily:
                case TypeAttributes.NestedFamORAssem:
                    var declaring = type.GetDeclaringType();
                    if (declaring.IsNil)
                    {
                        return false;
                    }
                    type = reader.GetTypeDefinition(declaring);
                    continue;
                default:
                    return false;
            }
        }
    }

    private static string Modifiers(TypeAttributes attributes)
    {
        var parts = new List<string>();
        if ((attributes & TypeAttributes.Interface) != 0)
        {
            parts.Add("interface");
        }
        if ((attributes & TypeAttributes.Abstract) != 0)
        {
            parts.Add("abstract");
        }
        if ((attributes & TypeAttributes.Sealed) != 0)
        {
            parts.Add("sealed");
        }
        return parts.Count == 0 ? "" : $" [{string.Join(" ", parts)}]";
    }

    private static string FullName(MetadataReader reader, TypeDefinitionHandle handle)
    {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return $"{FullName(reader, declaring)}+{name}";
        }
        var @namespace = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    private static string TypeName(MetadataReader reader, EntityHandle handle, TypeNameProvider provider) =>
        handle.Kind switch
        {
            HandleKind.TypeDefinition => FullName(reader, (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => FullName(reader, (TypeReferenceHandle)handle),
            HandleKind.TypeSpecification =>
                reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(provider, null),
            _ => "?",
        };

    private static string FullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var reference = reader.GetTypeReference(handle);
        var name = reader.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{FullName(reader, (TypeReferenceHandle)reference.ResolutionScope)}+{name}";
        }
        var @namespace = reader.GetString(reference.Namespace);
        return string.IsNullOrEmpty(@namespace) ? name : $"{@namespace}.{name}";
    }

    /// <summary>Renders signature blobs as stable strings; identity is all that is needed here.</summary>
    private sealed class TypeNameProvider : ISignatureTypeProvider<string, object?>
    {
        public string GetArrayType(string elementType, ArrayShape shape) =>
            $"{elementType}[{new string(',', shape.Rank - 1)}]";

        public string GetByReferenceType(string elementType) => $"{elementType}&";

        public string GetFunctionPointerType(MethodSignature<string> signature) =>
            $"delegate*<{string.Join(", ", signature.ParameterTypes.Append(signature.ReturnType))}>";

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) =>
            $"{genericType}<{string.Join(", ", typeArguments)}>";

        public string GetGenericMethodParameter(object? genericContext, int index) => $"!!{index}";

        public string GetGenericTypeParameter(object? genericContext, int index) => $"!{index}";

        // Custom modifiers (modreq/modopt) carry no meaning a consumer binds to by name.
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetPinnedType(string elementType) => elementType;

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            FullName(reader, handle);

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
            FullName(reader, handle);

        public string GetTypeFromSpecification(
            MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
    }
}
