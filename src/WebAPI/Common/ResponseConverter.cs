namespace WebAPI.Common
{
    public static class ResponseConverter
    {
        private static int _idCounter = 0;
        private static readonly object _lock = new();

        public static string GenerateId()
        {
            lock (_lock)
            {
                _idCounter++;
                return $"chatcmpl-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}-{_idCounter:X4}";
            }
        }

        public static long UnixTimestamp() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        /// <summary>
        /// 构建 SSE chunk（OpenAI 兼容格式）
        /// </summary>
        /// <param name="id">Completion ID</param>
        /// <param name="model">模型名</param>
        /// <param name="contentDelta">内容增量（可为空）</param>
        /// <param name="finishReason">结束原因（stop/null）</param>
        /// <param name="role">角色（仅在首个 chunk 中设置）</param>
        public static string BuildSseChunk(string id, string model, string contentDelta, string? finishReason, string? role = null)
        {
            string deltaObj;
            if (finishReason != null)
            {
                // 结束 chunk：delta 为空对象
                deltaObj = "{}";
            }
            else if (role != null)
            {
                // 首个 chunk：包含 role
                var escapedContent = EscapeJson(contentDelta);
                deltaObj = string.IsNullOrEmpty(contentDelta)
                    ? $"{{\"role\":\"{role}\"}}"
                    : $"{{\"role\":\"{role}\",\"content\":\"{escapedContent}\"}}";
            }
            else
            {
                // 内容 chunk
                var escapedContent = EscapeJson(contentDelta);
                deltaObj = $"{{\"content\":\"{escapedContent}\"}}";
            }

            string choicesObj = finishReason != null
                ? $"{{\"index\":0,\"delta\":{deltaObj},\"finish_reason\":\"{finishReason}\"}}"
                : $"{{\"index\":0,\"delta\":{deltaObj},\"finish_reason\":null}}";

            return $"data: {{\"id\":\"{id}\",\"object\":\"chat.completion.chunk\",\"created\":{UnixTimestamp()},\"model\":\"{model}\",\"choices\":[{choicesObj}]}}\n\n";
        }

        public static string BuildFullResponse(string model, string content)
        {
            var id = GenerateId();
            var created = UnixTimestamp();

            return System.Text.Json.JsonSerializer.Serialize(new
            {
                id,
                @object = "chat.completion",
                created,
                model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new { role = "assistant", content },
                        finish_reason = "stop"
                    }
                },
                usage = new
                {
                    prompt_tokens = 0,
                    completion_tokens = 0,
                    total_tokens = 0
                }
            });
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20)
                            sb.Append($"\\u{(int)c:X4}");
                        else
                            sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
