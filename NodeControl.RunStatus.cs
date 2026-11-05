using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Cat5201
{
    // NodeControl 部分類別 — 執行狀態、重新產生、逾時與載入動畫（自 NodeControl.xaml.cs 原樣搬出）
    public partial class NodeControl
    {
        // ===== Product UX：節點狀態（邊框顏色）+ 狀態列 =====

        private void ApplyRunStatus(NodeRunStatus status, string? detail = null)
        {
            _runStatus = status;

            StopStatusRevertTimer();

            if (NodeBorder != null)
            {
                NodeBorder.BorderBrush = status switch
                {
                    NodeRunStatus.Running => new SolidColorBrush(Color.FromRgb(0x1E, 0x73, 0xE6)), // 藍：執行中
                    NodeRunStatus.Success => new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B)), // 綠：成功
                    NodeRunStatus.Failed => new SolidColorBrush(Color.FromRgb(0xD1, 0x43, 0x43)),  // 紅：失敗
                    NodeRunStatus.Waiting => new SolidColorBrush(Color.FromRgb(0xE0, 0x92, 0x2B)), // 琥珀：等待上游
                    _ => new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00)),                      // 黑：閒置
                };
            }

            switch (status)
            {
                case NodeRunStatus.Running:
                    if (StatusText != null) StatusText.Text = "";
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Collapsed;
                    break;

                case NodeRunStatus.Success:
                    // §3：成功後可從右鍵選單「重新生成答案」(沿用 research)。footer 已無內容，收起避免空白條。
                    if (StatusText != null)
                    {
                        StatusText.Text = "";
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                    }
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Collapsed;
                    // 成功的綠框只短暫提示，之後回到黑框，避免畫布上一片綠。
                    StartStatusRevertTimer();
                    break;

                case NodeRunStatus.Failed:
                    if (StatusText != null)
                    {
                        StatusText.Text = string.IsNullOrWhiteSpace(detail) ? UiLanguage.P("執行失敗", "Run failed") : detail;
                        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x39, 0x2B));
                    }
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Visible;
                    break;

                case NodeRunStatus.Waiting:
                    // 排入流動工作流、等上游跑完前的過渡狀態；只換琥珀邊框，不顯示 footer。
                    if (StatusText != null) StatusText.Text = "";
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Collapsed;
                    break;

                default: // Idle
                    if (StatusFooter != null) StatusFooter.Visibility = Visibility.Collapsed;
                    break;
            }
        }

        /// <summary>被排入流動工作流、等待上游完成前的「等待中」狀態（節點琥珀框 + 手機鏡像顯示）。</summary>
        public void MarkWaiting()
        {
            if (_isGenerating || _runStatus == NodeRunStatus.Running)
                return; // 正在跑的節點不覆蓋
            ApplyRunStatus(NodeRunStatus.Waiting);
        }

        /// <summary>若仍停在等待中（上游失敗或鏈結束而未輪到它執行），復位為閒置。</summary>
        public void ClearWaitingStatus()
        {
            if (_runStatus == NodeRunStatus.Waiting)
                ApplyRunStatus(NodeRunStatus.Idle);
        }

        private void StartStatusRevertTimer()
        {
            StopStatusRevertTimer();

            _statusRevertTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2.5)
            };
            _statusRevertTimer.Tick += (_, _) =>
            {
                StopStatusRevertTimer();
                // 仍維持在成功狀態才回復黑框（避免覆蓋後續的新執行 / 失敗）。
                if (_runStatus == NodeRunStatus.Success && NodeBorder != null)
                    NodeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0x00, 0x00));
            };
            _statusRevertTimer.Start();
        }

        private void StopStatusRevertTimer()
        {
            if (_statusRevertTimer != null)
            {
                _statusRevertTimer.Stop();
                _statusRevertTimer = null;
            }
        }

        // 把底層拋出的技術性錯誤轉成使用者看得懂的訊息。
        private static string BuildFriendlyError(Exception ex)
        {
            string raw = ex?.Message ?? "";
            string lower = raw.ToLowerInvariant();

            if (lower.Contains("401") || lower.Contains("unauthorized") || lower.Contains("api key") || lower.Contains("api 金鑰"))
                return UiLanguage.P("API 金鑰無效或尚未設定，請檢查模型設定後再試。", "The API key is invalid or not set. Check the model settings and try again.");

            if (lower.Contains("429") || lower.Contains("rate limit") || lower.Contains("quota") || lower.Contains("insufficient"))
                return UiLanguage.P("請求太頻繁或額度不足，請稍候再試，或改用其他模型。", "Too many requests or not enough credit. Wait a moment and try again, or use another model.");

            if (ex is System.Net.Http.HttpRequestException ||
                lower.Contains("network") || lower.Contains("connection") || lower.Contains("socket") ||
                lower.Contains("name resolution") || lower.Contains("無法連線") || lower.Contains("timed out"))
                return UiLanguage.P("無法連線到 AI 服務，請確認網路後再試一次。", "Can't reach the AI service. Check your network and try again.");

            if (lower.Contains("500") || lower.Contains("502") || lower.Contains("503") || lower.Contains("server error") || lower.Contains("overloaded"))
                return UiLanguage.P("AI 服務暫時無法使用，請稍後再試。", "The AI service is temporarily unavailable. Please try again later.");

            if ((lower.Contains("content") && (lower.Contains("policy") || lower.Contains("blocked"))) ||
                lower.Contains("refus") || lower.Contains("safety"))
                return UiLanguage.P("這個請求被模型拒絕，可能涉及受限制的內容。", "The model refused this request; it may involve restricted content.");

            string trimmed = raw.Length > 140 ? raw.Substring(0, 140).TrimEnd() + "…" : raw;
            return string.IsNullOrWhiteSpace(trimmed)
                ? UiLanguage.P("發生未預期的錯誤，請重試一次。", "An unexpected error occurred. Please try again.")
                : UiLanguage.P($"執行時發生錯誤：{trimmed}", $"Error while running: {trimmed}");
        }

        // §3：只重新生成最終答案，沿用上一次的 research / capability 成果（較快、不重跑搜尋）。
        private async void RegenerateMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_isGenerating)
                return;

            if (_parent?.NodeService == null)
                return;

            // 有可沿用的成功 workspace（同一 session 內剛跑過）→ 只重生答案、省去 research（快）。
            // 沒有（例如重啟 app 後記憶體快取已失效、或此節點從沒在本 session 成功跑過）→ 直接完整重跑，
            // 避免落到 replay 找不到輸入而回空字串、跳出「沒有回傳內容」。
            if (!_parent.NodeService.CanRegenerateAnswer(this))
            {
                await RunCurrentTopTextAsync();
                return;
            }

            string prompt = string.IsNullOrWhiteSpace(_lastRunPrompt)
                ? BuildPromptForCurrentRun(GetTopText())
                : _lastRunPrompt;

            if (string.IsNullOrWhiteSpace(prompt))
                return;

            await GenerateBottomReplyFromTopAsync(
                prompt,
                (delta, token) => _parent.NodeService.RegenerateAnswerStreamAsync(this, delta, token));
        }

        /// <summary>
        /// 預覽視窗的「重新生成」：沿用節點原本的需求，另外附上這次的修改指示重跑整個流程。
        /// 不改動節點輸入框的內容（使用者的原始需求保持原樣）。
        /// </summary>
        public async Task<bool> RegenerateWithInstructionAsync(string? instruction, CancellationToken externalToken = default)
        {
            if (_isGenerating)
                return false;

            string top = GetTopText() ?? "";
            if (string.IsNullOrWhiteSpace(top))
                return false;

            string runTop = BuildPromptForCurrentRun(top);
            string extra = (instruction ?? "").Trim();
            if (extra.Length > 0)
            {
                runTop = runTop.TrimEnd() +
                         "\n\n【這次的修改要求（優先於原需求中的衝突部分，其餘沿用）】\n" + extra;
            }

            CommitEditingModel();
            _isTopLocked = true;
            EndEditBecauseSent();
            ContentChanged?.Invoke(this, EventArgs.Empty);
            _parent?.NotifyNodeSubmitted(this);

            await GenerateBottomReplyFromTopAsync(runTop, externalToken: externalToken);

            string bottom = GetBottomText() ?? "";
            return !string.IsNullOrWhiteSpace(bottom) &&
                   !bottom.TrimStart().StartsWith("（AI 產生失敗）", StringComparison.Ordinal);
        }

        // §3：用相同輸入整段重播工作流。
        public async Task<bool> RunCurrentTopTextAsync(CancellationToken externalToken = default)
        {
            if (_isGenerating)
                return false;

            string top = GetTopText() ?? "";
            if (string.IsNullOrWhiteSpace(top))
                return false;

            string runTop = BuildPromptForCurrentRun(top);

            if (_parent != null && _parent.IsAutoModelSelectionEnabled())
                _editingModelId = NormalizeSafeModelId(_parent.GetEffectiveNodeModel(this, runTop));

            CommitEditingModel();
            _isTopLocked = true;
            EndEditBecauseSent();
            ContentChanged?.Invoke(this, EventArgs.Empty);
            _parent?.NotifyNodeSubmitted(this);

            await GenerateBottomReplyFromTopAsync(runTop, externalToken: externalToken);

            string bottom = GetBottomText() ?? "";
            return !string.IsNullOrWhiteSpace(bottom) &&
                   !bottom.TrimStart().StartsWith("（AI 產生失敗）", StringComparison.Ordinal) &&
                   !bottom.TrimStart().StartsWith("（AI 產生逾時）", StringComparison.Ordinal) &&
                   !bottom.TrimStart().StartsWith("（AI 沒有回傳文字）", StringComparison.Ordinal);
        }

        private string BuildPromptForCurrentRun(string topText)
        {
            string prompt = topText ?? "";

            if (_parent != null &&
                _parent.TryBuildInputFromUpstreams(this, out var injectedPrompt) &&
                !string.IsNullOrWhiteSpace(injectedPrompt))
            {
                prompt = injectedPrompt;
            }

            return prompt;
        }

        private TimeSpan ResolveExecutionTimeout(string? topText)
        {
            // 影片生成：原生延伸接長片可能長達數十分鐘，且每段需輪詢——一律「無逾時上限」，
            // 只由使用者主動取消 / 工作流停止來中止（不受手動逾時設定限制）。
            if (IsVideoTask(topText))
                return Timeout.InfiniteTimeSpan;

            // §15 個人化：使用者若手動設定逾時上限（>0 秒），一律以它為準，覆蓋以下自動判斷。
            int manualSecs = _parent?.GetManualTimeoutSeconds() ?? 0;
            if (manualSecs > 0)
                return TimeSpan.FromSeconds(manualSecs);

            bool hasAttachments = _attachments.Count > 0;
            string text = topText ?? "";
            string lower = text.ToLowerInvariant();

            bool codePatchTask =
                ContainsAny(text, lower,
                    "bug", "debug", "修正", "修改", "修好", "patch", "diff", "重構", "程式", "程式碼");

            bool reportTask =
                ContainsAny(text, lower,
                    "報告", "report", "分析", "研究", "財報", "匯出", "export", "生成檔", "生成報", "generate",
                    "簡報", "投影片", "ppt", "pptx", "slides", "slide", "presentation");

            // 生成圖片：gpt-image-2 出圖較慢（可能 1～3 分鐘），需要更寬鬆的逾時。
            bool imageTask = IsImageTask(text);

            if (hasAttachments && codePatchTask)
                return TimeSpan.FromMinutes(10);

            if (hasAttachments)
                return TimeSpan.FromMinutes(6);

            if (imageTask)
                return TimeSpan.FromMinutes(8);

            // 文件技能（Claude 在沙盒裡寫程式、轉圖檢查）常要數分鐘，再加上前面的研究與整合。
            if (reportTask)
                return _parent?.GetDocumentEngine() == DocumentEngine.ClaudeSkills
                    ? TimeSpan.FromMinutes(25)
                    : TimeSpan.FromMinutes(8);

            return TimeSpan.FromMinutes(3);
        }

        // 生成圖片偵測（逾時策略用）：與 OrchestrationPlanner 共用 MediaTaskKeywords 單一真相，不再人工同步清單。
        private static bool IsImageTask(string? topText)
            => MediaTaskKeywords.IsImageGenerationCommand(topText);

        // 影片偵測（逾時策略用）：刻意用「寬鬆」清單——只是多給時間，寧誤勿漏；
        // 觸發實際生成的嚴格清單在 OrchestrationPlanner（走 MediaTaskKeywords.VideoGenerationCommands）。
        private static bool IsVideoTask(string? topText)
            => MediaTaskKeywords.MentionsVideoForTimeout(topText);

        private static string FormatTimeout(TimeSpan timeout)
        {
            if (timeout.TotalMinutes >= 1)
                return UiLanguage.P($"{(int)Math.Round(timeout.TotalMinutes)} 分鐘", $"{(int)Math.Round(timeout.TotalMinutes)} min");

            return UiLanguage.P($"{(int)Math.Round(timeout.TotalSeconds)} 秒", $"{(int)Math.Round(timeout.TotalSeconds)} sec");
        }

        private void StartBottomLoadingAnimation() => StartBottomLoadingAnimation(false);

        private void StartBottomLoadingAnimation(bool imageTask)
        {
            _isShowingLoadingText = true;
            MarkBottomPlain();
            BottomDisplay.Text = "";

            _loadingIsImageTask = imageTask;
            _loadingBaseText = imageTask ? UiLanguage.P("正在生成圖片", "Generating image") : UiLanguage.P("AI 正在生成", "AI is generating");
            _loadingExtraHint = "";
            _loadingStartUtc = DateTime.UtcNow;

            if (BottomLoadingText != null)
                BottomLoadingText.Text = _loadingBaseText + "…";

            if (BottomLoadingOverlay != null)
                BottomLoadingOverlay.Visibility = Visibility.Visible;

            StartSpinnerAnimation();
            StartLoadingTimer();
        }

        private void StartLoadingTimer()
        {
            StopLoadingTimer();

            _loadingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _loadingTimer.Tick += LoadingTimer_Tick;
            _loadingTimer.Start();
        }

        private void LoadingTimer_Tick(object? sender, EventArgs e)
        {
            if (BottomLoadingText == null)
                return;

            var elapsed = DateTime.UtcNow - _loadingStartUtc;

            string clock = elapsed.TotalMinutes >= 1
                ? $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:D2}"
                : UiLanguage.P($"{elapsed.Seconds} 秒", $"{elapsed.Seconds} s");

            string hint = string.IsNullOrWhiteSpace(_loadingExtraHint)
                ? (_loadingIsImageTask ? UiLanguage.P("（gpt-image 通常需 1～3 分鐘）", " (gpt-image usually takes 1–3 minutes)") : "")
                : $"（{_loadingExtraHint}）";

            BottomLoadingText.Text = $"{_loadingBaseText}… {clock}{hint}";
        }

        /// <summary>長任務（影片生成等）即時進度提示，可從背景執行緒呼叫。傳 null/空字串清除。</summary>
        public void SetLoadingHint(string? hint)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => SetLoadingHint(hint));
                return;
            }

            _loadingExtraHint = hint ?? "";
        }

        private void StopLoadingTimer()
        {
            if (_loadingTimer != null)
            {
                _loadingTimer.Stop();
                _loadingTimer.Tick -= LoadingTimer_Tick;
                _loadingTimer = null;
            }
        }

        private void StartSpinnerAnimation()
        {
            if (BottomLoadingSpinnerRotate == null)
                return;

            BottomLoadingSpinnerRotate.BeginAnimation(
                RotateTransform.AngleProperty,
                null);

            var animation = new DoubleAnimation
            {
                From = 0,
                To = 360,
                Duration = TimeSpan.FromSeconds(0.85),
                RepeatBehavior = RepeatBehavior.Forever
            };

            BottomLoadingSpinnerRotate.BeginAnimation(
                RotateTransform.AngleProperty,
                animation);
        }

        private void StopBottomLoadingAnimation(bool clearIfLoading)
        {
            StopLoadingTimer();

            if (BottomLoadingSpinnerRotate != null)
            {
                BottomLoadingSpinnerRotate.BeginAnimation(
                    RotateTransform.AngleProperty,
                    null);
            }

            if (BottomLoadingOverlay != null)
                BottomLoadingOverlay.Visibility = Visibility.Collapsed;

            if (clearIfLoading && _isShowingLoadingText)
            {
                MarkBottomPlain();
                BottomDisplay.Text = "";
            }

            _isShowingLoadingText = false;
        }
    }
}
