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
    // MainWindow 部分類別 — §17 手機鏡像(server 啟停/快照/遠端指令)
    // （P1-2 機械拆分:內容自 MainWindow.xaml.cs 原樣搬出,零邏輯變更。）
    public partial class MainWindow
    {

        public void SetLiveDecisionResolving(INodeContext nodeContext, NodeExecutionDecision decision)
        {
            // Slice B1 cast 橋：決策窗 UI 需要具體控制項。
            var node = nodeContext as NodeControl;
            if (node == null || decision == null)
                return;

            // §17-3：記住「最後執行的節點」——手機下的新指令會自動接在它的下游。
            _lastRunNode = node;

            string requestedLabel = GetDecisionModelLabel(
                string.IsNullOrWhiteSpace(decision.RequestedModelId) ? decision.ModelId : decision.RequestedModelId);

            string plannedLabel = GetDecisionModelLabel(decision.ModelId);

            string modelText = string.Equals(requestedLabel, plannedLabel, StringComparison.OrdinalIgnoreCase)
                ? plannedLabel
                : $"{plannedLabel} ← {requestedLabel}";

            var steps = new List<NodeDecisionStepViewData>
    {
        new NodeDecisionStepViewData
        {
            Title = "Task Mode",
            Detail = $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / confidence {decision.Confidence:0.00}",
            State = NodeDecisionStepState.Info,
            Highlight = true,
            DetailLines = BuildTaskModeLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Model Selection",
            Detail = modelText,
            State = NodeDecisionStepState.Info,
            Highlight = true,
            DetailLines = BuildModelLines(decision, actualModelId: null)
        },
         new NodeDecisionStepViewData
{
    Title = "Resolver",
    Detail = string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel,
    State = NodeDecisionStepState.Info,
    Highlight = true,
    IsActive = true,
    DetailLines = BuildResolverLines(decision, extra: "正在建立執行決策")
},
        new NodeDecisionStepViewData
{
    Title = "Capability Guard",
    Detail = decision.CapabilityAdjusted ? "已調整" : "檢查中",
    State = decision.CapabilityAdjusted ? NodeDecisionStepState.Warning : NodeDecisionStepState.Info,
    IsActive = !decision.CapabilityAdjusted,
    DetailLines = BuildCapabilityLines(decision, forcePendingText: !decision.CapabilityAdjusted)
},
        new NodeDecisionStepViewData
{
    Title = "Delegation",
    Detail = "尚未觸發",
    State = NodeDecisionStepState.Info,
    DetailLines = new[]
    {
        "目前尚未進入 agent delegation"
    }
},
        new NodeDecisionStepViewData
        {
            Title = "Fallback",
            Detail = "尚未觸發",
            State = NodeDecisionStepState.Info,
            DetailLines = new[]
            {
                "目前尚未進入 runtime fallback"
            }
        },
       new NodeDecisionStepViewData
{
    Title = "Execution",
    Detail = "等待執行",
    State = NodeDecisionStepState.Info,
    DetailLines = new[]
    {
        "執行尚未開始"
    }
}
    };

            var view = BuildLiveDecisionViewData(
                decision,
                modelText,
                $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / {decision.Confidence:0.00}",
                extra: decision.CapabilityAdjusted
                    ? (string.IsNullOrWhiteSpace(decision.CapabilityReason) ? "-" : decision.CapabilityReason)
                    : "-",
                steps: steps);

            _liveDecisionViewsByNode[node.Id] = view;
            RefreshDecisionForNode(node);
        }

        // ===== 手機鏡像（§17 階段一）：開關 → 啟停內嵌唯讀 web server + 每 1.5 秒推快照 =====
        private MobileMirrorServer? _mirrorServer;
        private DispatcherTimer? _mirrorPump;
        private string? _lastMirrorContentKey; // 快照去重：內容沒變就不重新序列化推送
        private bool _mobileMirrorEnabled; // 個人化偏好：是否啟用手機鏡像（存 _preferences.json，啟動自動開）
        // §17 階段二遠端二次確認：目前開著的「要不要產生」對話框 + 內容；手機回答時設其 DialogResult 即可關閉。
        private Window? _pendingGenConfirmWindow;
        private string? _pendingGenConfirmWhat;
        private DateTime? _pendingGenConfirmDeadlineUtc; // 自動同意期限（手機顯示近似倒數用）
        // §17-3：最後執行的節點（SetLiveDecisionResolving 時更新）；手機新指令接它的下游。
        private NodeControl? _lastRunNode;

        private static readonly JsonSerializerOptions MirrorJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };

        private async void MobileMirrorSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            bool on = MobileMirrorSwitch?.IsChecked == true;
            _mobileMirrorEnabled = on;      // 尊重個人化：使用者的明確選擇立即落地
            SavePreferences();
            if (on)
                await StartMobileMirrorAsync();
            else
                await StopMobileMirrorAsync();
        }

        // 啟動時若個人化已開啟手機鏡像，自動把 server 拉起來（開關同步是 gated 的、不會觸發，故明確呼叫）。
        private async Task AutoStartMobileMirrorIfEnabledAsync()
        {
            if (!_mobileMirrorEnabled) return;
            await StartMobileMirrorAsync();
        }

        private async Task StartMobileMirrorAsync()
        {
            try
            {
                _mirrorServer ??= new MobileMirrorServer();
                _mirrorServer.CommandHandler = HandleMirrorCommandAsync; // 手機輕操控（§17 階段二）
                _mirrorServer.NodeTextProvider = nodeId =>               // §17：手機看完整輸出（丟回 UI 執行緒讀）
                    Dispatcher.InvokeAsync(() =>
                    {
                        var node = MainCanvas.Children.OfType<NodeControl>()
                            .FirstOrDefault(n => n.Id.ToString() == nodeId);
                        // 顯示用途：清掉 Markdown 記號（資料層 GetBottomText 仍是原文）。
                        return node == null ? null : MarkdownDisplayText.Clean(node.GetBottomText());
                    }).Task;
                _lastMirrorContentKey = null; // 重啟 server 後第一份快照一定要推
                if (!_mirrorServer.IsRunning)
                    await _mirrorServer.StartAsync(MobileMirrorServer.DefaultPort);

                BuildAndPublishMirrorSnapshot(); // 先推一次，畫面不空白

                _mirrorPump ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
                _mirrorPump.Tick -= MirrorPump_Tick;
                _mirrorPump.Tick += MirrorPump_Tick;
                _mirrorPump.Start();

                string url = _mirrorServer.GetLanUrl();
                if (MobileMirrorUrlText != null) MobileMirrorUrlText.Text = url;
                if (MobileMirrorUrlBox != null) MobileMirrorUrlBox.Visibility = Visibility.Visible;
                UpdateMirrorQrImage(url);
            }
            catch (Exception ex)
            {
                AppLog.Error("MobileMirror", "手機鏡像伺服器啟動失敗", ex);
                _mirrorPump?.Stop();
                if (MobileMirrorSwitch != null) MobileMirrorSwitch.IsChecked = false;
                if (MobileMirrorUrlBox != null) MobileMirrorUrlBox.Visibility = Visibility.Collapsed;
                MessageBox.Show(
                    "無法啟動手機鏡像伺服器（可能是 " + MobileMirrorServer.DefaultPort + " 埠被占用，或防火牆阻擋）。\n\n" + ex.Message,
                    "手機鏡像", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private async Task StopMobileMirrorAsync()
        {
            _mirrorPump?.Stop();
            if (MobileMirrorUrlBox != null) MobileMirrorUrlBox.Visibility = Visibility.Collapsed;
            if (MobileMirrorQr != null) MobileMirrorQr.Source = null;
            if (_mirrorServer != null) await _mirrorServer.StopAsync();
        }

        // 用 QRCoder 產生 URL 的 QR 碼（PngByteQRCode 免 System.Drawing），手機掃描即連，免手打 IP。
        private void UpdateMirrorQrImage(string url)
        {
            if (MobileMirrorQr == null) return;
            try
            {
                using var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
                var png = new QRCoder.PngByteQRCode(data).GetGraphic(6);
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(png))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                MobileMirrorQr.Source = bmp;
            }
            catch
            {
                MobileMirrorQr.Source = null; // QR 失敗不影響鏡像本身，使用者仍可手打 URL
            }
        }

        private void MirrorPump_Tick(object? sender, EventArgs e) => BuildAndPublishMirrorSnapshot();

        /// <summary>從畫布節點建 UI 無關快照並推給 server。失敗絕不可影響主程式。</summary>
        private void BuildAndPublishMirrorSnapshot()
        {
            if (_mirrorServer == null || !_mirrorServer.IsRunning)
                return;
            try
            {
                var now = DateTime.Now;
                var snap = new WorkspaceSnapshot
                {
                    Version = now.Ticks,
                    GeneratedAtUtc = DateTime.UtcNow.ToString("o"),
                    GeneratedAtLocal = now.ToString("HH:mm:ss"),
                };

                foreach (var node in MainCanvas.Children.OfType<NodeControl>())
                {
                    bool hasReal = node.TryGetRealTokenUsage(out int inTok, out int outTok);
                    var (mediaUsd, _) = node.GetMediaCostUsd();
                    var files = node.GetOutputFilePaths()
                        .Select(p => Path.GetFileName(p))
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .ToList();

                    // 參與模型：顯示本次執行「所有」參與的模型（主 + 各產出物），與決策窗同一來源；
                    // 尚未執行過（無 log）才退回目前選的單一模型。
                    var modelLabels = NodeDecisionViewBuilder.GetParticipantModelLabels(GetLatestExecutionLog(node));
                    string modelText = modelLabels.Count > 0
                        ? string.Join(" + ", modelLabels)
                        : node.GetCommittedModelId();

                    snap.Nodes.Add(new NodeSnapshot
                    {
                        Id = node.Id.ToString(),
                        Title = node.GetInputTitle(),
                        Status = node.GetRunStatusKey(),
                        StatusLabel = node.GetRunStatusLabel(),
                        Model = modelText,
                        InputTokens = inTok,
                        OutputTokens = outTok,
                        HasRealTokens = hasReal,
                        MediaCostUsd = mediaUsd,
                        LoadingHint = node.GetLoadingHintText(),
                        OutputPreview = node.GetOutputPreview(),
                        Files = files,
                        CanStop = node.IsGenerating,
                        CanRerun = !node.IsGenerating && !string.IsNullOrWhiteSpace(node.GetInputTitle()),
                    });
                }

                snap.NodeCount = snap.Nodes.Count;
                snap.AnyRunning = snap.Nodes.Any(n => n.Status == "running");
                snap.Models = AiModelRegistry.Available
                    .Select(m => new MirrorModelOption { Id = m.Id, Name = m.DisplayName })
                    .ToList();
                if (_pendingGenConfirmWhat != null)
                    snap.Pending = new PendingConfirmationSnapshot
                    {
                        What = _pendingGenConfirmWhat,
                        RemainingSeconds = _pendingGenConfirmDeadlineUtc.HasValue
                            ? Math.Max(0, (int)(_pendingGenConfirmDeadlineUtc.Value - DateTime.UtcNow).TotalSeconds)
                            : -1,
                    };

                // 去重：內容（節點+待確認）沒變就不推送——畫布閒置時省序列化與手機流量。
                string contentKey = JsonSerializer.Serialize(new { snap.Nodes, snap.Pending }, MirrorJsonOptions);
                if (contentKey == _lastMirrorContentKey)
                    return;
                _lastMirrorContentKey = contentKey;

                _mirrorServer.Publish(JsonSerializer.Serialize(snap, MirrorJsonOptions));
            }
            catch (Exception ex) { AppLog.Warn("MobileMirror", "快照推送失敗（不影響主程式）", ex); }
        }

        // 手機端指令（§17 階段二）：Kestrel 執行緒呼叫進來，一律用 Dispatcher 丟回 WPF UI 執行緒執行，
        // 因為底下要碰 NodeControl（碰 UI 物件必須在 UI 執行緒——技術地雷）。
        private Task<MirrorCommandResult> HandleMirrorCommandAsync(MirrorCommand cmd)
            => Dispatcher.InvokeAsync(() => ExecuteMirrorCommand(cmd)).Task.Unwrap();

        private Task<MirrorCommandResult> ExecuteMirrorCommand(MirrorCommand cmd)
        {
            string action = (cmd.Action ?? "").Trim().ToLowerInvariant();

            // §17 階段三：手機直接下指令 → 在畫布建新節點並執行（不綁定既有節點）。
            if (action == "newnode")
            {
                string text = (cmd.Text ?? "").Trim();
                if (string.IsNullOrWhiteSpace(text))
                    return Task.FromResult(new MirrorCommandResult { Ok = false, Message = "指令是空的" });

                // 放置 + 連線（§17-3）：手機可指定連接節點——
                // ""＝自動（最後執行節點）；節點 Id＝連指定節點。找不到退回自動。
                // 使用者要求：手機不提供「獨立節點」選項——一律接上游（只有空畫布時不得已才獨立）。
                string parentChoice = (cmd.ParentNodeId ?? "").Trim();
                NodeControl? anchor = !string.IsNullOrEmpty(parentChoice)
                    ? MainCanvas.Children.OfType<NodeControl>()
                        .FirstOrDefault(n => n.Id.ToString() == parentChoice)
                    : null;
                anchor ??= (_lastRunNode != null && MainCanvas.Children.Contains(_lastRunNode))
                    ? _lastRunNode
                    : MainCanvas.Children.OfType<NodeControl>().LastOrDefault(); // 沒跑過任何節點就接最後一個節點

                double x, y;
                if (anchor != null)
                {
                    x = Canvas.GetLeft(anchor) + anchor.Width + 220;   // AddNode 以中心點定位
                    y = Canvas.GetTop(anchor) + anchor.Height / 2;
                }
                else
                {
                    int count = MainCanvas.Children.OfType<NodeControl>().Count();
                    x = 260 + (count % 4) * 90;
                    y = 180 + (count % 6) * 70;
                }

                var created = AddNode(x, y, beginEdit: false); // 遠端建點不開編輯器（正在跑時開編輯器會干擾）
                created.SetTopText(text);

                // §17-3：手機指定模型（空＝預設；未知 id 忽略）。Auto 模式下仍由自動選模覆寫——與桌面行為一致。
                string requestedModel = (cmd.ModelId ?? "").Trim();
                if (!string.IsNullOrEmpty(requestedModel) && AiModelRegistry.IsAvailable(requestedModel))
                    created.SetCommittedModelId(requestedModel);

                if (anchor != null)
                    CreateCurve(anchor, "ThumbTR", created, "ThumbTL", flowMode: false); // 灰色實線（使用者要求）；上游注入不依賴 FlowMode
                _ = created.RunCurrentTopTextAsync();          // fire-and-forget，立即回應手機（會自動注入上游輸出）
                BuildAndPublishMirrorSnapshot();
                AppLog.Info("MobileMirror", $"手機下指令建節點：{(text.Length > 40 ? text[..40] + "…" : text)}" +
                                            (anchor != null ? "（已接為下游）" : ""));
                return Task.FromResult(new MirrorCommandResult
                {
                    Ok = true,
                    Message = anchor != null ? "已建立節點（接在最後執行節點下游）並開始執行" : "已建立節點並開始執行",
                });
            }

            // 二次確認的回答不綁定節點：直接設對話框的 DialogResult（此方法已在 UI 執行緒），即會關閉 modal。
            if (action == "confirm" || action == "reject")
            {
                var win = _pendingGenConfirmWindow;
                if (win == null)
                    return Task.FromResult(new MirrorCommandResult { Ok = false, Message = "目前沒有待確認的項目" });
                try { win.DialogResult = (action == "confirm"); }
                catch { return Task.FromResult(new MirrorCommandResult { Ok = false, Message = "回覆失敗（可能已由電腦回答）" }); }
                return Task.FromResult(new MirrorCommandResult
                {
                    Ok = true,
                    Message = action == "confirm" ? "已確認執行" : "已取消，只給文字",
                });
            }

            var node = MainCanvas.Children.OfType<NodeControl>()
                .FirstOrDefault(n => n.Id.ToString() == cmd.NodeId);
            if (node == null)
                return Task.FromResult(new MirrorCommandResult { Ok = false, Message = "找不到節點" });

            switch (action)
            {
                case "stop":
                    bool stopped = node.StopActiveRun();
                    return Task.FromResult(new MirrorCommandResult
                    {
                        Ok = stopped,
                        Message = stopped ? "已送出停止" : "節點不在執行中",
                    });

                case "rerun":
                    if (node.IsGenerating)
                        return Task.FromResult(new MirrorCommandResult { Ok = false, Message = "節點執行中，無法重跑" });
                    _ = node.RerunFromMirrorAsync();  // 不等它跑完，立即回應手機
                    BuildAndPublishMirrorSnapshot();  // 立刻把「執行中」推給手機
                    return Task.FromResult(new MirrorCommandResult { Ok = true, Message = "已開始重跑" });

                default:
                    return Task.FromResult(new MirrorCommandResult { Ok = false, Message = "未知指令" });
            }
        }
    }
}
