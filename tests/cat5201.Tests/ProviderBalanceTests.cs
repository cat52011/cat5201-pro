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
        public void OverspendIsKeptSoTheNextTopUpIsNotOverstated()
        {
            FreshDir();
            string id = NewProvider();
            ProviderBalance.AddTopUp(id, 1);
            ProviderBalance.Deduct(id, 5);

            // 以前這裡夾在 0。夾掉的那 4 元不會憑空消失在官網那邊：
            // 使用者下次充值 10 元填進來，本機會以為還有 10，實際上只剩 6。
            // 帳要留真的，要不要顯示成 0 是畫面自己的事（見 RefreshSpendColumns）。
            Assert.Equal(-4, ProviderBalance.Remaining(id), 4);

            // 去官網對一次帳就把誤差吸收掉，不會一直累積下去。
            ProviderBalance.SetBalance(id, 6);
            Assert.Equal(6, ProviderBalance.Remaining(id), 4);
        }
    }
}
