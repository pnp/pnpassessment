using Serilog;
using System.Reflection;

namespace PnP.Scanning.Core.Services
{
    internal static class VersionManager
    {
        internal const string versionFileUrl = "https://raw.githubusercontent.com/pnp/pnpassessment/main/version.txt";
        internal const string newVersionDownloadUrl = "https://aka.ms/m365assessmentreleases";

        private static readonly HttpClient httpClient = new();

        internal static string GetCurrentVersion()
        {
            var coreAssembly = Assembly.GetExecutingAssembly();
            if (coreAssembly != null)
            {
                var assemblyFileVersionAttribute = coreAssembly.GetCustomAttribute(typeof(AssemblyFileVersionAttribute));
                if (assemblyFileVersionAttribute != null)
                {
                    return ((AssemblyFileVersionAttribute)assemblyFileVersionAttribute).Version;
                }
            }

            Log.Error("Version could not be read");
            throw new Exception("Version could not be read");
        }

        internal static async Task<Tuple<string, string>> LatestVersionAsync()
        {
            string latestVersion = "";
            string currentVersion = "";

            try
            {
                var coreAssembly = Assembly.GetExecutingAssembly();
                currentVersion = ((AssemblyFileVersionAttribute)coreAssembly.GetCustomAttribute(typeof(AssemblyFileVersionAttribute))).Version;
                
                // Drop the file revision
                var versionOld = new Version(currentVersion);
                currentVersion = $"{versionOld.Major}.{versionOld.Minor}.{versionOld.Build}";

                using (var request = new HttpRequestMessage(HttpMethod.Get, $"{versionFileUrl}?random={new Random().Next()}"))
                {
                    HttpResponseMessage response = await httpClient.SendAsync(request).ConfigureAwait(false);
                    latestVersion = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(latestVersion))
                {
                    latestVersion = latestVersion.Replace("\\r", "").Replace("\\t", "");
                    versionOld = new Version(currentVersion);

                    if (Version.TryParse(latestVersion, out Version versionNew))
                    {
                        if (versionOld.CompareTo(versionNew) >= 0)
                        {
                            // version is not newer
                            latestVersion = null;
                        }
                    }
                    else
                    {
                        // We could not get the version file
                        latestVersion = null;
                    }
                }

            }
            catch (Exception)
            {
                // Something went wrong
                latestVersion = null;
            }

            return new Tuple<string, string>(currentVersion, latestVersion);
        }
    }
}
