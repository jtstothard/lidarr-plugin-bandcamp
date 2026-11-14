using System.Collections.Generic;
using System.Reflection;
using DryIoc;
using Moq;
using NLog;
using NzbDrone.Common.Composition.Extensions;
using NzbDrone.Common.Http;
using NzbDrone.Common.Http.Dispatchers;
using NzbDrone.Common.Http.Proxy;
using NzbDrone.Core.Http.Bandcamp;
using Xunit;

namespace Lidarr.Plugin.Bandcamp.Test.Http
{
    /// <summary>
    /// Regression for issue #21: BandcampHttpDispatcher only helps if Lidarr's container
    /// resolves it as THE IHttpDispatcher. This composes the container the way
    /// NzbDrone.Host's Bootstrap does (WithNzbDroneRules + AutoAddServices, base
    /// assemblies first, plugin assemblies appended) and checks which dispatcher wins.
    /// </summary>
    public class BandcampDispatcherRegistrationFixture
    {
        [Fact]
        public void Lidarr_container_resolves_BandcampHttpDispatcher_when_plugin_is_loaded()
        {
            var assemblies = new List<Assembly>
            {
                typeof(ManagedHttpDispatcher).Assembly,        // Lidarr.Common
                typeof(NzbDrone.Core.Indexers.IIndexer).Assembly, // Lidarr.Core
                typeof(BandcampHttpDispatcher).Assembly        // plugin, appended last like Bootstrap
            };

            var container = new Container(rules => rules.WithNzbDroneRules())
                .AutoAddServices(assemblies);

            // Stand-ins for the dispatcher's constructor dependencies only; the
            // IHttpDispatcher selection itself is Lidarr's real registration logic.
            container.RegisterInstance(Mock.Of<IHttpProxySettingsProvider>(), IfAlreadyRegistered.Replace);
            container.RegisterInstance(Mock.Of<ICreateManagedWebProxy>(), IfAlreadyRegistered.Replace);
            container.RegisterInstance(Mock.Of<ICertificateValidationService>(), IfAlreadyRegistered.Replace);
            container.RegisterInstance(Mock.Of<IUserAgentBuilder>(), IfAlreadyRegistered.Replace);
            container.RegisterInstance(LogManager.GetCurrentClassLogger(), IfAlreadyRegistered.Replace);

            var dispatcher = container.Resolve<IHttpDispatcher>();

            Assert.IsType<BandcampHttpDispatcher>(dispatcher);
        }
    }
}
