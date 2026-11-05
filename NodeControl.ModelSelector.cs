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
    // NodeControl 部分類別 — 節點上的模型選單與編輯模式（自 NodeControl.xaml.cs 原樣搬出）
    public partial class NodeControl
    {
        // ===== 新增：對外可取目前已提交模型 =====
        public string GetCommittedModelId()
            => AiModelHelper.NormalizeNodeModel(_committedModelId);

        // ===== 新增：外部可設定已提交模型（例如載入專案時）=====
        public void SetCommittedModelId(string modelId, bool syncEditingModel = true)
        {
            string normalized = NormalizeSafeModelId(modelId);

            _committedModelId = normalized;
            if (syncEditingModel)
                _editingModelId = normalized;

            RefreshModelSelectionUI();
        }

        internal void EnterEditMode()
        {
            _isTopLocked = false;

            InitializeCommittedModelIfNeeded();

            // 進入編輯時，草稿模型 = 上次正式送出的模型
            _editingModelId = _committedModelId;

            TopEditor.IsReadOnly = false;
            TopEditor.Focus();
            TopEditor.CaretIndex = TopEditor.Text?.Length ?? 0;

            EnsureModelSelectorLoaded();
            RefreshModelSelectionUI();
            UpdateAutoTaskPreview();
            UpdateEditButtons();
        }

        internal void ForceExitEditMode()
        {
            RevertEditingModelToCommitted();

            TopEditor.IsReadOnly = true;
            RefreshModelSelectionUI();
            UpdateAutoTaskPreview();
            UpdateEditButtons();

            if (!IsMouseOver)
                ResetHoverVisualStateImmediately();
        }

        internal void EndEditBecauseSent()
        {
            TopEditor.IsReadOnly = true;
            RefreshModelSelectionUI();
            UpdateAutoTaskPreview();
            UpdateEditButtons();
            _parent?.NotifyEditEnded(this);
        }

        private void EnsureModelSelectorLoaded()
        {
            if (_modelsLoaded || ModelSelector == null)
                return;

            LoadModelsFromRegistry();
            _modelsLoaded = true;
        }

        private void NotifyConnectionLayoutChanged()
        {
            Moved?.Invoke(this, EventArgs.Empty);
        }

        private void LoadModelsFromRegistry()
        {
            if (ModelSelector == null)
                return;

            string currentSelectedId = GetSelectedModelIdFromComboBox();

            _isSyncingModelSelector = true;
            try
            {
                ModelSelector.ItemsSource = null;
                // Multi-Model v1：只列已啟用模型（休眠擴充點如 Gemini 不顯示）。
                ModelSelector.ItemsSource = AiModelRegistry.Available;
            }
            finally
            {
                _isSyncingModelSelector = false;
            }

            SelectModelInComboBox(currentSelectedId);
        }

        private string GetSelectedModelIdFromComboBox()
        {
            if (ModelSelector?.SelectedItem is AiModelDefinition model &&
                !string.IsNullOrWhiteSpace(model.Id))
            {
                return model.Id.Trim();
            }

            return AiModels.DefaultNodeModel;
        }

        private void SelectModelInComboBox(string modelId)
        {
            if (ModelSelector == null)
                return;

            modelId = NormalizeSafeModelId(modelId);

            _isSyncingModelSelector = true;
            try
            {
                var available = AiModelRegistry.Available;

                var match = available.FirstOrDefault(x =>
                    string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    ModelSelector.SelectedItem = match;
                    return;
                }

                if (available.Count > 0)
                    ModelSelector.SelectedItem = available[0];
            }
            finally
            {
                _isSyncingModelSelector = false;
            }
        }

        // ===== 新增：安全正規化 =====
        private static string NormalizeSafeModelId(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return AiModels.DefaultNodeModel;

            return AiModelHelper.NormalizeNodeModel(modelId);
        }

        // ===== 新增：初始化正式模型 =====
        private void InitializeCommittedModelIfNeeded()
        {
            if (_parent == null)
            {
                _committedModelId = NormalizeSafeModelId(_committedModelId);
                _editingModelId = NormalizeSafeModelId(_editingModelId);
                return;
            }

            bool committedMissing = string.IsNullOrWhiteSpace(_committedModelId);
            bool editingMissing = string.IsNullOrWhiteSpace(_editingModelId);

            if (committedMissing)
            {
                string stored = _parent.GetNodeSelectedModel(this);
                _committedModelId = NormalizeSafeModelId(stored);
            }
            else
            {
                _committedModelId = NormalizeSafeModelId(_committedModelId);
            }

            if (editingMissing)
            {
                _editingModelId = _committedModelId;
            }
            else
            {
                _editingModelId = NormalizeSafeModelId(_editingModelId);
            }
        }

        // ===== 新增：未送出離開編輯時回復 =====
        private void RevertEditingModelToCommitted()
        {
            _editingModelId = _committedModelId;
        }

        // ===== 新增：真正送出時提交模型 =====
        private void CommitEditingModel()
        {
            _committedModelId = NormalizeSafeModelId(_editingModelId);

            if (_parent != null)
            {
                _parent.SetNodeSelectedModel(this, _committedModelId);
            }
        }

        internal void RefreshModelSelectionUI()
        {
            EnsureModelSelectorLoaded();
            InitializeCommittedModelIfNeeded();

            if (_parent != null && !_parent.IsAutoModelSelectionEnabled() && IsEditing)
                RevertEditingModelToCommitted();

            // 編輯狀態顯示草稿模型；非編輯狀態顯示正式模型
            string displayModel = IsEditing ? _editingModelId : _committedModelId;
            SelectModelInComboBox(displayModel);

            // 協作模式要看得出來：模型選單只顯示一個，實際上會有好幾個模型一起回答。
            var collaborators = _parent?.GetNodeCollaborationModels(this) ?? Array.Empty<string>();
            bool collaborating = ModelCollaboration.IsActive(collaborators);
            string names = string.Join("、", collaborators.Select(id => AiModelRegistry.Find(id)?.DisplayName ?? id));
            if (ModelSelector != null)
                ModelSelector.ToolTip = collaborating ? UiLanguage.P($"協作模式：{names} 一起回答（右鍵 → 協作模型 可修改）", $"Collaboration: {names} answer together (right-click → Collaborating models to change)") : null;
            if (CollaborationMenuItem != null)
                CollaborationMenuItem.Header = collaborating ? UiLanguage.P($"協作模型…（{collaborators.Count} 個）", $"Collaborating models… ({collaborators.Count})") : UiLanguage.P("協作模型…", "Collaborating models…");

            UpdateEditButtons();
        }

        private void UpdateEditButtons()
        {
            bool editable = (TopEditor != null && TopEditor.IsReadOnly == false);

            if (PlusButton != null)
            {
                PlusButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
                PlusButton.IsEnabled = editable;
            }

            if (SendButton != null)
            {
                SendButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
                SendButton.IsEnabled = editable && !_isGenerating;
            }

            if (ModelSelector != null)
            {
                ModelSelector.Visibility = Visibility.Visible;

                bool canManualSelect =
                    _parent != null &&
                    _parent.CanUserManuallySelectModel();

                ModelSelector.IsEnabled = editable && !_isGenerating && canManualSelect;

                // §13 Manual/Auto 提示：清楚說明此節點的模型是「自己選的」還是「系統自動挑的」。
                ModelSelector.ToolTip = canManualSelect
                    ? UiLanguage.P("🛠 手動模式：可自行選擇此區塊使用的模型", "🛠 Manual mode: choose the model for this block yourself")
                    : UiLanguage.P("🤖 自動模式：系統依內容自動挑模型（要手動選請到個人化關閉自動）", "🤖 Auto mode: the model is picked from the content (turn off Auto on the Personal tab to choose manually)");
            }

            RefreshAttachmentsUI();
        }

        private void SyncModelSelectorFromParent()
        {
            EnsureModelSelectorLoaded();

            if (_parent == null || ModelSelector == null)
                return;

            // 自動模式下：
            // 編輯狀態顯示依當前文字推算的暫時模型，但不覆蓋 committed
            // 非編輯狀態永遠顯示 committed model
            if (IsEditing)
            {
                string model = _parent.GetEffectiveNodeModel(this, TopEditor?.Text ?? "");
                _editingModelId = NormalizeSafeModelId(model);
                SelectModelInComboBox(_editingModelId);
            }
            else
            {
                SelectModelInComboBox(_committedModelId);
            }
        }

        private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingModelSelector) return;
            if (_parent == null) return;
            if (ModelSelector.SelectedItem is not AiModelDefinition model) return;
            if (string.IsNullOrWhiteSpace(model.Id)) return;

            string selectedId = NormalizeSafeModelId(model.Id);

            if (!_parent.CanUserManuallySelectModel())
            {
                // 自動模式下不允許手動改正式值，直接回到目前應顯示的值
                SyncModelSelectorFromParent();
                return;
            }

            // 只改編輯草稿模型，不改正式模型
            if (IsEditing)
            {
                _editingModelId = selectedId;
            }
            else
            {
                // 非編輯狀態通常不應該改，但保險起見仍維持正式值
                SelectModelInComboBox(_committedModelId);
                return;
            }
        }
    }
}
