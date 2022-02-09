using Microsoft.AspNetCore.DataProtection;
using Microsoft.Identity.Client;

namespace PnP.Scanning.Core.Authentication
{
    internal class TokenCacheManager
    {
        private static readonly object FileLock = new object();

        private static string cacheFilePath;
        private static IDataProtectionProvider dataProtectionProvider;

        internal TokenCacheManager()
        {
        }

        private static void BeforeAccessNotification(TokenCacheNotificationArgs args)
        {

            //debug
            //var exchangeTokenCacheV3Bytes = args.TokenCache.SerializeMsalV3();
            //string jsonString = System.Text.Encoding.UTF8.GetString(exchangeTokenCacheV3Bytes);            

            lock (FileLock)
            {
                args.TokenCache.DeserializeMsalV3(File.Exists(cacheFilePath)
                    ? DecryptData(File.ReadAllBytes(cacheFilePath))
                    : null);
            }
        }

        private static void AfterAccessNotification(TokenCacheNotificationArgs args)
        {
            // if the access operation resulted in a cache update
            if (args.HasStateChanged)
            {
                lock (FileLock)
                {
                    // reflect changes in the persistent store
                    File.WriteAllBytes(cacheFilePath,
                                       EncryptData(args.TokenCache.SerializeMsalV3()));
                }
            }
        }

        internal static void EnableSerialization(ITokenCache tokenCache, IDataProtectionProvider dataProtectionProviderInstance, string cachePath)
        {
            cacheFilePath = CacheFilePath(cachePath);
            dataProtectionProvider = dataProtectionProviderInstance;
        
            tokenCache.SetBeforeAccess(BeforeAccessNotification);
            tokenCache.SetAfterAccess(AfterAccessNotification);
        }

        internal static string CacheFilePath(string cachePath)
        {
            return Path.Combine(cachePath, Constants.MsalCacheFile);
        }

        internal static byte[] EncryptData(byte[] input)
        {
            //https://docs.microsoft.com/en-us/aspnet/core/security/data-protection/consumer-apis/overview?view=aspnetcore-5.0
            var protector = dataProtectionProvider.CreateProtector(Constants.DataProtectorMsalCachePurpose);
            return protector.Protect(input);
        }

        internal static byte[] DecryptData(byte[] input)
        {
            //https://docs.microsoft.com/en-us/aspnet/core/security/data-protection/consumer-apis/overview?view=aspnetcore-5.0
            var protector = dataProtectionProvider.CreateProtector(Constants.DataProtectorMsalCachePurpose);
            return protector.Unprotect(input);
        }
    }
}
