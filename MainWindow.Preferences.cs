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
    // MainWindow 部分類別 — 全域個人化偏好(UserPreferencesState/存讀/遷移)
    // （P1-2 機械拆分:內容自 MainWindow.xaml.cs 原樣搬出,零邏輯變更。）
    public partial class MainWindow
    {

        // 全域個人化偏好：與「專案檔」分離，存成單一檔案，跨專案、跨重啟一致。
        // 開新專案或開啟舊專案都不會覆蓋這些設定——它們是「使用者的」，不是「某個檔案的」。
        internal record UserPreferencesState(
            bool AutoModelSelectionEnabled = false,
            bool AdvancedAutoResolverEnabled = false,
            string DownstreamAutoMode = "OneClick",
            string PresentationEngine = "Claude",
            Dictionary<string, string>? TaskRoutingOverrides = null,
            bool BlockOpus = false,
            bool BlockDeepResearch = false,
            int ManualTimeoutSeconds = 0,
            string VideoStyleOverride = "",
            string VideoModelTier = "Lite",
            bool ReadUpstreamAttachments = false,
            bool MobileMirrorEnabled = false,
            List<SkillDefinition>? Skills = null,
            // MVP 安全預設（只影響「沒有偏好檔」的新使用者；既有 _preferences.json 存的值一律優先）：
            //  DailyBudgetTwd 100＝新使用者預設每日 NT$100 上限（0＝不限制，太危險不當預設）；
            //  AutoConfirmSeconds 0＝產檔/媒體一律等人按（硬批准），要倒數自動同意得自己開。
            int DailyBudgetTwd = 100,
            int AutoConfirmSeconds = 0,
            bool GoogleAutoUploadDrive = false,
            List<string>? DriveUploadTypes = null,           // null＝全類型（向後相容）
            bool DriveConvertToGoogleFormat = false,
            string LastProjectPath = "",                     // P2-2 會話還原：最後開啟的專案
            string DocumentEngine = "ClaudeSkills"           // 文件產出引擎（預設 Claude 文件技能）
        );

        // ===== P2-2 會話還原：上次未正常關閉 → 提示開回最後的專案 =====
        private string _lastProjectPathFromPrefs = "";
        private string SessionLockPath => System.IO.Path.Combine(SavesDir, "_config", "_session.lock");

        /// <summary>啟動時呼叫：偵測上次是否未正常關閉，並寫入本次的 marker。回傳是否偵測到崩潰。</summary>
        private bool DetectCrashAndWriteSessionLock()
        {
            bool crashed = false;
            try
            {
                crashed = File.Exists(SessionLockPath);
                var dir = System.IO.Path.GetDirectoryName(SessionLockPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(SessionLockPath, DateTime.Now.ToString("o"));
            }
            catch (Exception ex) { AppLog.Warn("Session", "session marker 處理失敗", ex); }
            return crashed;
        }

        /// <summary>崩潰偵測後的還原提示（在 UI 就緒後呼叫）。</summary>
        private void OfferSessionRestoreIfCrashed(bool crashed)
        {
            if (!crashed || string.IsNullOrWhiteSpace(_lastProjectPathFromPrefs) || !File.Exists(_lastProjectPathFromPrefs))
                return;

            AppLog.Info("Session", "偵測到上次未正常關閉，提示還原：" + _lastProjectPathFromPrefs);
            bool yes = MenuConfirmDialog.ShowConfirm(
                this,
                "還原上次的工作？",
                $"偵測到程式上次未正常關閉。\n要開啟上次的專案「{DisplayNameFromPath(_lastProjectPathFromPrefs)}」嗎？",
                this,
                confirmText: "開啟",
                cancelText: "不用");
            if (yes)
                LoadState(_lastProjectPathFromPrefs);
        }

        // 正常關閉：刪除 marker（沒刪到＝下次啟動視為崩潰）。
        protected override void OnClosed(EventArgs e)
        {
            try { File.Delete(SessionLockPath); } catch { }
            base.OnClosed(e);
        }

        // 全域個人化偏好放在子資料夾，永遠不會被 SavesDir 的 *.json 專案掃描列舉到（非遞迴），
        // 因此不可能出現在檔案清單、也不可能被當成專案誤開而崩潰。
        private string PreferencesPath => System.IO.Path.Combine(SavesDir, "_config", "_preferences.json");
        // 舊版把偏好直接放在 SavesDir 根目錄；保留路徑以便一次性搬移。
        private string LegacyPreferencesPath => System.IO.Path.Combine(SavesDir, "_preferences.json");

        private void RefreshFileList()
        {
            var prefName = System.IO.Path.GetFileName(PreferencesPath);
            var items = Directory.GetFiles(SavesDir, "*.json")
                                 .Where(p => !string.Equals(System.IO.Path.GetFileName(p), prefName, StringComparison.OrdinalIgnoreCase))
                                 .OrderByDescending(File.GetCreationTime)
                                 .Select(p => new FileItem(p))
                                 .ToList();

            FileList.ItemsSource = items;

            if (!string.IsNullOrEmpty(_currentFilePath))
                SelectFileInList(_currentFilePath);
        }

        // 全域個人化偏好寫檔。不受 _hasStarted 限制——設定面板任何變更都會即時落地。
        private void SavePreferences()
        {
            try
            {
                var prefs = new UserPreferencesState(
                    AutoModelSelectionEnabled: _isAutoModelSelectionEnabled,
                    AdvancedAutoResolverEnabled: _isAdvancedAutoResolverEnabled,
                    DownstreamAutoMode: DownstreamAutoModeHelper.ToStorageValue(_downstreamAutoMode),
                    PresentationEngine: PresentationEngineHelper.ToStorageValue(_presentationEngine),
                    TaskRoutingOverrides: _taskRoutingOverrides.ToStorage(),
                    BlockOpus: AiAutoCostPolicy.BlockOpus,
                    BlockDeepResearch: AiAutoCostPolicy.BlockDeepResearch,
                    ManualTimeoutSeconds: _manualTimeoutSeconds,
                    VideoStyleOverride: _videoStyleOverride ?? "",
                    VideoModelTier: VeoModels.ToStorageValue(_videoModelTier),
                    ReadUpstreamAttachments: _readUpstreamAttachments,
                    MobileMirrorEnabled: _mobileMirrorEnabled,
                    Skills: new List<SkillDefinition>(SkillsRegistry.GetAll()),
                    DailyBudgetTwd: _dailyBudgetTwd,
                    AutoConfirmSeconds: _autoConfirmSeconds,
                    GoogleAutoUploadDrive: _googleAutoUploadDrive,
                    DriveUploadTypes: _driveUploadTypes.ToList(),
                    DriveConvertToGoogleFormat: _driveConvertGoogle,
                    LastProjectPath: _currentFilePath ?? "",
                    DocumentEngine: DocumentEngineHelper.ToStorageValue(_documentEngine)
                );

                var dir = System.IO.Path.GetDirectoryName(PreferencesPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(prefs, new JsonSerializerOptions { WriteIndented = true });
                AtomicFile.WriteAllText(PreferencesPath, json); // 原子寫入：斷電/崩潰不毀個人化，舊檔留 .bak
            }
            catch (Exception ex)
            {
                // 偏好寫檔失敗不應中斷主流程；下次變更會再試。留痕以便診斷「設定沒存到」。
                AppLog.Warn("Preferences", "SavePreferences 寫檔失敗", ex);
            }
        }

        // 啟動時載入全域個人化偏好，套用到記憶體狀態（在顯示 UI / 同步開關之前呼叫）。
        private void LoadPreferences()
        {
            try
            {
                // 一次性搬移：舊版偏好檔在 SavesDir 根目錄，移進 _config 子資料夾並刪掉舊檔，
                // 避免它繼續出現在使用者的存檔資料夾裡。
                if (!File.Exists(PreferencesPath) && File.Exists(LegacyPreferencesPath))
                {
                    try
                    {
                        var dir = System.IO.Path.GetDirectoryName(PreferencesPath);
                        if (!string.IsNullOrEmpty(dir))
                            Directory.CreateDirectory(dir);
                        File.Move(LegacyPreferencesPath, PreferencesPath);
                    }
                    catch
                    {
                        // 搬移失敗就盡量刪掉舊檔，至少不要再被誤當專案。
                        try { File.Delete(LegacyPreferencesPath); } catch { }
                    }
                }

                if (!File.Exists(PreferencesPath))
                {
                    // 首次升級遷移：個人化設定以前存在各專案檔裡，現在改成全域。
                    // 把「最近一次專案檔」的設定帶過來，使用者不必重設一次。
                    TrySeedPreferencesFromLatestProject();
                    return;
                }

                var prefs = JsonSerializer.Deserialize<UserPreferencesState>(File.ReadAllText(PreferencesPath));
                if (prefs == null)
                    return;

                _isAutoModelSelectionEnabled = prefs.AutoModelSelectionEnabled;
                _isAdvancedAutoResolverEnabled = _isAutoModelSelectionEnabled && prefs.AdvancedAutoResolverEnabled;
                _downstreamAutoMode = DownstreamAutoModeHelper.Parse(prefs.DownstreamAutoMode);

                // Gamma 已開放：沒設 GAMMA_API_KEY 時執行期自動 fallback 內建 builder（GammaPresentationService.NotConfigured）。
                _presentationEngine = PresentationEngineHelper.Parse(prefs.PresentationEngine);

                _taskRoutingOverrides.LoadFromStorage(prefs.TaskRoutingOverrides);
                AiAutoCostPolicy.BlockOpus = prefs.BlockOpus;
                AiAutoCostPolicy.BlockDeepResearch = prefs.BlockDeepResearch;
                _manualTimeoutSeconds = prefs.ManualTimeoutSeconds;
                _videoStyleOverride = prefs.VideoStyleOverride ?? "";
                _videoModelTier = VeoModels.ParseTier(prefs.VideoModelTier);
                _readUpstreamAttachments = prefs.ReadUpstreamAttachments;
                _mobileMirrorEnabled = prefs.MobileMirrorEnabled;
                SkillsRegistry.SetAll(prefs.Skills);   // §19：技能載入執行期單一真相
                _dailyBudgetTwd = Math.Max(0, prefs.DailyBudgetTwd);
                _autoConfirmSeconds = Math.Clamp(prefs.AutoConfirmSeconds, 0, 300);
                _googleAutoUploadDrive = prefs.GoogleAutoUploadDrive;
                _driveUploadTypes = prefs.DriveUploadTypes == null
                    ? new HashSet<string>(AllDriveTypes)            // 舊偏好檔：預設全開
                    : new HashSet<string>(prefs.DriveUploadTypes);
                _driveConvertGoogle = prefs.DriveConvertToGoogleFormat;
                _lastProjectPathFromPrefs = prefs.LastProjectPath ?? "";
                _documentEngine = DocumentEngineHelper.Parse(prefs.DocumentEngine);
            }
            catch (Exception ex)
            {
                // 偏好檔毀損時忽略，沿用預設值。留痕以便診斷「個人化被重置」。
                AppLog.Warn("Preferences", "LoadPreferences 讀取失敗，沿用預設值", ex);
            }
        }

        // 一次性遷移：沒有全域偏好檔時，從最近一次專案檔讀回個人化設定並寫成全域偏好。
        private void TrySeedPreferencesFromLatestProject()
        {
            try
            {
                var latest = Directory.GetFiles(SavesDir, "*.json")
                    .Where(p => !string.Equals(
                        System.IO.Path.GetFileName(p),
                        System.IO.Path.GetFileName(PreferencesPath),
                        StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTime)
                    .FirstOrDefault();

                if (latest == null)
                    return;

                var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(latest));
                if (state == null)
                    return;

                _isAutoModelSelectionEnabled = state.AutoModelSelectionEnabled;
                _isAdvancedAutoResolverEnabled = _isAutoModelSelectionEnabled && state.AdvancedAutoResolverEnabled;
                _downstreamAutoMode = DownstreamAutoModeHelper.Parse(state.DownstreamAutoMode);

                _presentationEngine = PresentationEngineHelper.Parse(state.PresentationEngine);

                _taskRoutingOverrides.LoadFromStorage(state.TaskRoutingOverrides);
                AiAutoCostPolicy.BlockOpus = state.BlockOpus;
                AiAutoCostPolicy.BlockDeepResearch = state.BlockDeepResearch;
                _manualTimeoutSeconds = state.ManualTimeoutSeconds;

                SavePreferences(); // 立刻落地成全域偏好，下次起點即一致。
            }
            catch
            {
                // 遷移失敗就沿用預設，不影響啟動。
            }
        }

        private GoogleDriveService GoogleDrive =>
            _googleDrive ??= new GoogleDriveService(System.IO.Path.GetDirectoryName(PreferencesPath) ?? SavesDir);
    }
}
