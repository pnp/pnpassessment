using PnP.Scanning.Core.Scanners;
using System.Net;
using System.Text;

namespace Microsoft.SharePoint.Client
{
    internal static class ClientContextExtensions
    {

        internal static async Task ExecuteQueryRetryAsync(this ClientRuntimeContext clientContext, int retryCount = 10)
        {
            await ExecuteQueryImplementationAsync(clientContext, retryCount);
        }

        private static async Task ExecuteQueryImplementationAsync(ClientRuntimeContext clientContext, int retryCount = 10)
        {
            // Set the TLS preference. Needed on some server os's to work when Office 365 removes support for TLS 1.0
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;

            var clientTag = string.Empty;
            int backoffInterval = 1000;
            int retryAttempts = 0;
            int retryAfterInterval = 0;
            bool retry = false;
            ClientRequestWrapper wrapper = null;
            ClientContextInfo clientContextInfo = clientContext.Tag as ClientContextInfo;

            if (retryCount <= 0)
            {
                throw new ArgumentException("Provide a retry count greater than zero.");
            }

            // Do while retry attempt is less than retry count
            while (retryAttempts < retryCount)
            {
                // Check if we're cancelling
                clientContextInfo.CancellationToken.ThrowIfCancellationRequested();

                try
                {
                    //clientContext.ClientTag = SetClientTag(clientTag);

                    // Make CSOM request more reliable by disabling the return value cache. Given we 
                    // often clone context objects and the default value is
                    clientContext.DisableReturnValueCache = true;

                    if (!retry)
                    {
                        await clientContext.ExecuteQueryAsync();
                    }
                    else
                    {
                        if (wrapper != null && wrapper.Value != null)
                        {
                            await clientContext.RetryQueryAsync(wrapper.Value);
                        }
                    }

                    return;
                }
                catch (WebException wex)
                {
                    var response = wex.Response as HttpWebResponse;
                    // Check if request was throttled - http status code 429
                    // Check is request failed due to server unavailable - http status code 503
                    if ((response != null &&
                        (response.StatusCode == (HttpStatusCode)429
                        || response.StatusCode == (HttpStatusCode)503
                        ))
                        || wex.Status == WebExceptionStatus.Timeout)
                    {
                        wrapper = (ClientRequestWrapper)wex.Data["ClientRequest"];
                        retry = true;
                        retryAfterInterval = 0;

                        //Add delay for retry, retry-after header is specified in seconds
                        if (response != null && response.Headers["Retry-After"] != null)
                        {
                            if (int.TryParse(response.Headers["Retry-After"], out int retryAfterHeaderValue))
                            {
                                retryAfterInterval = retryAfterHeaderValue * 1000;
                            }
                        }
                        else
                        {
                            retryAfterInterval = backoffInterval;
                            backoffInterval *= 2;
                        }

                        int retryAfterInSeconds = retryAfterInterval / 1000;

                        if (wex.Status == WebExceptionStatus.Timeout)
                        {
                            clientContextInfo.CsomEventHub.RequestRetry?.Invoke(new CsomRetryEvent(clientContextInfo.ScanId, 0, retryAfterInSeconds, new Exception("CSOM request timeout")));
                            clientContextInfo.Logger.Warning("CSOM request timeout. Retry attempt {RetryAttempts}. Sleeping for {RetryAfterInterval} milliseconds before retrying.", retryAttempts + 1, retryAfterInterval);
                        }
                        else
                        {
                            clientContextInfo.CsomEventHub.RequestRetry?.Invoke(new CsomRetryEvent(clientContextInfo.ScanId, (int)response.StatusCode, retryAfterInSeconds, null));
                            clientContextInfo.Logger.Warning("CSOM request frequency exceeded usage limits. Retry attempt {RetryAttempts}. Sleeping for {RetryAfterInterval} milliseconds before retrying.", retryAttempts + 1, retryAfterInterval);
                        }

                        await Task.Delay(retryAfterInterval, clientContextInfo.CancellationToken);

                        //Add to retry count and increase delay.
                        retryAttempts++;
                    }
                    else
                    {
                        var errorSb = new StringBuilder();

                        errorSb.AppendLine(wex.ToString());
                        errorSb.AppendLine($"TraceCorrelationId: {clientContext.TraceCorrelationId}");
                        errorSb.AppendLine($"Url: {clientContext.Url}");

                        //find innermost Error and check if it is a SocketException
                        Exception innermostEx = wex;
                        while (innermostEx.InnerException != null) innermostEx = innermostEx.InnerException;
                        var socketEx = innermostEx as System.Net.Sockets.SocketException;
                        if (socketEx != null)
                        {
                            errorSb.AppendLine($"ErrorCode: {socketEx.ErrorCode}"); //10054
                            errorSb.AppendLine($"SocketErrorCode: {socketEx.SocketErrorCode}"); //ConnectionReset
                            errorSb.AppendLine($"Message: {socketEx.Message}"); //An existing connection was forcibly closed by the remote host
                            clientContextInfo.Logger.Error(socketEx, string.Format("Socket exception: {0}", errorSb.ToString()));

                            //retry
                            wrapper = (ClientRequestWrapper)wex.Data["ClientRequest"];
                            retry = true;
                            retryAfterInterval = 0;

                            //Add delay for retry, retry-after header is specified in seconds
                            if (response != null && response.Headers["Retry-After"] != null)
                            {
                                if (int.TryParse(response.Headers["Retry-After"], out int retryAfterHeaderValue))
                                {
                                    retryAfterInterval = retryAfterHeaderValue * 1000;
                                }
                            }
                            else
                            {
                                retryAfterInterval = backoffInterval;
                                backoffInterval *= 2;
                            }
                            
                            int retryAfterInSeconds = retryAfterInterval / 1000;

                            clientContextInfo.CsomEventHub.RequestRetry?.Invoke(new CsomRetryEvent(clientContextInfo.ScanId, 0, retryAfterInSeconds, socketEx));
                            clientContextInfo.Logger.Warning("CSOM request socket exception. Retry attempt {RetryAttempts}. Sleeping for {RetryAfterInterval} milliseconds before retrying.", retryAttempts + 1, retryAfterInterval);

                            await Task.Delay(retryAfterInterval, clientContextInfo.CancellationToken);

                            //Add to retry count and increase delay.
                            retryAttempts++;
                        }
                        else
                        {
                            if (response != null)
                            {
                                if (response.Headers.AllKeys.Any(k => string.Equals(k, "SPRequestGuid", StringComparison.InvariantCultureIgnoreCase)))
                                {
                                    var spRequestGuid = response.Headers["SPRequestGuid"];
                                    errorSb.AppendLine($"ServerErrorTraceCorrelationId: {spRequestGuid}");
                                }
                            }

                            clientContextInfo.Logger.Error(wex, string.Format("Unhandled exception during CSOM request: {0}", errorSb.ToString()));
                            throw;
                        }
                    }
                }
                catch (ServerException serverEx)
                {
                    var errorSb = new StringBuilder();

                    errorSb.AppendLine(serverEx.ToString());
                    errorSb.AppendLine($"ServerErrorCode: {serverEx.ServerErrorCode}");
                    errorSb.AppendLine($"ServerErrorTypeName: {serverEx.ServerErrorTypeName}");
                    errorSb.AppendLine($"ServerErrorTraceCorrelationId: {serverEx.ServerErrorTraceCorrelationId}");
                    errorSb.AppendLine($"ServerErrorValue: {serverEx.ServerErrorValue}");
                    errorSb.AppendLine($"ServerErrorDetails: {serverEx.ServerErrorDetails}");

                    clientContextInfo.Logger.Error(serverEx, string.Format("Unhandled server exception during CSOM request: {0}", errorSb.ToString()));

                    throw;
                }
            }

            throw new Exception($"Maximum retry attempts {retryCount}, has be attempted.");
        }

    }
}
