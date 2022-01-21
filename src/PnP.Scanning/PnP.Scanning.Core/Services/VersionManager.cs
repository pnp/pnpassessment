using Serilog;
using System.Reflection;

namespace PnP.Scanning.Core.Services
{
    internal static class VersionManager
    {
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

    }
}
