using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    // AgentRuntime 的一部分：最終整合：把各能力的資料組成提示、整合成最後的答案。
    // （2026-11-03 依職責拆檔：方法原樣搬出，沒有改任何邏輯；主流程在 AgentRuntime.cs。）
    public sealed partial class AgentRuntime
    {
        private async Task<AiFallbackExecutionResult> RunFinalSynthesisAsync(
            INodeContext node,
            AgentDefinition rootAgent,
            string originalInput,
            AgentWorkspace workspace,
            NodeExecutionDecision rootDecision,
            string preferenceBlock,
            CancellationToken ct,
            string freshDataNotice = "")
        {
            using var usagePurpose = UsageMeter.Purpose("最終整合");
            var synthesizer = AgentRegistry.Get("general-agent");

            var synthesisDecision = new NodeExecutionDecision
            {
                RequestedAgentId = synthesizer.Id,
                ActualAgentId = synthesizer.Id,
                RequestedModelId = AiModelHelper.NormalizeNodeModel(synthesizer.DefaultModelId),
                ModelId = AiModelHelper.NormalizeNodeModel(synthesizer.DefaultModelId),
                ActualModelId = "",
                TaskMode = rootDecision.TaskMode,
                ResolverLabel = "Final Synthesizer",
                ResolverReason = "Parallel agents completed; general-agent synthesizes workspace outputs.",
                StatusLabel = "Auto",
                ForceSingleModel = true
            };

            string workspaceBlock = workspace.BuildPromptBlock();

            System.Diagnostics.Debug.WriteLine($"[Workspace] Count={workspace.GetAll().Count}");
            System.Diagnostics.Debug.WriteLine(
                $"[Workspace Preview]\n{workspaceBlock.Substring(0, Math.Min(1000, workspaceBlock.Length))}");

            bool hasVerifiedFacts = workspace.GetByType("verified_facts").Count > 0;
            bool isFinanceTask = FinanceTaskDetector.IsFinanceFocused(originalInput);
            string synthesisInstructions = hasVerifiedFacts && isFinanceTask
                ? BuildFinanceFinalSynthesisInstructions()
                : BuildGeneralFinalSynthesisInstructions();

            // 查證／研究類的整合也要附來源——不然平行代理這條路會變成唯一的漏洞。
            if (hasVerifiedFacts || workspace.GetByType("search_summary").Count > 0)
                synthesisInstructions += "\n\n" + BuildSourceEvidenceBlock();

            string prefHeader = string.IsNullOrWhiteSpace(preferenceBlock)
                ? ""
                : preferenceBlock.Trim() +
                  "\n（以上偏好為最高優先，蓋過下方任何「使用繁體中文」等預設規則；若偏好指定輸出語言或格式，最終答案必須改用該語言與格式。）\n\n";

            string synthesisInput =
        $@"{prefHeader}你是 final synthesizer。你的任務是把 shared workspace 整理成使用者真正需要看的最終答案。

【使用者原始任務】
{originalInput}

【Shared Workspace】
{workspaceBlock}

{synthesisInstructions}
{(string.IsNullOrWhiteSpace(freshDataNotice) ? "" : "\n" + freshDataNotice + "\n")}
請現在輸出最終答案：";

            return await _executeWithFallbackAsync(
                node,
                synthesisInput,
                synthesisDecision,
                null,
                false,
                ct);
        }

        private static string TrimForPrompt(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            string trimmed = text.Trim();
            return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max) + "...";
        }

        private static string BuildCompactOriginalTaskSummary(string? originalFinalInput)
        {
            if (string.IsNullOrWhiteSpace(originalFinalInput))
                return "原始任務內容未提供。";

            var lines = originalFinalInput
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(line =>
                    !line.Contains("Content:", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("```", StringComparison.Ordinal) &&
                    !line.Contains("import ", StringComparison.Ordinal) &&
                    !line.Contains("public class ", StringComparison.Ordinal) &&
                    !line.Contains("private ", StringComparison.Ordinal) &&
                    !line.Contains("protected ", StringComparison.Ordinal) &&
                    !line.Contains("void ", StringComparison.Ordinal))
                .Take(80);

            var compact = string.Join(Environment.NewLine, lines).Trim();
            if (string.IsNullOrWhiteSpace(compact))
                return "原始任務包含大型附件 snapshot。為控制 token，repair 階段不重貼完整原始碼；請只根據上一版 diff 與 validation 失敗原因修正 patch。";

            return TrimForPrompt(compact, 3000) +
                   "\n\n注意：完整附件 snapshot 保留在 workspace 供 dry-run validation 使用；repair prompt 不重貼完整原始碼，以避免大型檔案重複消耗 token。";
        }

        private static string BuildFinanceFinalSynthesisInstructions()
        {
            return
@"【最高優先規則】
1. 如果 Shared Workspace 中有【Verified Facts】，必須優先使用它作為價格、日期、財報、EPS、營收、毛利率、指引等事實來源。
2. 如果沒有【Verified Facts】，但有【Search Context】或 research-agent 的 search_summary，才可使用 search_summary 作為事實來源。
3. 只有在 Shared Workspace 完全沒有 Verified Facts、Search Context、search_summary 時，才可以回答資料不足。
4. Fact ownership 是硬規則：
   - UsageRole=numeric_fact_source 才能作為價格、日期、財報、EPS、營收、毛利率、指引等數字來源。
   - UsageRole=background_context 只能作為背景，不可覆蓋 numeric_fact_source。
   - UsageRole=analysis_only、Analysis Context、reasoning_analysis、parallel_agent_output、delegate_output 只能用於推論與整理，不可新增或覆蓋任何事實數字。
   - OwnerAgent=research-agent 且 OwnerCapability=search-capability 的 numeric facts 是事實擁有者。
5. Authority ranking 是採用順序：official > market_quote > trusted_news > search_context > model_generated。若同一 numeric fact 有衝突，採用 AuthorityRank 較高者；低權威來源只能作為補充或列入資料衝突。
6. Analysis Context、reasoning_analysis、parallel_agent_output、delegate_output 只能用於推論與整理，不可新增或覆蓋任何事實數字。
7. 若同一項資料有多個來源數字：
   - regular close、after-hours、pre-market 屬於不同交易時段，不可互相視為資料衝突；請分開標示。
   - 若 verified_facts 來自 official earnings / official facts repair，且 Search Context 或舊搜尋摘要有不同數字，應以 verified_facts 為準，不要把被 repair 取代的舊數字列為資料衝突。
   - 若數字接近，請合併成簡短區間或代表值。
   - 若數字明顯衝突，請列在「資料衝突」中。
   - 不要把所有來源逐條原封不動列出。
8. 若某個 ticker 只有部分欄位缺失，只能標示該欄位缺失；不可因此整體回答「財報核心數字不足」或「資料不足」。
9. 不要使用「資料批次」「較強的那組資料」「若採用某組資料」這類內部研究口徑；請直接使用最高權威 verified_facts。
10. 不可輸出內部標記，例如 Agent Workspace、Task Plan、Search Summary、Verified Facts、Search Context、Analysis Context、parallel_agent_output、delegate_output。
11. 不可輸出 citation marker，例如 [1][2][3]。
12. 不要寫成研究紀錄，不要把 workspace 全部倒出來。請輸出給一般使用者看的精簡結論。
13. 預設使用繁體中文；若提供了【使用者偏好】並指定輸出語言，必須改用該偏好指定的語言。

【輸出格式】
請嚴格使用以下格式。只針對使用者詢問的標的輸出，不得自行加入未被詢問的股票或比較項目：

結論
- 用 2～4 點直接回答使用者任務。
- 針對每個被詢問的標的給出短期判斷。
- 若使用者要求比較多個標的，直接說哪個較穩、哪個彈性較大、哪個風險較高。
- 若使用者只詢問單一標的，只回答該標的，不要加入其他標的的「資料不足」備注。

關鍵資料
- 只列被詢問標的最重要的股價、財報或市場資料，每個標的最多 5 點。
- 如果資料來源衝突，不要在這裡展開；只簡短標示「報價來源有衝突，詳見資料衝突」。

短期走勢判斷
- 只針對被詢問的標的，每個標的最多 1 段。
- 必須清楚區分「已知資料」與「合理推論」。
- 不要保證漲跌。

資料衝突 / 缺失
- 只列真正影響判斷的衝突或缺失。
- 若沒有重大衝突，就寫「目前沒有影響主要判斷的重大缺口」。
- 不要把所有來源重複列一遍。

總結一句話
- 用一句話收束。

【風格限制】
- 不要長篇列點。
- 不要重複同一個觀點。
- 不要把每個來源都展開。
- 不要用「已知資料 / 合理推論 / 風險」這三段舊格式。
- 最終答案長度控制在一般回答可讀範圍內。";
        }

        private static string BuildGeneralFinalSynthesisInstructions()
        {
            return
@"【最高優先規則】
1. 依照使用者原始任務回答，不要套用金融、股票、短期走勢、資料衝突等格式，除非使用者明確要求。
2. Shared Workspace 只作為背景與附件內容來源；不可輸出內部標記，例如 Agent Workspace、Task Plan、Search Summary、Code File Snapshot、parallel_agent_output、delegate_output。
3. 若使用者要求一句話，就只輸出一句話。
4. 若任務是程式摘要或檔案說明，直接說明檔案用途、主要功能與關鍵結構；不要加入不適用的金融段落。
5. 不可輸出 citation marker，例如 [1][2][3]。
6. 預設使用繁體中文；若提供了【使用者偏好】並指定輸出語言，必須改用該偏好指定的語言。

【風格限制】
- 簡潔、直接，避免把 workspace 逐條倒出來。
- 尊重使用者指定的長度、語氣與格式。";
        }
        private static string BuildCapabilityDataBlock(
            IReadOnlyDictionary<string, object> capabilityData)
        {
            if (capabilityData == null || capabilityData.Count == 0)
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("【Capability Data】");
            sb.AppendLine("以下內容來自系統工具/能力層的真實輸出，屬高優先參考。");
            sb.AppendLine("回答時請優先使用這些資料，不要忽略，也不要憑空改寫來源。");

            bool hasVerifiedFacts = capabilityData.ContainsKey("verified_facts");

            string codeUserGoal =
                (capabilityData.TryGetValue("code_analysis", out var caVal) && caVal is CodeAnalysisPayload caPayload)
                    ? caPayload.UserGoal ?? ""
                    : "";

            foreach (var kv in capabilityData)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                    continue;

                if (string.Equals(kv.Key, "search_summary", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasVerifiedFacts)
                    {
                        sb.AppendLine();
                        sb.AppendLine("【Search Summary Omitted】");
                        sb.AppendLine("Structured verified_facts are present. Search summary text is intentionally omitted so older or lower-authority numeric snippets cannot override verified facts.");
                        continue;
                    }

                    AppendSearchSummary(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "file_summary", StringComparison.OrdinalIgnoreCase))
                {
                    AppendFileSummary(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "code_analysis", StringComparison.OrdinalIgnoreCase))
                {
                    AppendCodeAnalysis(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "code_file_snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    AppendCodeFileSnapshot(sb, kv.Value, codeUserGoal);
                    continue;
                }

                if (string.Equals(kv.Key, "code_diff_draft", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, "code_diff", StringComparison.OrdinalIgnoreCase))
                {
                    AppendCodeDiffArtifact(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "task_plan", StringComparison.OrdinalIgnoreCase))
                {
                    AppendTaskPlan(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "reasoning_analysis", StringComparison.OrdinalIgnoreCase))
                {
                    AppendReasoningAnalysis(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "verified_facts", StringComparison.OrdinalIgnoreCase))
                {
                    AppendVerifiedFacts(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "search_results", StringComparison.OrdinalIgnoreCase))
                {
                    AppendSearchResults(sb, kv.Value);
                    continue;
                }
                sb.AppendLine();
                sb.AppendLine($"【{kv.Key}】");
                sb.AppendLine(kv.Value.ToString() ?? "");
            }

            return sb.ToString().Trim();
        }

        private static void AppendVerifiedFacts(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not VerifiedFactPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Verified Facts】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Verified Facts】");
            sb.AppendLine("以下資料是唯一可用來回答數字、價格、日期、財報、即時資訊的事實來源。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);

            if (payload.Facts == null || payload.Facts.Count == 0)
                return;

            foreach (var fact in payload.Facts)
            {
                if (fact == null)
                    continue;

                sb.AppendLine($"- {fact.Subject} / {fact.FactType}: {fact.Value} {fact.Unit}".Trim());

                if (!string.IsNullOrWhiteSpace(fact.AsOf))
                    sb.AppendLine($"  AsOf: {fact.AsOf}");

                if (!string.IsNullOrWhiteSpace(fact.SourceTitle))
                    sb.AppendLine($"  Source: {fact.SourceTitle}");

                if (!string.IsNullOrWhiteSpace(fact.SourceUrl))
                    sb.AppendLine($"  Url: {fact.SourceUrl}");

                if (!string.IsNullOrWhiteSpace(fact.Confidence))
                    sb.AppendLine($"  Confidence: {fact.Confidence}");

                if (!string.IsNullOrWhiteSpace(fact.OwnerAgentId))
                    sb.AppendLine($"  OwnerAgent: {fact.OwnerAgentId}");

                if (!string.IsNullOrWhiteSpace(fact.OwnerCapabilityId))
                    sb.AppendLine($"  OwnerCapability: {fact.OwnerCapabilityId}");

                if (!string.IsNullOrWhiteSpace(fact.AuthorityLevel))
                    sb.AppendLine($"  Authority: {fact.AuthorityLevel} (rank {FactOwnership.AuthorityRank(fact.AuthorityLevel)})");

                if (!string.IsNullOrWhiteSpace(fact.UsageRole))
                    sb.AppendLine($"  UsageRole: {fact.UsageRole}");
            }
        }

        /// <summary>
        /// 查證 / 研究 / 分析任務的來源規則。
        ///
        /// 這類回答的價值全在「這句話憑什麼」。沒有來源的數字跟猜的沒有差別，
        /// 但使用者看不出差別——所以寧可要求模型自己把沒來源的話標出來，
        /// 也不要讓它混在有來源的句子中間一起被當成事實。
        /// </summary>
        internal static string BuildSourceEvidenceBlock()
        {
            return
@"【來源規則 — 必須遵守】
1. 每一個關於外部世界的事實（數字、日期、事件、引述、他人的主張）都要能追到來源。
2. 在最終答案最後加一段「資料來源」，逐條列出：來源名稱 — 網址 — 取得/發布日期。
   網址用完整的 https:// 開頭，不要縮寫、不要寫「官網」兩個字了事。
3. 工具層（【Search Results】/【Search Summary】/ verified_facts）給了來源就照抄那個來源，不要自己改寫或換一個看起來更權威的。
4. 如果某個說法你找不到來源，不要刪掉也不要假裝有來源：把那一句標成
   「（無來源，為模型判斷）」，並在「資料來源」那段寫一行「以下內容無外部來源：…」。
5. 整份都沒有任何外部來源時，必須在開頭第一行寫明
   「以下內容沒有查到外部來源，是模型依既有知識的判斷，請自行查證。」
6. 不要寫 [1][2] 這種編號引用——系統會把它們清掉，寫了等於沒寫。來源請直接寫名稱與網址。";
        }

        /// <summary>
        /// 這次要不要強制附來源：使用者是在查證 / 研究 / 分析，而不是在聊天或改寫。
        /// 跑過搜尋能力＝一定要；任務模式是 Research 也一定要。
        /// </summary>
        private static bool RequiresSourceEvidence(
            System.Collections.Generic.IReadOnlyDictionary<string, object> capabilityData,
            AgentWorkspace workspace,
            NodeTaskMode taskMode)
        {
            if (taskMode == NodeTaskMode.Research)
                return true;

            return capabilityData.ContainsKey("search_summary")
                || capabilityData.ContainsKey("verified_facts")
                || workspace.GetByType("search_summary").Count > 0
                || workspace.GetByType("verified_facts").Count > 0;
        }

        private static string BuildFinalOutputFormatBlock()
        {
            return
@"【Final Output Format - Required】
請使用以下五段標題，且不可省略標題：

結論
- 2～4 點直接回答。

關鍵資料
- 對每個 ticker 標清楚資料類型：收盤價、盤後價、盤前價、即時價、財報、指引。
- 若某種報價未取得，請寫「盤前價：未取得」或「即時價：未取得」，不要把收盤價當即時價。

短期走勢判斷
- 明確區分 verified facts 與合理推論。

資料衝突 / 缺失
- 只列真正衝突或缺失。不同交易時段的收盤價、盤後價、盤前價不是衝突。

總結一句話
- 用一句話收束。

資料來源
- 逐條列出：來源名稱 — 網址 — 日期。每一個數字都要對得到其中一條。
- 沒有來源的數字不要寫進「關鍵資料」，改寫在「短期走勢判斷」並標明是推論。";
        }

        private static string BuildCodeDiffOutputInstructionBlock()
        {
            return
@"【Code Diff Output - Required When Fixing Code】
目前 workspace 中已有 code_diff_draft。若使用者要求修正、修改、重構或產生 patch，且你能根據已提供的實際原始碼內容安全產生修正，最終回答應包含一個 fenced unified diff：

```diff
diff --git a/path/to/file b/path/to/file
--- a/path/to/file
+++ b/path/to/file
@@
- old line
+ new line
```

規則：
1. diff 必須根據附件 snapshot 中的實際內容產生，不可捏造不存在的檔案。
2. 若資訊不足以安全產生 diff，請明確說明缺少什麼，不要輸出假 diff。
3. 不要宣稱已修改或已套用檔案；目前只能提出 patch。
4. 若 Code File Snapshot 顯示 PromptTruncated=True，代表你只看到低成本摘錄，不可宣稱已完整檢查整份檔案。
5. 對「把所有 bug 都修好」這類大範圍任務，若只能看到部分內容，只能提出可被摘錄內容支持的有限修正；若無法確認，請要求縮小範圍或先列候選區域。
6. 除了 diff，可以用短句說明修了什麼，但不要輸出 workspace 內部標記。";
        }

        // Code Agent v1.5：bug 盤點模式——先列清單、不出 patch，讓使用者挑選要修哪幾項。
        private static string BuildBugListingInstructionBlock()
        {
            return
@"【Bug 清單模式 - 先盤點，不要直接修】
使用者想先看到問題清單，再決定修哪一個。本次請「只列出 bug／問題清單」，不要輸出任何 diff 或 patch。

輸出格式（務必逐項編號，方便使用者挑選）：
Bug 1（嚴重度：高/中/低）｜位置：檔名:行號 或 類別.方法
- 問題：一句話描述問題與影響
- 建議：一句話說明修法方向（先不要給完整程式碼）

Bug 2（…）
…

規則：
1. 依嚴重度由高到低排序，最多列 12 項。
2. 只列出有實際根據（snapshot 內容支持）的問題，不要臆測不存在的程式碼。
3. 若內容被分段／截斷（PromptTruncated=True 或有 ChunkMap），請在清單最後說明還有哪些區段尚未檢查。
4. 結尾固定加一句：『想修正哪幾項？回覆「修正 1」或「修正 1,3」我就針對那幾項產生 patch。』";
        }

        // Code Agent v1.5：大型程式任務的成本/風險提醒（過程資訊，提醒模型保守處理）。
        private static string BuildCodeRiskNoteBlock(CodeTaskAssessmentPayload assessment)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【大型程式任務提醒】");
            sb.AppendLine(
                $"任務規模：{assessment.SizeTier}；風險：{assessment.RiskLevel}；" +
                $"檔案 {assessment.FileCount} 個、約 {assessment.TotalLines} 行。");

            if (assessment.Warnings != null)
            {
                foreach (var w in assessment.Warnings)
                {
                    if (!string.IsNullOrWhiteSpace(w))
                        sb.AppendLine("- " + w);
                }
            }

            sb.AppendLine("處理原則：優先給出有把握、範圍明確的修正；不要宣稱已完整檢查整個專案。");
            return sb.ToString().TrimEnd();
        }

        private static string TryGetCodeRequestType(
            IReadOnlyDictionary<string, object> capabilityData,
            AgentWorkspace workspace)
        {
            if (capabilityData != null &&
                capabilityData.TryGetValue("code_analysis", out var v) &&
                v is CodeAnalysisPayload p)
            {
                return p.RequestType ?? "";
            }

            var ws = workspace?
                .GetByType("code_analysis")
                .Select(x => x.Payload as CodeAnalysisPayload)
                .FirstOrDefault(x => x != null);

            return ws?.RequestType ?? "";
        }

        private static CodeFileSnapshotPayload? TryGetCodeSnapshot(
            IReadOnlyDictionary<string, object> capabilityData,
            AgentWorkspace workspace)
        {
            if (capabilityData != null &&
                capabilityData.TryGetValue("code_file_snapshot", out var v) &&
                v is CodeFileSnapshotPayload p)
            {
                return p;
            }

            return workspace?
                .GetByType("code_file_snapshot")
                .Select(x => x.Payload as CodeFileSnapshotPayload)
                .FirstOrDefault(x => x != null);
        }

        private static void AppendReasoningAnalysis(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not ReasoningPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Reasoning Analysis】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Reasoning Analysis】");
            sb.AppendLine("以下為系統推論策略。回答時請遵守，但不可輸出此內部區塊名稱。");

            if (!string.IsNullOrWhiteSpace(payload.ReasoningType))
                sb.AppendLine($"Type: {payload.ReasoningType}");

            if (!string.IsNullOrWhiteSpace(payload.Basis))
                sb.AppendLine($"Basis: {payload.Basis}");

            if (payload.Inferences != null && payload.Inferences.Count > 0)
            {
                sb.AppendLine("Inference Rules:");
                foreach (var item in payload.Inferences)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                        sb.AppendLine($"- {item}");
                }
            }

            if (payload.Uncertainties != null && payload.Uncertainties.Count > 0)
            {
                sb.AppendLine("Uncertainties:");
                foreach (var item in payload.Uncertainties)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                        sb.AppendLine($"- {item}");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.OutputGuidance))
                sb.AppendLine($"Output Guidance: {payload.OutputGuidance}");
        }

        private static void AppendTaskPlan(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not TaskDecompositionPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Task Plan】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Task Plan】");
            sb.AppendLine("以下是系統對複合任務的拆解。回答時請依照此順序整合工具資料，但不可把 Task Plan 標題或內部步驟原樣輸出。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);

            if (payload.Steps == null || payload.Steps.Count == 0)
                return;

            foreach (var step in payload.Steps.OrderBy(x => x.Order))
            {
                sb.AppendLine($"{step.Order}. {step.StepType}");

                if (!string.IsNullOrWhiteSpace(step.Goal))
                    sb.AppendLine($"   Goal: {step.Goal}");

                if (!string.IsNullOrWhiteSpace(step.RequiredInput))
                    sb.AppendLine($"   Required Input: {step.RequiredInput}");

                if (!string.IsNullOrWhiteSpace(step.OutputExpectation))
                    sb.AppendLine($"   Output: {step.OutputExpectation}");
            }
        }

        private static void AppendCodeAnalysis(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not CodeAnalysisPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Code Analysis】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Code Analysis】");
            sb.AppendLine("以下內容來自 code-capability 的結構化分析，回答程式問題時應優先依據此分析。");

            if (!string.IsNullOrWhiteSpace(payload.RequestType))
                sb.AppendLine($"Request Type: {payload.RequestType}");

            if (!string.IsNullOrWhiteSpace(payload.Language))
                sb.AppendLine($"Language: {payload.Language}");

            if (!string.IsNullOrWhiteSpace(payload.UserGoal))
                sb.AppendLine($"User Goal: {payload.UserGoal}");

            if (payload.DetectedSignals != null && payload.DetectedSignals.Count > 0)
            {
                sb.AppendLine("Detected Signals:");
                foreach (var signal in payload.DetectedSignals)
                {
                    if (!string.IsNullOrWhiteSpace(signal))
                        sb.AppendLine($"- {signal}");
                }
            }

            if (payload.RequiredActions != null && payload.RequiredActions.Count > 0)
            {
                sb.AppendLine("Required Actions:");
                foreach (var action in payload.RequiredActions)
                {
                    if (!string.IsNullOrWhiteSpace(action))
                        sb.AppendLine($"- {action}");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.Guidance))
                sb.AppendLine($"Guidance: {payload.Guidance}");
        }

        private static void AppendCodeFileSnapshot(StringBuilder sb, object value, string userGoal)
        {
            if (sb == null || value == null)
                return;

            if (value is not CodeFileSnapshotPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Code File Snapshot】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Code File Snapshot】");
            sb.AppendLine("以下是使用者附件中的文字/程式檔案快照。回答程式修改或 diff 任務時，只能根據此處內容與使用者要求推論。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);

            if (payload.Files == null || payload.Files.Count == 0)
                return;

            int remainingPromptChars = MaxCodeSnapshotPromptCharsTotal;
            foreach (var file in payload.Files.Take(4))
            {
                if (file == null)
                    continue;

                string content = file.Content ?? "";
                int promptLimit = Math.Min(MaxCodeSnapshotPromptCharsPerFile, remainingPromptChars);

                // Code Agent v1.5：針對性 / 分段擷取（檔頭 + 結構骨架 + 目標關鍵字上下文）。
                var focused = CodeContextExtractor.Build(content, userGoal, promptLimit);
                string promptContent = focused.Text;
                bool promptTruncated = focused.Chunked;
                string sourceOutline = BuildCodeSourceOutline(content, MaxCodeSourceOutlineCharsPerFile);
                remainingPromptChars = Math.Max(0, remainingPromptChars - promptContent.Length);

                sb.AppendLine();
                sb.AppendLine($"File: {file.FileName}");
                sb.AppendLine($"Path: {file.RelativePath}");
                sb.AppendLine($"Language: {file.Language}");
                sb.AppendLine($"Chars: {file.CharacterCount}; Lines: {file.LineCount}; Truncated: {file.IsTruncated}");
                sb.AppendLine($"PromptChars: {promptContent.Length}; PromptTruncated: {promptTruncated}");
                if (focused.Chunked)
                {
                    sb.AppendLine($"ChunkMap: included {focused.IncludedLines}/{focused.TotalLines} lines; segments=[{string.Join(", ", focused.SegmentRanges)}]");
                    sb.AppendLine("分段分析：以上摘錄是針對目標關鍵字與程式結構挑選的重點區段，非整份檔案。若需要某個未列出的區段，請指名行號範圍或函式名稱。");
                    sb.AppendLine("Do not claim a comprehensive whole-file fix when content is chunked. Prefer a narrow, evidence-backed patch or explain that targeted follow-up is required.");
                }

                if (!string.IsNullOrWhiteSpace(sourceOutline))
                {
                    sb.AppendLine("SourceOutline:");
                    sb.AppendLine("```");
                    sb.AppendLine(sourceOutline);
                    sb.AppendLine("```");
                }

                sb.AppendLine("PromptExcerpt:");
                sb.AppendLine("```");
                sb.AppendLine(promptContent);
                sb.AppendLine("```");

                if (remainingPromptChars <= 0)
                {
                    sb.AppendLine("Additional snapshot content omitted from prompt because the code prompt budget was reached.");
                    break;
                }
            }
        }

        private static string BuildCodeSourceOutline(string? content, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(content) || maxChars <= 0)
                return "";

            var outline = new List<string>();
            string[] lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int index = 0; index < lines.Length && outline.Count < 120; index++)
            {
                string raw = lines[index] ?? "";
                string line = raw.Trim();

                if (line.Length == 0)
                    continue;

                bool interesting =
                    line.StartsWith("package ", StringComparison.Ordinal) ||
                    line.StartsWith("import ", StringComparison.Ordinal) ||
                    line.Contains(" class ", StringComparison.Ordinal) ||
                    line.StartsWith("class ", StringComparison.Ordinal) ||
                    IsLikelyMemberSignature(line) ||
                    line.Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("FIXME", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("bug", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Exception", StringComparison.Ordinal);

                if (!interesting)
                    continue;

                outline.Add($"{index + 1}: {TrimForPrompt(line, 220)}");
            }

            return TrimForPrompt(string.Join(Environment.NewLine, outline), maxChars);
        }

        private static bool IsLikelyMemberSignature(string line)
        {
            if (!line.Contains("(", StringComparison.Ordinal) ||
                !line.Contains(")", StringComparison.Ordinal))
                return false;

            if (line.StartsWith("if ", StringComparison.Ordinal) ||
                line.StartsWith("if(", StringComparison.Ordinal) ||
                line.StartsWith("for ", StringComparison.Ordinal) ||
                line.StartsWith("for(", StringComparison.Ordinal) ||
                line.StartsWith("while ", StringComparison.Ordinal) ||
                line.StartsWith("while(", StringComparison.Ordinal) ||
                line.StartsWith("switch ", StringComparison.Ordinal) ||
                line.StartsWith("switch(", StringComparison.Ordinal) ||
                line.StartsWith("catch ", StringComparison.Ordinal) ||
                line.StartsWith("catch(", StringComparison.Ordinal))
                return false;

            return line.Contains("public ", StringComparison.Ordinal) ||
                   line.Contains("private ", StringComparison.Ordinal) ||
                   line.Contains("protected ", StringComparison.Ordinal) ||
                   line.Contains("static ", StringComparison.Ordinal) ||
                   line.Contains("@Override", StringComparison.Ordinal);
        }

        private static void AppendCodeDiffArtifact(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not CodeDiffArtifactPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Code Diff Artifact】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Code Diff Artifact】");
            sb.AppendLine("以下是 code-capability 建立的 diff artifact。若 Status=draft，代表尚未套用，不能宣稱已修改檔案。");

            if (!string.IsNullOrWhiteSpace(payload.Title))
                sb.AppendLine($"Title: {payload.Title}");

            if (!string.IsNullOrWhiteSpace(payload.Status))
                sb.AppendLine($"Status: {payload.Status}");

            if (!string.IsNullOrWhiteSpace(payload.BaseLabel))
                sb.AppendLine($"Base: {payload.BaseLabel}");

            if (!string.IsNullOrWhiteSpace(payload.TargetLabel))
                sb.AppendLine($"Target: {payload.TargetLabel}");

            if (payload.Files != null && payload.Files.Count > 0)
            {
                sb.AppendLine("Files:");
                foreach (var file in payload.Files.Take(12))
                {
                    if (file == null)
                        continue;

                    sb.AppendLine($"- {file.ChangeType}: {file.Path} (+{file.AddedLines}/-{file.RemovedLines})");

                    if (!string.IsNullOrWhiteSpace(file.Summary))
                        sb.AppendLine($"  Summary: {file.Summary}");
                }
            }

            if (payload.Notes != null && payload.Notes.Count > 0)
            {
                sb.AppendLine("Notes:");
                foreach (var note in payload.Notes.Take(8))
                {
                    if (!string.IsNullOrWhiteSpace(note))
                        sb.AppendLine($"- {note}");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.UnifiedDiff))
            {
                sb.AppendLine("UnifiedDiff:");
                sb.AppendLine("```diff");
                sb.AppendLine(payload.UnifiedDiff);
                sb.AppendLine("```");
            }
        }

        private static void AppendSearchSummary(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not SearchSummaryPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Search Summary】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Search Summary】");
            sb.AppendLine("?? 以下資料為唯一可信來源，不可自行補充未提供資訊。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);
            else
                sb.AppendLine("（無摘要）");

            if (payload.Items == null || payload.Items.Count == 0)
                return;

            sb.AppendLine();
            sb.AppendLine("【Sources】");

            int index = 1;
            foreach (var item in payload.Items)
            {
                if (item == null)
                    continue;

                sb.AppendLine($"{index}. {item.Title}");

                if (!string.IsNullOrWhiteSpace(item.Source))
                    sb.AppendLine($"   Url: {item.Source}");

                if (!string.IsNullOrWhiteSpace(item.Date))
                    sb.AppendLine($"   Date: {item.Date}");

                index++;
            }

            if (index == 1)
                sb.AppendLine("（無來源）");
        }
        private readonly AgentParallelPlanner _parallelPlanner = new();
        private readonly AgentRuntimeProfileResolver _agentProfileResolver = new();
        private static void AppendFileSummary(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not FileSummaryPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【File Summary】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【File Summary】");
            sb.AppendLine("?? 以下附件資訊為高優先來源，回答時應優先根據附件內容。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);
            else
                sb.AppendLine("（無附件摘要）");

            if (payload.Items == null || payload.Items.Count == 0)
                return;

            sb.AppendLine();
            sb.AppendLine("【Attached Files】");

            int index = 1;
            foreach (var item in payload.Items)
            {
                if (item == null)
                    continue;

                sb.AppendLine($"{index}. {item.FileName}");

                if (!string.IsNullOrWhiteSpace(item.FileType))
                    sb.AppendLine($"   Type: {item.FileType}");

                if (!string.IsNullOrWhiteSpace(item.MimeType))
                    sb.AppendLine($"   Mime: {item.MimeType}");

                if (!string.IsNullOrWhiteSpace(item.ContentPreview))
                    sb.AppendLine($"   Hint: {item.ContentPreview}");

                index++;
            }

            if (index == 1)
                sb.AppendLine("（無附件）");
        }
        private static void AppendSearchResults(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not System.Collections.IEnumerable enumerable)
            {
                sb.AppendLine();
                sb.AppendLine("【Search Results】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Search Results】");

            int index = 1;
            foreach (var item in enumerable)
            {
                if (item == null)
                    continue;

                string title = ReadObjectString(item, "Title");
                string url = ReadObjectString(item, "Url");
                string snippet = ReadObjectString(item, "Snippet");
                string date = ReadObjectString(item, "Date");

                sb.AppendLine($"{index}. {title}");

                if (!string.IsNullOrWhiteSpace(snippet))
                    sb.AppendLine($"   Snippet: {snippet}");

                if (!string.IsNullOrWhiteSpace(url))
                    sb.AppendLine($"   Url: {url}");

                if (!string.IsNullOrWhiteSpace(date))
                    sb.AppendLine($"   Date: {date}");

                index++;
            }

            if (index == 1)
                sb.AppendLine("（無結果）");
        }

        private static string ReadObjectString(object item, string propertyName)
        {
            if (item == null || string.IsNullOrWhiteSpace(propertyName))
                return "";

            var prop = item.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (prop == null)
                return "";

            var value = prop.GetValue(item);
            return value?.ToString() ?? "";
        }
    }
}
