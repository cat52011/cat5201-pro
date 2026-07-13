using System.Text.Json;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 守住個人化最關鍵的 invariant：新增偏好欄位後，舊的 _preferences.json（缺新欄位）仍能安全載入用預設值，
    /// 既有值不被吃掉。使用者明確要求「一切以個人化為主、永不被覆蓋、重啟不被修改」。
    /// </summary>
    public class UserPreferencesStateTests
    {
        [Fact]
        public void OldPrefsFile_MissingNewFields_LoadsWithSafeDefaults()
        {
            // 模擬「加手機鏡像/上游附件欄位之前」的舊偏好檔：完全沒有那些新欄位。
            const string oldJson = """
            {
                "AutoModelSelectionEnabled": true,
                "ManualTimeoutSeconds": 120,
                "VideoModelTier": "Standard"
            }
            """;

            var prefs = JsonSerializer.Deserialize<MainWindow.UserPreferencesState>(oldJson);

            Assert.NotNull(prefs);
            // 舊值保留
            Assert.True(prefs!.AutoModelSelectionEnabled);
            Assert.Equal(120, prefs.ManualTimeoutSeconds);
            Assert.Equal("Standard", prefs.VideoModelTier);
            // 新欄位安全退回預設（不會炸、更不會擅自開對外埠）
            Assert.False(prefs.MobileMirrorEnabled);
            Assert.False(prefs.ReadUpstreamAttachments);
            // MVP 安全預設（釘住，防止回歸）：新使用者預設每日 NT$100 上限、產檔硬批准（不倒數自動同意）。
            Assert.Equal(100, prefs.DailyBudgetTwd);
            Assert.Equal(0, prefs.AutoConfirmSeconds);
        }

        [Fact]
        public void RoundTrip_PreservesValues()
        {
            var original = new MainWindow.UserPreferencesState(
                AutoModelSelectionEnabled: true,
                ManualTimeoutSeconds: 300,
                MobileMirrorEnabled: true);

            var json = JsonSerializer.Serialize(original);
            var restored = JsonSerializer.Deserialize<MainWindow.UserPreferencesState>(json);

            Assert.Equal(original, restored); // record 值相等：所有欄位一致
        }
    }
}
