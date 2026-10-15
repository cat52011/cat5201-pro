using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    /// <summary>
    /// 「標記為記憶」時，先用一次便宜的模型把對話濃縮成值得長期記住的事實。
    ///
    /// 跟 <see cref="OutputIntentResolver"/> 同一套做法：語意判斷為主、規則為後備。
    /// 只用在使用者「明確標記」這個動作上——那是刻意的操作，等一兩秒可以接受；
    /// 自動記憶（每次執行都寫一筆）維持規則版，不然每跑一個節點就多一次 API 費用。
    /// </summary>
    public sealed class MemoryDigestResolver
    {
        private readonly AiServiceRouter _router;

        public MemoryDigestResolver(AiServiceRouter router)
        {
            _router = router;
        }

        public async Task<IReadOnlyList<string>> ExtractAsync(
            string? topText, string? bottomText, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(topText) && string.IsNullOrWhiteSpace(bottomText))
                return Array.Empty<string>();

            try
            {
                using var usagePurpose = UsageMeter.Purpose("記憶重點萃取");
                string raw = await _router
                    .GetOpenAiService(AiModels.OpenAi_Gpt56)
                    .GenerateAsync(
                        MemoryDigest.BuildSystemPrompt(),
                        MemoryDigest.BuildUserPrompt(topText ?? "", bottomText ?? ""),
                        300, ct);

                var facts = MemoryDigest.TryParse(raw);

                // 模型說「沒有值得長期記住的事」時要尊重它——使用者標記的通常不是空的，
                // 但硬塞一句無關的話進記憶，比少記一筆更糟。只有「解析不出來」才退回規則版。
                if (facts != null)
                    return facts;
            }
            catch (Exception ex)
            {
                AppLog.Warn("Memory", "記憶重點萃取失敗，改用規則版", ex);
            }

            return MemoryDigest.FromText(topText, bottomText);
        }
    }
}
