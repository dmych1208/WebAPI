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

            if (eventType is "title" or "update_session" or "search_result" or "ping")
                return true;

            return false;
        }

        public static string? ExtractContent(string json, Action<string>? logCallback = null)
        {
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("o", out var opProp) &&
                    opProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var op = opProp.GetString();
                    if (op == "APPEND" && root.TryGetProperty("p", out var pathProp))
                    {
                        var path = pathProp.GetString() ?? "";
                        if (path.Contains("content") && root.TryGetProperty("v", out var vProp))
                        {
                            if (vProp.ValueKind == System.Text.Json.JsonValueKind.String)
                                return vProp.GetString();
                        }
                    }
                    return null;
                }

                if (root.TryGetProperty("p", out var p2) && root.TryGetProperty("v", out var v2))
                {
                    var path2 = p2.GetString() ?? "";
                    if (path2.Contains("fragments") && v2.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in v2.EnumerateArray())
                        {
                            if (item.TryGetProperty("content", out var fragContent) &&
                                fragContent.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                return fragContent.GetString();
                            }
                        }
                    }
                }

                if (root.TryGetProperty("choices", out var choices) &&
                    choices.ValueKind == System.Text.Json.JsonValueKind.Array &&
                    choices.GetArrayLength() > 0)
                {
                    var firstChoice = choices[0];

                    if (firstChoice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var deltaContent) &&
                            deltaContent.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            return deltaContent.GetString();
                        }
                        if (delta.TryGetProperty("reasoning_content", out var reasoningContent) &&
                            reasoningContent.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var rc = reasoningContent.GetString();
                            if (!string.IsNullOrEmpty(rc)) return $"<think:{rc}>";
                        }
                    }

                    if (firstChoice.TryGetProperty("message", out var message) &&
                        message.TryGetProperty("content", out var messageContent) &&
                        messageContent.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return messageContent.GetString();
                    }

                    if (firstChoice.TryGetProperty("text", out var choiceText) &&
                        choiceText.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        return choiceText.GetString();
                    }
                }

                if (root.TryGetProperty("content", out var contentProp))
                {
                    if (contentProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        return contentProp.GetString();
                }

                if (root.TryGetProperty("text", out var textProp))
                {
                    if (textProp.ValueKind == System.Text.Json.JsonValueKind.String)
                        return textProp.GetString();
                }

                if (root.TryGetProperty("v", out var vOnly) &&
                    vOnly.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var propCount = 0;
                    foreach (var _ in root.EnumerateObject()) propCount++;
                    if (propCount == 1)
                    {
                        var vValue = vOnly.GetString();
                        if (!string.IsNullOrEmpty(vValue) && vValue != "FINISHED")
                            return vValue;
                    }
                }

                if (root.TryGetProperty("data", out var dataProp) &&
                    dataProp.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var dataStr = dataProp.GetString();
                    if (!string.IsNullOrEmpty(dataStr) && dataStr.Length > 0 && dataStr.Length < 10000)
                    {
                        var inner = ExtractContent(dataStr);
                        if (inner != null) return inner;
                    }
                }

                if (root.TryGetProperty("output", out var outputProp))
                {
                    if (outputProp.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        if (outputProp.TryGetProperty("text", out var outputText) &&
                            outputText.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var t = outputText.GetString();
                            if (!string.IsNullOrEmpty(t)) return t;
                        }
                        if (outputProp.TryGetProperty("choices", out var outputChoices) &&
                            outputChoices.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var oc in outputChoices.EnumerateArray())
                            {
                                if (oc.TryGetProperty("message", out var om) &&
                                    om.TryGetProperty("content", out var omc) &&
                                    omc.ValueKind == System.Text.Json.JsonValueKind.String)
                                {
                                    return omc.GetString();
                                }
                            }
                        }
                    }
                    if (outputProp.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var s = outputProp.GetString();
                        if (!string.IsNullOrEmpty(s)) return s;
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool TryGetDeepSeekContent(System.Text.Json.JsonElement root, out string? content)
        {
            content = null;

            if (root.TryGetProperty("choices", out var choices) &&
                choices.ValueKind == System.Text.Json.JsonValueKind.Array &&
                choices.GetArrayLength() > 0)
            {
                var firstChoice = choices[0];

                if (firstChoice.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var prop in firstChoice.EnumerateObject())
                    {
                        if (prop.Name == "delta" || prop.Name == "message" || prop.Name == "text")
                        {
                            var innerObj = prop.Value;
                            if (innerObj.TryGetProperty("content", out var innerContent) &&
                                innerContent.ValueKind == System.Text.Json.JsonValueKind.String)
                            {
                                content = innerContent.GetString();
                                return content != null;
                            }
                        }
                    }
                }
            }

            return false;
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