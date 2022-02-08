using Microsoft.AspNetCore.DataProtection;
using Microsoft.Identity.Client;
using PnP.Core.Services;
using PnP.Scanning.Core.Services;
using PnP.Scanning.Core.Storage;

namespace PnP.Scanning.Core.Authentication
{
    internal sealed class AuthenticationManager
    {
        private IClientApplicationBase? clientApplication;

        //internal const string DefaultClientId = "31359c7f-bd7e-475c-86db-fdb8c937548e";
        //internal const string OrganizationsTenantId = "organizations";
        //internal static readonly Uri DefaultRedirectUri = new Uri("http://localhost");
        //internal const string AuthorityFormat = "https://login.microsoftonline.com/{0}/";

        public AuthenticationManager(IDataProtectionProvider provider)
        {
            DataProtectionProvider = provider;
        }

        internal IDataProtectionProvider DataProtectionProvider { get; private set; }

        internal static AuthenticationManager Create(StartRequest request, IDataProtectionProvider provider)
        {
            var authenticationManager = new AuthenticationManager(provider);

            // Configure the authentication manager
            authenticationManager.InitializeAuthentication(request);

            return authenticationManager;
        }        

        private IClientApplicationBase? InitializeAuthentication(StartRequest request)
        {
            return InitializedAuthentication(request.Tenant, request.AuthMode, Enum.Parse<Microsoft365Environment>(request.Environment), Guid.Parse(request.ApplicationId),
                                             request.CertPath, request.CertFile, request.CertPassword);
        }


        private IClientApplicationBase? InitializedAuthentication(string tenantName, string authMode, Microsoft365Environment environment, Guid applicationId,
                                                                  string? certPath, string? certFile, string? certPassword)
        {            

            if (tenantName == null)
            {
                throw new ArgumentNullException(nameof(tenantName));
            }

            if (authMode == null)
            {
                throw new ArgumentNullException(nameof(authMode));
            }

            if (applicationId == Guid.Empty)
            {
                throw new ArgumentException("No application id specified", nameof(applicationId));
            }

            if (authMode.Equals("Interactive", StringComparison.OrdinalIgnoreCase))
            {
                var builder = PublicClientApplicationBuilder.Create(applicationId.ToString());
                builder = GetBuilderWithAuthority(builder, environment);
                builder = builder.WithRedirectUri("http://localhost");

                //if (!string.IsNullOrEmpty(tenantId))
                //{
                //    builder = builder.WithTenantId(tenantId);
                //}

                clientApplication = builder.Build();
            }
            else if (authMode.Equals("Device", StringComparison.OrdinalIgnoreCase))
            {

            }
            else if (authMode.Equals("Application", StringComparison.OrdinalIgnoreCase))
            {

            }
            else
            {
                throw new Exception($"Authentication type {authMode} is unknown");
            }

            // Setup a local encrypted cache
            TokenCacheManager.EnableSerialization(clientApplication.UserTokenCache, DataProtectionProvider, StorageManager.GetScannerFolder());

            return clientApplication;
        }

        internal async Task VerifyAuthenticationAsync(string tenantName, string authMode, Microsoft365Environment environment, Guid applicationId,
                                                          string? certPath, FileInfo? certFile, string? certPassword)
        {

            clientApplication = InitializedAuthentication(tenantName, authMode, environment, applicationId, certPath, certFile?.FullName, certPassword);

            if (clientApplication != null)
            {

                // Getting SharePoint access token
                var uri = new Uri(GetSiteFromTenant(tenantName));
                var scopes = new[] { $"{uri.Scheme}://{uri.Authority}/.default" };
                string accessToken = await GetAccessTokenAsync(scopes);
                if (string.IsNullOrEmpty(accessToken))
                {
                    throw new Exception("Failed to retrieve SharePoint access token");
                }

                // Getting Microsoft Graph access token
                uri = new Uri($"https://{CloudManager.GetMicrosoftGraphAuthority(environment)}");
                scopes = new[] { $"{uri.Scheme}://{uri.Authority}/.default" };
                accessToken = await GetAccessTokenAsync(scopes);
                if (string.IsNullOrEmpty(accessToken))
                {
                    throw new Exception("Failed to retrieve Graph access token");
                }
            }
            else
            {
                throw new Exception("No authentication provider was setup");
            }
        }

        internal static string GetSiteFromTenant(string tenantName)
        {
            if (Uri.TryCreate(tenantName, UriKind.Absolute, out var uri))
            {
                return $"https://{uri.DnsSafeHost}";
            }
            else
            {
                return $"https://{tenantName}";
            }
        }

        internal async Task<string> GetAccessTokenAsync(string[] scopes)
        {
#pragma warning disable CS8602 // Dereference of a possibly null reference.
            var accounts = (await clientApplication.GetAccountsAsync()).ToList();
#pragma warning restore CS8602 // Dereference of a possibly null reference.

            try
            {
                AuthenticationResult result = await clientApplication.AcquireTokenSilent(scopes, accounts.FirstOrDefault())
                    .ExecuteAsync()
                    .ConfigureAwait(false);
                return result.AccessToken;
            }
            catch (MsalUiRequiredException)
            {
                if (clientApplication is IPublicClientApplication publicClientApplication)
                {
                    var builder = publicClientApplication.AcquireTokenInteractive(scopes);
                    AuthenticationResult result = await builder.ExecuteAsync();
                    return result.AccessToken;
                }
            }

            return null;
        }        

        private PublicClientApplicationBuilder GetBuilderWithAuthority(PublicClientApplicationBuilder builder, Microsoft365Environment azureEnvironment)
        {
            if (azureEnvironment == Microsoft365Environment.Production)
            {
                var azureADEndPoint = $"https://{CloudManager.GetAzureADLoginAuthority(azureEnvironment)}";
                builder = builder.WithAuthority($"{azureADEndPoint}/organizations");
            }
            else
            {
                switch (azureEnvironment)
                {
                    case Microsoft365Environment.USGovernment:
                    case Microsoft365Environment.USGovernmentDoD:
                    case Microsoft365Environment.USGovernmentHigh:
                        {
                            builder = builder.WithAuthority(AzureCloudInstance.AzureUsGovernment, AadAuthorityAudience.AzureAdMyOrg);
                            break;
                        }
                    case Microsoft365Environment.Germany:
                        {
                            builder = builder.WithAuthority(AzureCloudInstance.AzureGermany, AadAuthorityAudience.AzureAdMyOrg);
                            break;
                        }
                    case Microsoft365Environment.China:
                        {
                            builder = builder.WithAuthority(AzureCloudInstance.AzureChina, AadAuthorityAudience.AzureAdMyOrg);
                            break;
                        }
                }
            }
            return builder;
        }

        public ConfidentialClientApplicationBuilder GetBuilderWithAuthority(ConfidentialClientApplicationBuilder builder, Microsoft365Environment azureEnvironment, string tenantId = "")
        {
            if (azureEnvironment == Microsoft365Environment.Production)
            {
                var azureADEndPoint = $"https://{CloudManager.GetAzureADLoginAuthority(azureEnvironment)}";
                if (!string.IsNullOrEmpty(tenantId))
                {
                    builder = builder.WithAuthority($"{azureADEndPoint}/organizations", tenantId);
                }
                else
                {
                    builder = builder.WithAuthority($"{azureADEndPoint}/organizations");
                }
            }
            else
            {
                switch (azureEnvironment)
                {
                    case Microsoft365Environment.USGovernment:
                    case Microsoft365Environment.USGovernmentDoD:
                    case Microsoft365Environment.USGovernmentHigh:
                        {
                            builder = builder.WithAuthority(AzureCloudInstance.AzureUsGovernment, AadAuthorityAudience.AzureAdMyOrg);
                            break;
                        }
                    case Microsoft365Environment.Germany:
                        {
                            builder = builder.WithAuthority(AzureCloudInstance.AzureGermany, AadAuthorityAudience.AzureAdMyOrg);
                            break;
                        }
                    case Microsoft365Environment.China:
                        {
                            builder = builder.WithAuthority(AzureCloudInstance.AzureChina, AadAuthorityAudience.AzureAdMyOrg);
                            break;
                        }
                }
            }
            return builder;
        }
    }
}
