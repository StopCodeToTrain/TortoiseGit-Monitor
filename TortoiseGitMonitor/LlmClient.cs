/**
 * LLM 调用封装：支持 OpenAI / Anthropic，结构化 JSON 输出，失败降级。
 * 移植自 git-sync 的 llm_client.py，行为保持一致。
 *
 * 统一接口 ResolveConflict(filePath, base, ours, theirs, repoName) -> LlmMergeResult 或 null。
 * - Anthropic: Messages API + tool_use 强制结构化输出
 * - OpenAI:    Chat Completions + response_format=json_object
 * 用 HttpWebRequest 直接发 HTTP，不引入第三方依赖。失败重试 3 次，最终失败返回 null
 * （上层降级为询问用户）。
 *
 * JSON 使用 .NET Framework 自带的 JavaScriptSerializer（System.Web.Extensions）。
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace TortoiseGitMonitor
{
    /// <summary>LLM 返回的冲突合并结果。</summary>
    internal sealed class LlmMergeResult
    {
        public string MergedContent = "";
        public string Confidence = "low";   // high | medium | low
        public bool IsDestructive;
        public string Reason = "";
    }

    internal sealed class LlmClient
    {
        // 文件扩展名 -> 语言描述（用于 prompt 提示 LLM 文件类型与语法约定）
        private static readonly Dictionary<string, string> LangByExt = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { ".c", "C / LPC 服务器脚本(C-like 语法,注意 LPC 的 ({...}) 数组、([...]) 列表语法)" },
            { ".h", "C / LPC 头文件" },
            { ".lua", "Lua" },
            { ".py", "Python" },
            { ".cpp", "C++" }, { ".cc", "C++" }, { ".cxx", "C++" },
            { ".go", "Go" },
            { ".js", "JavaScript" },
            { ".ts", "TypeScript" },
            { ".json", "JSON" },
            { ".md", "Markdown" },
            { ".txt", "纯文本" },
        };

        // 单路内容超过此字符数时视为大文件（LLM 可能截断，仅警告不阻断）
        private const int LargeFileThreshold = 60000;

        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,   // 合并结果可能很大（完整文件内容）
        };

        private readonly string _provider;
        private readonly string _apiKey;
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly int _timeoutMs;
        private readonly Action<string> _log;

        static LlmClient()
        {
            // .NET 4.8 默认可能未启用 TLS 1.2，Anthropic/OpenAI 均要求
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        }

        /// <summary>配置不完整（缺 api_key / model）时抛出 ArgumentException。</summary>
        public LlmClient(LlmSettings cfg, Action<string> logFn)
        {
            _log = logFn ?? (msg => { });
            _provider = (cfg.Provider ?? "anthropic").Trim().ToLowerInvariant();
            _apiKey = cfg.EffectiveApiKey();
            _baseUrl = (cfg.BaseUrl ?? "").Trim().TrimEnd('/');
            _model = (cfg.Model ?? "").Trim();
            _timeoutMs = Math.Max(5, cfg.Timeout) * 1000;
            if (_apiKey.Length == 0)
                throw new ArgumentException("LLM api_key 未配置（请在设置中填写，或设环境变量 GIT_SYNC_LLM_KEY）");
            if (_model.Length == 0)
                throw new ArgumentException("LLM model 未配置");
            if (_provider != "anthropic" && _provider != "openai")
                throw new ArgumentException("不支持的 provider: " + _provider);
        }

        // ═══════════════════════════
        //  统一接口
        // ═══════════════════════════

        /// <summary>返回合并结果，LLM 不可用时返回 null（上层降级为询问用户）。</summary>
        public LlmMergeResult ResolveConflict(string filePath, string baseContent, string ours, string theirs, string repoName)
        {
            string sysPrompt = SystemPrompt();
            string userPrompt = UserPrompt(filePath, baseContent, ours, theirs, repoName);

            int total = (baseContent ?? "").Length + (ours ?? "").Length + (theirs ?? "").Length;
            if (total > LargeFileThreshold)
                _log($"警告: {filePath} 三路内容共 {total} 字符,可能超出 LLM 输出上限,合并结果或被截断");

            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    string raw = _provider == "anthropic"
                        ? CallAnthropic(sysPrompt, userPrompt)
                        : CallOpenAi(sysPrompt, userPrompt);
                    if (!string.IsNullOrEmpty(raw))
                    {
                        LlmMergeResult parsed = ParseResult(raw);
                        if (parsed != null)
                            return parsed;
                    }
                    _log($"LLM 返回空或无法解析(尝试 {attempt + 1}/3)");
                }
                catch (Exception ex)
                {
                    _log($"LLM 调用错误(尝试 {attempt + 1}/3): {ex.Message}");
                }
                Thread.Sleep(1000 << attempt); // 1s / 2s / 4s 退避
            }
            _log("LLM 调用 3 次均失败,降级为询问用户");
            return null;
        }

        // ═══════════════════════════
        //  prompt 构建
        // ═══════════════════════════

        private static string SystemPrompt()
        {
            return
                "你是一个代码合并专家。给定冲突文件的三路版本(base=公共祖先, ours=本地, theirs=远程)," +
                "产出合并结果,保留双方意图,不丢失任何一方的实质改动。\n\n" +
                "规则:\n" +
                "1. 合并 base->ours 和 base->theirs 的改动,尽量两全\n" +
                "2. 仅当一方改动与另一方直接矛盾且无法两全时,才丢弃一方(此时 is_destructive=true)\n" +
                "3. 删除行、表头/结构变更、文件删除等标记 is_destructive=true\n" +
                "4. confidence: high=高确信合并正确; medium=有把握但不完全确定; low=难以判断\n" +
                "5. merged_content 必须是合并后的【完整文件内容】,不是 diff\n" +
                "6. 严格保留原文件缩进、换行、编码风格,不要改动无关行";
        }

        private static string UserPrompt(string filePath, string baseContent, string ours, string theirs, string repoName)
        {
            string ext = Path.GetExtension(filePath ?? "") ?? "";
            string lang;
            if (!LangByExt.TryGetValue(ext, out lang))
                lang = "未知类型";
            return
                $"文件: {filePath}\n仓库: {repoName}\n语言: {lang}\n\n" +
                $"=== base(公共祖先) ===\n{baseContent}\n\n" +
                $"=== ours(本地 HEAD) ===\n{ours}\n\n" +
                $"=== theirs(远程 origin) ===\n{theirs}\n\n" +
                "请合并并返回 JSON。";
        }

        // ═══════════════════════════
        //  Anthropic
        // ═══════════════════════════

        private string CallAnthropic(string sysPrompt, string userPrompt)
        {
            string url = (_baseUrl.Length > 0 ? _baseUrl : "https://api.anthropic.com") + "/v1/messages";
            var toolSchema = new Dictionary<string, object>
            {
                { "type", "object" },
                { "properties", new Dictionary<string, object>
                    {
                        { "merged_content", new Dictionary<string, object> { { "type", "string" }, { "description", "合并后的完整文件内容(不是 diff)" } } },
                        { "confidence", new Dictionary<string, object> { { "type", "string" }, { "enum", new[] { "high", "medium", "low" } }, { "description", "合并确信度" } } },
                        { "is_destructive", new Dictionary<string, object> { { "type", "boolean" }, { "description", "是否丢弃了一方的实质改动" } } },
                        { "reason", new Dictionary<string, object> { { "type", "string" }, { "description", "合并理由简述" } } },
                    }
                },
                { "required", new[] { "merged_content", "confidence", "is_destructive", "reason" } },
            };
            var body = new Dictionary<string, object>
            {
                { "model", _model },
                { "max_tokens", 8192 },
                { "system", sysPrompt },
                { "messages", new object[] { new Dictionary<string, object> { { "role", "user" }, { "content", userPrompt } } } },
                { "tools", new object[] { new Dictionary<string, object>
                    {
                        { "name", "resolve_conflict" },
                        { "description", "返回冲突文件的合并结果" },
                        { "input_schema", toolSchema },
                    } }
                },
                { "tool_choice", new Dictionary<string, object> { { "type", "tool" }, { "name", "resolve_conflict" } } },
            };
            var headers = new Dictionary<string, string>
            {
                { "x-api-key", _apiKey },
                { "anthropic-version", "2023-06-01" },
            };

            var data = PostJson(url, body, headers) as Dictionary<string, object>;
            if (data == null)
                return null;
            object contentObj;
            if (!data.TryGetValue("content", out contentObj))
                return null;
            var blocks = contentObj as object[];
            if (blocks == null)
                return null;
            foreach (object b in blocks)
            {
                var block = b as Dictionary<string, object>;
                if (block == null)
                    continue;
                object type, name, input;
                if (block.TryGetValue("type", out type) && (type as string) == "tool_use"
                    && block.TryGetValue("name", out name) && (name as string) == "resolve_conflict"
                    && block.TryGetValue("input", out input))
                {
                    return Json.Serialize(input);
                }
            }
            return null;
        }

        // ═══════════════════════════
        //  OpenAI
        // ═══════════════════════════

        private string CallOpenAi(string sysPrompt, string userPrompt)
        {
            string url = (_baseUrl.Length > 0 ? _baseUrl : "https://api.openai.com") + "/v1/chat/completions";
            var body = new Dictionary<string, object>
            {
                { "model", _model },
                { "messages", new object[]
                    {
                        new Dictionary<string, object> { { "role", "system" }, { "content", sysPrompt } },
                        new Dictionary<string, object> { { "role", "user" }, { "content", userPrompt + "\n\n只返回 JSON 对象,不要 markdown 代码块。" } },
                    }
                },
                { "response_format", new Dictionary<string, object> { { "type", "json_object" } } },
                { "temperature", 0 },
            };
            var headers = new Dictionary<string, string>
            {
                { "Authorization", "Bearer " + _apiKey },
            };

            var data = PostJson(url, body, headers) as Dictionary<string, object>;
            if (data == null)
                return null;
            try
            {
                var choices = data["choices"] as object[];
                var choice = choices[0] as Dictionary<string, object>;
                var message = choice["message"] as Dictionary<string, object>;
                return message["content"] as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ═══════════════════════════
        //  HTTP / 解析
        // ═══════════════════════════

        /// <summary>POST JSON 并解析响应；非 200 抛出异常（消息含状态码与响应摘要）。</summary>
        private object PostJson(string url, Dictionary<string, object> body, Dictionary<string, string> headers)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = _timeoutMs;
            request.ReadWriteTimeout = _timeoutMs;
            foreach (var kv in headers)
                request.Headers[kv.Key] = kv.Value;

            byte[] payload = new UTF8Encoding(false).GetBytes(Json.Serialize(body));
            request.ContentLength = payload.Length;
            using (Stream s = request.GetRequestStream())
                s.Write(payload, 0, payload.Length);

            string text;
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                    text = reader.ReadToEnd();
            }
            catch (WebException ex) when (ex.Response is HttpWebResponse)
            {
                string errBody;
                using (var reader = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                    errBody = reader.ReadToEnd();
                int code = (int)((HttpWebResponse)ex.Response).StatusCode;
                throw new InvalidOperationException(
                    $"LLM API {code}: {(errBody.Length > 300 ? errBody.Substring(0, 300) : errBody)}");
            }

            return Json.DeserializeObject(text);
        }

        /// <summary>解析 LLM 返回的 JSON 为合并结果；缺字段/无法解析返回 null。</summary>
        private LlmMergeResult ParseResult(string raw)
        {
            Dictionary<string, object> obj = null;
            try
            {
                obj = Json.DeserializeObject(raw) as Dictionary<string, object>;
            }
            catch (Exception)
            {
                // 尝试截取最外层 {...}（模型偶尔会在 JSON 外包一层说明文字）
                int start = raw.IndexOf('{');
                int end = raw.LastIndexOf('}');
                if (start >= 0 && end > start)
                {
                    try
                    {
                        obj = Json.DeserializeObject(raw.Substring(start, end - start + 1)) as Dictionary<string, object>;
                    }
                    catch (Exception)
                    {
                        // 落到下面的 null 处理
                    }
                }
            }
            if (obj == null)
            {
                _log("LLM 返回无法解析为 JSON: " + (raw.Length > 200 ? raw.Substring(0, 200) : raw));
                return null;
            }

            object merged, confidence, destructive;
            if (!obj.TryGetValue("merged_content", out merged) || merged == null
                || !obj.TryGetValue("confidence", out confidence) || confidence == null
                || !obj.TryGetValue("is_destructive", out destructive))
            {
                _log("LLM 返回缺少必要字段(merged_content/confidence/is_destructive)");
                return null;
            }

            string conf = confidence.ToString().ToLowerInvariant();
            if (conf != "high" && conf != "medium" && conf != "low")
                conf = "low";

            object reason;
            return new LlmMergeResult
            {
                MergedContent = merged.ToString(),
                Confidence = conf,
                IsDestructive = destructive is bool ? (bool)destructive : destructive.ToString() == "true",
                Reason = obj.TryGetValue("reason", out reason) && reason != null ? reason.ToString() : "",
            };
        }
    }
}
