namespace Cat5201
{
    /// <summary>
    /// 第一層語意判斷順便回答的三件事：這次要不要查即時資料、要不要推理分析、要不要寫程式。
    /// 由 OutputIntentResolver 跟輸出判斷同一次呼叫得出（多三個 JSON 欄位，不多一次 API）。
    ///
    /// 2026-11-05 模擬面試發現：輸出判斷早就交給模型了，能力規劃卻還是看「最新」「股價」這些字，
    /// 換一種說法（「台積電這禮拜收在多少」）或換一種語言就漏掉。現在能力規劃以這裡為準，
    /// 關鍵字只在模型沒給答案時當後援。
    /// </summary>
    /// <param name="NeedsCurrentDate">
    /// 答案要不要知道「今天是哪一天／現在幾點」（今天、這週末、最近、倒數、截止日…）。
    /// 需要時才從 UserClock 讀現在時間交給模型，不需要就不給（2026-11-06 使用者指示：需要的時候去讀，不需要就不管）。
    /// </param>
    public sealed record CapabilityNeeds(bool NeedsFreshFacts, bool NeedsReasoning, bool NeedsCode, bool NeedsCurrentDate = false);
}
