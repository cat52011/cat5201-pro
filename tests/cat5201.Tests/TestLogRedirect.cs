using System;
using System.IO;
using Xunit;

[assembly: Xunit.TestFramework("Cat5201.Tests.TestLogRedirect", "cat5201.Tests")]

namespace Cat5201.Tests
{
    /// <summary>
    /// 測試一跑起來就把 AppLog 導到暫存資料夾。
    ///
    /// 不這樣做的話，測試會寫進使用者真實的 %LOCALAPPDATA%\cat5201-pro\logs，
    /// 日誌裡於是混著「餘額設定為 US$100」「+US$25（AI 呼叫・claude-opus-5）」這些假帳。
    /// 2026-10-13 查「餘額跟官網差多少」時，第一件事就是花時間分辨哪幾行是真的——
    /// 日誌是診斷工具，被自己的測試污染就失去意義。
    /// </summary>
    public sealed class TestLogRedirect : Xunit.Sdk.XunitTestFramework
    {
        public TestLogRedirect(Xunit.Abstractions.IMessageSink messageSink)
            : base(messageSink)
        {
            Cat5201.AppLog.RedirectForTests(
                Path.Combine(Path.GetTempPath(), "cat5201-test-logs"));
        }
    }

    public class TestLogRedirectTests
    {
        [Fact]
        public void 測試過程的日誌不會寫進使用者的資料夾()
        {
            string userDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "cat5201-pro", "logs");

            AppLog.Info("TestRedirect", "這一行必須落在暫存資料夾，不是使用者的日誌");

            Assert.NotNull(AppLog.DirectoryPath);
            Assert.False(
                string.Equals(Path.GetFullPath(AppLog.DirectoryPath!).TrimEnd('\\'),
                              Path.GetFullPath(userDir).TrimEnd('\\'),
                              StringComparison.OrdinalIgnoreCase),
                $"測試的日誌寫進了使用者的資料夾：{AppLog.DirectoryPath}");
        }
    }
}
