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
    // NodeControl 部分類別 — 送出與執行、手機鏡像遠端操作（自 NodeControl.xaml.cs 原樣搬出）
    public partial class NodeControl
    {
        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsEditing) return;
            if (_isGenerating) return;

            var top = TopEditor.Text ?? "";
            if (string.IsNullOrWhiteSpace(top)) return;

            string runTop = BuildPromptForCurrentRun(top);

            // 送出前，將目前草稿模型正式提交為本次送出模型
            // 自動模式下取當前文字實際推算出的模型；手動模式下取 editingModel
            if (_parent != null && _parent.IsAutoModelSelectionEnabled())
            {
                _editingModelId = NormalizeSafeModelId(_parent.GetEffectiveNodeModel(this, runTop));
            }

            CommitEditingModel();

            _isTopLocked = true;
            EndEditBecauseSent();

            ContentChanged?.Invoke(this, EventArgs.Empty);
            _parent?.NotifyNodeSubmitted(this);

            // 藍虛線下游：送出「當下」就把整條執行路徑標成等待中（琥珀框＋手機顯示），
            // 不等母節點跑完——RunFlowWorkflowAsync 開頭那次標記對手動送出來說太晚了。
            bool hasFlowDownstream = _parent != null && _parent.NodeHasFlowDownstream(this);
            if (hasFlowDownstream)
                _parent!.MarkFlowSubtreeWaiting(this, includeRoot: false);

            await GenerateBottomReplyFromTopAsync(runTop);

            if (_parent != null && RunProducedUsableOutput())
            {
                // 藍色虛線（流動模式）下游：上游一完成就自動沿線往下跑（下游各自仍會檢查自己輸入端是否有內容，
                // 空的節點會被 RunCurrentTopTextAsync 略過）。這是明確畫的執行路徑，不受「自動展開」個人化開關影響。
                if (hasFlowDownstream)
                    await _parent.RunFlowWorkflowAsync(this, runStartNode: false);
                // 否則維持 §4 Mode 2（完全自動）：多階段任務且策略為 FullyAuto 時，AI 自動展開下游並依序執行。
                else
                    await _parent.MaybeAutoExpandAfterSubmitAsync(this);
            }

            // 兜底：母節點失敗、或工作流鏈忙碌被擋而沒真的往下跑時，把仍停在等待中的下游復位
            //（只動 Waiting 狀態，成功路徑上已由 RunFlowWorkflowAsync 的 finally 清過，重複呼叫無害）。
            if (hasFlowDownstream)
                _parent!.ClearFlowSubtreeWaiting(this);
        }

        // 本次執行是否產出可作為下游輸入的有效內容（排除錯誤 / 逾時 / 無回應訊息）。
        private bool RunProducedUsableOutput()
        {
            string bottom = (GetBottomText() ?? "").TrimStart();
            if (string.IsNullOrWhiteSpace(bottom))
                return false;

            return !bottom.StartsWith("（AI 產生失敗）", StringComparison.Ordinal)
                && !bottom.StartsWith("（AI 產生逾時）", StringComparison.Ordinal)
                && !bottom.StartsWith("（AI 沒有回傳文字）", StringComparison.Ordinal)
                && !bottom.StartsWith("AI 這次沒有回傳內容", StringComparison.Ordinal)
                && !bottom.StartsWith("AI 回應逾時", StringComparison.Ordinal)
                && !bottom.StartsWith("找不到主視窗", StringComparison.Ordinal)
                && !bottom.StartsWith("AI 服務尚未準備好", StringComparison.Ordinal)
                // 英文介面下同一批錯誤訊息的開頭（見下方 BottomDisplay.Text 的 UiLanguage.P）。
                && !bottom.StartsWith("The AI returned nothing", StringComparison.Ordinal)
                && !bottom.StartsWith("The AI timed out", StringComparison.Ordinal)
                && !bottom.StartsWith("Main window not found", StringComparison.Ordinal)
                && !bottom.StartsWith("The AI service isn't ready", StringComparison.Ordinal);
        }

        private async Task GenerateBottomReplyFromTopAsync(
            string topText,
            Func<Action<string>, CancellationToken, Task<string>>? executor = null,
            CancellationToken externalToken = default)
        {
            _isGenerating = true;
            topText ??= "";
            _lastRunPrompt = topText;
            // 成本可稽查：頂層執行開始時開新的帳目容器；本次所有子步驟、輔助呼叫、媒體生成都記到這裡。
            BeginUsageScope();
            bool isImageTask = IsImageTask(topText);
            UpdateEditButtons();
            ClearOutputFiles();
            ApplyRunStatus(NodeRunStatus.Running);
            TimeSpan executionTimeout = ResolveExecutionTimeout(topText);

            try
            {
                StartBottomLoadingAnimation(isImageTask);
                await Dispatcher.Yield(DispatcherPriority.Render);

                if (_parent == null)
                {
                    StopBottomLoadingAnimation(clearIfLoading: true);
                    MarkBottomPlain();
                    BottomDisplay.Text = UiLanguage.P("找不到主視窗，目前無法呼叫 AI。請重新開啟節點再試。", "Main window not found, so the AI can't be called right now. Please reopen the node and try again.");
                    ApplyRunStatus(NodeRunStatus.Failed, UiLanguage.P("主視窗未連接", "Main window not connected"));
                    return;
                }

                // 記下這次執行屬於哪個存檔：中途切到別的存檔時，產出仍寫回這個存檔的資料夾（見 MainWindow.Sessions.cs）。
                _parent.EnterRunProject(this);

                if (_parent.NodeService == null)
                {
                    StopBottomLoadingAnimation(clearIfLoading: true);
                    MarkBottomPlain();
                    BottomDisplay.Text = UiLanguage.P("AI 服務尚未準備好，請稍候幾秒再試一次。", "The AI service isn't ready yet. Please wait a few seconds and try again.");
                    ApplyRunStatus(NodeRunStatus.Failed, UiLanguage.P("服務初始化中", "Service starting up"));
                    return;
                }

                // 花錢安全：已達每日花費上限 → 不執行（所有執行路徑都經過這裡：送出/重跑/工作流/手機指令）。
                if (!_parent.CheckDailyBudgetAllows(out string budgetMessage))
                {
                    StopBottomLoadingAnimation(clearIfLoading: true);
                    MarkBottomPlain();
                    BottomDisplay.Text = budgetMessage;
                    ApplyRunStatus(NodeRunStatus.Failed, UiLanguage.P("已達每日花費上限", "Daily spending limit reached"));
                    return;
                }

                // 外部（工作流鏈「停止」/ 手機遠端「停止」）token 與本步逾時 token 連動：任一觸發都會取消這次執行。
                _activeManualStop?.Dispose();
                _activeManualStop = new CancellationTokenSource();
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(externalToken, _activeManualStop.Token);
                cts.CancelAfter(executionTimeout);

                Action<string> deltaHandler = delta =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (_isShowingLoadingText)
                            StopBottomLoadingAnimation(clearIfLoading: true);

                        MarkBottomPlain(); // 串流中：顯示=資料（結束後才換成清過記號的版本）
                        BottomDisplay.AppendText(delta);
                        BottomDisplay.ScrollToEnd();
                    });
                };

                string finalReply = await (executor != null
                    ? executor(deltaHandler, cts.Token)
                    : _parent.NodeService.GenerateStreamAsync(this, topText, deltaHandler, cts.Token));

                StopBottomLoadingAnimation(clearIfLoading: string.IsNullOrWhiteSpace(finalReply));

                if (string.IsNullOrWhiteSpace(finalReply))
                {
                    // 有 chip（檔案型任務成功）：不視為失敗，晶片本身就是輸出。
                    if (_outputFiles.Count > 0)
                    {
                        ApplyRunStatus(NodeRunStatus.Success);
                    }
                    else
                    {
                        MarkBottomPlain();
                        BottomDisplay.Text = UiLanguage.P("AI 這次沒有回傳內容，可能是請求被中斷或模型無回應。請再試一次。", "The AI returned nothing this time. The request may have been interrupted or the model didn't respond. Please try again.");
                        ApplyRunStatus(NodeRunStatus.Failed, UiLanguage.P("沒有回傳內容", "Nothing returned"));
                    }
                }
                else
                {
                    // 資料留原文（下游/存檔/builder 用）；TextBox 存清過記號的版本保底，畫面切到 FlowDocument 真渲染。
                    _bottomRawText = finalReply.Trim();
                    string display = MarkdownDisplayText.Clean(_bottomRawText);
                    if (!string.Equals(BottomDisplay.Text, display, StringComparison.Ordinal))
                    {
                        BottomDisplay.Text = display;
                    }
                    ShowRenderedBottom(_bottomRawText);

                    ApplyRunStatus(NodeRunStatus.Success);
                }

                UpdateAutoTaskPreview();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (OperationCanceledException)
            {
                StopBottomLoadingAnimation(clearIfLoading: true);

                if (externalToken.IsCancellationRequested || _activeManualStop?.IsCancellationRequested == true)
                {
                    // 使用者主動「停止」（工作流鏈，或手機遠端停止）：不是逾時，給對應訊息與狀態。
                    MarkBottomPlain();
                    BottomDisplay.Text =
                        UiLanguage.P("已手動停止。這一步尚未完成。\n", "Stopped manually. This step isn't finished.\n") +
                        UiLanguage.P("可右鍵「執行此節點與下游」從這裡重跑，或「略過此步、從下一步續跑」。", "Right-click and choose \"Run this node and downstream\" to rerun from here, or \"Skip this step and continue\".");
                    ApplyRunStatus(NodeRunStatus.Failed, UiLanguage.P("已停止", "Stopped"));
                }
                else
                {
                    MarkBottomPlain();
                    BottomDisplay.Text =
                        UiLanguage.P($"AI 回應逾時。這次任務超過 {FormatTimeout(executionTimeout)} 仍未完成，已自動取消。\n", $"The AI timed out. This task didn't finish within {FormatTimeout(executionTimeout)} and was cancelled automatically.\n") +
                        UiLanguage.P("可以試著縮短問題、減少附件，或換一個較快的模型再試。", "Try a shorter question, fewer attachments, or a faster model.");
                    ApplyRunStatus(NodeRunStatus.Failed, UiLanguage.P($"逾時（超過 {FormatTimeout(executionTimeout)}）", $"Timed out (over {FormatTimeout(executionTimeout)})"));
                }
            }
            catch (Exception ex)
            {
                StopBottomLoadingAnimation(clearIfLoading: true);
                string friendly = BuildFriendlyError(ex);
                MarkBottomPlain();
                BottomDisplay.Text = friendly;
                ApplyRunStatus(NodeRunStatus.Failed, friendly);
            }
            finally
            {
                StopBottomLoadingAnimation(clearIfLoading: false);
                _isGenerating = false;
                _activeManualStop?.Dispose();
                _activeManualStop = null;
                UpdateEditButtons();
            }
        }

        // ===== 手機鏡像（§17 階段二）遠端輕操控入口：皆須在 UI 執行緒呼叫（由 MainWindow 的 Dispatcher 保證）。 =====

        /// <summary>此節點是否正在執行（供快照 CanStop / CanRerun 判斷）。</summary>
        public bool IsGenerating => _isGenerating;

        /// <summary>遠端「停止」：取消目前執行。回傳是否確實有在跑而送出取消。</summary>
        public bool StopActiveRun()
        {
            if (!_isGenerating || _activeManualStop == null)
                return false;
            try { _activeManualStop.Cancel(); return true; }
            catch { return false; }
        }

        /// <summary>遠端「重跑」：沿用上一次 prompt（或現有輸入）重新生成；fire-and-forget，不阻塞呼叫端。</summary>
        public async Task RerunFromMirrorAsync()
        {
            if (_isGenerating)
                return;
            string prompt = string.IsNullOrWhiteSpace(_lastRunPrompt)
                ? BuildPromptForCurrentRun(GetTopText())
                : _lastRunPrompt;
            if (string.IsNullOrWhiteSpace(prompt))
                return;
            await GenerateBottomReplyFromTopAsync(prompt);
        }
    }
}
