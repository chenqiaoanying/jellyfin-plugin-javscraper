using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.JavScraper.Http
{
    public class HttpRetryMessageHandler : DelegatingHandler
    {
        public HttpRetryMessageHandler(HttpMessageHandler innerHandler) : base(innerHandler)
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var retryAttempt = 0;
            Exception? exception = null;

            while (retryAttempt <= 3)
            {
                try
                {
                    return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    exception = e;
                    retryAttempt++;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(3, retryAttempt)), cancellationToken).ConfigureAwait(false);
                }
            }

            throw exception!;
        }
    }
}
