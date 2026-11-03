using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 2026-11-03 使用者要求：寫程式時程式碼要以檔案呈現（不是整段貼在輸出區）、能在畫布內預覽；
    /// 同時要求程式碼與書面報告時要分類好。
    /// </summary>
    public class CodeOutputTests
    {
        private static string Fence(string info, params string[] lines)
            => "```" + info + "\n" + string.Join("\n", lines) + "\n```";

        private static string[] Lines(int count, string prefix = "x = ")
            => Enumerable.Range(1, count).Select(i => prefix + i).ToArray();

        // ── 哪些區塊會變成檔案 ──

        [Fact]
        public void FileNameInFirstLineComment_BecomesThatFile_AndTextGetsAReference()
        {
            string answer = "這是爬蟲：\n\n" +
                            Fence("python", "# scraper.py", "import requests", "print(requests.get('https://example.com').status_code)") +
                            "\n\n執行方式：python scraper.py";

            var r = CodeFileExtractor.Extract(answer);

            var file = Assert.Single(r.Files);
            Assert.Equal("scraper.py", file.FileName);
            Assert.Contains("import requests", file.Content);
            Assert.DoesNotContain("import requests", r.Text);                // 程式碼不再貼在輸出區
            Assert.Contains("程式碼已存成檔案：`scraper.py`", r.Text);
            Assert.Contains("執行方式：python scraper.py", r.Text);          // 說明文字照留
        }

        [Fact]
        public void FileNameInInfoString_OrInLabelLine_IsUsed()
        {
            string answer =
                Fence("js app.js", "console.log(1);") + "\n\n" +
                "**style.css**\n" + Fence("css", "body { margin: 0; }") + "\n\n" +
                Fence("html title=\"index.html\"", "<html></html>");

            var r = CodeFileExtractor.Extract(answer);

            Assert.Equal(new[] { "app.js", "style.css", "index.html" }, r.Files.Select(f => f.FileName));
            Assert.DoesNotContain("**style.css**", r.Text);   // 只寫檔名的那一行併進參照，不重複
        }

        [Fact]
        public void ShortUnnamedSnippets_CommandsAndOutput_StayInTheText()
        {
            string answer =
                Fence("bash", "pip install requests") + "\n\n" +           // 指令
                Fence("python", "x = 1", "print(x)") + "\n\n" +            // 短片段
                Fence("", "200", "OK") + "\n\n" +                          // 執行結果
                Fence("json", Lines(12, "\"k\": ").ToArray());             // 沒檔名的資料

            var r = CodeFileExtractor.Extract(answer);

            Assert.Empty(r.Files);
            Assert.Equal(answer, r.Text);
        }

        [Fact]
        public void LongUnnamedCode_GetsADefaultName_DuplicatesAreNumbered()
        {
            string answer = Fence("python", Lines(6)) + "\n\n" + Fence("python", Lines(6));

            var r = CodeFileExtractor.Extract(answer);

            Assert.Equal(new[] { "main.py", "main_2.py" }, r.Files.Select(f => f.FileName));
        }

        [Fact]
        public void UnclosedFence_IsLeftAlone()
        {
            string answer = "說明\n```python\n# a.py\nprint(1)";
            var r = CodeFileExtractor.Extract(answer);

            Assert.Empty(r.Files);
            Assert.Equal(answer, r.Text);
        }

        [Theory]
        [InlineData("../../evil.py", "evil.py")]
        [InlineData("src/app.py", "src/app.py")]
        [InlineData("src\\util\\io.py", "src/util/io.py")]
        [InlineData("C:/Windows/x.py", null)]
        public void FileNames_CannotEscapeTheOutputFolder(string name, string? expected)
            => Assert.Equal(expected, CodeFileExtractor.SafeRelativePath(name));

        // ── 什麼時候要存成檔案 ──

        [Theory]
        [InlineData("寫一個 Python 爬蟲抓天氣資料", true)]
        [InlineData("幫我做一個可以算 BMI 的網頁", true)]
        [InlineData("write a function that reverses a string", true)]
        [InlineData("做一份 5 頁簡報，介紹澳洲特有美食", false)]
        [InlineData("apple 跟 happy 怎麼念", false)]                 // app、api 不能命中一般英文字
        [InlineData("寫一個 Python 爬蟲，不要檔案，直接貼在這裡", false)]
        public void WantsCodeFiles_FromTheRequest(string request, bool expected)
            => Assert.Equal(expected, CodeFileExtractor.WantsCodeFiles(request, NodeTaskMode.Chat));

        [Fact]
        public void CodeTaskMode_AlwaysWantsFiles()
            => Assert.True(CodeFileExtractor.WantsCodeFiles("幫我修這個", NodeTaskMode.Code));

        // ── 寫檔 ──

        [Fact]
        public void WriteCodeFiles_KeepsNamesTogetherInOneFolder_WithoutBom()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-code-" + Guid.NewGuid().ToString("N"));
            var files = new[]
            {
                new CodeFileExtractor.CodeFile("index.html", "html", "<link href=\"style.css\">\n"),
                new CodeFileExtractor.CodeFile("style.css", "css", "body{}\n"),
                new CodeFileExtractor.CodeFile("src/app.js", "js", "#!/usr/bin/env node\n"),
            };

            var written = GeneratedFileWriter.WriteCodeFiles(dir, "BMI 計算機", files, "test");

            Assert.All(written, w => Assert.True(w.Success, w.ErrorMessage));
            var folders = written.Select(w => Path.GetDirectoryName(w.FilePath)).ToList();
            Assert.Equal(folders[0], folders[1]);                                   // HTML 與 CSS 在同一層，相對路徑才接得上
            Assert.Equal(Path.Combine(folders[0]!, "src"), folders[2]);
            Assert.Equal("index.html", Path.GetFileName(written[0].FilePath));      // 檔名不加時間戳
            Assert.Equal("src/app.js", written[2].FileName);

            byte[] bytes = File.ReadAllBytes(written[2].FilePath);
            Assert.Equal((byte)'#', bytes[0]);                                     // 沒有 BOM，#! 才有效
        }

        // ── 分類 ──

        [Fact]
        public void OutputChips_AreLabeledAndGroupedByCategory()
        {
            var names = new[] { "main.py", "報告.docx", "index.html", "報告.pdf", "style.css", "簡報.pptx" };

            var sorted = OutputFileCategory.Sort(names, n => n);

            Assert.Equal(new[] { "簡報.pptx", "報告.docx", "報告.pdf", "main.py", "index.html", "style.css" }, sorted);
            Assert.Equal("程式碼", OutputFileCategory.Of("main.py").Label);
            Assert.Equal("報告", OutputFileCategory.Of("報告.docx").Label);
            Assert.Equal("網頁", OutputFileCategory.Of("index.html").Label);
        }

        // ── 預覽 ──

        [Fact]
        public void CodePreview_HighlightsAndEscapes()
        {
            string html = CodeHtmlRenderer.Build("app.py",
                "# 註解\ns = \"# 不是註解\"\nif x < 10:\n    print('<script>')\n");

            Assert.Contains("<span class='c'>", html);                 // 註解有上色
            Assert.Contains("<span class='k'>if</span>", html);        // 關鍵字
            Assert.Contains("&lt;script&gt;", html);                  // 程式碼裡的 HTML 被跳脫，不會被執行
            Assert.DoesNotContain("<script>", html);
            Assert.Contains("Python", html);
            Assert.Contains("4 行", html);

            // 字串裡的 # 不能被當成註解。
            string line = CodeHtmlRenderer.Highlight("s = \"# x\"", "a.py");
            Assert.DoesNotContain("class='c'", line);
        }

        [Fact]
        public void MarkupPreview_ApostropheInTextIsNotAString()
        {
            string line = CodeHtmlRenderer.Highlight("<p>don't stop</p>", "index.html");
            Assert.DoesNotContain("class='s'", line);
            Assert.Contains("class='t'", line);
        }

        // ── 下游拿得到程式碼 ──

        [Fact]
        public void Downstream_GetsTheCodeFileContents()
        {
            string dir = Path.Combine(Path.GetTempPath(), "cat5201-up-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string py = Path.Combine(dir, "app.py");
            File.WriteAllText(py, "print('hi')\n");
            string pptx = Path.Combine(dir, "deck.pptx");
            File.WriteAllText(pptx, "binary");

            string merged = UpstreamMerge.WithCodeFiles("> 程式碼已存成檔案：`app.py`", new[] { py, pptx });

            Assert.Contains("【檔案：app.py】", merged);
            Assert.Contains("print('hi')", merged);
            Assert.DoesNotContain("deck.pptx", merged);
        }

        // ── 走完整個 ExecuteAsync ──

        private sealed class Host : IAgentHost
        {
            public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cat5201-tests", Guid.NewGuid().ToString("N"));
            public bool IsAutoModelSelectionEnabled() => true;
            public bool IsAdvancedAutoResolverEnabled() => false;
            public PresentationEngine GetPresentationEngine() => PresentationEngine.Claude;
            public DocumentEngine GetDocumentEngine() => DocumentEngine.Builtin;
            public VeoModelTier GetVeoModelTier() => VeoModelTier.Lite;
            public string GetEffectiveVeoModel() => "";
            public string GetEffectiveVideoStylePrompt() => "";
            public IReadOnlyList<AttachmentInfo> GetAttachmentsForNode(INodeContext node) => Array.Empty<AttachmentInfo>();
            public IReadOnlyList<AttachmentInfo> GetEffectiveAttachmentsForNode(INodeContext node) => Array.Empty<AttachmentInfo>();
            public string GetAttachmentsRootDir() => Dir;
            public string GetGeneratedFilesDir() => Dir;
            public void SetLiveDecisionResolving(INodeContext node, NodeExecutionDecision decision) { }
            public Task<GenerationApproval> ConfirmGenerationAsync(OrchestrationTaskType t, OutputIntent? i, GenerationChoices c)
                => Task.FromResult(GenerationApproval.Accepted);
            public string GetNodeSelectedModel(INodeContext node) => "";
        }

        private sealed class Node : INodeContext
        {
            public Guid Id { get; } = Guid.NewGuid();
            public void SetLoadingHint(string? hint) { }
        }

        private sealed class Resolver : IExecutionDecisionResolver
        {
            public Task<NodeExecutionDecision> ResolveAsync(INodeContext n, string topText, CancellationToken ct)
                => Task.FromResult(new NodeExecutionDecision
                {
                    ModelId = AiModelRegistry.Default.Id,
                    RequestedModelId = AiModelRegistry.Default.Id,
                    TaskMode = NodeTaskMode.Chat,
                    Confidence = 1.0,
                });
        }

        private sealed class Finalizer : IDecisionFinalizer
        {
            public NodeExecutionDecision FinalizeDecision(NodeExecutionDecision d, AiFallbackExecutionResult e)
            {
                d.ActualModelId = string.IsNullOrWhiteSpace(e.ActualModelId) ? d.ModelId : e.ActualModelId;
                return d;
            }
        }

        private const string CodeAnswer =
            "這是一個計算 BMI 的小程式。\n\n" +
            "```python\n# bmi.py\ndef bmi(weight, height):\n    return weight / (height ** 2)\n\nprint(round(bmi(60, 1.7), 1))\n```\n\n" +
            "用 `python bmi.py` 執行，會印出 20.8。";

        private static async Task<(AgentExecutionResult Result, AgentWorkspace Workspace, ConcurrentBag<string> Prompts)> RunAsync(string request)
        {
            var prompts = new ConcurrentBag<string>();
            var host = new Host();
            var runtime = new AgentRuntime(host, new Resolver(),
                (node, prompt, decision, onDelta, streaming, ct) =>
                {
                    prompts.Add(prompt);
                    // 主回覆給程式碼；後續步驟（例如報告撰寫）拿到的是已換成檔案參照的文字，照它寫一段說明。
                    bool laterStep = prompt.Contains("程式碼已存成檔案", StringComparison.Ordinal);
                    return Task.FromResult(new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = laterStep ? "# BMI 計算程式說明\n\n程式以身高與體重計算 BMI，程式碼見 bmi.py。" : CodeAnswer,
                        ActualModelId = decision.ModelId,
                    });
                },
                new Finalizer());

            var workspace = new AgentWorkspace();
            var result = await runtime.ExecuteAsync(new AgentExecutionRequest
            {
                Node = new Node(),
                Agent = AgentRegistry.Get(null),
                TopText = request,
                SkipCapabilities = true,
                Workspace = workspace,
                CancellationToken = CancellationToken.None,
            });
            return (result, workspace, prompts);
        }

        private static List<GeneratedFilePayload> Files(AgentWorkspace ws)
            => ws.GetByType("generated_file").Select(x => x.Payload).OfType<GeneratedFilePayload>().Where(f => f.Success).ToList();

        [Fact]
        public async Task CodeRequest_SavesCodeAsFile_AndTheAnswerShowsAReference()
        {
            var (result, ws, prompts) = await RunAsync("寫一個 Python 程式計算 BMI");

            Assert.True(result.IsSuccess);
            var file = Assert.Single(Files(ws));
            Assert.Equal("bmi.py", file.FileName);
            Assert.Contains("def bmi", File.ReadAllText(file.FilePath));

            Assert.DoesNotContain("def bmi", result.FinalText);              // 程式碼不在輸出區
            Assert.Contains("bmi.py", result.FinalText);                     // 但看得到檔名
            Assert.Contains("會印出 20.8", result.FinalText);                 // 說明照留
            Assert.Contains(prompts, p => p.Contains("【程式碼輸出格式】"));  // 有請模型標檔名
        }

        /// <summary>
        /// 要求沒提到寫程式：不加「程式碼輸出格式」的指示。
        /// 但答案自己交付了一個有檔名的程式（例如作業要求寫在附件裡），照樣存成檔案——
        /// 2026-11-03 機器學習作業就是這種情形，只看要求那一句會漏掉。
        /// </summary>
        [Fact]
        public async Task NonCodeRequest_NoFormatInstruction_ButADeliveredFileIsStillSaved()
        {
            var (result, ws, prompts) = await RunAsync("介紹一下 BMI 是什麼");

            Assert.DoesNotContain(prompts, p => p.Contains("【程式碼輸出格式】"));
            Assert.Equal("bmi.py", Assert.Single(Files(ws)).FileName);
            Assert.DoesNotContain("def bmi", result.FinalText);
        }

        /// <summary>
        /// 程式碼＋書面報告：程式碼歸程式碼檔、報告只放說明，答案裡兩類分開列。
        /// </summary>
        [Fact]
        public async Task CodeAndReport_AreKeptApart()
        {
            var (result, ws, prompts) = await RunAsync("寫一個 Python 程式計算 BMI，並附上一份書面報告說明");

            // 程式碼只出現在主回覆的輸出；之後任何一步（報告撰寫等）的輸入都不能再帶著原始碼。
            Assert.DoesNotContain(prompts, p => p.Contains("def bmi", StringComparison.Ordinal));

            var files = Files(ws);
            var code = Assert.Single(files, f => f.FileName == "bmi.py");
            var docx = Assert.Single(files, f => f.FilePath.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));

            string report = ArtifactTextExtractor.ExtractDocx(docx.FilePath);
            Assert.DoesNotContain("def bmi", report);                        // 報告裡沒有整段原始碼
            Assert.Contains("bmi.py", report);                               // 只提到檔名

            Assert.Contains("程式碼檔案：bmi.py", result.FinalText);          // 答案裡程式碼另列一行
            Assert.DoesNotContain("def bmi", result.FinalText);
        }
    }
}
