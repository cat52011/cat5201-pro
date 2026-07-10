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
    // MainWindow 部分類別 — §18 Google Drive 整合(連結/自動上傳/選檔附件)
    // （P1-2 機械拆分:內容自 MainWindow.xaml.cs 原樣搬出,零邏輯變更。）
    public partial class MainWindow
    {

        // ===== §18 Google Drive 整合 =====
        private bool _googleAutoUploadDrive; // 個人化：產出檔案自動上傳 Drive（預設關）

        // 個人化：哪些「檔案類型」要上傳（各類型可個別開關）。
        private static readonly string[] AllDriveTypes = { "pdf", "word", "ppt", "excel", "image", "video", "text" };
        private HashSet<string> _driveUploadTypes = new(AllDriveTypes);
        private bool _driveConvertGoogle; // DOCX/XLSX 上傳時轉成 Google 文件/試算表格式

        private static string DriveCategoryOf(string path) =>
            System.IO.Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pdf" => "pdf",
                ".docx" => "word",
                ".pptx" => "ppt",
                ".xlsx" => "excel",
                ".png" or ".jpg" or ".jpeg" or ".webp" => "image",
                ".mp4" => "video",
                _ => "text", // .md / .txt 及其他
            };

        private GoogleDriveService? _googleDrive;

        // 依開關與憑證狀態掛/卸產檔後鉤子。上傳走背景執行緒 fire-and-forget，成敗都留診斷日誌。
        private void WireDrivePostWriteHook()
        {
            // 必須「已連結」才掛鉤子——避免背景上傳觸發互動式授權（從背景執行緒彈瀏覽器）。
            if (_googleAutoUploadDrive && GoogleDrive.IsConfigured && GoogleDrive.IsAuthorized)
            {
                GeneratedFileWriter.PostWriteHook = path =>
                {
                    // 個人化：各檔案類型可個別開關（讀即時欄位值，改設定立即生效、免重掛鉤子）。
                    if (!_driveUploadTypes.Contains(DriveCategoryOf(path)))
                        return;

                    // 個人化：DOCX/XLSX 可轉成 Google 文件/試算表格式（Drive 端轉檔）。
                    string? convertTo = !_driveConvertGoogle
                        ? null
                        : System.IO.Path.GetExtension(path).ToLowerInvariant() switch
                        {
                            ".docx" => GoogleDriveService.GoogleDocMime,
                            ".xlsx" => GoogleDriveService.GoogleSheetMime,
                            _ => null,
                        };

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var (id, link) = await GoogleDrive.UploadFileAsync(path, CancellationToken.None, convertTo);
                            AppLog.Info("GoogleDrive",
                                $"已自動上傳：{System.IO.Path.GetFileName(path)}{(convertTo != null ? "（已轉 Google 格式）" : "")} → {link}");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Warn("GoogleDrive", $"自動上傳失敗：{System.IO.Path.GetFileName(path)}", ex);
                        }
                    });
                };
            }
            else
            {
                GeneratedFileWriter.PostWriteHook = null;
            }
        }

        private async void GoogleAuth_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!GoogleDrive.IsConfigured)
                {
                    MenuConfirmDialog.ShowMessage(this, "Google 整合",
                        "尚未設定憑證。請先到 Google Cloud Console 建立「桌面應用程式」OAuth 用戶端，\n" +
                        "然後設定環境變數 GOOGLE_OAUTH_CLIENT_ID 與 GOOGLE_OAUTH_CLIENT_SECRET，重啟本程式。", this);
                    return;
                }
                await GoogleDrive.AuthorizeAsync(CancellationToken.None);
                string linkedEmail = "";
                try { linkedEmail = await GoogleDrive.GetUserEmailAsync(CancellationToken.None); } catch { }
                RefreshGoogleStatus();
                WireDrivePostWriteHook();
                MenuConfirmDialog.ShowMessage(this, "Google 整合",
                    string.IsNullOrWhiteSpace(linkedEmail) ? "✓ 已連結 Google 帳戶。" : $"✓ 已連結 {linkedEmail}。", this);
            }
            catch (Exception ex)
            {
                AppLog.Warn("GoogleDrive", "授權失敗", ex);
                MenuConfirmDialog.ShowMessage(this, "Google 整合", "連結失敗：" + ex.Message, this);
            }
        }

        private void GoogleUnlink_Click(object sender, RoutedEventArgs e)
        {
            GoogleDrive.Unlink();
            RefreshGoogleStatus();
            WireDrivePostWriteHook();
        }

        private void GoogleAutoUploadSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            _googleAutoUploadDrive = GoogleAutoUploadSwitch?.IsChecked == true;
            SavePreferences();
            WireDrivePostWriteHook();
        }

        // 各檔案類型的上傳開關（一個 handler 讀全部勾選框）。
        private void DriveTypeCheck_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;

            var set = new HashSet<string>();
            foreach (var cb in new[] { DriveTypePdf, DriveTypeWord, DriveTypePpt, DriveTypeExcel, DriveTypeImage, DriveTypeVideo, DriveTypeText })
            {
                if (cb?.IsChecked == true && cb.Tag is string tag)
                    set.Add(tag);
            }
            _driveUploadTypes = set;
            SavePreferences(); // 鉤子讀即時欄位值，免重掛
        }

        private void DriveConvertSwitch_Changed(object sender, RoutedEventArgs e)
        {
            if (_syncingCostControls)
                return;
            _driveConvertGoogle = DriveConvertSwitch?.IsChecked == true;
            SavePreferences();
        }

        private bool _fetchingGoogleEmail;

        /// <summary>NodeControl 附件鈕用：Drive 是否已可用（有憑證且已連結）。</summary>
        public bool IsGoogleDriveLinked() => GoogleDrive.IsConfigured && GoogleDrive.IsAuthorized;

        /// <summary>
        /// §18：從 Drive 選檔 → 下載到暫存 → 走既有附件流程加進節點。回傳是否有加入任何附件。
        /// </summary>
        public async Task<bool> AttachFromDriveAsync(NodeControl node)
        {
            try
            {
                var files = await GoogleDrive.ListFilesAsync(CancellationToken.None);
                var picker = new DriveFilePickerDialog(this, files);
                if (picker.ShowDialog() != true || picker.Selected.Count == 0)
                    return false;

                string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cat5201-drive");
                Directory.CreateDirectory(tempDir);

                var localPaths = new List<string>();
                foreach (var (id, name, mime) in picker.Selected)
                {
                    string safe = string.Concat(name.Split(System.IO.Path.GetInvalidFileNameChars()));

                    // P3-3：Google 原生格式（Docs/Sheets/Slides）走 export 轉純文字/CSV 當輸入;
                    // 一般檔案照舊直接下載。轉出的 .txt/.csv 自然流進既有附件文字管線。
                    var plan = GoogleDriveService.GetExportPlan(mime);
                    if (plan is { } p)
                    {
                        string baseName = System.IO.Path.GetFileNameWithoutExtension(safe);
                        string local = System.IO.Path.Combine(tempDir, baseName + p.Ext);
                        await GoogleDrive.ExportFileAsync(id, p.ExportMime, local, CancellationToken.None);
                        localPaths.Add(local);
                    }
                    else
                    {
                        string local = System.IO.Path.Combine(tempDir, safe);
                        await GoogleDrive.DownloadFileAsync(id, local, CancellationToken.None);
                        localPaths.Add(local);
                    }
                }

                AddAttachmentsForNode(node, localPaths); // 既有流程會複製進專案附件資料夾
                AppLog.Info("GoogleDrive", $"從 Drive 附加 {localPaths.Count} 個檔案");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Warn("GoogleDrive", "從 Drive 附加檔案失敗", ex);
                MenuConfirmDialog.ShowMessage(this, "Google Drive", "從 Drive 取檔失敗：" + ex.Message, this);
                return false;
            }
        }

        private void RefreshGoogleStatus()
        {
            if (GoogleStatusText == null) return;

            if (!GoogleDrive.IsConfigured)
            {
                GoogleStatusText.Text = "狀態：未設定憑證（App 內 API 分頁或環境變數 GOOGLE_OAUTH_CLIENT_ID / GOOGLE_OAUTH_CLIENT_SECRET）";
                return;
            }
            if (!GoogleDrive.IsAuthorized)
            {
                GoogleStatusText.Text = "狀態：已設定憑證，尚未連結（按「連結 Google」完成授權）";
                return;
            }

            // 已連結：顯示帳戶信箱。沒快取先顯示通用文字，背景抓一次再更新。
            string? email = GoogleDrive.CachedEmail;
            GoogleStatusText.Text = string.IsNullOrWhiteSpace(email)
                ? "狀態：✓ 已連結 Google 帳戶"
                : $"狀態：✓ 已連結 {email}";

            if (string.IsNullOrWhiteSpace(email) && !_fetchingGoogleEmail)
            {
                _fetchingGoogleEmail = true;
                _ = FetchGoogleEmailAsync();
            }
        }

        private async Task FetchGoogleEmailAsync()
        {
            try
            {
                string email = await GoogleDrive.GetUserEmailAsync(CancellationToken.None);
                if (!string.IsNullOrWhiteSpace(email) && GoogleStatusText != null)
                    GoogleStatusText.Text = $"狀態：✓ 已連結 {email}"; // await 後回到 UI 執行緒（無 ConfigureAwait(false)）
            }
            catch (Exception ex)
            {
                AppLog.Warn("GoogleDrive", "取帳戶信箱失敗（顯示通用文字）", ex);
            }
            finally
            {
                _fetchingGoogleEmail = false;
            }
        }
    }
}
