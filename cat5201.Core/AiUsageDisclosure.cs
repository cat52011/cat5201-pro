using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// AI 使用紀錄：一次執行裡，誰做了哪一段、哪些決策是誰提的、花了多少。
    ///
    /// 2026-11-03 使用者要求：要求揭露 AI 使用的場合（作業、報告）可以直接附上，也方便自己除錯。
    /// 內容全部來自執行當下的紀錄（AiExecutionLogEntry：逐筆帳目、各步驟收到的輸入、產出物），
    /// 由程式組成，不經 AI 改寫——揭露文件本身不能是 AI 寫的摘要。
    /// </summary>
    public static class AiUsageDisclosure
    {
        public static string Build(AiExecutionLogEntry log, string? userRequest, DateTime? generatedAt = null)
        {
            var sb = new StringBuilder();
            var started = log.StartedAtUtc == default ? (DateTime?)null : log.StartedAtUtc.ToLocalTime();
            var records = (log.UsageRecords ?? Array.Empty<UsageRecord>()).OrderBy(r => r.AtUtc).ToList();

            sb.AppendLine("# AI 使用紀錄").AppendLine();
            sb.AppendLine("本紀錄由 cat5201-pro 依執行當下的逐筆帳目自動產生，沒有經過 AI 改寫。").AppendLine();

            sb.AppendLine("## 概要").AppendLine();
            if (started != null)
                sb.AppendLine($"- 執行時間：{started:yyyy-MM-dd HH:mm}，耗時 {Duration(log.DurationMs)}");
            sb.AppendLine($"- 結果：{(log.Success ? "完成" : "失敗" + (string.IsNullOrWhiteSpace(log.ErrorMessage) ? "" : "（" + OneLine(log.ErrorMessage) + "）"))}");
            var models = records.Where(r => !string.IsNullOrWhiteSpace(r.ModelId)).Select(r => Name(r.ModelId)).Distinct().ToList();
            if (models.Count > 0)
                sb.AppendLine($"- 參與的模型：{string.Join("、", models)}");
            double total = records.Sum(r => r.UsdCost);
            if (records.Count > 0)
                sb.AppendLine($"- 總花費：US${total:0.000}（約 NT${total * TwdPerUsd:0}），共 {records.Count} 次呼叫" +
                              (records.Any(r => !r.IsActual) ? "；標示「估算」的是依字數估的，其餘是服務商回報的實際用量" : ""));
            sb.AppendLine();

            string request = (userRequest ?? "").Trim();
            if (request.Length > 0)
            {
                sb.AppendLine("## 使用者的要求").AppendLine();
                if (request.Length > 600) request = request[..600] + "…";
                foreach (var line in request.Replace("\r", "").Split('\n'))
                    sb.AppendLine("> " + line);
                sb.AppendLine();
            }

            sb.AppendLine("## 決策：由誰決定").AppendLine();
            sb.AppendLine("| 決策 | 結果 | 由誰決定 | 依據 |").AppendLine("|---|---|---|---|");
            foreach (var row in Decisions(log, records))
                sb.AppendLine($"| {Cell(row.What)} | {Cell(row.Result)} | {Cell(row.Who)} | {Cell(row.Why)} |");
            sb.AppendLine();

            sb.AppendLine("## 各步驟由誰完成").AppendLine();
            if (records.Count == 0)
                sb.AppendLine("（這次沒有記到逐筆帳目。）");
            else
            {
                sb.AppendLine("| 時間 | 步驟 | 模型或服務 | 用量 | 費用（US$） |").AppendLine("|---|---|---|---|---|");
                foreach (var r in records)
                    sb.AppendLine($"| {r.AtUtc.ToLocalTime():HH:mm:ss} | {Cell(r.Purpose)} | {Cell(Name(r.ModelId))} | {Cell(Usage(r))} | {r.UsdCost:0.0000}{(r.IsActual ? "" : "（估算）")} |");
                sb.AppendLine($"| | **合計** | | | **{total:0.0000}** |");
                sb.AppendLine();

                sb.AppendLine("各模型的份量：").AppendLine();
                foreach (var g in records.GroupBy(r => Name(r.ModelId)).OrderByDescending(g => g.Sum(r => r.UsdCost)))
                {
                    double share = total > 0 ? g.Sum(r => r.UsdCost) / total : 0;
                    var purposes = g.Select(r => r.Purpose).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct();
                    sb.AppendLine($"- {g.Key}：{g.Count()} 次（{string.Join("、", purposes)}），佔費用 {share:P0}");
                }
            }
            sb.AppendLine();

            var files = (log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>())
                .Where(a => !string.IsNullOrWhiteSpace(a.FilePath)).ToList();
            if (files.Count > 0)
            {
                sb.AppendLine("## 產出的檔案").AppendLine();
                sb.AppendLine("| 檔案 | 類型 | 由誰產生 |").AppendLine("|---|---|---|");
                foreach (var f in files)
                    sb.AppendLine($"| {Cell(System.IO.Path.GetFileName(f.FilePath))} | {Cell(OutputFileCategory.Of(f.FilePath).Label)} | {Cell(Author(f))} |");
                sb.AppendLine();
            }

            var research = (log.WorkspaceArtifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>())
                .Where(a => string.IsNullOrWhiteSpace(a.FilePath) &&
                            (a.FactCount > 0 || !string.IsNullOrWhiteSpace(a.VerificationLabel)))
                .ToList();
            if (research.Count > 0)
            {
                sb.AppendLine("## 查到的資料").AppendLine();
                foreach (var a in research)
                    sb.AppendLine($"- {OneLine(a.Title)}" +
                                  (a.FactCount > 0 ? $"：{a.FactCount} 筆資料" : "") +
                                  (string.IsNullOrWhiteSpace(a.VerificationLabel) ? "" : $"（{a.VerificationLabel}）") +
                                  $"，{Author(a)}");
                sb.AppendLine();
            }

            var inputs = log.StepInputs ?? Array.Empty<StepInputRecord>();
            if (inputs.Count > 0)
            {
                sb.AppendLine("## 各步驟收到的輸入").AppendLine();
                sb.AppendLine("用來確認每一步拿到了什麼：字數、是否包含使用者原文、內容指紋（SHA-256 前 12 碼，可比對兩步是否收到同一份內容）。").AppendLine();
                sb.AppendLine("| 步驟 | 模型 | 字數 | 含使用者原文 | 指紋 |").AppendLine("|---|---|---|---|---|");
                foreach (var i in inputs)
                    sb.AppendLine($"| {Cell(i.Step)} | {Cell(Name(i.ModelId))} | {i.Chars:N0} | {(i.ContainsOriginalRequest ? "是" : "否")} | {Cell(i.Sha256.Length > 12 ? i.Sha256[..12] : i.Sha256)} |");
                sb.AppendLine();
            }

            sb.AppendLine("---").AppendLine();
            sb.AppendLine($"產生於 {(generatedAt ?? DateTime.Now):yyyy-MM-dd HH:mm}。費用依各服務商公布的價目計算，與帳單可能有小幅差異。");
            return sb.ToString();
        }

        /// <summary>美元換新台幣的粗略匯率（只用在紀錄裡的「約 NT$」）。</summary>
        internal const double TwdPerUsd = 32.0;

        internal sealed record DecisionRow(string What, string Result, string Who, string Why);

        internal static IReadOnlyList<DecisionRow> Decisions(AiExecutionLogEntry log, IReadOnlyList<UsageRecord> records)
        {
            var rows = new List<DecisionRow>();
            string mode = log.SelectionMode ?? "";
            bool manual = mode is not ("Auto" or "API Auto" or "Collaboration");

            rows.Add(new DecisionRow("執行模式",
                mode switch { "Auto" => "Auto（規則）", "API Auto" => "Auto（模型判斷）", "Collaboration" => "協作", _ => "手動" },
                "你（節點設定）", ""));

            string requested = Name(log.RequestedModelId);
            string actual = Name(string.IsNullOrWhiteSpace(log.ActualModelId) ? log.PlannedModelId : log.ActualModelId);
            if (manual)
                rows.Add(new DecisionRow("回答的模型", requested.Length > 0 ? requested : actual, "你（手動選擇）", ""));
            else
            {
                var picker = records.FirstOrDefault(r => r.Purpose.Contains("模型自動選擇", StringComparison.Ordinal));
                rows.Add(new DecisionRow("回答的模型", actual,
                    picker != null ? $"系統（{Name(picker.ModelId)} 判斷）" : $"系統（{(string.IsNullOrWhiteSpace(log.Resolver) ? "規則" : log.Resolver)}）",
                    OneLine(log.ResolverReason)));
            }

            if (!string.IsNullOrWhiteSpace(log.OutputIntentSummary))
            {
                var intent = records.FirstOrDefault(r => r.Purpose.Contains("輸出意圖判斷", StringComparison.Ordinal));
                rows.Add(new DecisionRow("要交出什麼", OneLine(log.OutputIntentSummary),
                    intent != null ? $"系統（{Name(intent.ModelId)} 判斷）" : "系統（關鍵字規則）",
                    "產出檔案前會先跳確認框，由你決定是否產生"));
            }

            if (!string.IsNullOrWhiteSpace(log.CapabilityPlanSummary))
            {
                // 跟「要交出什麼」是同一次判斷：模型有回答就是那個模型判斷的。
                // 手動模式不自動加能力（你決定）；關鍵字只在規則式 Auto 當後援。
                string summary = log.CapabilityPlanSummary;
                var intent = records.FirstOrDefault(r => r.Purpose.Contains("輸出意圖判斷", StringComparison.Ordinal));
                string who =
                    summary == AgentCapabilityExecutionPlan.ManualModeSummary ? "你（手動模式）" :
                    summary.Contains(AgentCapabilityExecutionPlan.KeywordFallbackNote, StringComparison.Ordinal) ? "系統（關鍵字規則）" :
                    summary.Contains(AgentCapabilityExecutionPlan.NoJudgementNote, StringComparison.Ordinal) ? "系統（沒有判斷結果）" :
                    intent != null ? $"系統（{Name(intent.ModelId)} 判斷）" : "系統（語意判斷）";
                rows.Add(new DecisionRow("要不要查資料／推理／寫程式", OneLine(summary), who, ""));
            }

            if (log.CapabilityAdjusted)
                rows.Add(new DecisionRow("模型能力調整",
                    $"{Name(log.CapabilityRequestedModelId)} → {Name(log.CapabilityResolvedModelId)}",
                    "系統（能力檢查）", OneLine(log.CapabilityReason)));

            var attempts = log.FallbackAttempts ?? Array.Empty<AiFallbackAttempt>();
            if (log.RuntimeFallbackUsed || attempts.Any(a => !a.Success))
                rows.Add(new DecisionRow("換模型", OneLine(log.RuntimeFallbackSummary).Length > 0
                        ? OneLine(log.RuntimeFallbackSummary)
                        : string.Join("、", attempts.Where(a => !a.Success).Select(a => Name(a.ModelId) + " 失敗")),
                    "系統（原本的模型執行失敗）", "答案最上方會註明換成了誰"));

            if (!string.IsNullOrWhiteSpace(log.EffectivePreferences))
                rows.Add(new DecisionRow("套用的個人化", OneLine(log.EffectivePreferences), "你（個人化設定）", ""));

            return rows;
        }

        private static string Author(AgentWorkspaceArtifactRecord a)
        {
            string model = Name(a.ModelId);
            string agent = string.IsNullOrWhiteSpace(a.SourceAgentId) ? "" : a.SourceAgentId;
            return model.Length > 0 && agent.Length > 0 ? $"{model}（{agent}）" : model.Length > 0 ? model : agent;
        }

        private static string Usage(UsageRecord r)
        {
            if (r.Kind != UsageKinds.Llm)
                return r.Quantity > 0 ? $"{r.Quantity.ToString("0.##", CultureInfo.InvariantCulture)} {r.Unit}".Trim() : r.Kind;
            string input = $"輸入 {r.TotalInputTokens:N0}";
            if (r.CacheReadTokens > 0) input += $"（快取 {r.CacheReadTokens:N0}）";
            return $"{input}／輸出 {r.OutputTokens:N0} token";
        }

        private static string Name(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId)) return "";
            try { return AiModelHelper.GetDefinition(modelId).DisplayName; }
            catch { return modelId!; }
        }

        private static string Duration(long ms)
            => ms <= 0 ? "—" : ms < 60_000 ? $"{ms / 1000.0:0.#} 秒" : $"{ms / 60_000.0:0.0} 分鐘";

        private static string OneLine(string? s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

        private static string Cell(string? s) => OneLine(s).Replace("|", "／");
    }
}
