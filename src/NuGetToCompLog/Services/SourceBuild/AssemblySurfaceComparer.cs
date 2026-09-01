using System.Collections.Immutable;
using System.Globalization;
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
            var typeConstraints = GenericParameters(reader, type.GetGenericParameters(), provider);
            surface.Add($"T:{typeName}{typeConstraints}{Modifiers(type.Attributes)}{baseType}{implements}");

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (!IsVisible(method.Attributes))
                {
                    continue;
                }
                var signature = method.DecodeSignature(provider, null);
                var parameters = string.Join(", ", signature.ParameterTypes);
                var generics = signature.GenericParameterCount > 0
                    ? $"`{signature.GenericParameterCount}{GenericParameters(reader, method.GetGenericParameters(), provider)}"
                    : "";
                surface.Add(
                    $"M:{typeName}.{reader.GetString(method.Name)}{generics}({parameters}) : {signature.ReturnType}" +
                    Modifiers(method.Attributes));
            }

            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (!IsVisible(field.Attributes))
                {
                    continue;
                }
                var constant = ConstantValue(reader, field);
                surface.Add(
                    $"F:{typeName}.{reader.GetString(field.Name)} : {field.DecodeSignature(provider, null)}" +
                    $"{constant}{Modifiers(field.Attributes)}");
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

    /// <summary>
    /// The parts of a method a consumer binds to beyond its signature. Making a method
    /// non-static, or narrowing it from public to protected, breaks callers that compile against
    /// it while leaving the rendered signature untouched - so accessibility and the
    /// static/abstract/virtual/sealed shape are part of its identity here.
    /// </summary>
    private static string Modifiers(MethodAttributes attributes)
    {
        var parts = new List<string>
        {
            (attributes & MethodAttributes.MemberAccessMask) switch
            {
                MethodAttributes.Public => "public",
                MethodAttributes.Family => "protected",
                MethodAttributes.FamORAssem => "protected internal",
                _ => "?",
            },
        };
        if ((attributes & MethodAttributes.Static) != 0)
        {
            parts.Add("static");
        }
        if ((attributes & MethodAttributes.Abstract) != 0)
        {
            parts.Add("abstract");
        }
        else if ((attributes & MethodAttributes.Virtual) != 0)
        {
            // NewSlot introduces a new virtual; without it the method overrides one it inherited,
            // and the two are not interchangeable to a derived type in another assembly.
            parts.Add((attributes & MethodAttributes.NewSlot) != 0 ? "virtual" : "override");
        }
        if ((attributes & MethodAttributes.Final) != 0)
        {
            parts.Add("sealed");
        }
        return $" [{string.Join(" ", parts)}]";
    }

    /// <summary>
    /// The same for a field. A const's value is compiled into every caller, so changing it
    /// changes what consumers do without changing anything a signature comparison would see;
    /// static and readonly are equally observable at the call site.
    /// </summary>
    private static string Modifiers(FieldAttributes attributes)
    {
        var parts = new List<string>
        {
            (attributes & FieldAttributes.FieldAccessMask) switch
            {
                FieldAttributes.Public => "public",
                FieldAttributes.Family => "protected",
                FieldAttributes.FamORAssem => "protected internal",
                _ => "?",
            },
        };
        if ((attributes & FieldAttributes.Literal) != 0)
        {
            parts.Add("const");
        }
        else if ((attributes & FieldAttributes.Static) != 0)
        {
            parts.Add("static");
        }
        if ((attributes & FieldAttributes.InitOnly) != 0)
        {
            parts.Add("readonly");
        }
        return $" [{string.Join(" ", parts)}]";
    }

    /// <summary>
    /// A literal field's value, rendered from the constant blob, or "" when the field has none.
    /// </summary>
    private static string ConstantValue(MetadataReader reader, FieldDefinition field)
    {
        var handle = field.GetDefaultValue();
        if (handle.IsNil)
        {
            return "";
        }

        var constant = reader.GetConstant(handle);
        var blob = reader.GetBlobReader(constant.Value);
        var value = constant.TypeCode switch
        {
            ConstantTypeCode.Boolean => blob.ReadBoolean().ToString(),
            ConstantTypeCode.Char => ((int)blob.ReadChar()).ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.SByte => blob.ReadSByte().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Byte => blob.ReadByte().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Int16 => blob.ReadInt16().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt16 => blob.ReadUInt16().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Int32 => blob.ReadInt32().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt32 => blob.ReadUInt32().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Int64 => blob.ReadInt64().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.UInt64 => blob.ReadUInt64().ToString(CultureInfo.InvariantCulture),
            ConstantTypeCode.Single => blob.ReadSingle().ToString("R", CultureInfo.InvariantCulture),
            ConstantTypeCode.Double => blob.ReadDouble().ToString("R", CultureInfo.InvariantCulture),
            ConstantTypeCode.String => $"\"{blob.ReadUTF16(blob.RemainingBytes)}\"",
            ConstantTypeCode.NullReference => "null",
            _ => "?",
        };
        return $" = {value}";
    }

    /// <summary>
    /// Generic parameters with their variance and constraints. A constraint is part of what a
    /// caller may substitute, so tightening one is a breaking change that leaves the arity - the
    /// only thing a signature records - unmoved.
    /// </summary>
    private static string GenericParameters(
        MetadataReader reader, GenericParameterHandleCollection parameters, TypeNameProvider provider)
    {
        if (parameters.Count == 0)
        {
            return "";
        }

        var rendered = new List<string>();
        foreach (var handle in parameters)
        {
            var parameter = reader.GetGenericParameter(handle);
            var parts = new List<string>();
            switch (parameter.Attributes & GenericParameterAttributes.VarianceMask)
            {
                case GenericParameterAttributes.Covariant:
                    parts.Add("out");
                    break;
                case GenericParameterAttributes.Contravariant:
                    parts.Add("in");
                    break;
            }
            if ((parameter.Attributes & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
            {
                parts.Add("class");
            }
            if ((parameter.Attributes & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
            {
                parts.Add("struct");
            }
            if ((parameter.Attributes & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
            {
                parts.Add("new()");
            }
            parts.AddRange(parameter.GetConstraints()
                .Select(c => TypeName(reader, reader.GetGenericParameterConstraint(c).Type, provider))
                .Order(StringComparer.Ordinal));
            rendered.Add($"{reader.GetString(parameter.Name)}{(parts.Count == 0 ? "" : $": {string.Join(", ", parts)}")}");
        }
        return $"<{string.Join("; ", rendered)}>";
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

        // A required modifier is part of the signature the runtime and the compiler match on -
        // `in` and `ref readonly` parameters are a modreq, and so is an init-only setter - so
        // dropping it would hide a change a consumer cannot compile through. Optional modifiers
        // are ignored, as C# itself ignores them.
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) =>
            isRequired ? $"{unmodifiedType} modreq({modifier})" : unmodifiedType;

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
