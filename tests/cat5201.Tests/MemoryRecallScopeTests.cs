using System.Linq;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 使用者明確標記的記憶在個人化裡是列在「全域」清單、不分存檔顯示的。
    /// 召回時若照存檔過濾掉，使用者會遇到「UI 說記得，但換個存檔問就答不出來」——
    /// 介面承諾了一件提示詞沒有兌現的事。
    /// </summary>
    public class MemoryRecallScopeTests
    {
        private static MemoryStore NewStore() => new MemoryStore(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetRandomFileName()));

        [Fact]
        public void UserMarkedMemoryIsRecalledFromAnotherSave()
        {
            var store = NewStore();
            store.Add(new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "user_marked",
                FileKey = "存檔A",
                IsSharedMemory = true,
                Title = "⭐ 使用者標記：自我介紹",
                Content = "我是美國人",
                Importance = 0.97,
            });

            // 在另一個存檔裡問 —— 仍然要召回得到。
            var hits = store.Query("存檔B", agentId: "general-agent", text: "我是哪裡人", maxCount: 6);

            Assert.Contains(hits, h => h.Content.Contains("美國人"));
        }

        [Fact]
        public void GlobalScopeMemoryIsNotTrappedInOneSave()
        {
            var store = NewStore();
            store.Add(new MemoryItem
            {
                Scope = MemoryScope.Global,
                Category = "fact",
                FileKey = "存檔A",
                IsSharedMemory = true,
                Title = "全域事實",
                Content = "使用者住在西雅圖",
            });

            Assert.Contains(store.Query("存檔B", "general-agent", "住在哪", 6),
                h => h.Content.Contains("西雅圖"));
        }

        [Fact]
        public void OrdinaryMemoryStaysInItsOwnSave()
        {
            // 一般的執行記憶仍然要隔離，否則別的專案的內容會污染這次的回答。
            var store = NewStore();
            store.Add(new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "episodic",
                FileKey = "存檔A",
                Title = "某次執行",
                Content = "這是存檔A的內部細節",
            });

            Assert.DoesNotContain(store.Query("存檔B", "general-agent", "細節", 6),
                h => h.Content.Contains("存檔A"));
        }
    }
}
