using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// §6 第一層輸出判斷：先跑一次（便宜的）API，判斷使用者想要的輸出是「簡報 / 報告 / 表格」之中的哪幾個，
    /// 就像 AiAutoModelResolverService 判斷該給哪個模型一樣。API 失敗 / 無金鑰時退回關鍵字判斷，永不中斷。
    /// </summary>
    public sealed class OutputIntentResolver
    {
        private readonly AiServiceRouter _router;

        public OutputIntentResolver(AiServiceRouter router)
        {
            _router = router;
        }

        public async Task<OutputIntent> ResolveAsync(string topText, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return OutputIntent.None;

            string raw;
            try
            {
                using var usagePurpose = UsageMeter.Purpose("輸出意圖判斷");
                raw = await _router
                    .GetOpenAiService(AiModels.OpenAi_Gpt56)
                    .GenerateAsync(BuildSystemPrompt(), BuildUserPrompt(topText), 200, ct);
            }
            catch
            {
                // API 失敗 → 退回關鍵字判斷，至少不漏掉明顯的輸出需求。
                return OutputIntent.FromKeywords(topText);
            }

            var apiIntent = TryParse(raw);
            if (apiIntent == null)
                return OutputIntent.FromKeywords(topText);

            // API 說「都不要檔案」，但關鍵字明顯指出要某種輸出時，以關鍵字為準，避免明明要卻沒產出。
            if (!apiIntent.WantsAny)
            {
                var kw = OutputIntent.FromKeywords(topText);
                if (kw.WantsAny)
                    return kw;
            }

            return apiIntent;
        }

        /// <summary>
        /// 單獨判斷一段話有沒有「要配圖」的意思（預覽裡的單頁修改要求用）。
        /// 一樣是語意判斷：使用者用哪種語言講都要認得。API 不通才退回關鍵字。
        /// </summary>
        public async Task<bool> WantsIllustrationsAsync(string? text, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            try
            {
                using var usagePurpose = UsageMeter.Purpose("配圖意圖判斷");
                string raw = await _router
                    .GetOpenAiService(AiModels.OpenAi_Gpt56)
                    .GenerateAsync(
                        "你判斷一句話有沒有『要求在文件／投影片裡加入圖片』的意思（配圖、插圖、照片、換一張圖…）。" +
                        "使用者可能用任何語言。明說不要圖時回 false。只輸出 JSON：{\"illustrations\": true/false}",
                        $"這句話：\n{text}\n\n請輸出 JSON。", 60, ct);

                var match = Regex.Match(raw ?? "", @"\{[\s\S]*\}");
                if (match.Success)
                {
                    using var doc = JsonDocument.Parse(match.Value);
                    return GetBool(doc.RootElement, "illustrations");
                }
            }
            catch { /* 落到關鍵字 */ }

            return AgentRuntime.PresentationWantsCoverImage(text);
        }

        private static string BuildSystemPrompt()
        {
            return
@"你是一個「輸出格式判斷器」。根據使用者輸入，判斷他『要你實際產出的東西』是下列哪幾種（可多選，也可能完全不要）：

- presentation：簡報 / 投影片 / ppt / slides
- report：書面報告 / 文件 / 文章 / 說明文（Word 類）
- table：表格 / 試算表 / excel / 數據比較表
- image：圖片 / 圖像 / 插圖 / 畫一張（要你生成或修改一張圖）
- video：影片 / 短片 / 動畫 / 一段 X 秒的片子（要你生成一段影片）

另外再判斷兩件事：
- illustrations：使用者有沒有要求『產出的文件裡要有圖』（配圖、插圖、照片、圖文並茂、每頁一張圖…）。
  這與 image 不同：image 是要一張獨立的圖檔，illustrations 是簡報／報告裡面要配圖。
  使用者明說『不要圖』『純文字』時一律 false。
- slides：使用者有沒有指定投影片張數。有就填數字，沒有填 0。『約五頁』『5 頁左右』也算指定（填 5）。
- language：使用者這次有沒有明確指定輸出語言（例如「用英文寫」「日本語で」「make it in English」）。
  有就填語言名稱（繁體中文 / English / 日本語…），沒有明確指定就填空字串。
  注意：使用者用什麼語言發問，不代表他指定了輸出語言——要他真的講了才算。

你只能輸出 JSON，不要任何多餘文字、不要 markdown：

{
  ""presentation"": true/false,
  ""report"": true/false,
  ""table"": true/false,
  ""image"": true/false,
  ""video"": true/false,
  ""illustrations"": true/false,
  ""slides"": 0,
  ""language"": """"
}

使用者可能用任何語言（繁中、簡中、英文、日文…），請依語意判斷，不要依賴特定字詞。

★最重要的原則：區分「使用者要你做出什麼」與「使用者只是在描述內容」。
只有使用者『下令產出』某種東西時才標 true。輸入內文裡提到的格式名稱，如果只是被當作『主題的一部分』『系統功能的介紹』『在描述別人做的東西』『要寫進簡報的條列內容』，一律不算，不要標 true。

判斷步驟：
1. 找出使用者的主要指令動詞（做一份 / 製作 / 生成 / 給我一個 / 畫 / 寫一份…）以及它的受詞是什麼。
2. 那個受詞才決定要產什麼。內文後面的描述、條列、舉例一律不影響判斷。

範例：
- 「做一份簡報，內容介紹這個能生成 PDF / Word / PPT / Excel 的系統」
  → 只有 presentation=true。Word/Excel/PPT 只是被介紹的功能，不是要產的檔。
- 「做一份報告，另外也幫我做一份簡報」
  → report=true 且 presentation=true（兩個都是明確指令）。
- 「整理成 excel 表格」 → table=true。
- 「給我一個大約15秒的影片，內容包含一隊人在沙漠中前進」
  → video=true。『給我一個…影片』是明確下令產出影片，後面是影片內容描述。
- 「畫一張賽博龐克城市的插圖」 → image=true。
- 「他昨天發了一個影片限動，超好笑」 → 全部 false。只是在描述別人發的影片，不是要你生影片。
- 「這張圖裡有幾隻貓？」 → 全部 false。是在問既有圖片的問題，不是要你產圖。
- 「幫我分析這份財報」（沒說要任何輸出）→ 全部 false。
- 「給我一份約五頁的簡報，內容需附圖」 → presentation=true、illustrations=true、slides=5。
- 「Make me a 7-slide deck about French fine dining, must include images」
  → presentation=true、illustrations=true、slides=7。
- 「做一份简报，配上图片」 → presentation=true、illustrations=true、slides=0。
- 「做一份簡報，不要放圖」 → presentation=true、illustrations=false。

其他原則：
- 使用者明確下令要簡報 / 報告 / 表格 / 圖片 / 影片 → 對應項 true。
- 只是問問題、聊天、描述、要一段純文字回答 → 全部 false。
- 不確定某項到底是『要產的東西』還是『內文描述』時，當作內文描述，標 false（寧可少產，不要多產）。
- 影片成本最高，特別謹慎：只有非常明確要你『生成一段影片』時才標 video=true。";
        }

        private static string BuildUserPrompt(string text)
        {
            return
$@"使用者輸入：
{text}

請輸出 JSON。";
        }

        /// <summary>解析模型回的 JSON（測試用 internal：這是語意判斷唯一能離線驗證的部分）。</summary>
        internal static OutputIntent? TryParse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            try
            {
                var match = Regex.Match(raw, @"\{[\s\S]*\}");
                string json = match.Success ? match.Value : raw;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                return new OutputIntent
                {
                    WantsPresentation = GetBool(root, "presentation"),
                    WantsReport = GetBool(root, "report"),
                    WantsTable = GetBool(root, "table"),
                    WantsImage = GetBool(root, "image"),
                    WantsVideo = GetBool(root, "video"),
                    WantsIllustrations = GetBool(root, "illustrations"),
                    RequestedSlides = GetInt(root, "slides"),
                    RequestedLanguage = GetString(root, "language"),
                    Source = "api:" + raw.Trim()
                };
            }
            catch
            {
                return null;
            }
        }

        private static string GetString(JsonElement root, string name)
            => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "").Trim()
                : "";

        private static int GetInt(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v))
                return 0;

            return v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt32(out int n) ? Math.Max(0, n) : 0,
                JsonValueKind.String => int.TryParse(v.GetString(), out int p) ? Math.Max(0, p) : 0,
                _ => 0
            };
        }

        private static bool GetBool(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v))
                return false;

            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
                _ => false
            };
        }
    }
}
