using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace StremioVideoCenter.Services
{
    // =============================================================
    // BOUNDED HTTP (Slice 20 Stage 1A)
    // =============================================================
    //
    // Shared download helper for the services that previously called
    // WebClient.DownloadString / DownloadData with no timeout of their
    // own (WebClient's default is ~100 seconds) and no way to cancel.
    // Each call here has:
    //
    //   - a hard overall deadline (timeoutMs). The underlying
    //     HttpWebRequest.Timeout / ReadWriteTimeout are also set, but the
    //     deadline is enforced by racing the download against a timer
    //     (the same approach KitsuService documents), because
    //     HttpWebRequest.Timeout alone does not reliably cover every hang
    //     (e.g. name resolution).
    //   - optional cancellation. A cancelled or expired call aborts the
    //     in-flight request and the caller gets an exception back
    //     immediately; the background thread finishes by itself.
    //
    // The request itself is unchanged from before: still a plain
    // WebClient over TLS 1.2 with the same User-Agent, so response
    // decoding and headers behave exactly as they did.
    //
    // Failure contract:
    //   - caller cancelled        -> OperationCanceledException
    //   - deadline expired        -> TimeoutException (fixed message, never
    //                                contains the URL)
    //   - anything else           -> the original exception (WebException...)
    //
    // KitsuService keeps its own, older timeout implementation and is
    // intentionally not routed through this class.
    internal static class BoundedHttp
    {
        public const string UserAgent = "StremioVideoCenter/1.0";

        public static Task<string> DownloadStringAsync(
            string url,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            return RunBoundedAsync<string>(
                client => client.DownloadString(url),
                timeoutMs,
                cancellationToken);
        }

        public static Task<byte[]> DownloadDataAsync(
            string url,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            return RunBoundedAsync<byte[]>(
                client => client.DownloadData(url),
                timeoutMs,
                cancellationToken);
        }

        private static async Task<T> RunBoundedAsync<T>(
            Func<BoundedWebClient, T> download,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using (CancellationTokenSource deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (BoundedWebClient client = new BoundedWebClient(timeoutMs))
            {
                deadline.CancelAfter(timeoutMs);

                using (deadline.Token.Register(client.AbortQuietly))
                {
                    Task<T> work = Task.Run(() =>
                    {
                        ServicePointManager.SecurityProtocol =
                            SecurityProtocolType.Tls12;

                        client.Headers[HttpRequestHeader.UserAgent] = UserAgent;

                        return download(client);
                    });

                    Task expiry = Task.Delay(Timeout.Infinite, deadline.Token);

                    Task first = await Task.WhenAny(work, expiry).ConfigureAwait(false);

                    if (first == work)
                    {
                        try
                        {
                            return await work.ConfigureAwait(false);
                        }
                        catch (Exception)
                        {
                            // The abort itself can surface as a WebException
                            // just before the race is observed; report that
                            // as the timeout/cancellation it really was.
                            if (deadline.IsCancellationRequested)
                            {
                                throw CreateStopException(cancellationToken, timeoutMs);
                            }

                            throw;
                        }
                    }

                    // Deadline or cancellation won. The abandoned download
                    // fails on its own after the abort - observe its
                    // exception so it is never reported as unobserved.
                    Task observed = work.ContinueWith(
                        t => { Exception ignored = t.Exception; },
                        TaskContinuationOptions.OnlyOnFaulted);

                    throw CreateStopException(cancellationToken, timeoutMs);
                }
            }
        }

        private static Exception CreateStopException(
            CancellationToken cancellationToken,
            int timeoutMs)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new OperationCanceledException(cancellationToken);
            }

            return new TimeoutException(
                "The request did not complete within " + timeoutMs + " ms.");
        }

        // WebClient exposes no timeout of its own: GetWebRequest is the
        // documented hook for bounding it, and also lets us keep hold of
        // the request so it can be aborted.
        private sealed class BoundedWebClient : WebClient
        {
            private readonly int _timeoutMs;
            private WebRequest _request;
            private bool _aborted;

            public BoundedWebClient(int timeoutMs)
            {
                _timeoutMs = timeoutMs;
            }

            protected override WebRequest GetWebRequest(Uri address)
            {
                WebRequest request = base.GetWebRequest(address);

                HttpWebRequest http = request as HttpWebRequest;

                if (http != null)
                {
                    http.Timeout = _timeoutMs;
                    http.ReadWriteTimeout = _timeoutMs;
                }

                lock (this)
                {
                    _request = request;

                    if (_aborted)
                    {
                        AbortRequest(request);
                    }
                }

                return request;
            }

            public void AbortQuietly()
            {
                lock (this)
                {
                    _aborted = true;

                    if (_request != null)
                    {
                        AbortRequest(_request);
                    }
                }
            }

            private static void AbortRequest(WebRequest request)
            {
                try
                {
                    request.Abort();
                }
                catch (Exception)
                {
                    // Aborting is best-effort; the deadline race already
                    // released the caller.
                }
            }
        }
    }
}
