using NzbDrone.Common.Http.Dispatchers;
using DryIoc;
using Moq;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Http.Bandcamp;
using NLog;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
var root = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, n) => File.Exists(Path.Combine(root, n.Name + ".dll")) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, n.Name + ".dll")) : null;
Console.WriteLine(RuntimeInformation.FrameworkDescription);
foreach(var name in new[]{"Lidarr.Core", "Lidarr.Common"}) {
 var a = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(root, name+".dll"));
 Console.WriteLine(a.FullName);
}
if(args.Length == 1) return;
var plugin = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[1]));
Console.WriteLine(plugin.FullName);
foreach(var reference in plugin.GetReferencedAssemblies()) Console.WriteLine("reference " + reference);
var types = plugin.GetTypes();
Console.WriteLine($"Loaded all {types.Length} plugin types");
int count = 0;
foreach(var t in types.Where(t=>!t.ContainsGenericParameters))
 foreach(var method in t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly).Cast<MethodBase>().Concat(t.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static))) {
  if(method.IsAbstract || method.ContainsGenericParameters || method.GetMethodBody() == null) continue;
  RuntimeHelpers.PrepareMethod(method.MethodHandle); count++;
 }
Console.WriteLine($"JIT prepared {count} method/constructor bodies without invocation");
var descriptor = Activator.CreateInstance(types.Single(t=>t.Name == "BandcampPlugin"))!;
Console.WriteLine("Plugin descriptor " + descriptor.GetType().GetProperty("Name")!.GetValue(descriptor));
DiProof.Run(plugin);
foreach(var path in new[]{"/srv/docker-state/stacks/media/lidarr/config.xml", "/config/config.xml", "/root/.config/Lidarr/config.xml", "/home/jay/.config/Lidarr/config.xml"}) {
 if(File.Exists(path)) throw new Exception("Confinement failed: " + path);
}
try {
 using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
 throw new Exception("IPv4 socket creation unexpectedly allowed");
} catch(System.Net.Sockets.SocketException) { Console.WriteLine("Confinement: production/default config paths unavailable; AF_INET socket creation denied"); }

static class DiProof {
[MethodImpl(MethodImplOptions.NoInlining)]
public static void Run(Assembly plugin) {
var assemblies = new List<Assembly> {
 typeof(NzbDrone.Common.Http.Dispatchers.ManagedHttpDispatcher).Assembly,
 typeof(NzbDrone.Core.Indexers.IIndexer).Assembly,
 plugin
};
var container = new Container(rules => rules.WithNzbDroneRules()).AutoAddServices(assemblies);

container.RegisterInstance(Mock.Of<IHttpProxySettingsProvider>(), IfAlreadyRegistered.Replace);
container.RegisterInstance(Mock.Of<ICreateManagedWebProxy>(), IfAlreadyRegistered.Replace);
container.RegisterInstance(Mock.Of<ICertificateValidationService>(), IfAlreadyRegistered.Replace);
container.RegisterInstance(Mock.Of<IUserAgentBuilder>(), IfAlreadyRegistered.Replace);
container.RegisterInstance(LogManager.GetCurrentClassLogger(), IfAlreadyRegistered.Replace);
var dispatcher = container.Resolve<NzbDrone.Common.Http.Dispatchers.IHttpDispatcher>();
if(dispatcher.GetType() != typeof(BandcampHttpDispatcher)) throw new Exception("Wrong HTTP dispatcher");
Console.WriteLine("Native DI selected " + dispatcher.GetType().FullName + " with mock constructor dependencies");

}
}
