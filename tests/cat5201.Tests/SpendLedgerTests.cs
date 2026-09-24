using System;
using System.IO;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>花錢安全核心：帳本累計、持久化、每日上限判斷。這層錯＝使用者鈔票安全網失效。</summary>
    [Collection("SpendLedger")]
    public class SpendLedgerTests
    {
        private static string FreshDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Add_Accumulates_Today()
        {
            SpendLedger.Initialize(FreshDir());
            double before = SpendLedger.TodayUsd;
            SpendLedger.Add(0.10, "test");
            SpendLedger.Add(0.05, "test");
            Assert.Equal(before + 0.15, SpendLedger.TodayUsd, 6);
        }

        [Fact]
        public void Add_Ignores_InvalidAmounts()
        {
            SpendLedger.Initialize(FreshDir());
            SpendLedger.Add(0, "test");
            SpendLedger.Add(-5, "test");
            SpendLedger.Add(double.NaN, "test");
            Assert.Equal(0, SpendLedger.TodayUsd, 6);
        }

        [Fact]
        public void Persists_AcrossReinitialize()
        {
            string dir = FreshDir();
            SpendLedger.Initialize(dir);
            SpendLedger.Add(0.25, "test");

            SpendLedger.Initialize(dir); // 模擬重啟：從檔案載回
            Assert.Equal(0.25, SpendLedger.TodayUsd, 6);
        }

        [Fact]
        public void IsOverDailyBudget_ZeroBudget_NeverBlocks()
        {
            SpendLedger.Initialize(FreshDir());
            SpendLedger.Add(999, "test");
            Assert.False(SpendLedger.IsOverDailyBudget(0));
        }

        [Fact]
        public void IsOverDailyBudget_BlocksWhenReached()
        {
            SpendLedger.Initialize(FreshDir());
            SpendLedger.Add(1.0, "test"); // = NT$32
            Assert.True(SpendLedger.IsOverDailyBudget(30));
            Assert.False(SpendLedger.IsOverDailyBudget(100));
        }
    }

    /// <summary>原子寫檔：使用者資產（偏好/專案/帳本）的防毀損保證。</summary>
    public class AtomicFileTests
    {
        private static string FreshFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "data.json");
        }

        [Fact]
        public void Write_CreatesFile()
        {
            string path = FreshFile();
            AtomicFile.WriteAllText(path, "hello");
            Assert.Equal("hello", File.ReadAllText(path));
        }

        [Fact]
        public void Overwrite_KeepsBackup()
        {
            string path = FreshFile();
            AtomicFile.WriteAllText(path, "v1");
            AtomicFile.WriteAllText(path, "v2");
            Assert.Equal("v2", File.ReadAllText(path));
            Assert.Equal("v1", File.ReadAllText(path + ".bak"));
        }

        [Fact]
        public void Read_FallsBackToBak_WhenMainMissing()
        {
            string path = FreshFile();
            AtomicFile.WriteAllText(path, "v1");
            AtomicFile.WriteAllText(path, "v2");
            File.Delete(path); // 模擬主檔毀損/消失
            Assert.Equal("v1", AtomicFile.ReadAllTextWithFallback(path));
        }

        [Fact]
        public void Read_ReturnsNull_WhenNothingExists()
        {
            Assert.Null(AtomicFile.ReadAllTextWithFallback(FreshFile()));
        }
    }
}
