using System.Text.Json;
using WebAPI.Models;

namespace WebAPI.Common
{
    /// <summary>OpenAI 请求解析器（优先使用 System.Text.Json，失败时 fallback 正则）</summary>
    public static class RequestParser
    {
        /// <summary>从JSON请求体解析ChatRequest</summary>
        public static ChatRequest Parse(string jsonBody)
        {
            if (string.IsNullOrWhiteSpace(jsonBody))
                return new ChatRequest();

            try
            {
                return ParseWithJson(jsonBody);
            }
            catch (Exception ex)
            {
                LogManager.Write($"JSON 解析请求失败，fallback 正则: {ex.Message}", LogLevel.Warn);
                return ParseWithRegex(jsonBody);
            }
        }

        private static ChatRequest ParseWithJson(string jsonBody)
        {
            var req = new ChatRequest();
            using var doc = JsonDocument.Parse(jsonBody);
            var root = doc.RootElement;

            if (root.TryGetProperty("model", out var modelProp))
                req.Model = modelProp.GetString() ?? "";

            if (root.TryGetProperty("stream", out var streamProp))
                req.Stream = streamProp.GetBoolean();

            if (root.TryGetProperty("temperature", out var tempProp))
                req.Temperature = tempProp.GetDouble();

            if (root.TryGetProperty("max_tokens", out var tokensProp))
                req.MaxTokens = tokensProp.GetInt32();

            if (root.TryGetProperty("messages", out var messagesProp) &&
                messagesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var msg in messagesProp.EnumerateArray())
                {
                    var role = msg.TryGetProperty("role", out var roleProp)
                        ? roleProp.GetString() ?? ""
                        : "";

                    var content = "";
                    if (msg.TryGetProperty("content", out var contentProp))
                    {
                        // content 可能是字符串或数组（多模态）
                        if (contentProp.ValueKind == JsonValueKind.String)
                        {
                            content = contentProp.GetString() ?? "";
                        }
                        else if (contentProp.ValueKind == JsonValueKind.Array)
                        {
                            // 提取所有 type=text 的内容
                            var sb = new System.Text.StringBuilder();
                            foreach (var part in contentProp.EnumerateArray())
                            {
                                if (part.TryGetProperty("type", out var typeProp) &&
                                    typeProp.GetString() == "text" &&
                                    part.TryGetProperty("text", out var textProp))
                                {
                                    sb.Append(textProp.GetString());
                                }
                            }
                            content = sb.ToString();
                        }
                    }

                    if (!string.IsNullOrEmpty(role))
                    {
                        req.Messages.Add(new ChatMessage { Role = role, Content = content });
                    }
                }
            }

            return req;
        }

        /// <summary>Fallback：正则解析（仅当 JSON 解析失败时使用）</summary>
        private static ChatRequest ParseWithRegex(string jsonBody)
        {
            var req = new ChatRequest();

            var modelMatch = System.Text.RegularExpressions.Regex.Match(jsonBody, "\"model\"\\s*:\\s*\"([^\"]+)\"");
            if (modelMatch.Success) req.Model = modelMatch.Groups[1].Value;

            var streamMatch = System.Text.RegularExpressions.Regex.Match(jsonBody, "\"stream\"\\s*:\\s*(true|false)");
            if (streamMatch.Success) req.Stream = streamMatch.Groups[1].Value == "true";

            var tempMatch = System.Text.RegularExpressions.Regex.Match(jsonBody, "\"temperature\"\\s*:\\s*([0-9.]+)");
            if (tempMatch.Success && double.TryParse(tempMatch.Groups[1].Value, out double temp))
                req.Temperature = temp;

            var tokensMatch = System.Text.RegularExpressions.Regex.Match(jsonBody, "\"max_tokens\"\\s*:\\s*(\\d+)");
            if (tokensMatch.Success && int.TryParse(tokensMatch.Groups[1].Value, out int tokens))
                req.MaxTokens = tokens;

            var roleRegex = new System.Text.RegularExpressions.Regex("\"role\"\\s*:\\s*\"(system|user|assistant)\"");
            var contentRegex = new System.Text.RegularExpressions.Regex("\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");

            var roles = roleRegex.Matches(jsonBody);
            var contents = contentRegex.Matches(jsonBody);

            if (roles.Count > 0 && roles.Count == contents.Count)
            {
                for (int i = 0; i < roles.Count; i++)
                {
                    req.Messages.Add(new ChatMessage
                    {
                        Role = roles[i].Groups[1].Value,
                        Content = UnescapeJson(contents[i].Groups[1].Value)
                    });
                }
            }

            return req;
        }

        /// <summary>提取 system 和 user prompt</summary>
        public static (string systemPrompt, string userPrompt) ExtractPrompts(ChatRequest req)
        {
            string systemPrompt = "";
            string userPrompt = "";

            foreach (var msg in req.Messages)
            {
                if (msg.Role == "system")
                    systemPrompt += msg.Content + "\n\n";
                else if (msg.Role == "user")
                    userPrompt = msg.Content;
            }

            return (systemPrompt.TrimEnd(), userPrompt);
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
                      .Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
