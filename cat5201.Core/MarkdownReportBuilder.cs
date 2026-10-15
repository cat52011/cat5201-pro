using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Cat5201
{
    /// <summary>
    /// File Generation v1：把 final answer + shared workspace 組成一份人類可讀的 Markdown 報告。
    /// 純字串組裝，無外部依賴。報告結構：標題 → 中繼資料 → 正文（final answer）→ 來源（verified_facts）。
    /// </summary>
    public static class MarkdownReportBuilder
    {
        public sealed class Request
        {
            public string UserInput { get; init; } = "";
            public string FinalAnswer { get; init; } = "";
            public AgentWorkspace? Workspace { get; init; }
            public OrchestrationTaskType TaskType { get; init; }
            public string PipelineId { get; init; } = "";
            public string ModelId { get; init; } = "";
            public string AgentId { get; init; } = "";
        }

        public static string Build(Request request)
        {
            request ??= new Request();

            var sb = new StringBuilder();

            string title = BuildTitle(request.UserInput);
            sb.AppendLine($"# {title}");
            sb.AppendLine();

            // 中繼資料區（產生時間、模型、管線），讓報告本身可追溯。
            sb.AppendLine("> " + string.Join(" ｜ ", BuildMetadataParts(request)));
            sb.AppendLine();

            // 不再包一層「## 內容」：報告正文自己就有摘要 / 關鍵發現 / 結論與建議等章節，
            // 多一個沒有意義的外層標題只會出現在 Word 的導覽窗格與目錄裡，看起來像沒編輯過。
            string body = (request.FinalAnswer ?? "").Trim();
            sb.AppendLine(string.IsNullOrWhiteSpace(body) ? "（本次未產生內容。）" : body);
            sb.AppendLine();

            AppendSources(sb, request.Workspace);

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        private static IReadOnlyList<string> BuildMetadataParts(Request request)
        {
            var parts = new List<string>
            {
                $"產生時間：{DateTime.Now:yyyy-MM-dd HH:mm}"
            };

            if (!string.IsNullOrWhiteSpace(request.AgentId))
                parts.Add($"代理：{request.AgentId}");

            if (!string.IsNullOrWhiteSpace(request.ModelId))
                parts.Add($"模型：{request.ModelId}");

            if (!string.IsNullOrWhiteSpace(request.PipelineId))
                parts.Add($"管線：{request.PipelineId}");

            return parts;
        }

        /// <summary>
        /// 這次實際查證過的來源（去重、有標題或網址的才算）。
        /// 報告尾巴的「資料來源」章節與表格的來源註記共用同一份，兩份檔案講的來源才會一致。
        /// </summary>
        public static IReadOnlyList<VerifiedFactItem> Sources(AgentWorkspace? workspace, int max = 20)
        {
            if (workspace == null || max <= 0)
                return Array.Empty<VerifiedFactItem>();

            return workspace.GetByType("verified_facts")
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .SelectMany(p => p.Facts ?? Array.Empty<VerifiedFactItem>())
                .Where(f => f != null &&
                            (!string.IsNullOrWhiteSpace(f.SourceTitle) || !string.IsNullOrWhiteSpace(f.SourceUrl)))
                .GroupBy(f => (f.SourceTitle ?? "").Trim() + "|" + (f.SourceUrl ?? "").Trim())
                .Select(g => g.First())
                .Take(max)
                .ToList();
        }

        /// <summary>「資料來源：協會年報、米其林指南」這種一行註記；沒有來源就回空字串。</summary>
        public static string SourceNote(AgentWorkspace? workspace, string? language, int max = 3)
        {
            var labels = Sources(workspace, max)
                .Select(f => string.IsNullOrWhiteSpace(f.SourceTitle) ? f.SourceUrl.Trim() : f.SourceTitle.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            return labels.Count == 0 ? "" : $"{ReportWords.Source(language)}：{string.Join("、", labels)}";
        }

        private static void AppendSources(StringBuilder sb, AgentWorkspace? workspace)
        {
            var sourced = Sources(workspace);
            if (sourced.Count == 0)
                return;

            sb.AppendLine("## 資料來源");
            sb.AppendLine();

            int i = 1;
            foreach (var f in sourced)
            {
                string label = string.IsNullOrWhiteSpace(f.SourceTitle)
                    ? f.SourceUrl
                    : f.SourceTitle.Trim();

                string line = string.IsNullOrWhiteSpace(f.SourceUrl)
                    ? $"{i}. {label}"
                    : $"{i}. [{label}]({f.SourceUrl.Trim()})";

                if (!string.IsNullOrWhiteSpace(f.AsOf))
                    line += $"（{f.AsOf.Trim()}）";

                sb.AppendLine(line);
                i++;
            }

            sb.AppendLine();
        }

        private static string BuildTitle(string userInput)
        {
            string text = (userInput ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

            if (string.IsNullOrWhiteSpace(text))
                return "報告";

            // 取第一行 / 前 40 字作為標題，避免整段提示變成超長標題。
            if (text.Length > 40)
                text = text.Substring(0, 40).TrimEnd() + "…";

            return text;
        }
    }
}
