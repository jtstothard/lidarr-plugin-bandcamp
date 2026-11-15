using System.Collections;
using System.Net;
using System.Reflection;
using System.Runtime.Loader;

// Reflection keeps compile-side application assemblies out of this process.
// Mirrors BandcampSessionHealthCheckFixture: real registry/provider/API/wrapper, fake HTTP.
public sealed class SessionFixture
{
    private const string SessionNamespace = "NzbDrone.Core.Download.Clients.Bandcamp.Session.";
    private const string ClientNamespace = "NzbDrone.Core.Download.Clients.Bandcamp.";
    private readonly Assembly plugin;
    private readonly Assembly core;
    private readonly object registry;
    private readonly IList clients;
    public bool HomepageLive { get; set; } = true;
    public int HomepageCalls { get; private set; }

    public SessionFixture(Assembly plugin, AssemblyLoadContext context)
    {
        this.plugin = plugin;
        core = context.LoadFromAssemblyName(new AssemblyName("Lidarr.Core"));
        var common = context.LoadFromAssemblyName(new AssemblyName("Lidarr.Common"));
        var nlog = context.LoadFromAssemblyName(new AssemblyName("NLog"));
        var logger = nlog.GetType("NLog.LogManager", true)!.GetMethod("GetLogger", new[] { typeof(string) })!
            .Invoke(null, new object[] { "PackagedCompatibility" });
        var http = EmptyBoundary.Create(common.GetType("NzbDrone.Common.Http.IHttpClient", true)!, (method, args) =>
        {
            // Fail closed for anything other than the fake homepage lookup.
            if (method.Name != "ExecuteAsync" || args?.Length != 1)
                throw new InvalidOperationException("Unexpected HTTP boundary call: " + method);
            var request = args[0]!;
            if (new Uri(request.GetType().GetProperty("Url")!.GetValue(request)!.ToString()!).AbsoluteUri != "https://bandcamp.com/")
                throw new InvalidOperationException("Only the fake homepage is supported");
            HomepageCalls++;
            var identity = HomepageLive ? "&quot;fanId&quot;:12345678,&quot;isLoggedIn&quot;:true" : "&quot;fanId&quot;:null,&quot;isLoggedIn&quot;:false";
            var body = "<html><body><div id=\"HomepageApp\" data-blob=\"{&quot;pageContext&quot;:{&quot;identity&quot;:{" + identity + "}}}\"></div></body></html>";
            var responseType = common.GetType("NzbDrone.Common.Http.HttpResponse", true)!;
            var header = Activator.CreateInstance(common.GetType("NzbDrone.Common.Http.HttpHeader", true)!);
            var responseCtor = responseType.GetConstructors().Single(c => c.GetParameters().Any(p => p.ParameterType == typeof(string)));
            var responseArgs = responseCtor.GetParameters().Select(p =>
                p.ParameterType == request.GetType() ? request :
                p.ParameterType == header!.GetType() ? header :
                p.ParameterType == typeof(string) ? body :
                p.ParameterType == typeof(HttpStatusCode) ? (object)HttpStatusCode.OK :
                p.HasDefaultValue ? p.DefaultValue : throw new InvalidOperationException("Unknown HttpResponse parameter: " + p)).ToArray();
            var response = responseCtor.Invoke(responseArgs);
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(responseType).Invoke(null, new[] { response });
        });
        // The integration intentionally made the fake-HTTP constructor internal so
        // host DI cannot select it. Keep this fixture on that exact transport seam;
        // never instantiate a real dispatcher or make an external request here.
        var wrapperType = plugin.GetType("NzbDrone.Core.Http.Bandcamp.BandcampHttpClient", true)!;
        var wrapperConstructor = wrapperType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new[] { common.GetType("NzbDrone.Common.Http.IHttpClient", true)!, logger!.GetType() }, null)
            ?? throw new MissingMethodException(wrapperType.FullName, "fixture HTTP constructor");
        var wrapper = wrapperConstructor.Invoke(new[] { http, logger });
        var api = Activator.CreateInstance(plugin.GetType(ClientNamespace + "BandcampApiClient", true)!, wrapper, logger);
        var registryConstructor = plugin.GetType(SessionNamespace + "BandcampSessionProviderRegistry", true)!
            .GetConstructors().Single();
        var registryArguments = new List<object?> { api, null, null, logger };
        foreach (var parameter in registryConstructor.GetParameters().Skip(4))
        {
            if (!parameter.IsOptional || parameter.ParameterType.Name != "IDownloadClientRepository")
                throw new InvalidOperationException("Unexpected registry fixture parameter: " + parameter);
            registryArguments.Add(null);
        }
        registry = registryConstructor.Invoke(registryArguments.ToArray());
        clients = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(core.GetType("NzbDrone.Core.Download.DownloadClientDefinition", true)!))!;
    }

    public object AddClient(int id, string name, bool enabled = true)
    {
        var definition = Activator.CreateInstance(core.GetType("NzbDrone.Core.Download.DownloadClientDefinition", true)!)!;
        var settings = Activator.CreateInstance(plugin.GetType(ClientNamespace + "BandcampDownloadSettings", true)!)!;
        Set(settings, "Cookies", "identity=packaged-fixture-only");
        Set(settings, "DownloadPath", "/unused-packaged-fixture");
        Set(definition, "Id", id);
        Set(definition, "Name", name);
        Set(definition, "Enable", enabled);
        Set(definition, "Implementation", "BandcampDownloadClient");
        Set(definition, "Settings", settings);
        clients.Add(definition);
        return registry.GetType().GetMethod("Get")!.Invoke(registry, new[] { (object)id, settings })!;
    }

    public async Task ValidateAsync(object provider, bool live)
    {
        HomepageLive = live;
        await (Task)provider.GetType().GetMethod("RenewAsync")!.Invoke(provider, new object[] { CancellationToken.None })!;
    }

    public object CreateCheck(Type checkType)
    {
        var ctor = checkType.GetConstructors().Single();
        var dependencies = ctor.GetParameters().Select(p => p.ParameterType.Name switch
        {
            "IBandcampSessionProviderRegistry" => registry,
            "IDownloadClientFactory" => EmptyBoundary.Create(p.ParameterType, (m, _) =>
                m.Name == "All" ? clients : throw new InvalidOperationException("Unexpected factory call: " + m)),
            "ILocalizationService" => EmptyBoundary.Create(p.ParameterType),
            _ => throw new InvalidOperationException("Unexpected check dependency: " + p.ParameterType)
        }).ToArray();
        return ctor.Invoke(dependencies);
    }

    private static void Set(object target, string property, object value) =>
        target.GetType().GetProperty(property)!.SetValue(target, value);
}
