using PnP.Core.Services;
using PnP.Scanning.Core.Authentication;
using Xunit;

namespace PnP.Scanning.Core.Tests.Authentication
{
    public class CloudManagerTests
    {
        [Fact]
        public void SharePointDeUsesDelosCloud()
        {
            var environment = CloudManager.GetEnvironmentFromUri(
                new Uri("https://contoso.sharepoint.de/sites/example"));

            Assert.Equal(Microsoft365Environment.DelosCloud, environment);
        }

        [Fact]
        public void DelosCloudUsesPnPCoreGraphAndLoginAuthorities()
        {
            Assert.Equal("graph.svc.sovcloud.de",
                CloudManager.GetMicrosoftGraphAuthority(Microsoft365Environment.DelosCloud));
            Assert.Equal("login.sovcloud-identity.de",
                CloudManager.GetAzureADLoginAuthority(Microsoft365Environment.DelosCloud));
        }

        [Fact]
        public void DelosCloudManagementApiFailsClosed()
        {
            var exception = Assert.Throws<NotSupportedException>(() =>
                CloudManager.GetManagementApiAuthority(Microsoft365Environment.DelosCloud));

            Assert.Contains("Delos Cloud", exception.Message);
        }
    }
}
