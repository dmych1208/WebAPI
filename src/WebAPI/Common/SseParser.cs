using System.IO;
using System.Text;

namespace WebAPI.Common
{
    public class SseParser
    {
        public class SseEvent
        {
            public string EventType { get; set; } = "";
            public string Data { get; set; } = "";
            public bool IsDone { get; set; }
            public bool IsInternal { get; set; }
        }

        private readonly StringBuilder _buffer = new();
        private string _lastEventType = "";

        public SseEvent? Parse(string rawChunk)
        {
            if (string.IsNullOrEmpty(rawChunk)) return null;

            _buffer.Append(rawChunk);
            var bufferStr = _buffer.ToString();

            int lastNewline = bufferStr.LastIndexOf('\n');
            if (lastNewline <= 0) return null;

            var processablePart = bufferStr.Substring(0, lastNewline + 1);
            _buffer.Remove(0, lastNewline + 1);

            using var reader = new StringReader(processablePart);
            string? line;
            SseEvent? result = null;

            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    _lastEventType = "";
                    continue;
                }

                line = line.Trim();

                if (line.StartsWith("event:"))
                {
                    _lastEventType = line.Substring(6).Trim();
                }
                else if (line.StartsWith("data:"))
                {
                    string data = line.Substring(5).Trim();

                    if (data == "[DONE]")
                    {
                        result = new SseEvent { Data = data, IsDone = true };
                        continue;
                    }

                    if (IsInternalMessage(data, _lastEventType))
                        continue;

                    string? content = ExtractContent(data);
                    if (content == null) continue;

                    result = new SseEvent
                    {
                        EventType = _lastEventType,
                        Data = content,
                        IsDone = false,
                        IsInternal = false
                    };
                }
            }

            return result;
        }

        public static bool IsInternalMessage(string json, string eventType)
        {
            if (string.IsNullOrEmpty(json)) return true;

            if (json.Contains("\"p\": \"status\"") ||
                json.Contains("\"accumulated_token_usage\"") ||
                json.Contains("\"p\":\"status\"") ||
                json == "FINISHED")
                return true;

            if (json.Contains("AI question rephraser") ||
                json.Contains("rephrase") ||
                json.Contains("query_rewrite") ||
                json.Contains("search_query"))
                return true;

            if (eventType is "title" or "update_session" or "search_result" or "ping" or "search" or "rephrase" or "rewrite")
                return true;

            return false;
        }

        public static string? ExtractContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            string[] keys = { "v", "content", "delta", "text", "choices" };

            foreach (var key in keys)
            {
                if (key == "choices")
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        json, "\"delta\"\\s*:\\s*\\{[^}]*\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (match.Success)
                        return UnescapeJson(match.Groups[1].Value);
                }
                else
                {
                    var match = System.Text.RegularExpressions.Regex.Match(
                        json, $"\"{key}\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                    if (match.Success && !string.IsNullOrEmpty(match.Groups[1].Value))
                        return UnescapeJson(match.Groups[1].Value);
                }
            }

            return null;
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
                      .Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        public void Reset()
        {
            _buffer.Clear();
            _lastEventType = "";
        }
    }
}