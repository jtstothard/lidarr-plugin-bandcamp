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
const string expiredCause = "session cookie expired or invalid; paste a fresh identity cookie into the client settings";
object? provider = null;
if (scenario is "healthy" or "warning" or "restored" or "disabled" or "multiple")
{
    provider = fixture.AddClient(7, "My Bandcamp", scenario != "disabled");
    await fixture.ValidateAsync(provider, scenario == "healthy");
    if (scenario == "disabled")
        await fixture.ValidateAsync(fixture.AddClient(8, "Healthy Bandcamp"), true);
    if (scenario == "multiple")
        await fixture.ValidateAsync(fixture.AddClient(8, "Other Bandcamp"), false);
}
else throw new ArgumentException("Unknown scenario: " + scenario);
var check = fixture.CreateCheck(checkType);
Exception? failure = null;
object? result = null;
bool? warningBeforeRestore = null;
try
{
    if (scenario == "restored")
    {
        // The same Check instance must warn before validation, then return OK after it.
        if (expected == "behavior")
            warningBeforeRestore = IsWarning(checkType.GetMethod("Check")!.Invoke(check, null), false);
        await fixture.ValidateAsync(provider!, true);
    }
    result = checkType.GetMethod("Check")!.Invoke(check, null);
}
catch (TargetInvocationException e) { failure = e.InnerException; }

string? Property(object? value, string name) => value?.GetType().GetProperty(name)?.GetValue(value)?.ToString();
bool IsWarning(object? value, bool multiple)
{
    var message = Property(value, "Message");
    return Property(value, "Type") == "Warning" &&
        message?.Contains("My Bandcamp: " + expiredCause) == true &&
        (!multiple || message.Contains("Other Bandcamp: " + expiredCause)) &&
        (value?.GetType().GetProperty("Reason") == null || Property(value, "Reason") == "DownloadClientStatusSingleClient");
}
var severity = Property(result, "Type");
var healthMessage = Property(result, "Message");
var reason = Property(result, "Reason");
// An expected diagnostic is distinct from a passing health behavior.
const string removedConstructor = "Method not found: 'Void NzbDrone.Core.HealthCheck.HealthCheck..ctor(System.Type, NzbDrone.Core.HealthCheck.HealthCheckResult, System.String, System.String)'.";
var passed = expected switch
{
    "known-bad" => failure is MissingMethodException && failure.Message == removedConstructor,
    "healthy" => failure == null && severity == "Ok",
    "behavior" => failure == null && (scenario switch
    {
        "warning" => IsWarning(result, false),
        "multiple" => IsWarning(result, true),
        "restored" => warningBeforeRestore == true && severity == "Ok" && string.IsNullOrEmpty(healthMessage),
        _ => severity == "Ok" && string.IsNullOrEmpty(healthMessage)
    }),
    _ => throw new ArgumentException("Expected behavior, healthy or known-bad")
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
    diagnosticPassed = expected == "known-bad" && passed,
    behavioralPassed = expected != "known-bad" && passed,
    warningBeforeRestore,
    severity,
    healthMessage,
    reason,
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
