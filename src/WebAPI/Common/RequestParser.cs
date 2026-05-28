using System.Text.RegularExpressions;
using WebAPI.Models;

namespace WebAPI.Common
{
    /// <summary>OpenAI 请求解析器</summary>
    public static class RequestParser
    {
        private static readonly Regex RoleRegex = new("\"role\"\\s*:\\s*\"(system|user|assistant)\"", RegexOptions.Compiled);
        private static readonly Regex ContentRegex = new("\"content\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);

        /// <summary>从JSON请求体解析ChatRequest</summary>
        public static ChatRequest Parse(string jsonBody)
        {
            var req = new ChatRequest();

            // 提取 model
            var modelMatch = Regex.Match(jsonBody, "\"model\"\\s*:\\s*\"([^\"]+)\"");
            if (modelMatch.Success)
                req.Model = modelMatch.Groups[1].Value;

            // 提取 stream (默认true)
            var streamMatch = Regex.Match(jsonBody, "\"stream\"\\s*:\\s*(true|false)");
            if (streamMatch.Success)
                req.Stream = streamMatch.Groups[1].Value == "true";

            // 提取 temperature
            var tempMatch = Regex.Match(jsonBody, "\"temperature\"\\s*:\\s*([0-9.]+)");
            if (tempMatch.Success && double.TryParse(tempMatch.Groups[1].Value, out double temp))
                req.Temperature = temp;

            // 提取 max_tokens
            var tokensMatch = Regex.Match(jsonBody, "\"max_tokens\"\\s*:\\s*(\\d+)");
            if (tokensMatch.Success && int.TryParse(tokensMatch.Groups[1].Value, out int tokens))
                req.MaxTokens = tokens;

            // 提取 messages
            var roles = RoleRegex.Matches(jsonBody);
            var contents = ContentRegex.Matches(jsonBody);

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

            return (systemPrompt, userPrompt);
        }

        private static string UnescapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return s.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t")
                      .Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
