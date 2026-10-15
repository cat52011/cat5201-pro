# cat5201 — Codex Handoff Document
**Generated:** 2026-06-20  
**Purpose:** Full project status summary for AI coding assistant onboarding.

---

## What This Project Is

A WPF desktop application (Windows, .NET 8, namespace `test`) that functions as a **multi-model AI workspace**, not a simple chat tool. Users can assign tasks to nodes on a canvas; each node runs a multi-agent orchestration pipeline and produces structured workspace artifacts, generated files, and downstream nodes.

**Target:** Commercial-grade MVP by September 2026.

---

## Tech Stack

- **Runtime:** .NET 8.0-windows, WPF (XAML)
- **AI Providers:** OpenAI (GPT-4o, gpt-image-2, Veo via Gemini API), Claude (claude-3-7-sonnet, opus), Perplexity (sonar-pro, sonar-deep-research), Gemini (2.5-flash, 2.5-pro)
- **File generation:** OpenXML SDK (PPTX, DOCX), QuestPDF Community (PDF reports, deck PDF)
- **Video:** VeoVideoService (Google Veo 3.1 via predictLongRunning), FfmpegService (FastCut concat)
- **Image:** OpenAIImageService (gpt-image-2)
- **Persistence:** JSON files in `_preferences.json`, `_generated/` subfolder for output files
- **No database.** All session state in memory; workspace artifacts in `AgentWorkspace`.

---

## Repository Layout (key files)

### Orchestration & Routing
| File | Role |
|------|------|
| `OrchestrationPlanner.cs` | Detects task type, builds capability order |
| `AgentRuntime.cs` | Main execution engine — runs capability pipeline, calls providers, generates files |
| `NodeExecutionCoreService.cs` | WPF-side executor; wires UI to AgentRuntime |
| `NodeService.cs` | Node state management, preferences, memory |
| `AgentRegistry.cs` | Maps task type → agent definition |
| `NodeTaskRoutingRegistry.cs` | Maps task → pipeline capabilities |
| `AiServiceRouter.cs` | Routes model ID → provider instance |

### AI Providers
| File | Role |
|------|------|
| `ClaudeChatService.cs` / `ClaudeProvider.cs` | Anthropic Claude |
| `OpenAIChatService.cs` / `OpenAiProvider.cs` | OpenAI GPT |
| `GeminiChatService.cs` / `GeminiProvider.cs` | Google Gemini |
| `PerplexityService.cs` / `PerplexitySonarService.cs` | Perplexity search |
| `ApiKeyStore.cs` | **Centralized API key resolution** — env var first, then user-entered (DPAPI-encrypted), then empty (→ fallback chain) |
| `OpenAIImageService.cs` | gpt-image-2 image generation |
| `VeoVideoService.cs` | Veo 3.1 video generation + native extend (up to 148s) |
| `OpenAIVideoService.cs` | Legacy OpenAI video (Sora removed) |
| `FfmpegService.cs` | FastCut mode — trim+concat mp4 segments |

### Model Registry & Cost
| File | Role |
|------|------|
| `AiModelRegistry.cs` | All model definitions, capability tags, cost tiers |
| `AiModelDefinition.cs` | Model metadata: id, provider, capabilities, cost tier |
| `AiModelCapability.cs` | Capability constants: Search, Vision, Image, Video, Code, Cheap, Premium |
| `AiCostTier.cs` | Economy / Standard / Premium tiers |
| `ModelCostEstimator.cs` | Per-token + per-image cost lookup; `ImageCostDisplay()` for UI hints |
| `AiAutoCostPolicy.cs` | Auto mode: block Opus + Perplexity Deep unless explicitly requested |
| `AiCapabilityGuard.cs` | Fallback: pick cheapest capable model |
| `AiFallbackPlanner.cs` | Last-resort fallback chain |

### File Generation
| File | Role |
|------|------|
| `PptxBuilder.cs` | PPTX via OpenXML — business theme (navy/teal), cover+content+footer |
| `DeckPdfBuilder.cs` | Deck PDF via QuestPDF — matches PptxBuilder theme |
| `PdfReportBuilder.cs` | Report PDF via QuestPDF — CJK font embedded |
| `DocxReportBuilder.cs` | DOCX via OpenXML — H1-3, lists, bold, blockquote, tables |
| `GeneratedFileWriter.cs` | Writes files to `_generated/` with sanitized names + timestamps |
| `GeneratedFilePayload.cs` | Workspace artifact for generated files |
| `NotebookLmBundleBuilder.cs` | Builds NotebookLM-importable .txt from workspace facts |

### Presentation Pipeline
| File | Role |
|------|------|
| `PresentationOutlineBuilder.cs` | Topic → structured outline; `EnforceSlideCount()` safety net |
| `PresentationOutlinePayload.cs` | Outline schema: title, topic, slides (cover/content/sources) |
| `GammaPresentationService.cs` | Gamma API integration — dormant until `GAMMA_API_KEY` set |

### Video Pipeline
| File | Role |
|------|------|
| `VideoPlanBuilder.cs` | Claude-as-director → structured `VideoPlanPayload` |
| `VideoPlanPayload.cs` | Director output: scenes, segment_prompts, style, duration |
| `VideoStyle.cs` | Default cinematic style prompt (surrealist/oneiric aesthetic) |
| `VeoModels.cs` | Tier constants: Standard=$0.40/s, Fast=$0.15/s, Lite=$0.05/s |

### Output Detection
| File | Role |
|------|------|
| `OutputFormatDetector.cs` | Keyword detection for: presentation, report, spreadsheet, NotebookLM bundle |
| `OutputIntent.cs` | Intent enum for output routing |
| `OutputIn[formatDetector].cs` | (same file) |

### Workspace & Artifacts
| File | Role |
|------|------|
| `AgentWorkspace.cs` | Holds all capability artifacts for a node execution |
| `AgentWorkspaceItem.cs` | Single artifact item with payload + metadata |
| `AgentWorkspaceArtifactRecord.cs` | Display record: kind, status, labels, preview, dependencies |
| `AgentWorkspaceBuilder.cs` | Factory for workspace items from capability data |
| `ArtifactStatus.cs` | Status constants: draft, ready, validated, exported, failed |
| `ArtifactHtmlRenderer.cs` | Renders artifact HTML for decision window |

### Memory & Personalization
| File | Role |
|------|------|
| `NodeMemoryService.cs` | Preference storage, recall, episodic memory |
| `MemoryRecallStats.cs` | Memory usage stats |
| `_preferences.json` | User preferences (VideoStyleOverride, model choices, etc.) |

### Downstream Nodes / Workflow
| File | Role |
|------|------|
| `DownstreamNodePlanBuilder.cs` | Builds downstream node proposals by task type |
| `DownstreamAutoMode.cs` | Auto-expansion mode enum |
| `WorkflowPlanBuilder.cs` | Workflow schema builder |

### UI
| File | Role |
|------|------|
| `MainWindow.xaml` / `.cs` | Main canvas, node layout, personalization dialog |
| `NodeControl.xaml` / `.cs` | Per-node UI: input, output, decision window, status |
| `NodeDecisionViewBuilder.cs` | Decision window content renderer |
| `NodeDecisionStepViewData.cs` | Decision step data model |

### Code Agent
| File | Role |
|------|------|
| `CodeCapability.cs` | Code task execution |
| `CodeContextExtractor.cs` | Anchor-window + chunk-map context extraction |
| `CodeTaskAssessor.cs` | Risk/cost assessment before large code tasks |
| `CodeTaskAssessmentPayload.cs` | Assessment result payload |

---

## Completed Features (§1–§13 all done)

### §1 Regression Tests ✅
Fixed checklist covering: finance, general, attachment, multi-node, model, error, workspace tests.

### §2 Orchestrator v1 ✅
Full task state machine: detect task → select pipeline → execute capabilities → write workspace → final synthesis. Statuses: pending/running/success/failed/partial/skipped.

### §3 Workflow Schema ✅
Workflow artifacts with nodes/edges/step I-O. Replay (same input, full rerun), Regenerate Answer (skip capabilities, rerun synthesis only), Resume from failed step.

### §4 Automatic Downstream Nodes ✅
Large tasks auto-split into downstream canvas nodes with edges. Chain executor: RunManualWorkflowChainAsync follows GetFirstDownstreamNode, `{{input}}` injects upstream output. Stop/skip/rerun-from-step via context menu.

### §5 Workspace & Artifact v2 ✅
Unified artifact schema. Status lifecycle (draft→exported). Source metadata (agent/model/capability/node). Timestamps. Dependencies (final depends on facts/search; file depends on final). 220-char previews. Export/copy per artifact. Product-grade card surface (not debug dump).

### §6 File Generation v1 ✅
- Markdown report (MarkdownReportBuilder)
- PDF export (PdfReportBuilder, QuestPDF, CJK font embedded, validated %PDF-)
- DOCX export (DocxReportBuilder, OpenXML, H1-3/lists/bold/tables, validated PK zip)
- PPTX (PptxBuilder, OpenXML, 0 validation errors)
- Stable `_generated/` path handling with timestamps

### §7 Presentation Agent ✅
- Topic → outline → PPTX + DeckPDF
- Slide count detection (3/5/10 via keyword + EnforceSlideCount safety net)
- **Business theme:** cover (navy bg + centered title + teal accent line + subtitle + optional cover image), content (navy title bar + teal separator + bullets + footer page number)
- Cover image embedding from image generation pipeline
- Single-slide regenerate
- NotebookLM B 方案: `NotebookLmBundleBuilder` generates importable .txt with facts + source URLs

### §8 Image Generation v1 ✅
- gpt-image-2 via OpenAIImageService
- Workspace preview + export to `_generated/.png`
- Cover image auto-embedded in presentations
- Status lifecycle: queued → generating → completed/failed
- **Cost hint:** per-image pricing table in `ModelCostEstimator.ImageCostUsd/ImageCostDisplay`

### §9 Video Generation v1 ✅
- Veo 3.1 (veo-3.1-generate-preview) via Google Gemini API (predictLongRunning + poll)
- **FastCut mode:** trailer/montage keywords → per-shot Veo 4s clips → ffmpeg trim+concat (max 6 shots)
- **Continuous mode:** Veo native extend, base 8s + segments of +7s, up to 148s total
- Claude-as-director: generates `VideoPlanPayload` (scenes/segment_prompts/style) from user input + upstream synthesis
- Veo model tier selection: Standard/Fast/Lite (cost-aware)
- Long-running progress UI: "影片生成中 N% · 已等待Xs"
- Cost estimate shown before generation

**Known Veo API quirks (already fixed):**
1. `numberOfVideos` field → 400 error (must be omitted)
2. Extend source must use `video.uri` reference (not inlineData)
3. Extend source must be 16:9
4. Extend occasionally returns done=true with no video (handled as partial success)

**Default video style (as of 2026-06-20):** Surrealist/oneiric aesthetic — faded Kodachrome film stock, tactile grain, gauzy diffused light, mythic symbolism, Tarkovsky time sense. See `VideoStyle.DefaultCinematicPrompt`.

### §10 Multi-Model Expansion ✅
- Unified `AiModelRegistry` + `IAiProvider` interface
- Capability tags: Search, Vision, Image, Video, Code, Cheap, Premium
- Gemini live (2.5-flash economy, 2.5-pro standard)
- Fallback policy: cheapest capable model
- Model cost tier badges in UI

### §11 Memory v1 ✅
- Preference extraction from conversation (format, language, model cost, workflows)
- Episodic execution history
- Manual clear via "當前記憶" toggle panel (per-item delete + bulk clear)
- Memory recall visualized in decision window

### §12 Code Agent v1.5 (partial — 2 items explicitly deferred)
✅ Snapshot/diff/validation foundation  
✅ Large-task cost/risk warning (CodeTaskAssessor)  
✅ Bug listing → select one to fix  
✅ Targeted context extraction (anchor windows + chunk map)  
✅ Patch validation + repair loop  
⏸ **sandbox apply** (deferred — requires decision on AI write permissions)  
⏸ **Codex-like multi-file editing** (deferred — post-MVP)  

### §13 Product UX ✅
- Friendly error messages (timeout/key/quota/network/server/refused)
- Loading/running/success states with colored node borders
- Real token + cost display (all 4 providers)
- Readable execution log ("執行摘要" plain-language card)
- Clear Manual/Auto mode hints

---

## Pending Work

### §14 Demo & Delivery Polish (ALL ITEMS PENDING — biggest remaining gap)

These are end-to-end demo scripts, not new features. The goal is to verify each pipeline runs cleanly and produce screenshots/recordings for September MVP presentation.

| # | Demo | Pipeline to validate |
|---|------|---------------------|
| 1 | Stock analysis | Finance pipeline → Perplexity → workspace facts → final synthesis |
| 2 | Learning plan PDF | General task → PdfReportBuilder → `_generated/.pdf` |
| 3 | Research topic → presentation | Perplexity research → PresentationOutlineBuilder → PptxBuilder + DeckPdfBuilder |
| 4 | Image generation → presentation | OpenAIImageService → cover image → PptxBuilder with embedded image |
| 5 | Attachment summary → report | File attachment → summarize → DocxReportBuilder → `_generated/.docx` |

Additional:
- **Document known limitations** (a limitations.md or in-app info panel)
- **Package demo-ready version** (clean build, API keys configured, test run verified)
- **Freeze stable September build** (git tag `v0.9-mvp`)

---

## Waiting on External Dependencies

| Item | Condition |
|------|-----------|
| Gamma presentation (§G) | `GAMMA_API_KEY` — September |
| Gemini Drive/Docs/Sheets integration | When needed; extension points ready in §10 |

---

## Key Conventions

- **Namespace:** `test` (entire project)
- **File output:** always to `_generated/` subfolder; `GeneratedFileWriter` handles path + timestamp
- **No short-circuit of main synthesis:** video/output tasks always run after `execution.IsSuccess` — user sees both main synthesis text and file generation note
- **Personalization:** user settings in `_preferences.json`, never overridden by system; `VideoStyleOverride` = empty string means use `VideoStyle.DefaultCinematicPrompt`
- **Cost policy:** Auto mode blocks Opus + Deep Search; only user can override
- **API keys (2026-06-20):** never read env vars directly in services — use `ApiKeyStore.Resolve("XXX_API_KEY")` / `ResolveAny(...)`. Resolution order: env var → user-entered (in-app, DPAPI-encrypted in `_config/_apikeys.json`) → empty. Empty → service constructor throws → `ExecuteWithFallbackAsync` auto-switches to next available model and shows it in the decision window → all fail → friendly error. Settings gear opens a 2-tab dialog: 個人化 / API. After saving keys call `AiServiceRouter.ResetServices()`. CAT5201_* config flags stay as direct env reads.
- **XAML naming:** `x:Name` matches C# property names exactly (e.g. `MemoryListPanel`, `PreferenceList`, `ClearAllMemoryButton`)
- **Build:** `dotnet build` from `C:\claudework\cat5201`; 0 warnings expected
- **Sync:** after changes, robocopy to `D:\desk\college\final\cat5201` (excludes `.git .vs bin obj docs docs_bak *.user *.suo .gitignore`)
