using System;
using System.Collections.Generic;
using System.Text;
using FrontierArchitect.Core.Data;

namespace FrontierArchitect.Core.Claude
{
    /// <summary>Final message rebuilt from a Messages API event stream.</summary>
    public sealed class MessageResult
    {
        public string Id;
        public string Model;
        public string StopReason;
        public JObject StopDetails;
        public JObject Usage = new JObject();
        /// <summary>Content blocks exactly as the model produced them (thinking blocks keep their signature).</summary>
        public readonly JArray Content = new JArray();
        /// <summary>tool_use ids whose streamed input was not valid JSON, with the raw text received.</summary>
        public readonly Dictionary<string, string> InvalidToolInputs = new Dictionary<string, string>();

        public string Text
        {
            get
            {
                var sb = new StringBuilder();
                foreach (var b in Content.Objects())
                    if (b.Str("type") == "text") sb.Append(b.Str("text"));
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Folds Server-Sent Events from POST /v1/messages (stream: true) into a <see cref="MessageResult"/>.
    /// Handles text, thinking (+signature), redacted thinking, tool_use (input_json_delta) and keeps
    /// any other block type (for example "fallback") exactly as it arrived.
    /// </summary>
    public sealed class StreamAccumulator
    {
        private readonly MessageResult _msg = new MessageResult();
        private readonly Dictionary<int, JObject> _blocks = new Dictionary<int, JObject>();
        private readonly Dictionary<int, StringBuilder> _text = new Dictionary<int, StringBuilder>();
        private readonly Dictionary<int, StringBuilder> _json = new Dictionary<int, StringBuilder>();
        private readonly Dictionary<int, StringBuilder> _thinking = new Dictionary<int, StringBuilder>();
        private readonly Dictionary<int, StringBuilder> _signature = new Dictionary<int, StringBuilder>();
        private string _eventName;
        private readonly StringBuilder _data = new StringBuilder();

        public bool Started { get; private set; }
        public bool Completed { get; private set; }
        public JObject Error { get; private set; }

        /// <summary>Raised for every parsed event (for progress display).</summary>
        public event Action<string, JObject> OnEvent;

        public MessageResult Result => _msg;

        /// <summary>Feed one raw SSE line.</summary>
        public void FeedLine(string line)
        {
            if (line == null) return;
            if (line.Length == 0) { Dispatch(); return; }
            if (line.StartsWith(":")) return; // comment
            if (line.StartsWith("event:")) { _eventName = line.Substring(6).Trim(); return; }
            if (line.StartsWith("data:"))
            {
                if (_data.Length > 0) _data.Append('\n');
                _data.Append(line.Substring(5).TrimStart());
            }
        }

        /// <summary>Flush a trailing event that was not followed by a blank line.</summary>
        public void Finish() => Dispatch();

        private void Dispatch()
        {
            if (_data.Length == 0) { _eventName = null; return; }
            string data = _data.ToString();
            _data.Length = 0;
            JObject ev;
            try { ev = Json.ParseObject(data); }
            catch { _eventName = null; return; }
            string type = ev.Str("type") ?? _eventName;
            _eventName = null;
            Handle(type, ev);
            OnEvent?.Invoke(type, ev);
        }

        private void Handle(string type, JObject ev)
        {
            switch (type)
            {
                case "message_start":
                    {
                        Started = true;
                        var m = ev.Obj("message");
                        _msg.Id = m.Str("id");
                        _msg.Model = m.Str("model");
                        if (m.Obj("usage") != null) MergeUsage(m.Obj("usage"));
                        break;
                    }
                case "content_block_start":
                    {
                        int idx = ev.Int("index");
                        var block = (ev.Obj("content_block") ?? new JObject()).Clone();
                        _blocks[idx] = block;
                        string bt = block.Str("type");
                        if (bt == "text") _text[idx] = new StringBuilder(block.Str("text", ""));
                        else if (bt == "thinking")
                        {
                            _thinking[idx] = new StringBuilder(block.Str("thinking", ""));
                            _signature[idx] = new StringBuilder(block.Str("signature", ""));
                        }
                        else if (bt == "tool_use" || bt == "server_tool_use") _json[idx] = new StringBuilder();
                        break;
                    }
                case "content_block_delta":
                    {
                        int idx = ev.Int("index");
                        var d = ev.Obj("delta");
                        switch (d.Str("type"))
                        {
                            case "text_delta": Get(_text, idx).Append(d.Str("text", "")); break;
                            case "thinking_delta": Get(_thinking, idx).Append(d.Str("thinking", "")); break;
                            case "signature_delta": Get(_signature, idx).Append(d.Str("signature", "")); break;
                            case "input_json_delta": Get(_json, idx).Append(d.Str("partial_json", "")); break;
                        }
                        break;
                    }
                case "content_block_stop":
                    Close(ev.Int("index"));
                    break;
                case "message_delta":
                    {
                        var d = ev.Obj("delta");
                        if (d != null)
                        {
                            if (d.Str("stop_reason") != null) _msg.StopReason = d.Str("stop_reason");
                            if (d.Obj("stop_details") != null) _msg.StopDetails = d.Obj("stop_details");
                        }
                        if (ev.Obj("usage") != null) MergeUsage(ev.Obj("usage"));
                        break;
                    }
                case "message_stop":
                    foreach (var idx in new List<int>(_blocks.Keys)) Close(idx);
                    Completed = true;
                    break;
                case "error":
                    Error = ev.Obj("error") ?? ev;
                    break;
            }
        }

        private static StringBuilder Get(Dictionary<int, StringBuilder> d, int idx)
        {
            StringBuilder sb;
            if (!d.TryGetValue(idx, out sb)) { sb = new StringBuilder(); d[idx] = sb; }
            return sb;
        }

        private void Close(int idx)
        {
            JObject block;
            if (!_blocks.TryGetValue(idx, out block)) return;
            _blocks.Remove(idx);
            string bt = block.Str("type");
            if (bt == "text" && _text.ContainsKey(idx)) block.Set("text", _text[idx].ToString());
            else if (bt == "thinking")
            {
                block.Set("thinking", _thinking.ContainsKey(idx) ? _thinking[idx].ToString() : "");
                block.Set("signature", _signature.ContainsKey(idx) ? _signature[idx].ToString() : block.Str("signature", ""));
            }
            else if ((bt == "tool_use" || bt == "server_tool_use") && _json.ContainsKey(idx))
            {
                string raw = _json[idx].ToString();
                if (raw.Trim().Length == 0) block.Set("input", block.Obj("input") ?? new JObject());
                else
                {
                    object parsed; string err;
                    if (Json.TryParse(raw, out parsed, out err) && parsed is JObject po) block.Set("input", po);
                    else
                    {
                        block.Set("input", new JObject());
                        _msg.InvalidToolInputs[block.Str("id") ?? ("idx" + idx)] = raw;
                    }
                }
            }
            // keep blocks ordered by stream index
            while (_msg.Content.Count <= idx) _msg.Content.Add(null);
            _msg.Content[idx] = block;
        }

        /// <summary>Content with gaps removed (indices are dense in practice; this is defensive).</summary>
        public void Compact()
        {
            for (int i = _msg.Content.Count - 1; i >= 0; i--) if (_msg.Content[i] == null) _msg.Content.RemoveAt(i);
        }

        private void MergeUsage(JObject u)
        {
            foreach (var kv in u)
            {
                if (Json.IsNumber(kv.Value))
                {
                    // message_delta usage values are cumulative totals, so the latest value wins.
                    _msg.Usage.Set(kv.Key, kv.Value);
                }
                else if (kv.Value != null) _msg.Usage.Set(kv.Key, kv.Value);
            }
        }
    }
}
