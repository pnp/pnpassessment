using Microsoft.SharePoint.Client;

namespace PnP.Scanning.Core.Scanners
{
    /// <summary>
	/// Implementation of SharePoint WebRequestExecutorFactory that utilizes HttpClient
	/// </summary>
	/// <example>
	/// var authManager = new PnP.Framework.AuthenticationManager(clientId, certificate, tenantId);
	/// var clientContext = authManager.GetContext(siteUrl);
	/// clientContext.WebRequestExecutorFactory = new HttpClientWebRequestExecutorFactory(PnPHttpClient.Instance.GetHttpClient());
	/// clientContext.Load(clientContext.Web, w => w.Title);
	/// await clientContext.ExecuteQueryRetryAsync();
	/// </example>
	internal sealed class HttpClientWebRequestExecutorFactory : WebRequestExecutorFactory
    {
        private readonly HttpClient httpClient;

        /// <summary>
        /// Creates a WebRequestExecutorFactory that utilizes the specified HttpClient
        /// </summary>
        /// <param name="httpClientInstance">HttpClient to use when creating new web requests</param>
        public HttpClientWebRequestExecutorFactory(HttpClient httpClientInstance)
        {
            httpClient = httpClientInstance;
        }

        /// <summary>
        /// Creates a WebRequestExecutor that utilizes HttpClient
        /// </summary>
        /// <param name="context">A SharePoint ClientContext</param>
        /// <param name="requestUrl">The url to create the request for</param>
        /// <returns>A WebRequestExecutor object created for the passed site URL</returns>
        public override WebRequestExecutor CreateWebRequestExecutor(ClientRuntimeContext context, string requestUrl)
        {
            return new HttpClientSPWebRequestExecutor(httpClient, context, requestUrl);
        }
    }
}
