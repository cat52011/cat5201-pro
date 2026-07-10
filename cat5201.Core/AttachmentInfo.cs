namespace Cat5201
{
    /// <summary>
    /// 附件的中繼資料（檔名/專案內相對路徑/MIME/種類）。
    /// B3b：自 MainWindow 巢狀類提出到 Core——它是專案存檔與執行請求的共用 DTO，
    /// 不該綁在 UI 類底下。屬性名不變 → 舊專案 JSON 完全相容。
    /// </summary>
    public sealed class AttachmentInfo
    {
        public string FileName { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string MimeType { get; set; } = "application/octet-stream";
        public string Kind { get; set; } = "file";
    }
}
