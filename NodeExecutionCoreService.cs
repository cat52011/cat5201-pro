using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Cat5201
{
    public sealed class NodeExecutionCoreService
    {
        private readonly AiServiceRouter _router;
        private readonly NodeContextStrategyResolver _contextStrategyResolver;
        private readonly NodePromptBuilder _promptBuilder;
        private readonly NodeInstructionBuilder _instructionBuilder;
        private readonly NodeTextProcessingService _textProcessing;
        private readonly Func<string, string, int, bool, int, CancellationToken, Task<AiRequest>> _buildRequestFactory;

        private readonly int _continuationMaxRounds;
        private readonly int _mainReplyMaxOutputTokens;
        private readonly NodeMemoryService _memoryService;
        public NodeExecutionCoreService(
    AiServiceRouter router,
    NodeContextStrategyResolver contextStrategyResolver,
    NodePromptBuilder promptBuilder,
    NodeInstructionBuilder instructionBuilder,
    NodeTextProcessingService textProcessing,
    NodeMemoryService memoryService,
    Func<string, string, int, bool, int, CancellationToken, Task<AiRequest>> buildRequestFactory,
    int continuationMaxRounds,
    int mainReplyMaxOutputTokens)
        {
            _router = router;
            _contextStrategyResolver = contextStrategyResolver;
            _promptBuilder = promptBuilder;
            _instructionBuilder = instructionBuilder;
            _textProcessing = textProcessing;
            _memoryService = memoryService;
            _buildRequestFactory = buildRequestFactory;
            _continuationMaxRounds = continuationMaxRounds;
            _mainReplyMaxOutputTokens = mainReplyMaxOutputTokens;
        }
        public async Task<string> ExecuteAsync(
    NodeControl currentNode,
    string agentId,
    string topText,
    string model,
    NodeTaskMode taskMode,
    CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.PurposeIfUnset("主回覆");
            var memory = ShouldSuppressMemoryForFreshFinance(topText, taskMode)
                ? new MemoryQueryResult()
                : _memoryService.RecallRelevant(currentNode, agentId, topText, taskMode);
            string prompt = _promptBuilder.BuildPrompt(new NodePromptBuildRequest
            {
                CurrentNode = currentNode,
                TopText = topText,
                TaskMode = taskMode,
                Strategy = _contextStrategyResolver.Resolve(model, taskMode),
                MemoryBlock = memory.PromptBlock,
                PreferenceBlock = memory.PreferenceBlock
            });

            return await GenerateWithContinuationAsync(
                currentNode,
                model,
                async followUp => await _buildRequestFactory(
                    model,
                    prompt + followUp,
                    (int)taskMode,
                    false,
                    MaxOutputFor(model),
                    ct),
                ct);
        }
        public async Task<string> ExecuteStreamAsync(
     NodeControl currentNode,
     string agentId,
     string topText,
     string model,
     NodeTaskMode taskMode,
     Action<string> onDelta,
     CancellationToken ct)
        {
            using var usagePurpose = UsageMeter.PurposeIfUnset("主回覆");
            var memory = ShouldSuppressMemoryForFreshFinance(topText, taskMode)
                ? new MemoryQueryResult()
                : _memoryService.RecallRelevant(currentNode, agentId, topText, taskMode);
            string prompt = _promptBuilder.BuildPrompt(new NodePromptBuildRequest
            {
                CurrentNode = currentNode,
                TopText = topText,
                TaskMode = taskMode,
                Strategy = _contextStrategyResolver.Resolve(model, taskMode),
                MemoryBlock = memory.PromptBlock,
                PreferenceBlock = memory.PreferenceBlock
            });

            return await GenerateWithContinuationStreamingAsync(
                currentNode,
                model,
                async followUp => await _buildRequestFactory(
                    model,
                    prompt + followUp,
                    (int)taskMode,
                    true,
                    MaxOutputFor(model),
                    ct),
                onDelta,
                ct);
        }

        /// <summary>
        /// 主回覆每一輪的輸出上限。Perplexity 維持原本的 8,000（沒查到它更高的上限，送太大會被拒）。
        ///
        /// 其他模型提高到 16,000：2026-11-03 機器學習作業的答案約 1.3 萬 token，8,000 的上限讓它分三輪寫完，
        /// 每一輪續寫都重送整份提示與附件（一輪約 11 萬 token），光主回覆就花了 US$1.2、三分鐘。
        /// 分輪也正是那次程式被切壞的起點（見 NodeTextProcessingService.JoinContinuation）。
        /// </summary>
        internal int MaxOutputFor(string model)
            => AiModelHelper.GetProviderKind(model) is AiProviderKind.PerplexitySonar or AiProviderKind.PerplexityTool
                ? Math.Min(_mainReplyMaxOutputTokens, PerplexityMaxOutputTokens)
                : _mainReplyMaxOutputTokens;

        internal const int PerplexityMaxOutputTokens = 8000;

        private async Task<string> GenerateWithContinuationAsync(
    NodeControl currentNode,
    string model,
    Func<string, Task<AiRequest>> buildRequestFactory,
    CancellationToken ct)
        {
            var finalText = new StringBuilder();
            var provider = _router.GetProvider(model);

            for (int round = 0; round < _continuationMaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                string followUp = round == 0
                    ? ""
                    : $@"

【你前一次已輸出的內容（不可重複，僅供接續）】
{finalText}

請直接從上一行未完成處繼續輸出。
不要重複前面內容。
若這次已完整完成，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";

                var request = await buildRequestFactory(followUp);
                var response = await provider.GenerateAsync(request, ct);
                string reply = response.Text;

                if (string.IsNullOrWhiteSpace(reply))
                    break;

                bool ended = _textProcessing.HasEndMarker(reply);
                string cleaned = _textProcessing.RemoveEndMarker(reply);

                if (round == 0)
                {
                    finalText.Append(cleaned.Trim());
                }
                else
                {
                    // 接縫不硬塞換行、不吃掉續寫開頭的縮排（見 NodeTextProcessingService.JoinContinuation）。
                    string append = _textProcessing.JoinContinuation(finalText.ToString(), reply);
                    if (append.Length > 0)
                        finalText.Append(append);
                }

                if (ended)
                    break;

                // 自然講完就停：這一輪輸出沒有逼近 8000 上限，代表模型是「講完了」而非「被截斷」，
                // 不再多跑 continuation。模型常忘了輸出 [[END_OF_RESPONSE]] 標記，舊版只認標記，
                // 因此一個其實 1 輪就答完的簡單問題會空跑滿 5 輪，每輪重送整包 prompt+附件、token 又逐輪累加，
                // 造成動輒上百 K token。只在 provider 有回報真實 OutputTokens 時套用；沒回報時退回靠標記的舊行為，
                // 避免長輸出在不回報 usage 的 provider 上被中途截斷。
                if (response.OutputTokens.HasValue &&
                    response.OutputTokens.Value < MaxOutputFor(model) - 512)
                    break;
            }

            return _textProcessing.RemoveRepeatedBlocks(finalText.ToString().Trim());
        }

        private static bool ShouldSuppressMemoryForFreshFinance(string text, NodeTaskMode taskMode)
        {
            if (taskMode == NodeTaskMode.Translate ||
                taskMode == NodeTaskMode.Rewrite ||
                taskMode == NodeTaskMode.Summarize)
            {
                return false;
            }

            return FinanceTaskDetector.IsFinanceLike(text);
        }

        private async Task<string> GenerateWithContinuationStreamingAsync(
    NodeControl currentNode,
    string model,
    Func<string, Task<AiRequest>> buildRequestFactory,
    Action<string> onDelta,
    CancellationToken ct)
        {
            var finalText = new StringBuilder();
            var provider = _router.GetProvider(model);

            for (int round = 0; round < _continuationMaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                string followUp = round == 0
                    ? ""
                    : $@"

【你前一次已輸出的內容（不可重複，僅供接續）】
{finalText}

請直接從上一行未完成處繼續輸出。
不要重複前面內容。
若這次已完整完成，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";

                var request = await buildRequestFactory(followUp);
                string reply;

                int? roundOutputTokens;
                if (round == 0)
                {
                    var streamed = await provider.GenerateStreamAsync(request, delta => onDelta?.Invoke(delta), ct);
                    reply = streamed.Text;
                    roundOutputTokens = streamed.OutputTokens;
                }
                else
                {
                    var normal = await provider.GenerateAsync(request, ct);
                    reply = normal.Text;
                    roundOutputTokens = normal.OutputTokens;
                }

                if (string.IsNullOrWhiteSpace(reply))
                    break;

                bool ended = _textProcessing.HasEndMarker(reply);
                string cleaned = _textProcessing.RemoveEndMarker(reply);

                if (round == 0)
                {
                    finalText.Append(cleaned.Trim());
                }
                else
                {
                    // 接縫不硬塞換行、不吃掉續寫開頭的縮排（見 NodeTextProcessingService.JoinContinuation）。
                    string append = _textProcessing.JoinContinuation(finalText.ToString(), reply);
                    if (append.Length > 0)
                    {
                        finalText.Append(append);
                        onDelta?.Invoke(append);
                    }
                }

                if (ended)
                    break;

                // 自然講完就停（與非串流路徑同邏輯）：這輪輸出沒逼近 8000 上限＝模型講完，不是被截斷，
                // 不再多跑 continuation，避免空跑多輪重送整包 prompt+附件、token 逐輪累加。
                if (roundOutputTokens.HasValue &&
                    roundOutputTokens.Value < MaxOutputFor(model) - 512)
                    break;
            }

            return _textProcessing.RemoveRepeatedBlocks(finalText.ToString().Trim());
        }
    }
}
