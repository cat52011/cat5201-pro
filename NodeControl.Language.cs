namespace Cat5201
{
    // NodeControl 部分類別 — 切換介面語言後，重新套用由程式碼填進去的字。
    public partial class NodeControl
    {
        /// <summary>XAML 上的字會自己換；這裡只處理程式碼設定的提示與按鈕文字。</summary>
        public void RefreshLocalizedText()
        {
            RefreshModelSelectionUI();   // 協作模型選單、模型提示
            UpdateEditButtons();         // 手動／自動模式提示
        }
    }
}
