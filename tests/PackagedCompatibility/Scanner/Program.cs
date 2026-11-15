using System.Text.Json;
using Mono.Cecil;

if (args.Length != 2) return 2;
var runtime = Path.GetFullPath(args[0]);
var errors = new List<string>();
var missingTypes = new List<string>();
var missingMembers = new List<string>();
int typeCount = 0, memberCount = 0;
using var resolver = new RuntimeResolver(runtime);
try
{
    using var module = ModuleDefinition.ReadModule(args[1], new ReaderParameters { AssemblyResolver = resolver });
    bool IsLidarr(TypeReference type)
    {
        while (type is TypeSpecification specification) type = specification.ElementType;
        var scope = type.Scope?.Name ?? "";
        return scope.StartsWith("Lidarr.", StringComparison.Ordinal) || scope.StartsWith("NzbDrone.", StringComparison.Ordinal);
    }
    foreach (var type in module.GetTypeReferences().Where(IsLidarr))
    {
        typeCount++;
        try { if (type.Resolve() is null) missingTypes.Add(type.FullName); }
        catch (Exception e) { errors.Add($"type {type.FullName}: {e.GetType().Name}: {e.Message}"); }
    }
    foreach (var member in module.GetMemberReferences().Where(m => IsLidarr(m.DeclaringType)))
    {
        memberCount++;
        try
        {
            var resolved = member switch
            {
                MethodReference method => (IMemberDefinition?)method.Resolve(),
                FieldReference field => field.Resolve(),
                _ => throw new NotSupportedException(member.GetType().Name)
            };
            if (resolved is null) missingMembers.Add(member.FullName);
        }
        catch (Exception e) { errors.Add($"member {member.FullName}: {e.GetType().Name}: {e.Message}"); }
    }
    if (typeCount == 0 || memberCount == 0) errors.Add("No direct Lidarr references scanned; coverage is empty");
}
catch (Exception e) { errors.Add($"input: {e.GetType().Name}: {e.Message}"); }
Console.WriteLine(JsonSerializer.Serialize(new {
    coverage = "Direct Lidarr/NzbDrone assembly-scoped TypeRef and MemberRef metadata; includes Cecil base/member resolution. Excludes reflective calls, inherited interface contracts, native startup and behavior.",
    types = typeCount, members = memberCount, missingTypes, missingMembers, errors,
    resolvedAssemblies = resolver.Resolved
}, new JsonSerializerOptions { WriteIndented = true }));
return missingTypes.Count + missingMembers.Count + errors.Count == 0 ? 0 : 1;

sealed class RuntimeResolver(string runtime) : IAssemblyResolver
{
    readonly Dictionary<string, AssemblyDefinition> cache = new();
    public List<object> Resolved { get; } = new();
    public AssemblyDefinition Resolve(AssemblyNameReference name) => Resolve(name, new ReaderParameters());
    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters)
    {
        if (cache.TryGetValue(name.Name, out var existing)) return existing;
        // No working-directory, NuGet, compile-reference or GAC fallback.
        var path = Path.Combine(runtime, name.Name + ".dll");
        if (!File.Exists(path)) throw new AssemblyResolutionException(name);
        var assembly = AssemblyDefinition.ReadAssembly(path, new ReaderParameters { AssemblyResolver = this });
        cache.Add(name.Name, assembly);
        Resolved.Add(new { name = assembly.Name.FullName, path,
            sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });
        return assembly;
    }
    public void Dispose() { foreach (var assembly in cache.Values) assembly.Dispose(); }
}
