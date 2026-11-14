using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

// No plugin/Lidarr project or assembly references: all application types come from inputs.
var runtime = Path.GetFullPath(args[0]);
var pluginPath = Path.GetFullPath(args[1]);
var expected = args.Length > 2 ? args[2] : "healthy";
var context = new RuntimeContext(runtime);
var plugin = context.LoadFromAssemblyPath(pluginPath);
var checkType = plugin.GetType("NzbDrone.Core.Download.Clients.Bandcamp.Session.BandcampSessionHealthCheck", true)!;
var fixture = new SessionFixture(plugin, context);
var scenario = args.Length > 3 ? args[3] : "healthy";
if (scenario is "warning" or "restored")
{
    var provider = fixture.AddClient(7, "My Bandcamp");
    await fixture.ValidateAsync(provider, false);
    if (scenario == "restored") await fixture.ValidateAsync(provider, true);
}
else if (scenario != "healthy") throw new ArgumentException("Unknown scenario: " + scenario);
var check = fixture.CreateCheck(checkType);
Exception? failure = null;
object? result = null;
try { result = checkType.GetMethod("Check")!.Invoke(check, null); }
catch (TargetInvocationException e) { failure = e.InnerException; }

// An expected diagnostic is distinct from a passing health behavior.
const string removedConstructor = "Method not found: 'Void NzbDrone.Core.HealthCheck.HealthCheck..ctor(System.Type, NzbDrone.Core.HealthCheck.HealthCheckResult, System.String, System.String)'.";
var passed = expected switch
{
    "known-bad" => failure is MissingMethodException && failure.Message == removedConstructor,
    "healthy" => failure == null && result?.GetType().GetProperty("Type")?.GetValue(result)?.ToString() == "Ok",
    _ => throw new ArgumentException("Expected healthy or known-bad")
};
var assemblies = context.Assemblies.Select(a => new
{
    name = a.GetName().Name,
    identity = a.FullName,
    path = a.Location,
    sha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(a.Location)))
}).OrderBy(a => a.name).ToArray();
if (!assemblies.Any(a => a.name == "Lidarr.Core") ||
    assemblies.Where(a => a.name != plugin.GetName().Name).Any(a => Path.GetDirectoryName(a.path) != runtime))
    throw new Exception("Application dependency escaped the selected runtime");
Console.WriteLine(JsonSerializer.Serialize(new
{
    processId = Environment.ProcessId,
    hostRuntime = Environment.Version.ToString(),
    scenario,
    homepageCalls = fixture.HomepageCalls,
    diagnosticPassed = passed,
    behavioralPassed = failure == null,
    exception = failure?.GetType().FullName,
    message = failure?.Message,
    assemblies
}, new JsonSerializerOptions { WriteIndented = true }));
return passed ? 0 : 1;

sealed class RuntimeContext(string runtime) : AssemblyLoadContext("selected-runtime", isCollectible: false)
{
    protected override Assembly? Load(AssemblyName name)
    {
        // Use this process's framework, not cross-platform ReadyToRun framework files in the archive.
        var framework = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        if (framework.Any(p => Path.GetFileNameWithoutExtension(p) == name.Name &&
            Path.GetDirectoryName(p) == Path.GetDirectoryName(typeof(object).Assembly.Location))) return null;
        var path = Path.Combine(runtime, name.Name + ".dll");
        if (File.Exists(path)) return LoadFromAssemblyPath(path);
        throw new FileNotFoundException("Selected runtime dependency missing: " + name, path);
    }
}

// Inert external boundary for IDownloadClientFactory (empty clients) and localization.
// No HTTP, credentials, download worker, service host, database or production config.
public class EmptyBoundary : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?>? Handler { get; set; }
    public static object Create(Type type, Func<MethodInfo, object?[]?, object?>? handler = null)
    {
        var proxy = (EmptyBoundary)Create(type, typeof(EmptyBoundary));
        proxy.Handler = handler;
        return proxy;
    }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (Handler != null) return Handler(method!, args);
        var type = method!.ReturnType;
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
            return Activator.CreateInstance(type);
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
