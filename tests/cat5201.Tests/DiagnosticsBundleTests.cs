using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>「回報問題」打包檔會寄給開發者：金鑰與使用者名稱絕不能漏出去。</summary>
    public class DiagnosticsBundleTests
    {
        [Theory]
        [InlineData("OpenAI API 失敗 key=sk-proj-AbCdEfGhIjKlMnOpQrStUv123456")]
        [InlineData("x-api-key: sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWx")]
        [InlineData("url?key=AIzaSyA1234567890abcdefghijklmnopqrstu")]
        [InlineData("Authorization: Bearer pplx-AbCdEfGhIjKlMnOpQrSt1234")]
        [InlineData("\"access_token\": \"ya29.a0AfH6SMBxyzxyzxyzxyzxyz\"")]
        public void Scrub_MasksSecrets(string line)
        {
            string scrubbed = DiagnosticsBundle.Scrub(line);
            Assert.Contains("[已遮蔽]", scrubbed);
            Assert.DoesNotContain("AbCdEfGhIjKlMnOp", scrubbed);
            Assert.DoesNotContain("AIzaSyA123456789", scrubbed);
            Assert.DoesNotContain("ya29.a0AfH6SMBxyz", scrubbed);
        }

        [Fact]
        public void Scrub_RemovesWindowsUserName()
        {
            string scrubbed = DiagnosticsBundle.Scrub(@"寫入失敗：C:\Users\Lin Po-Yen\AppData\Local\cat5201-pro\x.json");
            Assert.Equal(@"寫入失敗：C:\Users\<user>\AppData\Local\cat5201-pro\x.json", scrubbed);
        }

        [Fact]
        public void Scrub_LeavesOrdinaryLogAlone()
        {
            const string line = "2026-09-17 14:23:10.679 [INFO] Spend — +US$0.4（影片生成・veo-3.1-lite-generate-preview）";
            Assert.Equal(line, DiagnosticsBundle.Scrub(line));
        }
    }
}
