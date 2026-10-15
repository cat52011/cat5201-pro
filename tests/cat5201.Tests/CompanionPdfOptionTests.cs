using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 「要不要同時產出對照 PDF」是個人化設定。預設必須維持原本行為——
    /// 這個設定是為了速度（PDF 是產出流程裡最慢的一步），不是改變預設交付內容。
    /// </summary>
    // 全域靜態狀態（帳本、預留、DeliveryOptions）是所有測試共用的：
    // 碰到它們的測試必須在同一個集合裡循序執行，否則別的類別平行跑時會改掉這裡的前提。
    [Collection("spend-ledger")]
    public class CompanionPdfOptionTests
    {
        [Fact]
        public void DefaultsToProducingThePdf()
        {
            DeliveryOptions.ResetForTests();
            Assert.True(DeliveryOptions.CompanionPdf);
        }

        [Fact]
        public void PreferencesRoundTripKeepsTheChoice()
        {
            try
            {
                DeliveryOptions.CompanionPdf = false;
                Assert.False(DeliveryOptions.CompanionPdf);

                DeliveryOptions.CompanionPdf = true;
                Assert.True(DeliveryOptions.CompanionPdf);
            }
            finally { DeliveryOptions.ResetForTests(); }
        }
    }
}
