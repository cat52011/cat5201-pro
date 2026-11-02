using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// Claude 文件技能（與 Claude App 同級的簡報/報告產出）：不打 API 也能驗證的部分——
    /// 串流組回完整訊息（pause_turn 續跑要原樣回傳）、找出產出檔、挑最終交付檔、請求格式、提示詞。
    /// </summary>
    public class DocumentSkillsTests
    {
        private const string SampleStream =
@"event: message_start
data: {""type"":""message_start"",""message"":{""id"":""msg_1"",""type"":""message"",""role"":""assistant"",""model"":""claude-opus-5"",""content"":[],""stop_reason"":null,""usage"":{""input_tokens"":1200,""cache_read_input_tokens"":800,""output_tokens"":1}}}

event: content_block_start
data: {""type"":""content_block_start"",""index"":0,""content_block"":{""type"":""thinking"",""thinking"":"""",""signature"":""""}}

event: content_block_delta
data: {""type"":""content_block_delta"",""index"":0,""delta"":{""type"":""signature_delta"",""signature"":""sig-abc""}}

event: content_block_stop
data: {""type"":""content_block_stop"",""index"":0}

event: content_block_start
data: {""type"":""content_block_start"",""index"":1,""content_block"":{""type"":""server_tool_use"",""id"":""srvtoolu_1"",""name"":""bash_code_execution"",""input"":{}}}

event: content_block_delta
data: {""type"":""content_block_delta"",""index"":1,""delta"":{""type"":""input_json_delta"",""partial_json"":""{\""command\"": \""cp deck.pptx""}}

event: content_block_delta
data: {""type"":""content_block_delta"",""index"":1,""delta"":{""type"":""input_json_delta"",""partial_json"":"" outputs/\""}""}}

event: content_block_stop
data: {""type"":""content_block_stop"",""index"":1}

event: content_block_start
data: {""type"":""content_block_start"",""index"":2,""content_block"":{""type"":""bash_code_execution_tool_result"",""tool_use_id"":""srvtoolu_1"",""content"":{""type"":""bash_code_execution_result"",""stdout"":"""",""stderr"":"""",""return_code"":0,""content"":[{""type"":""bash_code_execution_output"",""file_id"":""file_deck""}]}}}

event: content_block_stop
data: {""type"":""content_block_stop"",""index"":2}

event: content_block_start
data: {""type"":""content_block_start"",""index"":3,""content_block"":{""type"":""text"",""text"":""""}}

event: content_block_delta
data: {""type"":""content_block_delta"",""index"":3,""delta"":{""type"":""text_delta"",""text"":""已完成 12 頁""}}

event: content_block_delta
data: {""type"":""content_block_delta"",""index"":3,""delta"":{""type"":""text_delta"",""text"":""簡報。""}}

event: content_block_stop
data: {""type"":""content_block_stop"",""index"":3}

event: message_delta
data: {""type"":""message_delta"",""delta"":{""stop_reason"":""pause_turn"",""container"":{""id"":""container_xyz"",""expires_at"":""2026-09-17T12:00:00Z""}},""usage"":{""output_tokens"":3456}}

event: message_stop
data: {""type"":""message_stop""}

";

        private static async Task<ClaudeDocumentSkillsService.SseMessageAssembler> Assemble()
        {
            var assembler = new ClaudeDocumentSkillsService.SseMessageAssembler();
            await assembler.ReadAsync(new StringReader(SampleStream), default);
            return assembler;
        }

        [Fact]
        public async Task Stream_AssemblesFullMessage_ForPauseTurnReplay()
        {
            var a = await Assemble();
            var m = a.Message;
            var content = (JsonArray)m["content"]!;

            Assert.Equal("pause_turn", (string?)m["stop_reason"]);
            Assert.Equal("container_xyz", ClaudeDocumentSkillsService.ReadContainerId(m));
            Assert.Equal(4, content.Count);
            Assert.Equal("sig-abc", (string?)content[0]!["signature"]);                  // 思考簽章要原樣回傳
            Assert.Equal("cp deck.pptx outputs/", (string?)content[1]!["input"]!["command"]); // 工具參數由片段組回
            Assert.Equal("已完成 12 頁簡報。", ClaudeDocumentSkillsService.ExtractText(content));
            Assert.Equal(1200, (int?)m["usage"]!["input_tokens"]);
            Assert.Equal(3456, (int?)m["usage"]!["output_tokens"]);                    // message_delta 更新輸出用量
            Assert.Equal("", a.StreamError);
        }

        [Fact]
        public async Task FileIds_AreFoundInToolResults()
        {
            var a = await Assemble();
            var ids = ClaudeDocumentSkillsService.ExtractFileIds((JsonArray)a.Message["content"]!);
            Assert.Equal(new[] { "file_deck" }, ids);
        }

        [Fact]
        public void FinalText_SkipsNarrationBeforeTools()
        {
            // 實測：Claude 會先說「我先讀技能說明」再動工具；給使用者的收尾說明只取最後工具之後的文字。
            var content = new JsonArray
            {
                new JsonObject { ["type"] = "text", ["text"] = "I'll start by reading the pptx skill." },
                new JsonObject { ["type"] = "server_tool_use", ["id"] = "t1" },
                new JsonObject { ["type"] = "bash_code_execution_tool_result", ["tool_use_id"] = "t1" },
                new JsonObject { ["type"] = "text", ["text"] = "已完成簡報與 PDF。" },
            };
            Assert.Equal("已完成簡報與 PDF。", ClaudeDocumentSkillsService.ExtractFinalText(content));
        }

        [Fact]
        public void StreamErrorEvent_IsSurfaced()
        {
            var a = new ClaudeDocumentSkillsService.SseMessageAssembler();
            a.Apply("error", @"{""type"":""error"",""error"":{""type"":""overloaded_error"",""message"":""Overloaded""}}");
            Assert.Equal("Overloaded", a.StreamError);
        }

        [Fact]
        public void Deliverables_PairPdfsByName_KeepLatestVersion_DropScratchFiles()
        {
            var produced = new List<(string, string)>
            {
                ("f1", "thumbnail-1.png"),
                ("f2", "台積電分析.pptx"),        // 第一版
                ("f3", "台積電分析.pptx"),        // 修正後最終版
                ("f4", "台積電分析.pdf"),
                ("f5", "台積電完整報告.docx"),
                ("f6", "台積電完整報告.pdf"),
                ("f7", "slides.html"),
            };

            var picked = ClaudeDocumentSkillsService.SelectDeliverables(
                produced, new[] { ".pptx", ".docx", ".pdf" });

            Assert.Equal(new[] { "f3", "f5", "f4", "f6" }, picked.Select(p => p.Id).ToArray());
        }

        [Fact]
        public void Deliverables_SinglePrimary_TakesLastPdfEvenIfNamedDifferently()
        {
            var picked = ClaudeDocumentSkillsService.SelectDeliverables(
                new List<(string, string)> { ("a", "deck.pptx"), ("b", "export.pdf") },
                new[] { ".pptx", ".pdf" });

            Assert.Equal(new[] { "a", "b" }, picked.Select(p => p.Id).ToArray());
        }

        [Fact]
        public void RequestBody_HasSkillsContainerToolAndReusesContainer()
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hi" } };
            var body = ClaudeDocumentSkillsService.BuildRequestBody(
                "claude-opus-5", "sys", messages, new[] { "pptx", "docx", "pptx" }, "container_xyz",
                ClaudeDocumentSkillsService.ToolTypeCurrent, useFallbacks: true);

            Assert.Equal("claude-opus-5", (string?)body["model"]);
            Assert.True((bool)body["stream"]!);
            Assert.Equal("adaptive", (string?)body["thinking"]!["type"]);
            Assert.Equal("container_xyz", (string?)body["container"]!["id"]);
            var skills = (JsonArray)body["container"]!["skills"]!;
            Assert.Equal(2, skills.Count);                                      // 重複的技能去重
            Assert.Equal("anthropic", (string?)skills[0]!["type"]);
            Assert.Equal("latest", (string?)skills[0]!["version"]);
            Assert.Equal("code_execution_20260521", (string?)body["tools"]![0]!["type"]);
            Assert.Equal("default", (string?)body["fallbacks"]);
            Assert.Equal("ephemeral", (string?)body["cache_control"]!["type"]);

            var noFallback = ClaudeDocumentSkillsService.BuildRequestBody(
                "claude-opus-5", "sys", messages, new[] { "pptx" }, null,
                ClaudeDocumentSkillsService.ToolTypeLegacy, useFallbacks: false);
            Assert.Null(noFallback["fallbacks"]);
            Assert.Null(noFallback["container"]!["id"]);
        }

        /// <summary>
        /// 沙箱迴圈的錢幾乎全花在「每一步都把前面所有 stdout 再當輸入送一次」。
        /// 少了這段，一份簡報 2026-10-15 實測跑到 US$10.48。
        /// </summary>
        [Fact]
        public void RequestBody_ClearsOldToolResults_ToStopPayingForThemEveryStep()
        {
            var messages = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hi" } };
            var body = ClaudeDocumentSkillsService.BuildRequestBody(
                "claude-sonnet-5", "sys", messages, new[] { "docx" }, null,
                ClaudeDocumentSkillsService.ToolTypeCurrent, useFallbacks: true);

            var edit = (JsonObject)body["context_management"]!["edits"]![0]!;
            Assert.Equal("clear_tool_uses_20250919", (string?)edit["type"]);
            Assert.Equal("input_tokens", (string?)edit["trigger"]!["type"]);
            Assert.True((int)edit["keep"]!["value"]! > 0);            // 最近幾步一定要留著
            Assert.True((int)edit["clear_at_least"]!["value"]! > 0);  // 清太少不值得打斷快取
            Assert.Null(edit["clear_tool_inputs"]);                   // 它寫過的程式碼要留著

            // 門檻必須拉得夠高。清除會讓快取前綴失效、整段重寫，而讀快取只收一折、
            // 寫快取收 1.25 倍——門檻太低時清除反而更貴（2026-10-27 實測 US$4.64 → US$8.27）。
            Assert.True((int)edit["trigger"]!["value"]! >= 200_000,
                "清除門檻太低會讓快取一直重寫，比不清還貴");
            Assert.True((int)edit["clear_at_least"]!["value"]! >= 50_000,
                "每次至少要清掉夠多，才值得打斷快取");

            // 伺服器不收這個參數時要能整包拿掉重送。
            var off = ClaudeDocumentSkillsService.BuildRequestBody(
                "claude-sonnet-5", "sys", messages, new[] { "docx" }, null,
                ClaudeDocumentSkillsService.ToolTypeCurrent, useFallbacks: true,
                useCache: true, useContextEditing: false);
            Assert.Null(off["context_management"]);
        }

        /// <summary>
        /// 重試時只有最後一塊會變，前面照片與主提示那幾塊一字不差——
        /// 這正是快取前綴能不能命中的分界線。
        /// </summary>
        [Fact]
        public void RetryNote_IsItsOwnBlock_SoThePrefixStaysCacheable()
        {
            var photos = new[]
            {
                new ClaudeDocumentSkillsService.ContainerFile("file_1", "cover.png")
            };

            var first = ClaudeDocumentSkillsService.BuildFirstMessageContent("做一份京都簡報", null, photos);
            var retry = ClaudeDocumentSkillsService.BuildFirstMessageContent(
                "做一份京都簡報", "【重要】上一次沒有輸出檔案。", photos);

            Assert.Equal(2, first.Count);     // 照片 + 主提示
            Assert.Equal(3, retry.Count);     // 照片 + 主提示 + 重試提示

            // 前面每一塊都必須逐字相同，否則前綴就斷了
            for (int i = 0; i < first.Count; i++)
                Assert.Equal(first[i]!.ToJsonString(), retry[i]!.ToJsonString());

            Assert.Equal("container_upload", (string?)retry[0]!["type"]);
            Assert.Equal("做一份京都簡報", (string?)retry[1]!["text"]);
            Assert.Equal("【重要】上一次沒有輸出檔案。", (string?)retry[2]!["text"]);

            // 空白的重試提示不要多塞一塊空白訊息
            Assert.Equal(2, ClaudeDocumentSkillsService.BuildFirstMessageContent("x", "   ", photos).Count);
        }

        /// <summary>
        /// 個人化關掉「同時產出對照 PDF」時，技能路徑也要照辦。
        /// 以前只有內建路徑看這個設定，技能路徑照樣叫 Claude 轉 PDF——
        /// 多出來的那幾步 LibreOffice 轉檔，後面每一輪都要再當輸入買一次。
        /// </summary>
        [Fact]
        public void CompanionPdfOff_IsHonouredByTheSkillsPath()
        {
            var r = new DocumentSkillsPrompt.Request
            {
                UserInput = "做一份澳洲美食簡報",
                MainContent = "袋鼠肉、維吉麥醬……",
                WantsDeck = true,
                WantsPdf = false,
            };

            Assert.DoesNotContain(".pdf", DocumentSkillsPrompt.WantedExtensions(r));
            Assert.Contains(".pptx", DocumentSkillsPrompt.WantedExtensions(r));

            string prompt = DocumentSkillsPrompt.BuildUserPrompt(r);
            Assert.DoesNotContain("同內容 .pdf", prompt);
            Assert.DoesNotContain("轉一份同內容的 PDF", prompt);

            string system = DocumentSkillsPrompt.SystemPromptFor(wantsPdf: false);
            Assert.DoesNotContain(DocumentSkillsPrompt.PdfDeliveryRule, system);
            Assert.Contains(DocumentSkillsPrompt.NoPdfDeliveryRule, system);

            // 開著的時候維持原樣。
            var withPdf = r with { WantsPdf = true };
            Assert.Contains(".pdf", DocumentSkillsPrompt.WantedExtensions(withPdf));
            Assert.Contains("同內容 .pdf", DocumentSkillsPrompt.BuildUserPrompt(withPdf));
            Assert.Contains(DocumentSkillsPrompt.PdfDeliveryRule, DocumentSkillsPrompt.SystemPromptFor(wantsPdf: true));
        }

        /// <summary>費用上限是「已經花了這麼多」才回的訊息，再重試一次就是再花一次。</summary>
        [Fact]
        public void CostCeiling_IsNotRetried()
        {
            var hitCeiling = new ClaudeDocumentSkillsService.Result
            {
                Success = false,
                ToolSteps = 12,
                Rounds = 3,
                ErrorMessage = "已達這份文件的費用上限 US$3.00（實際已用 US$3.12，3 輪 / 12 步），已停止以免繼續累積費用。"
            };

            Assert.False(ClaudeDocumentSkillsService.IsRetryable(hitCeiling));
        }


        [Fact]
        public void Prompt_ListsRequestedFiles_SlideCount_AndSources()
        {
            var r = new DocumentSkillsPrompt.Request
            {
                UserInput = "做一份台積電 10 頁簡報和報告",
                MainContent = "台積電 Q2 營收……",
                WantsDeck = true,
                WantsReport = true,
                RequestedSlides = 10,
                Sources = new List<(string, string)> { ("TSMC Quarterly Results", "https://investor.tsmc.com/") }
            };

            string prompt = DocumentSkillsPrompt.BuildUserPrompt(r);
            // 頁數＝整份頁數（含封面與來源頁）——使用者說十頁就是十頁。
            // 2026-10-29：封面＋內容 N 頁，資料來源另加 1 頁。
            Assert.Contains("封面＋內容正好 10 頁", prompt);
            Assert.Contains("整份共 11 頁", prompt);
            Assert.Contains("書面報告 .docx", prompt);
            Assert.Contains("https://investor.tsmc.com/", prompt);
            Assert.Contains("台積電 Q2 營收", prompt);
            // 簡報不掛 pptx 技能（它一個人就注入 118 萬 token，2026-10-15 實測），
            // 改用沙箱預裝的 python-pptx + 明確指示；docx / xlsx 技能照掛。
            Assert.Equal(new[] { "docx" }, DocumentSkillsPrompt.SkillIds(r));
            Assert.Contains("python-pptx", prompt);
            Assert.Contains(".pdf", DocumentSkillsPrompt.WantedExtensions(r));
            Assert.DoesNotContain(".xlsx", DocumentSkillsPrompt.WantedExtensions(r));
        }

        [Fact]
        public void Engine_DefaultsToBuiltin_AndRespectsOpusBlock()
        {
            // 2026-10-13 起預設改成內建快速版：最佳品質模式做簡報三次全失敗，
            // 單次 8～14 分鐘而且每次失敗照樣收錢，使用者的結論是
            // 「不如原本的簡報，不管品質、時間還是成本」。等它穩定了再換回去。
            Assert.Equal(DocumentEngine.Builtin, DocumentEngineHelper.Parse(null));
            Assert.Equal(DocumentEngine.Builtin, DocumentEngineHelper.Parse(""));

            // 使用者自己選過最佳品質模式時要照舊（存檔裡的設定不能被預設值蓋掉）。
            Assert.Equal(DocumentEngine.ClaudeSkills, DocumentEngineHelper.Parse("ClaudeSkills"));
            Assert.Equal(DocumentEngine.Builtin, DocumentEngineHelper.Parse("Builtin"));
            Assert.Equal("claude-opus-5-5", DocumentEngineHelper.ResolveSkillsModel(blockOpus: false));
            // 使用者自己封鎖 Opus 時尊重；2026-10-29 起退到的是 Sonnet 5.5。
            Assert.Equal("claude-sonnet-5-5", DocumentEngineHelper.ResolveSkillsModel(blockOpus: true));
        }

        [Fact]
        public async Task 每收到一行都會回報一次活著()
        {
            // 「沒動靜就放棄」的計時器完全靠這個回報活著。沒有它，連線掛住跟
            // 真的在跑長工作看起來一模一樣，只能用很長的總上限去等——
            // 2026-10-13 的紀錄裡，四次簡報產出各白等了 8 分鐘。
            int beats = 0;
            var assembler = new ClaudeDocumentSkillsService.SseMessageAssembler();

            await assembler.ReadAsync(new StringReader(SampleStream), default, onActivity: () => beats++);

            // 每一個 data: 行至少要回報一次。
            int dataLines = System.Text.RegularExpressions.Regex.Matches(SampleStream, "data:").Count;
            Assert.True(beats >= dataLines, $"只回報了 {beats} 次，資料行有 {dataLines} 行");
        }

        [Fact]
        public async Task 串流讀取會真的被取消()
        {
            // ReadLineAsync 不帶 token 的話，沒有資料時會卡在那一行，
            // 迴圈開頭的取消檢查永遠輪不到——這正是原本卡 8 分鐘的機制。
            using var cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            var assembler = new ClaudeDocumentSkillsService.SseMessageAssembler();

            await Assert.ThrowsAnyAsync<System.OperationCanceledException>(
                () => assembler.ReadAsync(new StringReader(SampleStream), cts.Token));
        }

    }
}
