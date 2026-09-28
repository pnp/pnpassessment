using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;

namespace PnP.Scanning.Core.Discovery;

/// <summary>
/// A scan-frozen, metadata-only snapshot of operator-supplied deployed assemblies. No assembly
/// is loaded into the CLR. Identity binding is exact; runtime probing/redirects are not simulated.
/// </summary>
internal sealed class PublishingLayoutTypeCatalog
{
    internal const int CurrentRuleVersion = 1;
    internal const string RootName = "Microsoft.SharePoint.Publishing.PublishingLayoutPage";
    internal const string PublishingAssembly = "Microsoft.SharePoint.Publishing, Version=16.0.0.0, Culture=neutral, PublicKeyToken=71e9bce111e9429c";
    internal const string ObjectIdentity = "System.Object, mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089";

    public List<TypeEdge> Types { get; set; } = new();
    public List<string> Errors { get; set; } = new();

    internal sealed record TypeEdge(string Identity, string BaseIdentity, string Provenance);

    internal string ToJson() => JsonSerializer.Serialize(this);
    internal static PublishingLayoutTypeCatalog FromJson(string json) => string.IsNullOrEmpty(json)
        ? new() : JsonSerializer.Deserialize<PublishingLayoutTypeCatalog>(json);

    internal static PublishingLayoutTypeCatalog Capture(IEnumerable<string> assemblyPaths)
    {
        var catalog = new PublishingLayoutTypeCatalog();
        foreach (var path in assemblyPaths)
        {
            try
            {
                // Capture hash and metadata from the same bytes. Retain no local machine paths.
                var bytes = File.ReadAllBytes(path);
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                using var pe = new PEReader(new MemoryStream(bytes, writable: false));
                var reader = pe.GetMetadataReader();
                var assembly = reader.GetAssemblyDefinition();
                var identity = AssemblyIdentity(reader.GetString(assembly.Name), assembly.Version,
                    reader.GetString(assembly.Culture), reader.GetBlobBytes(assembly.PublicKey), true);
                var definitions = new List<TypeEdge>();
                foreach (var handle in reader.TypeDefinitions)
                {
                    var type = reader.GetTypeDefinition(handle);
                    // Nested/generic definitions and TypeSpec bases require a broader binder.
                    // Keep them unresolved rather than approximating CLR identity.
                    if (type.IsNested || type.GetGenericParameters().Count != 0) continue;
                    var name = FullName(reader, type.Namespace, type.Name);
                    if (name == "<Module>") continue;
                    string baseIdentity = BaseIdentity(reader, type.BaseType, identity);
                    definitions.Add(new(name + ", " + identity, baseIdentity,
                        "ECMA335:" + identity + ";SHA256=" + hash));
                }
                catalog.Types.AddRange(definitions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException)
            {
                catalog.Errors.Add("AssemblyEvidenceUnavailable:" + ex.GetType().Name);
            }
        }
        return catalog;
    }

    private static string BaseIdentity(MetadataReader reader, EntityHandle handle, string assembly)
    {
        if (handle.Kind == HandleKind.TypeDefinition && !handle.IsNil)
        {
            var type = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            return type.IsNested ? null : FullName(reader, type.Namespace, type.Name) + ", " + assembly;
        }
        if (handle.Kind == HandleKind.TypeReference && !handle.IsNil)
        {
            var type = reader.GetTypeReference((TypeReferenceHandle)handle);
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference) return null;
            var reference = reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            return FullName(reader, type.Namespace, type.Name) + ", " + AssemblyIdentity(
                reader.GetString(reference.Name), reference.Version, reader.GetString(reference.Culture),
                reader.GetBlobBytes(reference.PublicKeyOrToken), (reference.Flags & AssemblyFlags.PublicKey) != 0);
        }
        return null;
    }

    private static string FullName(MetadataReader reader, StringHandle ns, StringHandle name) =>
        string.IsNullOrEmpty(reader.GetString(ns)) ? reader.GetString(name) : reader.GetString(ns) + "." + reader.GetString(name);

    private static string AssemblyIdentity(string name, Version version, string culture, byte[] key, bool fullKey)
    {
        var assembly = new AssemblyName { Name = name, Version = version, CultureName = culture };
        if (fullKey && key.Length != 0) assembly.SetPublicKey(key);
        else assembly.SetPublicKeyToken(key);
        return assembly.FullName;
    }

    internal static string Bind(string declaration)
    {
        if (string.IsNullOrWhiteSpace(declaration)) return null;
        var comma = declaration.IndexOf(',');
        if (comma < 1) return null;
        var name = declaration[..comma].Trim();
        // Only simple, fully assembly-qualified type identities. No aliases or generic syntax.
        if (name.IndexOfAny(new[] { '[', ']', '+', '&', '*', '`' }) >= 0) return null;
        var qualification = declaration[(comma + 1)..];
        if (!qualification.Contains("Version=", StringComparison.OrdinalIgnoreCase) ||
            !qualification.Contains("Culture=", StringComparison.OrdinalIgnoreCase) ||
            !qualification.Contains("PublicKeyToken=", StringComparison.OrdinalIgnoreCase)) return null;
        try { return name + ", " + new AssemblyName(qualification.Trim()).FullName; }
        catch (Exception ex) when (ex is ArgumentException or FileLoadException) { return null; }
    }

    internal (string Decision, string Status, string Reason, TypeEdge[] Chain) Resolve(string identity)
    {
        if (identity == null) return ("Unknown", "Unknown", "UnresolvedIdentity", Array.Empty<TypeEdge>());
        var chain = new List<TypeEdge>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = identity;
        while (current != null && seen.Add(current))
        {
            var candidates = Types.Where(type => type.Identity == current).Distinct().ToArray();
            if (candidates.Select(type => type.BaseIdentity).Distinct().Count() > 1)
                return ("Unknown", "Unknown", "ConflictingAncestry", chain.Concat(candidates).ToArray());
            // Fixed identity anchors, not guesses based on a CLR name or ContentType.
            if (current == RootName + ", " + PublishingAssembly ||
                current == RootName + ", " + PublishingAssembly.Replace("16.0.0.0", "15.0.0.0"))
            {
                chain.Add(new(current, null, "WellKnownPublishingLayoutIdentity:v1"));
                return ("Member", "Resolved", "RootOrProvenSubclass", chain.ToArray());
            }
            if (current == ObjectIdentity)
            {
                chain.Add(new(current, null, "WellKnownSystemObjectIdentity:v1"));
                return ("NonMember", "Resolved", "ProvenOutsideFamily", chain.ToArray());
            }
            if (candidates.Length == 0)
                return ("Unknown", "Unknown", chain.Count == 0 ? "UnresolvedIdentity" : "IncompleteAncestry", chain.ToArray());
            chain.AddRange(candidates);
            current = candidates[0].BaseIdentity;
        }
        return ("Unknown", "Unknown", current == null ? "IncompleteAncestry" : "AncestryCycle", chain.ToArray());
    }
}
