using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// 協作模式：使用者在一個節點上指定「這次由哪幾個模型一起回答」。
    ///
    /// 2026-10-29 使用者的產品構想（模擬面試 Q16～Q19 的回答）：
    /// 讓不同 AI 在相同背景下評斷同一件事、看過彼此的回應再討論；分歧交給使用者決定，
    /// 而不是讓某個模型替使用者整合掉。面試官追問「最終輸出是否固定列出共識與分歧」——
    /// 這裡讓它固定列出，而且分歧照各模型的原話並列，不經任何一個模型改寫。
    ///
    /// 流程：
    ///   第一輪：每個模型各自回答同一份輸入（相同的搜尋結果、偏好與背景）。
    ///   第二輪：每個模型讀完其他模型的完整回答，說明認同什麼、不同意什麼與理由。
    ///   輸出：各模型回答 → 交叉檢視 → 共識與分歧（由程式依各模型自己列出的項目整理）。
    ///
    /// 某個模型失敗時照實標示「未回應」，不找別的模型頂替——頂替會讓兩份「不同模型」的回答
    /// 其實出自同一個模型，比較就失去意義。
    /// </summary>
    public static class ModelCollaboration
    {
        public const int MinModels = 2;
        public const int MaxModels = 4;

        public sealed record Answer(string ModelId, bool Success, string Text, string Error);

        public sealed record Disagreement(string Point, string Mine, string Theirs, string With, string Reason);

        public sealed record Review(
            string ModelId, bool Success, string Prose,
            IReadOnlyList<string> Agree, IReadOnlyList<Disagreement> Disagree, bool Structured, string Error);

        /// <summary>使用者選的協作模型：去空白、去重複、只留目前可用的，最多 MaxModels 個。</summary>
        public static IReadOnlyList<string> Normalize(IEnumerable<string>? models)
            => (models ?? Array.Empty<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => AiModelRegistry.ResolveAlias(m.Trim()) ?? m.Trim())
                .Where(m => AiModelRegistry.Find(m)?.IsAvailable == true)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxModels)
                .ToList();

        public static bool IsActive(IEnumerable<string>? models) => Normalize(models).Count >= MinModels;

        /// <summary>
        /// 跑完兩輪並組出最後的文字。execute＝（模型、提示詞、用途）→ 執行結果，由呼叫端決定怎麼呼叫；
        /// 每次呼叫都必須只用指定的那一個模型（呼叫端要設 ForceSingleModel）。
        /// </summary>
        public static async Task<AiFallbackExecutionResult> RunAsync(
            IReadOnlyList<string> models,
            string firstRoundInput,
            string originalRequest,
            Func<string, string, string, Task<AiFallbackExecutionResult>> execute,
            Action<string>? progress,
            CancellationToken ct)
        {
            var chosen = Normalize(models);
            if (chosen.Count < MinModels)
                throw new ArgumentException("協作模式至少要兩個可用的模型。", nameof(models));

            progress?.Invoke($"{Names(chosen)} 正在各自回答…");
            var round1 = await Task.WhenAll(chosen.Select(async m =>
            {
                ct.ThrowIfCancellationRequested();
                var r = await execute(m, firstRoundInput, $"協作第一輪：{Name(m)}");
                return new Answer(m, r.IsSuccess && !string.IsNullOrWhiteSpace(r.Text),
                    (r.Text ?? "").Trim(), r.IsSuccess ? "" : r.ErrorMessage ?? "");
            }));

            var answered = round1.Where(a => a.Success).ToList();
            var round2 = new List<Review>();

            if (answered.Count >= MinModels)
            {
                progress?.Invoke($"{Names(answered.Select(a => a.ModelId))} 正在看彼此的回答…");
                round2 = (await Task.WhenAll(answered.Select(async me =>
                {
                    ct.ThrowIfCancellationRequested();
                    var others = answered.Where(o => o.ModelId != me.ModelId)
                                         .Select(o => (Name(o.ModelId), o.Text)).ToList();
                    string prompt = BuildReviewPrompt(originalRequest, Name(me.ModelId), me.Text, others);
                    var r = await execute(me.ModelId, prompt, $"協作交叉檢視：{Name(me.ModelId)}");
                    return ParseReview(me.ModelId, r);
                }))).ToList();
            }

            progress?.Invoke(null!);

            return new AiFallbackExecutionResult
            {
                IsSuccess = answered.Count > 0,
                Text = Assemble(round1, round2),
                ActualModelId = answered.FirstOrDefault()?.ModelId ?? chosen[0],
                ErrorMessage = answered.Count > 0 ? "" : "協作的模型全部沒有回應：" +
                    string.Join("；", round1.Select(a => $"{Name(a.ModelId)}（{a.Error}）")),
                Summary = $"協作：{Names(chosen)}",
                Attempts = Array.Empty<AiFallbackAttempt>(),
            };
        }

        public static string BuildReviewPrompt(
            string originalRequest, string myName, string myAnswer, IReadOnlyList<(string Name, string Text)> others)
        {
            var sb = new StringBuilder();
            sb.Append("你正在和其他模型一起回答同一個問題。下面是使用者的原始要求、你在第一輪的回答，")
              .Append("以及其他模型的回答（完整、地位對等）。請檢視其他模型的回答：\n")
              .Append("1. 你認同哪些部分（具體指出）。\n")
              .Append("2. 你不同意哪些部分、理由是什麼；如果對方說得對，請直接承認並修正你的看法。\n")
              .Append("3. 看完之後你會怎麼修正你的回答（可以不改，但要說明理由）。\n")
              .Append("不要只是重複自己的答案，也不要為了附和而同意。\n")
              .Append("最後另起一行，輸出一個 ```json 區塊，格式如下（沒有就給空陣列）：\n")
              .Append("{\"agree\":[\"…\"],\"disagree\":[{\"point\":\"論點\",\"mine\":\"我的看法\",")
              .Append("\"theirs\":\"對方的看法\",\"with\":\"對方模型名稱\",\"reason\":\"理由\"}]}\n\n")
              .Append("【使用者原始要求】\n").Append(originalRequest?.Trim()).Append("\n\n")
              .Append("【你的第一輪回答（").Append(myName).Append("）】\n").Append(myAnswer?.Trim()).Append("\n\n")
              .Append("【其他模型的回答】\n");

            foreach (var (name, text) in others)
                sb.Append("\n【").Append(name).Append("】\n").Append(text?.Trim()).Append('\n');

            return sb.ToString();
        }

        private static readonly Regex JsonFence = new(@"```json\s*(?<json>\{[\s\S]*?\})\s*```", RegexOptions.IgnoreCase);

        public static Review ParseReview(string modelId, AiFallbackExecutionResult? r)
        {
            if (r == null || !r.IsSuccess || string.IsNullOrWhiteSpace(r.Text))
                return new Review(modelId, false, "", Array.Empty<string>(), Array.Empty<Disagreement>(), false,
                    r?.ErrorMessage ?? "沒有回應");

            string text = r.Text.Trim();
            var match = JsonFence.Matches(text).Cast<Match>().LastOrDefault();
            if (match == null)
                return new Review(modelId, true, text, Array.Empty<string>(), Array.Empty<Disagreement>(), false, "");

            string prose = text.Remove(match.Index, match.Length).Trim();
            try
            {
                using var doc = JsonDocument.Parse(match.Groups["json"].Value);
                var root = doc.RootElement;

                var agree = root.TryGetProperty("agree", out var a) && a.ValueKind == JsonValueKind.Array
                    ? a.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString())
                        .Where(x => !string.IsNullOrWhiteSpace(x)).ToList()
                    : new List<string>();

                var disagree = new List<Disagreement>();
                if (root.TryGetProperty("disagree", out var d) && d.ValueKind == JsonValueKind.Array)
                    foreach (var item in d.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object))
                        disagree.Add(new Disagreement(
                            Str(item, "point"), Str(item, "mine"), Str(item, "theirs"), Str(item, "with"), Str(item, "reason")));

                return new Review(modelId, true, prose, agree, disagree, true, "");
            }
            catch (JsonException)
            {
                return new Review(modelId, true, text, Array.Empty<string>(), Array.Empty<Disagreement>(), false, "");
            }
        }

        /// <summary>
        /// 組出最後的文字。「共識與分歧」完全由程式依各模型自己列出的項目排列——
        /// 沒有任何一個模型能在這一步刪掉別人的異議（模擬面試 Q18 的擔心）。
        /// </summary>
        public static string Assemble(IReadOnlyList<Answer> round1, IReadOnlyList<Review> round2)
        {
            var sb = new StringBuilder();

            sb.Append("## 各模型的回答\n");
            foreach (var a in round1)
            {
                sb.Append("\n### ").Append(Name(a.ModelId)).Append('\n');
                sb.Append(a.Success ? a.Text : $"（這次沒有回應：{(string.IsNullOrWhiteSpace(a.Error) ? "原因不明" : a.Error)}）")
                  .Append('\n');
            }

            if (round2.Count == 0)
            {
                sb.Append("\n> 只有一個模型完成回答，這次沒有進行交叉檢視。\n");
                return sb.ToString();
            }

            sb.Append("\n## 交叉檢視\n");
            foreach (var r in round2)
            {
                sb.Append("\n### ").Append(Name(r.ModelId)).Append(" 看完其他回答後\n");
                sb.Append(r.Success ? (r.Prose.Length > 0 ? r.Prose : "（沒有額外說明）") : $"（這一輪沒有回應：{r.Error}）")
                  .Append('\n');
            }

            sb.Append("\n## 共識與分歧\n")
              .Append("這一段由系統依各模型自己列出的項目排列，沒有經過任何模型改寫或取捨。\n");

            var agreeLines = round2.Where(r => r.Structured)
                .SelectMany(r => r.Agree.Select(x => $"- {Name(r.ModelId)}：{x}"))
                .ToList();
            sb.Append("\n**各模型認同的部分**\n");
            sb.Append(agreeLines.Count > 0 ? string.Join("\n", agreeLines) : "- （沒有模型列出）").Append('\n');

            var disagreements = round2.Where(r => r.Structured)
                .SelectMany(r => r.Disagree.Select(d => (r.ModelId, d)))
                .ToList();
            // 2026-11-02 第三方審查（GPT）：theirs 是 A 寫的「對方的看法」，原本直接掛在 B 的名字後面，
            // 讀起來像 B 的原話。學生正在練習判斷「誰主張什麼」，這種呈現會讓他判斷錯人。
            // 現在只有在 B 第一輪的回答裡真的找得到那句話時才標成原句，其餘一律寫成 A 的轉述。
            sb.Append("\n**分歧**（各模型自己列出；標「理解」的是轉述，不是對方原話）\n");
            if (disagreements.Count == 0)
            {
                sb.Append("- （沒有模型列出分歧）\n");
            }
            else
            {
                int i = 1;
                foreach (var (modelId, d) in disagreements)
                {
                    sb.Append(i++).Append(". ").Append(string.IsNullOrWhiteSpace(d.Point) ? "（未標論點）" : d.Point).Append('\n');
                    sb.Append("   - ").Append(Name(modelId)).Append("：").Append(d.Mine).Append('\n');
                    sb.Append(DescribeOtherSide(modelId, d, round1)).Append('\n');
                    if (!string.IsNullOrWhiteSpace(d.Reason))
                        sb.Append("   - 理由（").Append(Name(modelId)).Append("）：").Append(d.Reason).Append('\n');
                }
            }

            foreach (var r in round2.Where(r => r.Success && !r.Structured))
                sb.Append("\n> ").Append(Name(r.ModelId)).Append(" 沒有列出結構化的共識與分歧，請看上方交叉檢視的全文。\n");

            sb.Append("\n> 分歧怎麼取捨由你決定。若要交給模型整合，請另接一個下游節點並在那裡選定整合的模型——")
              .Append("各模型的完整回答會以對等的方式傳給它。\n");

            return sb.ToString();
        }

        /// <summary>
        /// 分歧裡「對方的看法」那一行。對方第一輪的回答裡找得到這句話（忽略空白）才標原句；
        /// 找不到就寫成「A 對 B 立場的理解」，並請讀者對照 B 的回答。
        /// </summary>
        internal static string DescribeOtherSide(string reviewerId, Disagreement d, IReadOnlyList<Answer> round1)
        {
            var other = ResolveOther(reviewerId, d.With, round1);
            string otherName = other != null ? Name(other.ModelId)
                             : string.IsNullOrWhiteSpace(d.With) ? "對方" : d.With.Trim();
            string theirs = (d.Theirs ?? "").Trim();

            if (theirs.Length == 0)
                return $"   - {otherName}：（{Name(reviewerId)} 沒有寫出對方的看法）";

            bool quoted = other != null && other.Success
                          && theirs.Count(c => !char.IsWhiteSpace(c)) >= 4
                          && StepInputRecord.ContainsVerbatim(other.Text, theirs);

            return quoted
                ? $"   - {otherName}（原句）：「{theirs}」"
                : $"   - {Name(reviewerId)} 對 {otherName} 立場的理解：{theirs}（請對照上方 {otherName} 的回答）";
        }

        /// <summary>with 欄位是模型自己填的自由文字：比對名稱或 id；沒填而且只有一個對手時就是那一個。</summary>
        private static Answer? ResolveOther(string reviewerId, string? with, IReadOnlyList<Answer> round1)
        {
            var others = round1.Where(a => !string.Equals(a.ModelId, reviewerId, StringComparison.OrdinalIgnoreCase)).ToList();
            string w = (with ?? "").Trim();

            if (w.Length > 0)
            {
                var hit = others.FirstOrDefault(a =>
                    w.Contains(Name(a.ModelId), StringComparison.OrdinalIgnoreCase) ||
                    w.Contains(a.ModelId, StringComparison.OrdinalIgnoreCase) ||
                    Name(a.ModelId).Contains(w, StringComparison.OrdinalIgnoreCase));
                if (hit != null) return hit;
            }

            return others.Count == 1 ? others[0] : null;
        }

        private static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";

        private static string Name(string modelId) => AiModelRegistry.Find(modelId)?.DisplayName ?? modelId;

        private static string Names(IEnumerable<string> ids) => string.Join("、", ids.Select(Name));
    }
}
