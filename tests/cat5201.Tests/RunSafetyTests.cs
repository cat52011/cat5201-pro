using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-14 外部審查指出的三條底線：
    /// 重跑失敗仍保有舊成果、失敗的節點不能帶動下游、任何專案都碰不到別案的同名檔。
    /// 另外一條：診斷檔裡的日誌不能帶提問內容。
    /// </summary>
    public class RunSafetyTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "cat5201-runsafety-" + Guid.NewGuid().ToString("N"));

        public RunSafetyTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        private string MakeFile(string relative, string content = "x")
        {
            string path = Path.Combine(_dir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }

        /// <summary>在有訊息迴圈的 STA 執行緒上跑（節點是 WPF 控制項，執行流程裡有 await Dispatcher.Yield）。</summary>
        private static void OnUiThread(Func<Task> body)
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                dispatcher.InvokeAsync(async () =>
                {
                    try { await body(); }
                    catch (Exception ex) { failure = ex; }
                    finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
                });
                Dispatcher.Run();
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "UI 執行緒逾時");
            if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        }

        // ── 失敗的一輪：回報失敗（下游不會跑），上一輪的檔案還在磁碟上、也還掛在節點上 ──
        [Fact]
        public void AFailedRerun_ReportsFailure_AndKeepsLastRunsFiles()
        {
            string report = MakeFile(@"甲\分析報告.docx", "昨天的報告");

            OnUiThread(async () =>
            {
                var node = new NodeControl();
                node.RestoreOutputFiles(new[] { report });
                node.SetTopText("完成此作業");

                // 沒有主視窗＝這一輪一定失敗，而且失敗訊息（「找不到主視窗…」）不是舊檢查認得的三種開頭——
                // 舊版會把它當成成功，讓下游拿錯誤訊息繼續跑。
                bool success = await node.RunCurrentTopTextAsync();

                Assert.False(success);
                Assert.False(string.IsNullOrWhiteSpace(node.GetBottomText()));   // 畫面上確實有字，但那是錯誤訊息
                Assert.Contains(report, node.GetOutputFilePaths());
            });

            Assert.True(File.Exists(report));
            Assert.Equal("昨天的報告", File.ReadAllText(report));
        }

        // ── 成功的一輪：沿用的檔案留著，新的一輪不再需要的舊檔才刪 ──
        [Fact]
        public void ASuccessfulRerun_DeletesOnlyTheOldFilesItReplaced()
        {
            string oldReport = MakeFile(@"甲\分析報告.docx", "舊");
            string kept = MakeFile(@"甲\資料.xlsx", "沿用");

            OnUiThread(() =>
            {
                var node = new NodeControl();
                node.RestoreOutputFiles(new[] { oldReport, kept });

                node.SetAsidePreviousOutputs();
                // 重跑中、還沒有新檔：存檔要記得舊成果，不能把連結洗掉
                Assert.Equal(new[] { oldReport, kept }.OrderBy(x => x), node.GetOutputFilePaths().OrderBy(x => x));

                string newReport = MakeFile(@"甲\分析報告_2.docx", "新");
                node.SetOutputFiles(new List<GeneratedFilePayload>
                {
                    new() { Success = true, FilePath = newReport, FileName = "分析報告_2.docx" },
                    new() { Success = true, FilePath = kept, FileName = "資料.xlsx" },
                });
                node.SettlePreviousOutputs(succeeded: true);

                Assert.False(File.Exists(oldReport));
                Assert.True(File.Exists(kept));
                Assert.True(File.Exists(newReport));
                return Task.CompletedTask;
            });
        }

        // ── 失敗但已產出部分新檔：新舊都留著 ──
        [Fact]
        public void AFailedRerunWithPartialFiles_KeepsBoth()
        {
            string oldReport = MakeFile(@"甲\分析報告.docx", "舊");

            OnUiThread(() =>
            {
                var node = new NodeControl();
                node.RestoreOutputFiles(new[] { oldReport });
                node.SetAsidePreviousOutputs();

                string partial = MakeFile(@"甲\分析報告_2.py", "做到一半");
                node.SetOutputFiles(new List<GeneratedFilePayload>
                {
                    new() { Success = true, FilePath = partial, FileName = "分析報告_2.py" },
                });
                node.SettlePreviousOutputs(succeeded: false);

                var paths = node.GetOutputFilePaths();
                Assert.Contains(oldReport, paths);
                Assert.Contains(partial, paths);
                return Task.CompletedTask;
            });

            Assert.True(File.Exists(oldReport));
        }

        // ── 日誌裡的節點代號不帶提問內容 ──
        [Fact]
        public void TheLogLabelOfANode_DoesNotContainItsQuestion()
        {
            OnUiThread(() =>
            {
                var node = new NodeControl();
                node.SetTopText("幫台積電客戶整理第三季機密財報");

                string label = MainWindow.ShortLabel(node);

                Assert.DoesNotContain("台積電", label);
                Assert.StartsWith("node:", label);
                Assert.Equal("node:".Length + 8, label.Length);
                return Task.CompletedTask;
            });
        }
    }

    /// <summary>產出檔案不在原位置時，只連到能確定屬於這個專案的檔案。</summary>
    public class GeneratedFileRelinkTests
    {
        private const string Root = @"C:\gen";

        [Fact]
        public void AnotherProjectsFileWithTheSameName_IsNeverPicked()
        {
            // 甲的原檔不見了；乙剛好有一份同名的報告。舊版會連到乙的，甲一重跑還會把它刪掉。
            string? hit = GeneratedFileRelink.Pick(
                @"C:\gen\甲\分析報告.docx", Root, @"C:\gen\甲",
                new[] { @"C:\gen\乙\分析報告.docx" });

            Assert.Null(hit);
        }

        [Fact]
        public void AFileThatMovedWithinTheSameProject_IsFound()
            => Assert.Equal(@"C:\gen\甲\程式\hw1.py",
                GeneratedFileRelink.Pick(@"C:\gen\甲\hw1.py", Root, @"C:\gen\甲",
                    new[] { @"C:\gen\乙\hw1.py", @"C:\gen\甲\程式\hw1.py" }));

        [Fact]
        public void AWholeOutputFolderMovedElsewhere_KeepsTheSameRelativePath()
            => Assert.Equal(@"D:\雲端\產出\甲\分析報告.docx",
                GeneratedFileRelink.Pick(@"C:\gen\甲\分析報告.docx", @"D:\雲端\產出", @"D:\雲端\產出\甲",
                    new[] { @"D:\雲端\產出\乙\分析報告.docx", @"D:\雲端\產出\甲\分析報告.docx" }));

        [Fact]
        public void ARestoredSave_FindsItsFileInOther_OnlyWhenItIsTheOnlyOne()
        {
            Assert.Equal(@"C:\gen\other\分析報告.docx",
                GeneratedFileRelink.Pick(@"C:\gen\甲\分析報告.docx", Root, @"C:\gen\甲",
                    new[] { @"C:\gen\other\分析報告.docx" }));

            Assert.Null(GeneratedFileRelink.Pick(@"C:\gen\甲\分析報告.docx", Root, @"C:\gen\甲",
                new[] { @"C:\gen\other\分析報告.docx", @"C:\gen\乙\分析報告.docx" }));
        }

        [Fact]
        public void TwoCandidatesInsideTheProject_AreAmbiguous()
            => Assert.Null(GeneratedFileRelink.Pick(@"C:\gen\甲\hw1.py", Root, @"C:\gen\甲",
                new[] { @"C:\gen\甲\a\hw1.py", @"C:\gen\甲\b\hw1.py" }));
    }
}
