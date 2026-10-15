using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-13 使用者實測回報的問題。每一個測試對應一條「我按了這個，結果不對」。
    /// 這些事情自動化測試本來抓不到，是因為根本沒有人寫——現在寫了。
    /// </summary>
    // 全域靜態狀態（帳本、預留、DeliveryOptions）是所有測試共用的：
    // 碰到它們的測試必須在同一個集合裡循序執行，否則別的類別平行跑時會改掉這裡的前提。
    [Collection("spend-ledger")]
    public class UserReportedFixesTests
    {
        // ────────────────────────────── 回到上一版 / 下一版

        [Fact]
        public void 退回之後還能再前進回來()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "簡報.pptx");

            File.WriteAllText(path, "v1");
            GeneratedFileHistory.SaveVersion(path);   // 存下 v1

            File.WriteAllText(path, "v2");
            Assert.True(GeneratedFileHistory.HasPrevious(path));
            Assert.False(GeneratedFileHistory.HasNext(path));

            Assert.True(GeneratedFileHistory.RestorePrevious(path));
            Assert.Equal("v1", File.ReadAllText(path));

            // 這一步是重點：退回之後 v2 不能就這樣消失，不然只能再花一次錢重生。
            Assert.True(GeneratedFileHistory.HasNext(path));
            Assert.True(GeneratedFileHistory.RestoreNext(path));
            Assert.Equal("v2", File.ReadAllText(path));

            Assert.True(GeneratedFileHistory.HasPrevious(path));
        }

        [Fact]
        public void 重新產生之後就不能再前進到舊的那條分支()
        {
            string dir = NewTempDir();
            string path = Path.Combine(dir, "簡報.pptx");

            File.WriteAllText(path, "v1");
            GeneratedFileHistory.SaveVersion(path);
            File.WriteAllText(path, "v2");

            GeneratedFileHistory.RestorePrevious(path);          // 回到 v1，v2 進 redo
            Assert.True(GeneratedFileHistory.HasNext(path));

            File.WriteAllText(path, "v3");
            GeneratedFileHistory.SaveVersion(path);              // 產生新內容＝歷史分岔

            Assert.False(GeneratedFileHistory.HasNext(path));    // v2 已經不在這條線上
        }

        // ────────────────────────────── 簡報風格變體

        [Fact]
        public void 同一份簡報每次算出來的風格都一樣()
        {
            var outline = Outline("法國高檔餐飲的虛以藝術", new DateTime(2026, 9, 25, 21, 2, 13, DateTimeKind.Utc));

            var a = DeckArtDirection.ForOutline(outline);
            var b = DeckArtDirection.ForOutline(outline);

            // 預覽、pptx、pdf 是三條獨立的渲染路徑，算出不同的顏色就會前後不一致。
            Assert.Equal(a.Accent, b.Accent);
            Assert.Equal(a.Variant, b.Variant);
        }

        [Fact]
        public void 主題相近但不同時間做的簡報不會長得一模一樣()
        {
            // 使用者回報：連做三份同類主題的簡報，三份的配色設計完全相同。
            var made = Enumerable.Range(0, 24)
                .Select(i => Outline("法國高檔餐飲簡報", new DateTime(2026, 9, 25, 0, 0, 0, DateTimeKind.Utc).AddHours(i)))
                .Select(DeckArtDirection.ForOutline)
                .ToList();

            // 同一個藝術方向（餐飲＝雜誌編輯）底下，至少要出現兩種以上的調子。
            Assert.Single(made.Select(x => x.Id).Distinct());
            Assert.True(made.Select(x => x.Accent).Distinct().Count() > 1,
                "主題相同的簡報每一份都拿到同一組顏色");
        }

        [Fact]
        public void 換配色不會換掉整套設計語言()
        {
            var baseline = DeckArtDirection.ById("editorial");

            foreach (int seed in new[] { 1, 2, 7, 31 })
            {
                var art = DeckArtDirection.ForTopic("米其林餐廳", seed);

                // 背景、主文字色、字體決定「這是哪一種簡報」；變體只換強調色。
                Assert.Equal(baseline.Background, art.Background);
                Assert.Equal(baseline.Ink, art.Ink);
                Assert.Equal(baseline.DisplayEaFont, art.DisplayEaFont);
                Assert.Equal(baseline.Family, art.Family);
            }
        }

        // ────────────────────────────── 超過每日上限時先問

        [Fact]
        public void 使用者同意之後這一次就不再被上限擋下來()
        {
            SpendReservations.ResetForTests();
            int previousBudget = SpendLedger.DailyBudgetTwd;

            try
            {
                // 把上限設得比「今天已經花掉的」還低，任何一筆都會超標。
                SpendLedger.DailyBudgetTwd = 1;

                Assert.True(SpendReservations.WouldExceedBudget(5.0));
                Assert.Null(SpendReservations.TryReserveWithinBudget("生圖", 5.0));

                using (SpendReservations.BeginOverBudgetScope())
                {
                    // 還沒點頭之前照樣擋。
                    Assert.Null(SpendReservations.TryReserveWithinBudget("生圖", 5.0));

                    SpendReservations.GrantOverBudget();
                    using var granted = SpendReservations.TryReserveWithinBudget("生圖", 5.0);
                    Assert.NotNull(granted);
                }

                // 離開這一次執行之後，授權不留下來——下一個任務照常受限制。
                Assert.Null(SpendReservations.TryReserveWithinBudget("生圖", 5.0));
            }
            finally
            {
                SpendLedger.DailyBudgetTwd = previousBudget;
                SpendReservations.ResetForTests();
            }
        }

        // ────────────────────────────── 來源證明

        [Fact]
        public void 查證類任務的指示有要求附上來源與網址()
        {
            string block = AgentRuntime.BuildSourceEvidenceBlock();

            Assert.Contains("資料來源", block);
            Assert.Contains("https://", block);
            // 找不到來源時要標出來，而不是刪掉或假裝有。
            Assert.Contains("無來源，為模型判斷", block);
        }

        // ────────────────────────────── 全域記憶

        [Fact]
        public void 使用者標記的事實會進到一律注入的全域區塊()
        {
            string dir = NewTempDir();
            var store = new MemoryStore(dir);

            store.Add(new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "user_marked",
                FileKey = "存檔A",
                IsSharedMemory = true,
                Title = "⭐ 使用者標記：記住我是美國人",
                Content = "使用者是美國人",
                Importance = 0.97,
            });

            // 這是 AgentRuntime 唯一會收到的記憶區塊；user_marked 沒進來，
            // 換個存檔問「我是哪國人」就會答不出來（使用者 2026-10-13 回報）。
            var marked = store.GetUserMarked();
            Assert.Single(marked);
            Assert.Equal("使用者是美國人", marked[0].Content);
        }

        private static PresentationOutlinePayload Outline(string title, DateTime createdUtc) =>
            new()
            {
                Title = title,
                Topic = "",
                CreatedAtUtc = createdUtc,
                Slides = Array.Empty<PresentationSlidePayload>(),
            };

        private static string NewTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-fixes-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
