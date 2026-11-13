using System;
using System.Collections.Generic;
using System.Threading;
using FrontierArchitect.Core.Data;
using FrontierArchitect.Core.Net;

namespace FrontierArchitect.Core.Claude
{
    public sealed class ClaudeOptions
    {
        public string ApiKey;
        public string BaseUrl = "https://api.anthropic.com";
        public string ApiVersion = "2026-10-21";
        public int MaxRetries = 4;
        public int ConnectTimeoutMs = 60000;
        public int StreamIdleTimeoutMs = 300000;
        public string UserAgent = "FrontierArchitect";
    }

    public enum ClaudeErrorKind { None, Auth, Permission, NotFound, InvalidRequest, RateLimit, Overloaded, Server, Network, Cancelled, Refusal, Protocol }

    public sealed class ClaudeException : Exception
    {
        public ClaudeErrorKind Kind { get; }
        public int Status { get; }
        public string ApiErrorType { get; }

        public ClaudeException(ClaudeErrorKind kind, int status, string apiType, string message) : base(message)
        {
            Kind = kind; Status = status; ApiErrorType = apiType;
        }

        public bool Retryable => Kind == ClaudeErrorKind.RateLimit || Kind == ClaudeErrorKind.Overloaded ||
                                 Kind == ClaudeErrorKind.Server || Kind == ClaudeErrorKind.Network;

        /// <summary>Text safe to show a player (never contains the key).</summary>
        public string PlayerMessage
        {
            get
            {
                switch (Kind)
                {
                    case ClaudeErrorKind.Auth: return "The API key was rejected. Check it in Settings.";
                    case ClaudeErrorKind.Permission: return "This API key may not use the requested model or feature: " + Message;
                    case ClaudeErrorKind.RateLimit: return "Rate limited by the Anthropic API. Try again in a minute.";
                    case ClaudeErrorKind.Overloaded: return "The Anthropic API is overloaded. Try again shortly.";
                    case ClaudeErrorKind.Network: return "Could not reach api.anthropic.com: " + Message;
                    case ClaudeErrorKind.Cancelled: return "Cancelled.";
                    default: return Message;
                }
            }
        }
    }

    /// <summary>
    /// Raw-HTTPS client for the Anthropic Messages API (streaming). The official C# SDK depends on
    /// System.Text.Json and friends, which do not load reliably inside the game's Unity Mono
    /// runtime; this client needs nothing beyond the base class library.
    /// </summary>
    public sealed class ClaudeClient
    {
        private readonly IHttpTransport _http;
        private readonly ClaudeOptions _opt;
        private readonly Func<int, TimeSpan> _backoff;
        private readonly Action<TimeSpan, CancellationToken> _sleep;

        public ClaudeClient(IHttpTransport http, ClaudeOptions options, Action<TimeSpan, CancellationToken> sleep = null)
        {
            _http = http;
            _opt = options;
            _backoff = attempt => TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
            _sleep = sleep ?? ((t, c) => c.WaitHandle.WaitOne(t));
        }

        private HttpRequest NewRequest(string method, string path, IEnumerable<string> betas)
        {
            if (string.IsNullOrWhiteSpace(_opt.ApiKey))
                throw new ClaudeException(ClaudeErrorKind.Auth, 0, "authentication_error", "No API key configured.");
            var r = new HttpRequest
            {
                Method = method,
                Url = _opt.BaseUrl.TrimEnd('/') + path,
                TimeoutMs = _opt.ConnectTimeoutMs,
                ReadTimeoutMs = _opt.StreamIdleTimeoutMs
            }
            .Header("x-api-key", _opt.ApiKey.Trim())
            .Header("anthropic-version", _opt.ApiVersion)
            .Header("User-Agent", _opt.UserAgent);
            if (betas != null)
            {
                var list = new List<string>(betas);
                if (list.Count > 0) r.Header("anthropic-beta", string.Join(",", list));
            }
            return r;
        }

        /// <summary>GET /v1/models — the ids this key may use.</summary>
        public List<string> ListModels(CancellationToken cancel)
        {
            var ids = new List<string>();
            string after = null;
            for (int page = 0; page < 5; page++)
            {
                var req = NewRequest("GET", "/v1/models?limit=100" + (after != null ? "&after_id=" + Uri.EscapeDataString(after) : ""), null);
                var resp = _http.Send(req, null, cancel);
                if (!resp.Ok) throw ToError(resp);
                var o = Json.ParseObject(resp.Body);
                foreach (var m in o.Arr("data").Objects()) ids.Add(m.Str("id"));
                if (!o.Bool("has_more")) break;
                after = o.Str("last_id");
            }
            return ids;
        }

        /// <summary>
        /// POST /v1/messages with stream:true. Retries 429/5xx/529 and dropped connections with
        /// exponential backoff (honouring retry-after). Returns the complete message.
        /// </summary>
        public MessageResult Stream(JObject body, IEnumerable<string> betas, Action<string, JObject> onEvent, CancellationToken cancel)
        {
            body.Set("stream", true);
            string payload = Json.Write(body);
            var betaList = betas == null ? new List<string>() : new List<string>(betas);
            for (int attempt = 0; ; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                var acc = new StreamAccumulator();
                if (onEvent != null) acc.OnEvent += onEvent;
                var req = NewRequest("POST", "/v1/messages", betaList);
                req.Header("Content-Type", "application/json");
                req.Body = payload;
                HttpResponse resp = _http.Send(req, acc.FeedLine, cancel);
                acc.Finish();
                acc.Compact();

                ClaudeException err = null;
                if (cancel.IsCancellationRequested) throw new ClaudeException(ClaudeErrorKind.Cancelled, 0, null, "cancelled");
                if (resp.TransportError != null) err = new ClaudeException(ClaudeErrorKind.Network, 0, null, resp.TransportError);
                else if (!resp.Ok) err = ToError(resp);
                else if (acc.Error != null)
                {
                    string t = acc.Error.Str("type", "api_error");
                    err = new ClaudeException(KindFromType(t, 0), 0, t, acc.Error.Str("message", "stream error"));
                }
                else if (!acc.Completed) err = new ClaudeException(ClaudeErrorKind.Network, resp.Status, null, "stream ended before message_stop");

                if (err == null) return acc.Result;
                if (!err.Retryable || attempt >= _opt.MaxRetries) throw err;
                var wait = _backoff(attempt);
                string ra = resp.Header("retry-after");
                if (ra != null && double.TryParse(ra, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double secs))
                    wait = TimeSpan.FromSeconds(Math.Min(120, Math.Max(1, secs)));
                _sleep(wait, cancel);
            }
        }

        private static ClaudeException ToError(HttpResponse resp)
        {
            if (resp.TransportError != null) return new ClaudeException(ClaudeErrorKind.Network, 0, null, resp.TransportError);
            string type = null, message = "HTTP " + resp.Status;
            try
            {
                var o = Json.ParseObject(resp.Body ?? "{}");
                var e = o.Obj("error");
                if (e != null) { type = e.Str("type"); message = e.Str("message", message); }
            }
            catch { if (!string.IsNullOrEmpty(resp.Body)) message += ": " + (resp.Body.Length > 300 ? resp.Body.Substring(0, 300) : resp.Body); }
            return new ClaudeException(KindFromType(type, resp.Status), resp.Status, type, message);
        }

        private static ClaudeErrorKind KindFromType(string type, int status)
        {
            switch (type)
            {
                case "authentication_error": return ClaudeErrorKind.Auth;
                case "permission_error": return ClaudeErrorKind.Permission;
                case "not_found_error": return ClaudeErrorKind.NotFound;
                case "invalid_request_error": return ClaudeErrorKind.InvalidRequest;
                case "rate_limit_error": return ClaudeErrorKind.RateLimit;
                case "overloaded_error": return ClaudeErrorKind.Overloaded;
                case "api_error": return ClaudeErrorKind.Server;
            }
            if (status == 401) return ClaudeErrorKind.Auth;
            if (status == 403) return ClaudeErrorKind.Permission;
            if (status == 404) return ClaudeErrorKind.NotFound;
            if (status == 400 || status == 413) return ClaudeErrorKind.InvalidRequest;
            if (status == 429) return ClaudeErrorKind.RateLimit;
            if (status == 529) return ClaudeErrorKind.Overloaded;
            if (status >= 500) return ClaudeErrorKind.Server;
            return ClaudeErrorKind.Protocol;
        }

        // ------------------------------------------------------------------ history helpers

        /// <summary>
        /// The assistant content to append to the conversation. Blocks are passed back unchanged
        /// (thinking blocks are bound to the conversation and must not be edited). One exception,
        /// from the server-side fallback rules: after a mid-output fallback, thinking, redacted
        /// thinking and tool_use blocks that come before the last "fallback" block are omitted,
        /// and the fallback block itself stays where it appeared.
        /// </summary>
        public static JArray EchoContent(JArray content)
        {
            int lastFallback = -1;
            for (int i = 0; i < content.Count; i++)
                if ((content[i] as JObject)?.Str("type") == "fallback") lastFallback = i;
            var echo = new JArray();
            for (int i = 0; i < content.Count; i++)
            {
                var b = content[i] as JObject;
                if (b == null) continue;
                string t = b.Str("type");
                if (i < lastFallback && (t == "thinking" || t == "redacted_thinking" || t == "tool_use" || t == "server_tool_use"))
                    continue;
                echo.Add(b);
            }
            return echo;
        }
    }

    /// <summary>
    /// Resolves "most-capable" to a concrete model id from the models this key can use.
    /// </summary>
    public static class ModelResolver
    {
        public const string MostCapable = "most-capable";

        /// <summary>Most capable first. Fable 5.2 is Anthropic's most capable widely released model.</summary>
        public static readonly string[] Preference =
        {
            "claude-fable-5-2", "claude-opus-5-5", "claude-fable-5-1", "claude-opus-5", "claude-opus-4-8", "claude-sonnet-5-5"
        };

        public const string SafeDefault = "claude-opus-5-5";

        public static List<string> Candidates(string configured, IList<string> available)
        {
            var list = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured) && configured != MostCapable)
            {
                list.Add(configured.Trim());
                return list;
            }
            if (available != null)
                foreach (var p in Preference)
                    if (available.Contains(p)) list.Add(p);
            if (!list.Contains(SafeDefault)) list.Add(SafeDefault);
            return list;
        }

        /// <summary>Models on which the server-side refusal fallback ("fallbacks": "default") applies.</summary>
        public static bool SupportsServerFallback(string model) =>
            model == "claude-fable-5-2" || model == "claude-fable-5-1" || model == "claude-opus-5-5" ||
            model == "claude-opus-5" || model == "claude-sonnet-5-5";
    }
}
