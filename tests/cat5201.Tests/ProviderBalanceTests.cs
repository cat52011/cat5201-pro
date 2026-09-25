using System.IO;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    [Collection("spend-ledger")]
    public class ProviderBalanceTests
    {
        // ProviderBalance 是全域靜態狀態，UsageMeter 的每一筆帳目都會扣它。
        // 其他測試類別平行跑時會扣到同名服務，所以這裡每次都用獨一無二的服務 ID。
        private static string NewProvider() => "test-" + Guid.NewGuid().ToString("N")[..8];

        private static string FreshDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-balance-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            ProviderBalance.Initialize(dir);
            return dir;
        }

        [Fact]
        public void TopUpAddsAndEveryChargeDeducts()
        {
            FreshDir();
            string id = NewProvider();

            Assert.False(ProviderBalance.Has(id));

            ProviderBalance.AddTopUp(id, 20);
            Assert.True(ProviderBalance.Has(id));
            Assert.Equal(20, ProviderBalance.Remaining(id), 4);

            ProviderBalance.Deduct(id, 4.71);
            Assert.Equal(15.29, ProviderBalance.Remaining(id), 4);

            // 再次充值是「加上去」，不是覆蓋——使用者只填這次加了多少。
            ProviderBalance.AddTopUp(id, 10);
            Assert.Equal(25.29, ProviderBalance.Remaining(id), 4);
            Assert.Equal(30, ProviderBalance.TotalToppedUp(id), 4);

            // 沖銷（負數花費）要加回去。
            ProviderBalance.Deduct(id, -0.29);
            Assert.Equal(25.58, ProviderBalance.Remaining(id), 4);
        }

        [Fact]
        public void UntrackedProviderIsNeverCreatedByCharges()
        {
            FreshDir();
            string id = NewProvider();

            ProviderBalance.Deduct(id, 3.5);

            // 沒設定過餘額的服務不該因為一筆花費就冒出「剩餘 -3.5」這種沒有意義的數字。
            Assert.False(ProviderBalance.Has(id));
            Assert.Equal(0, ProviderBalance.Remaining(id), 4);
        }

        [Fact]
        public void BalanceSurvivesRestart()
        {
            string dir = FreshDir();
            string id = NewProvider();
            ProviderBalance.AddTopUp(id, 12.5);
            ProviderBalance.Deduct(id, 2.5);

            ProviderBalance.Initialize(dir);   // 模擬重開 App

            Assert.Equal(10, ProviderBalance.Remaining(id), 4);
            Assert.Equal(12.5, ProviderBalance.TotalToppedUp(id), 4);
        }

        [Fact]
        public void SetBalanceReplacesTheNumberAndKeepsDeducting()
        {
            FreshDir();
            string id = NewProvider();

            // 使用者照官網餘額直接改成這個數字，不是加上去。
            ProviderBalance.SetBalance(id, 20);
            ProviderBalance.Deduct(id, 5);
            Assert.Equal(15, ProviderBalance.Remaining(id), 4);

            ProviderBalance.SetBalance(id, 40);
            Assert.Equal(40, ProviderBalance.Remaining(id), 4);

            ProviderBalance.Deduct(id, 0.5);
            Assert.Equal(39.5, ProviderBalance.Remaining(id), 4);

            // 清空＝不再追蹤，之後的花費不會再記到這個服務頭上。
            ProviderBalance.Forget(id);
            Assert.False(ProviderBalance.Has(id));
            ProviderBalance.Deduct(id, 1);
            Assert.False(ProviderBalance.Has(id));
        }

        [Fact]
        public void RemainingNeverGoesNegative()
        {
            FreshDir();
            string id = NewProvider();
            ProviderBalance.AddTopUp(id, 1);
            ProviderBalance.Deduct(id, 5);

            Assert.Equal(0, ProviderBalance.Remaining(id), 4);
        }
    }
}
