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
    // NodeControl 部分類別 — 右鍵選單（自 NodeControl.xaml.cs 原樣搬出）
    public partial class NodeControl
    {
        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;
            bool busy = _isGenerating || _parent.IsWorkflowChainRunning;
            DeleteMenuItem.IsEnabled = !_parent.IsInitialNode(this);
            EditMenuItem.IsEnabled = true;
            FontSizeMenuItem.IsEnabled = true;
            // 不可用時直接隱藏（與 ⚡展開 / ⏭略過 / ⏹停止 一致），避免「灰著佔位」。
            // 一鍵執行下游:本節點要「已執行成功(有產出)」且「有自動狀態(流動模式)的下游連線」才顯示。
            // 沿流動邊扇出、不重跑本節點;自動流僅限 FlowMode 連線(GetFlowDownstreamNodes 已過濾)。
            bool canRunChain = !busy
                && !string.IsNullOrWhiteSpace(GetBottomText())
                && _parent.NodeHasFlowDownstream(this);
            RunWorkflowMenuItem.IsEnabled = canRunChain;
            RunWorkflowMenuItem.Visibility = canRunChain ? Visibility.Visible : Visibility.Collapsed;

            // §4：只有「可拆成多階段工作流」的節點才啟用一鍵展開（一般對話不顯示啟用）。
            bool canExpand = !busy &&
                             !string.IsNullOrWhiteSpace(GetTopText()) &&
                             _parent.NodeCanExpandToWorkflow(this);
            ExpandWorkflowMenuItem.IsEnabled = canExpand;
            ExpandWorkflowMenuItem.Visibility = canExpand
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 這個節點自己正在跑的時候才給「強制停止生成」。
            // 以前只有手機鏡像叫得到 StopActiveRun()，桌面這邊唯一的停止是「停止工作流」，
            // 而那個只在整條鏈在跑時才出現——單獨跑一個節點就完全沒有煞車。
            bool canStopThisNode = _isGenerating && _activeManualStop != null;
            StopGenerationMenuItem.IsEnabled = canStopThisNode;
            StopGenerationMenuItem.Visibility = canStopThisNode ? Visibility.Visible : Visibility.Collapsed;

            // §4 stop：只有「鏈正在跑」時才顯示「停止工作流」。
            bool chainRunning = _parent.IsWorkflowChainRunning;
            StopWorkflowMenuItem.IsEnabled = chainRunning;
            StopWorkflowMenuItem.Visibility = chainRunning ? Visibility.Visible : Visibility.Collapsed;

            // §4 skip：鏈未在跑、此節點未在生成、且有下游節點時，才提供「略過此步」。
            bool canSkip = !chainRunning && !_isGenerating && _parent.NodeHasDownstream(this);
            SkipStepMenuItem.IsEnabled = canSkip;
            SkipStepMenuItem.Visibility = canSkip ? Visibility.Visible : Visibility.Collapsed;

            // 加入記憶：只要此節點有產出內容就能手動標記為重要記憶。
            bool canRemember = !busy && !string.IsNullOrWhiteSpace(GetBottomText());
            AddToMemoryMenuItem.IsEnabled = canRemember;
            AddToMemoryMenuItem.Visibility = canRemember ? Visibility.Visible : Visibility.Collapsed;

            // 重新生成答案：節點已有輸出且不在忙碌時才提供（沿用既有 research/分析成果，只重生最終答案）。
            // 原本是輸出區右下角的按鈕，已移到此右鍵選單。
            bool canRegenerate = !busy && !string.IsNullOrWhiteSpace(GetBottomText());
            RegenerateMenuItem.IsEnabled = canRegenerate;
            RegenerateMenuItem.Visibility = canRegenerate ? Visibility.Visible : Visibility.Collapsed;

            // 變更上游：只有「有上游連線」且非初始節點、鏈未在跑時才提供。
            bool canRewire = !busy && !_parent.IsInitialNode(this) && _parent.GetFirstUpstreamNode(this) != null;
            ChangeUpstreamMenuItem.IsEnabled = canRewire;
            ChangeUpstreamMenuItem.Visibility = canRewire ? Visibility.Visible : Visibility.Collapsed;

            // 更換連接方向：只要此節點有任何連線相連即可（純視覺互換接孔，不影響上下游/記憶）。
            bool canSwapSide = !busy && _parent.NodeHasAnyConnection(this);
            SwapConnectionSideMenuItem.IsEnabled = canSwapSide;
            SwapConnectionSideMenuItem.Visibility = canSwapSide ? Visibility.Visible : Visibility.Collapsed;
        }

        private void AddToMemoryMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.AddNodeToMemory(this);
        }

        // 協作模式：指定這個節點由哪幾個模型一起回答（2026-10-29）。
        private void CollaborationMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;

            var dlg = new CollaborationModelsDialog(_parent, _parent.GetNodeCollaborationModels(this));
            if (dlg.ShowDialog() == true && dlg.SelectedModels != null)
            {
                _parent.SetNodeCollaborationModels(this, dlg.SelectedModels);
                RefreshModelSelectionUI();
            }
        }

        private void ChangeUpstreamMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.BeginRewireUpstream(this);
        }

        private void SwapConnectionSideMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.SwapNodeConnectionSide(this);
        }

        private void EditMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.RequestBeginEdit(this, MainWindow.EditReason.UserEdit);
        }

        private void FontSizeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FontSizeSliderDialog(
                owner: Window.GetWindow(this),
                title: UiLanguage.P("調整字體大小", "Font size"),
                initialValue: _fontSize,
                min: 5,
                max: 200,
                onPreviewValueChanged: v => ApplyFontSize(v)
            );

            bool? ok = dlg.ShowDialog();
            if (ok == true) SetFontSize(dlg.SelectedValue);
            else ApplyFontSize(_fontSize);
        }

        private async void ExpandWorkflowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null || _isGenerating)
                return;

            await _parent.ExpandAndRunDownstreamWorkflowAsync(this);
        }

        private async void RunWorkflowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null || _isGenerating)
                return;

            // #4：一鍵執行下游——本節點已執行成功(有產出),不重跑,直接沿「流動模式」邊扇出跑各自動下游。
            await _parent.RunFlowWorkflowAsync(this, runStartNode: false);
        }

        // §4 stop：停止整條正在跑的工作流鏈。
        /// <summary>
        /// 強制停止這個節點目前的生成。
        ///
        /// 停掉之後不會再有新的 API 呼叫，串流也會斷開讓伺服器停止生成；
        /// 但已經生出來的 token 該收的還是會收（AgentRuntime 會把那一段估進帳）。
        /// 影片是例外：Veo 是非同步任務，取消輪詢不會讓雲端那邊停工，
        /// 費用照算，任務也留在 VideoJobJournal 裡可以之後取回。
        /// </summary>
        private void StopGenerationMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (!StopActiveRun())
                return;

            AppLog.Info("Node", "使用者強制停止生成");
            SetLoadingHint(UiLanguage.P("正在停止…", "Stopping…"));
        }

        private void StopWorkflowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.StopWorkflowChain();
        }

        // §4 skip：略過此步，從下一步繼續整條鏈。
        private async void SkipStepMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null || _isGenerating)
                return;

            await _parent.SkipStepAndContinueAsync(this);
        }

        // §4 skip：把此節點標成「已略過」（琥珀框 + 狀態列），但保留 bottom 文字作為下游 passthrough。
        public void MarkChainStepSkipped()
        {
            _runStatus = NodeRunStatus.Idle;
            StopStatusRevertTimer();

            var amber = new SolidColorBrush(Color.FromRgb(0xB9, 0x7B, 0x16));
            if (NodeBorder != null)
                NodeBorder.BorderBrush = amber;
            if (StatusText != null)
            {
                StatusText.Text = UiLanguage.P("已略過此步（沿用上一步結果）", "Step skipped (the previous step's result is used)");
                StatusText.Foreground = amber;
            }
            if (StatusFooter != null) StatusFooter.Visibility = Visibility.Visible;
        }

        private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;
            if (_parent.IsInitialNode(this)) return;

            if (_parent.HasOutgoingConnections(this))
            {
                var ok = MenuConfirmDialog.ShowDeleteConfirm(
                    owner: _parent,
                    title: UiLanguage.P("確認刪除", "Confirm delete"),
                    message: UiLanguage.P("當前區塊不是末端區塊，是否確認刪除？", "This block isn't the last one. Delete it anyway?"),
                    resourceHost: _parent);

                if (!ok) return;
            }

            _parent.DeleteNodeAndDescendants(this);
        }
    }
}
