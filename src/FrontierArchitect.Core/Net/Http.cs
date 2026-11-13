using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace FrontierArchitect.Core.Net
{
    public sealed class HttpRequest
    {
        public string Method = "GET";
        public string Url;
        public readonly List<KeyValuePair<string, string>> Headers = new List<KeyValuePair<string, string>>();
        public string Body;
        public int TimeoutMs = 60000;
        /// <summary>Max silence between two reads of a streamed body.</summary>
        public int ReadTimeoutMs = 180000;
        public bool AllowRedirect = true;
        public bool BinaryBody;

        public HttpRequest Header(string name, string value)
        {
            Headers.Add(new KeyValuePair<string, string>(name, value));
            return this;
        }
    }

    public sealed class HttpResponse
    {
        public int Status;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body;
        public byte[] BodyBytes;
        /// <summary>Set when the request never produced an HTTP status (DNS, TLS, reset, timeout).</summary>
        public string TransportError;
        public bool Ok => TransportError == null && Status >= 200 && Status < 300;

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }
    }

    public interface IHttpTransport
    {
        /// <summary>
        /// Sends a request. When <paramref name="onLine"/> is given and the status is 2xx, the body is
        /// streamed line by line to it (Server-Sent Events) instead of being buffered.
        /// </summary>
        HttpResponse Send(HttpRequest request, Action<string> onLine, CancellationToken cancel);
    }

    /// <summary>
    /// HttpWebRequest transport. It is the HTTP stack that behaves inside Unity's Mono runtime,
    /// and it runs on background threads (never call it from the Unity main thread).
    /// </summary>
    public sealed class WebRequestTransport : IHttpTransport
    {
        static WebRequestTransport()
        {
            try
            {
                // TLS 1.2 (+1.3 where the runtime knows it). 12288 = Tls13 on runtimes that have it.
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { }
                ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 8);
                ServicePointManager.Expect100Continue = false;
            }
            catch { }
        }

        public HttpResponse Send(HttpRequest request, Action<string> onLine, CancellationToken cancel)
        {
            var result = new HttpResponse();
            HttpWebRequest req;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(request.Url);
            }
            catch (Exception e)
            {
                result.TransportError = "Bad URL: " + e.Message;
                return result;
            }
            req.Method = request.Method;
            req.Timeout = request.TimeoutMs;
            req.ReadWriteTimeout = request.ReadTimeoutMs;
            req.AllowAutoRedirect = request.AllowRedirect;
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            foreach (var h in request.Headers)
            {
                if (h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) req.ContentType = h.Value;
                else if (h.Key.Equals("User-Agent", StringComparison.OrdinalIgnoreCase)) req.UserAgent = h.Value;
                else if (h.Key.Equals("Accept", StringComparison.OrdinalIgnoreCase)) req.Accept = h.Value;
                else req.Headers[h.Key] = h.Value;
            }

            using (cancel.Register(() => { try { req.Abort(); } catch { } }))
            {
                try
                {
                    if (request.Body != null)
                    {
                        var bytes = Encoding.UTF8.GetBytes(request.Body);
                        req.ContentLength = bytes.Length;
                        using (var s = req.GetRequestStream()) s.Write(bytes, 0, bytes.Length);
                    }
                    using (var resp = (HttpWebResponse)req.GetResponse())
                    {
                        Fill(result, resp, request, onLine, cancel);
                    }
                }
                catch (WebException we) when (we.Response is HttpWebResponse errResp)
                {
                    using (errResp) Fill(result, errResp, request, null, cancel);
                }
                catch (Exception e)
                {
                    result.TransportError = cancel.IsCancellationRequested ? "cancelled" : e.GetType().Name + ": " + e.Message;
                }
            }
            return result;
        }

        private static void Fill(HttpResponse result, HttpWebResponse resp, HttpRequest request, Action<string> onLine, CancellationToken cancel)
        {
            result.Status = (int)resp.StatusCode;
            foreach (string key in resp.Headers.AllKeys) result.Headers[key] = resp.Headers[key];
            using (var stream = resp.GetResponseStream())
            {
                if (stream == null) return;
                bool ok = result.Status >= 200 && result.Status < 300;
                if (ok && onLine != null)
                {
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            cancel.ThrowIfCancellationRequested();
                            onLine(line);
                        }
                    }
                    return;
                }
                if (ok && request.BinaryBody)
                {
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        result.BodyBytes = ms.ToArray();
                    }
                    return;
                }
                using (var reader = new StreamReader(stream, Encoding.UTF8)) result.Body = reader.ReadToEnd();
            }
        }
    }
}
