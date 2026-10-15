using System;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-10-13：一份簡報跑了 14 分鐘、沒有產出檔案。拆開 log 看到的是
    /// 「第 1 次被伺服器退回 prompt is too long（1,112,319 tokens），花了 8.8 分鐘；
    /// 　第 2 次原封不動再送一次，又燒掉剩下的 5.2 分鐘」。
    /// 重試是對的，但重試一個送幾次都一樣的錯就只是重複浪費時間。
    /// </summary>
    public class DocSkillsRetryTests
    {
        private static ClaudeDocumentSkillsService.Result Fail(string message, int rounds = 1, int steps = 0, bool stalled = false)
            => new()
            {
                Success = false,
                ErrorMessage = message,
                Rounds = rounds,
                ToolSteps = steps,
                Stalled = stalled,
            };

        [Fact]
        public void 提示太長不重試()
        {
            Assert.False(ClaudeDocumentSkillsService.IsRetryable(
                Fail("Claude 文件技能串流錯誤：prompt is too long: 1112319 tokens > 1000000 maximum")));
        }

        [Theory]
        [InlineData("authentication_error: invalid x-api-key")]
        [InlineData("invalid_request_error: unknown field")]
        [InlineData("permission denied for this model")]
        [InlineData("Claude 拒絕了這個文件產出請求。")]
        [InlineData("找不到 ANTHROPIC_API_KEY。")]
        public void 送幾次都一樣的錯不重試(string message)
        {
            Assert.False(ClaudeDocumentSkillsService.IsRetryable(Fail(message)));
        }

        [Fact]
        public void 卡住值得再試一次()
        {
            // 同一份提示隔幾十秒再送一次就成功過，這種才該重試。
            Assert.True(ClaudeDocumentSkillsService.IsRetryable(
                Fail("Claude 的回應中斷了（150 秒沒有任何動靜，1 輪 / 0 步）。", stalled: true)));
        }

        [Fact]
        public void 跑了步驟卻沒交出檔案值得再試一次()
        {
            Assert.True(ClaudeDocumentSkillsService.IsRetryable(
                Fail("Claude 執行了 14 個步驟，但沒有回傳任何可下載的檔案。", rounds: 1, steps: 14)));
        }

        [Fact]
        public void 成功就不要重試()
        {
            Assert.False(ClaudeDocumentSkillsService.IsRetryable(
                new ClaudeDocumentSkillsService.Result { Success = true }));
            Assert.False(ClaudeDocumentSkillsService.IsRetryable(null!));
        }
    }
}
