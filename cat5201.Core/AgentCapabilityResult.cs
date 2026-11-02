using System.Collections.Generic;

namespace Cat5201
{
    public sealed class AgentCapabilityResult
    {
        public bool Handled { get; init; }

        public string Output { get; init; } = "";

        public string AugmentedPrompt { get; init; } = "";

        public Dictionary<string, object> Data { get; init; } = new();

        /// <summary>
        /// 這次能力層實際呼叫的模型 id。
        ///
        /// 2026-10-29 使用者回報：關閉 Auto 之後，決策窗的「模型」欄仍寫著 Perplexity Sonar。
        /// 查下去是工作區在推導產物模型時寫死了「搜尋固定由 Perplexity Sonar 執行」，
        /// 但實際送出的是 Perplexity Agent API + openai/gpt-5.6-sol，逐筆帳目裡也只有後者。
        /// 決策窗因此顯示了一個沒有被呼叫、也沒有被計費的模型——
        /// 對一個主打「每一次呼叫都攤開給你看」的產品，這是最不能出現的那種錯。
        /// 能力層從此自己報出用了什麼，推導規則不再猜。
        /// </summary>
        public string ModelId { get; init; } = "";

        /// <summary>
        /// 要放在答案最上方給使用者看的說明（例如「你選的模型無法搜尋，改由某模型完成」）。
        /// 手動模式下所選模型確定失敗時可以換模型，但一定要講——這是那句話的通道。
        /// </summary>
        public string Notice { get; set; } = "";

        // ✅ NEW：真正工具資料
        public static AgentCapabilityResult WithData(string key, object value, string modelId = "")
        {
            return new AgentCapabilityResult
            {
                Handled = true,
                ModelId = modelId ?? "",
                Data = new Dictionary<string, object>
                {
                    [key] = value
                }
            };
        }

        public static AgentCapabilityResult WithAugmentedPrompt(string prompt)
        {
            return new AgentCapabilityResult
            {
                Handled = false,
                AugmentedPrompt = prompt
            };
        }

        public static AgentCapabilityResult DirectHandle(string output)
        {
            return new AgentCapabilityResult
            {
                Handled = true,
                Output = output
            };
        }

        public static AgentCapabilityResult NotHandled()
        {
            return new AgentCapabilityResult
            {
                Handled = false
            };
        }
    }
}