using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PathShape = System.Windows.Shapes.Path;

namespace Cat5201
{
    // MainWindow 部分類別 — 花錢安全(帳本/每日上限/產檔確認)
    // （P1-2 機械拆分:內容自 MainWindow.xaml.cs 原樣搬出,零邏輯變更。）
    public partial class MainWindow
    {

        // 只登記執行紀錄，不碰帳本：花費由 UsageMeter 在每次 AI 呼叫完成當下入帳。
        // 因此載入專案歷史 log 時天然不會重複計費（Codex P0-3 的根因從結構上消失）。
        public void AddExecutionLog(AiExecutionLogEntry entry)
        {
            _executionLogService.Add(entry);
        }

        // 第一層意圖閘門（Auto 模式）：實際產生影片/圖片/檔案前的二次確認。
        // 回傳 true = 使用者同意產出；false = 只給純文字回答。
        // 在 UI 執行緒上同步彈窗（AgentRuntime.ExecuteAsync 本就跑在 UI 執行緒），故回傳已完成的 Task。
        public Task<bool> ConfirmGenerationAsync(OrchestrationTaskType taskType, OutputIntent? outputIntent)
        {
            // 逐項（名稱 / 說明）——成本一律取自 ModelCostEstimator 同一價目表。
            var items = new List<(string Icon, string Name, string Detail)>();

            // 這次估計要花多少（美元）。只用來判斷會不會超過今日上限並顯示給使用者，
            // 真正入帳的仍是 UsageMeter 記到的實際花費。
            double estimatedUsd = 0;

            void AddTarget(string name, string detail, double usd = 0)
            {
                if (items.Exists(x => x.Name == name)) return;
                items.Add(("", name, detail));
                estimatedUsd += usd;
            }

            double imageUsd = ModelCostEstimator.ImageCostUsd("1024x1024", "high");
            // 影片以最短可用長度估；真正的秒數要等導演企劃完才知道，寧可低估也不要在這裡嚇人。
            double videoUsd = VeoModels.UsdPerSecondForModel(GetEffectiveVeoModel()) * 8;
            // 文件：最佳品質模式會讓 Claude 在沙盒裡實際寫程式排版，來回數分鐘。
            double documentUsd = _documentEngine == DocumentEngine.ClaudeSkills ? 3.0 : 0.05;

            switch (taskType)
            {
                case OrchestrationTaskType.VideoGeneration:
                    AddTarget("影片", "依秒計費，需較長時間", videoUsd);
                    break;
                case OrchestrationTaskType.ImageGeneration:
                    AddTarget("圖片", ModelCostEstimator.ImageUnitCostText(), imageUsd);
                    break;
                case OrchestrationTaskType.ImageEdit:
                    AddTarget("圖片編輯", ModelCostEstimator.ImageUnitCostText(), imageUsd);
                    break;
                case OrchestrationTaskType.Presentation:
                    AddTarget("簡報", DocumentDetail(DeckFormats()), documentUsd);
                    break;
                case OrchestrationTaskType.GenerateFile:
                    AddTarget("檔案", "");
                    break;
            }

            if (outputIntent != null)
            {
                if (outputIntent.WantsPresentation) AddTarget("簡報", DocumentDetail(DeckFormats()), documentUsd);
                if (outputIntent.WantsReport) AddTarget("書面報告", DocumentDetail(WithPdf("Word")), documentUsd);
                if (outputIntent.WantsTable) AddTarget("表格", DocumentDetail(WithPdf("Excel")), documentUsd);
                if (outputIntent.WantsImage) AddTarget("圖片", ModelCostEstimator.ImageUnitCostText(), imageUsd);
                if (outputIntent.WantsVideo) AddTarget("影片", "依秒計費，需較長時間", videoUsd);
            }

            if (items.Count == 0)
                AddTarget("檔案／媒體", "");

            // 手機端橫幅文字（名稱＋說明合併成一行）。
            string what = string.Join("、", items.ConvertAll(x =>
                string.IsNullOrWhiteSpace(x.Detail) ? x.Name : $"{x.Name}（{x.Detail}）"));

            // 超上限時先問清楚，不要直接拒絕：使用者正要產出一份要交出去的東西，
            // 丟一句「超過上限」等於叫他去改設定再重跑一次。
            var budgetWarning = SpendReservations.WouldExceedBudget(estimatedUsd)
                ? new GenerationConfirmDialog.BudgetWarning(
                    SpendLedger.CommittedDisplay(), _dailyBudgetTwd, estimatedUsd)
                : null;

            var dlg = new GenerationConfirmDialog(
                this, items, autoConfirmSeconds: _autoConfirmSeconds, budget: budgetWarning);
            dlg.Loaded += (_, __) =>
            {
                // 手機鏡像（§17 階段二）：登記此二次確認，手機端就能看到並遠端回答。
                _pendingGenConfirmWindow = dlg;
                _pendingGenConfirmWhat = what;
                _pendingGenConfirmDeadlineUtc = _autoConfirmSeconds > 0
                    ? DateTime.UtcNow.AddSeconds(_autoConfirmSeconds)
                    : null;
                BuildAndPublishMirrorSnapshot();
            };
            bool ok = dlg.ShowDialog() == true;

            // 對話框已關（桌面或手機任一回答）：清除待確認狀態並更新手機畫面。
            _pendingGenConfirmWindow = null;
            _pendingGenConfirmWhat = null;
            _pendingGenConfirmDeadlineUtc = null;
            BuildAndPublishMirrorSnapshot();

            // 使用者明知會超過上限仍按下去：這一次放行，範圍由 AgentRuntime 開的 scope 框住。
            if (ok && budgetWarning != null)
                SpendReservations.GrantOverBudget();

            return Task.FromResult(ok);
        }

        /// <summary>簡報會產出哪些檔案（對照 PDF 由個人化決定）。</summary>
        private static string DeckFormats() => WithPdf("PowerPoint");

        /// <summary>個人化開著對照 PDF 時才把 PDF 寫進說明——關掉了還說要產 PDF 就是騙人。</summary>
        private static string WithPdf(string format)
            => DeliveryOptions.CompanionPdf ? $"{format}＋PDF" : format;

        // 文件類產出的確認框說明：最佳品質模式要讓使用者事先知道會花幾分鐘、費用較高。
        // 不寫「文件技能」——那是實作名稱，使用者在個人化裡看到的選項叫「最佳品質」。
        private string DocumentDetail(string formats)
            => _documentEngine == DocumentEngine.ClaudeSkills
                ? $"{formats}・最佳品質模式，約需數分鐘"
                : $"{formats}・快速版";

        // 花錢安全：每日花費上限（台幣，0 = 不限制）。存個人化偏好。
        // MVP 安全預設＝NT$100/天（只在沒有偏好檔時生效；使用者存過的值一律優先）。
        private int _dailyBudgetTwd = 100;

        // 產檔確認框自動同意秒數（0 = 不自動，等使用者手動選擇）。存個人化偏好——
        // 「可稽查」產品該讓使用者自己決定要不要自動同意，不寫死。
        // MVP 安全預設＝0（硬批准：沒人點就不執行），與對外「產出前人類批准」的說法一致。
        private int _autoConfirmSeconds;

        /// <summary>執行前檢查每日預算；超標回 false 並給使用者看的訊息。</summary>
        public bool CheckDailyBudgetAllows(out string message)
        {
            if (SpendLedger.IsOverDailyBudget(_dailyBudgetTwd))
            {
                message =
                    $"已達今日花費上限 NT${_dailyBudgetTwd}（今日已花 {SpendLedger.CommittedDisplay()}）。\n" +
                    "今天不再執行新任務。可到 設定 → 個人化 → 花費 調高或清除上限。";
                return false;
            }
            message = "";
            return true;
        }

        // 每日花費上限：空白/0 = 不限制；上限 999999。
        private void DailyBudgetInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (_syncingCostControls || DailyBudgetInput == null)
                return;

            string raw = (DailyBudgetInput.Text ?? "").Trim();
            _dailyBudgetTwd = int.TryParse(raw, out int twd) && twd > 0 ? twd : 0;
            SpendLedger.DailyBudgetTwd = _dailyBudgetTwd;
            SavePreferences();
        }

        // 產檔確認框自動同意秒數：空白/0 = 不自動；上限 300 秒。
        private void AutoConfirmSecondsInput_Changed(object sender, TextChangedEventArgs e)
        {
            if (_syncingCostControls || AutoConfirmSecondsInput == null)
                return;

            string raw = (AutoConfirmSecondsInput.Text ?? "").Trim();
            _autoConfirmSeconds = int.TryParse(raw, out int secs) && secs > 0 ? Math.Min(secs, 300) : 0;
            SavePreferences();
        }
    }
}
