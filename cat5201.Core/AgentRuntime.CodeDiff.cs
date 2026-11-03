using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    // AgentRuntime 的一部分：程式修改（diff）：無效修改的修補與大範圍修改的防護。
    // （2026-11-03 依職責拆檔：方法原樣搬出，沒有改任何邏輯；主流程在 AgentRuntime.cs。）
    public sealed partial class AgentRuntime
    {
        private async Task<AiFallbackExecutionResult?> TryRepairInvalidCodeDiffAsync(
            INodeContext node,
            string originalFinalInput,
            string userGoal,
            AiFallbackExecutionResult previousExecution,
            CodeDiffArtifactPayload invalidDiff,
            CodeDiffValidationPayload validation,
            NodeExecutionDecision decision,
            CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.Purpose("程式修補重試");
            if (invalidDiff == null || validation == null)
                return null;

            string repairInput = BuildInvalidCodeDiffRepairPrompt(
                originalFinalInput,
                userGoal,
                previousExecution?.Text ?? "",
                invalidDiff,
                validation);

            var repairDecision = new NodeExecutionDecision
            {
                RequestedAgentId = decision.ActualAgentId,
                ActualAgentId = decision.ActualAgentId,
                RequestedModelId = decision.ModelId,
                ModelId = decision.ModelId,
                ActualModelId = "",
                TaskMode = decision.TaskMode,
                ResolverLabel = "Code Diff Repair",
                ResolverReason = "Previous model-generated diff failed dry-run validation; requesting one repair attempt.",
                StatusLabel = decision.StatusLabel,
                ForceSingleModel = decision.ForceSingleModel,
                UseStreaming = false
            };

            var repaired = await _executeWithFallbackAsync(
                node,
                repairInput,
                repairDecision,
                null,
                false,
                ct);

            repaired = FinalAnswerSanitizer.Sanitize(
                repaired,
                enforceSynthesisFormat: false);

            if (string.IsNullOrWhiteSpace(repaired.Text))
                return null;

            return repaired;
        }

        private static AiFallbackExecutionResult WithSafeInvalidPatchMessage(
            AiFallbackExecutionResult execution,
            CodeDiffValidationPayload validation)
        {
            var lines = new List<string>
            {
                "我找到可能的修改方向，但產生的 patch 沒有通過 dry-run validation，因此目前不應套用。",
                "",
                "驗證結果：",
                validation?.Summary ?? "Diff validation failed."
            };

            foreach (var file in validation?.Files ?? Array.Empty<CodeDiffValidationFileResult>())
            {
                if (file == null)
                    continue;

                lines.Add($"- {file.Path}: {file.Message}");
            }

            lines.Add("");
            lines.Add("建議下一步：改用更小範圍的修正請求，或讓系統先列出可確認的 bug，再逐項產生 patch。");

            return new AiFallbackExecutionResult
            {
                IsSuccess = execution.IsSuccess,
                Text = string.Join(Environment.NewLine, lines),
                ActualModelId = execution.ActualModelId,
                UsedFallback = execution.UsedFallback,
                Summary = execution.Summary,
                ErrorMessage = execution.ErrorMessage,
                Attempts = execution.Attempts
            };
        }

        private static string BuildInvalidCodeDiffRepairPrompt(
            string originalFinalInput,
            string userGoal,
            string previousOutput,
            CodeDiffArtifactPayload invalidDiff,
            CodeDiffValidationPayload validation)
        {
            var validationLines = new List<string>();

            if (!string.IsNullOrWhiteSpace(validation.Summary))
                validationLines.Add(validation.Summary);

            foreach (var file in validation.Files ?? Array.Empty<CodeDiffValidationFileResult>())
            {
                if (file == null)
                    continue;

                validationLines.Add(
                    $"{file.Status}: {file.Path} (+{file.AddedLines}/-{file.RemovedLines}) - {file.Message}");
            }

            foreach (var message in validation.Messages ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(message))
                    validationLines.Add(message);
            }

            return
$@"你剛剛輸出的 unified diff 沒有通過 dry-run validation。請只做一次修復，重新輸出一份可以對上附件 snapshot 的 unified diff。

【使用者目標】
{TrimForPrompt(userGoal, 1200)}

【原始任務摘要】
{BuildCompactOriginalTaskSummary(originalFinalInput)}

【上一版輸出】
{TrimForPrompt(previousOutput, MaxRepairPreviousOutputChars)}

【上一版 diff】
```diff
{TrimForPrompt(invalidDiff.UnifiedDiff, MaxRepairDiffChars)}
```

【Validation 失敗原因】
{string.Join("\n", validationLines)}

【修復規則】
1. 必須根據附件 snapshot 內實際存在的原始碼行產生 diff。
2. 不要使用 validation 已指出找不到的 source line。
3. 必須輸出 fenced unified diff，格式為 ```diff。
4. 不要宣稱已套用檔案。
5. 如果無法安全修復，請直接說明無法修復的原因，不要輸出假 diff。";
        }

        private static bool IsLargeBroadCodePatchRequest(
            string userGoal,
            IReadOnlyDictionary<string, object> capabilityData,
            AgentWorkspace workspace)
        {
            if (!IsBroadCodePatchGoal(userGoal))
                return false;

            var snapshot = capabilityData.TryGetValue("code_file_snapshot", out var snapshotValue)
                ? snapshotValue as CodeFileSnapshotPayload
                : workspace.GetByType("code_file_snapshot")
                    .Select(x => x.Payload as CodeFileSnapshotPayload)
                    .FirstOrDefault(x => x != null);

            if (snapshot?.Files == null || snapshot.Files.Count == 0)
                return false;

            int totalChars = snapshot.Files
                .Where(x => x != null)
                .Sum(x => x.CharacterCount);

            int totalLines = snapshot.Files
                .Where(x => x != null)
                .Sum(x => x.LineCount);

            return totalChars >= 30000 || totalLines >= 700;
        }

        private static bool IsBroadCodePatchGoal(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = text.Trim().ToLowerInvariant();

            return ContainsAnyInvariant(normalized,
                "把看到的 bug 都修好",
                "看到的 bug 都修好",
                "看到的bug都修好",
                "所有 bug",
                "all bugs",
                "fix all",
                "fix every",
                "全部修好",
                "都修好",
                "全面修",
                "整份",
                "整個程式",
                "whole file",
                "entire file");
        }

        private static string BuildLargeBroadCodePatchGuardMessage(
            string userGoal,
            IReadOnlyDictionary<string, object> capabilityData,
            AgentWorkspace workspace)
        {
            var snapshot = capabilityData.TryGetValue("code_file_snapshot", out var snapshotValue)
                ? snapshotValue as CodeFileSnapshotPayload
                : workspace.GetByType("code_file_snapshot")
                    .Select(x => x.Payload as CodeFileSnapshotPayload)
                    .FirstOrDefault(x => x != null);

            var files = snapshot?.Files?
                .Where(x => x != null)
                .Take(5)
                .Select(x => $"- {x.FileName}：{x.LineCount} 行 / {x.CharacterCount} 字元")
                .ToList() ?? new List<string>();

            string fileText = files.Count == 0
                ? "- 已偵測到大型程式附件。"
                : string.Join(Environment.NewLine, files);

            return
$@"這個請求目前先不直接產生 patch，因為它是大型檔案的全檔泛修任務，直接丟給模型會很貴，而且容易產生無法套用的假 diff。

已建立附件快照：
{fileText}

建議下一步：
1. 先要求「列出可疑 bug 與對應方法/行號」，不產生 patch。
2. 選其中一個 bug 或一個方法，再要求產生 unified diff。
3. 系統會對該小範圍 patch 做 dry-run validation，通過後再顯示為可用 diff。

目前不應對這種任務使用「把看到的 bug 都修好」的一次性全檔 patch 流程，否則會繼續浪費 token 並得到不可靠結果。";
        }

        private static bool ContainsAnyInvariant(string text, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(text) || needles == null || needles.Length == 0)
                return false;

            foreach (var needle in needles)
            {
                if (!string.IsNullOrWhiteSpace(needle) &&
                    text.Contains(needle.ToLowerInvariant(), StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}
