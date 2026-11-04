using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Cat5201
{
    // MainWindow 的一部分：切換存檔時，執行中的存檔留在背景繼續跑（2026-11-04 使用者要求）。
    //
    // 原本切換存檔＝清空畫布、清空各節點的設定表、把「目前檔案」換掉。執行中的節點雖然還在背景 await，
    // 但它要查的附件、模型、上下游連線、產出資料夾全都變成新存檔的；跑完的結果只會存進新存檔；
    // 切回原存檔時又從 JSON 重建節點——等於被中止，連執行中的動畫與狀態也不見了。
    //
    // 做法：有節點在跑的存檔，切走時整包留在記憶體（同一批 NodeControl、連線、畫布上的元件），
    // 不重建、不取消，所以執行、串流文字、載入動畫、狀態都照常進行：
    //   - 各節點的設定表（附件、模型、任務模式、代理、自動流、執行紀錄）以節點 Id 為鍵，Id 全域唯一，
    //     切換時只清掉不屬於背景存檔的項目，背景節點照樣查得到自己的設定；
    //   - 依節點查上下游的函式看「目前畫面＋所有背景存檔」的連線；
    //   - 產出資料夾：每次執行開始時記下節點所屬的存檔（AsyncLocal），之後寫檔都寫到那個存檔的資料夾；
    //   - 背景節點內容變動時存回它自己的存檔檔案；全部跑完後最後存一次，就從記憶體釋放。
    //   - 切回背景中的存檔：直接把同一批節點掛回畫布，不從 JSON 重建。
    // 存檔清單上，有節點在跑的存檔名稱會有流光效果（FileItem.IsRunning），直到全部跑完。
    public partial class MainWindow
    {
        /// <summary>一個存檔的「目前路徑」。改名時改這個物件，執行中的節點記的是同一個物件，路徑會跟著更新。</summary>
        private sealed class ProjectRef
        {
            public string? Path;
        }

        /// <summary>切走時仍有節點在跑的存檔：整包留在記憶體。</summary>
        private sealed class ProjectSession
        {
            public ProjectRef Project = new();
            public List<UIElement> Children = new();
            public List<Connection> Connections = new();
            public int ZIndexCounter;
            public NodeControl? InitialNode;
            public bool FileNameLocked;
            public string LastAppliedAutoKeyword = "";
            public string LastInitialTopSnapshot = "";

            /// <summary>從什麼時候開始沒有節點在跑（null＝還在跑）。閒置夠久才釋放，見 SessionTimer_Tick。</summary>
            public DateTime? IdleSinceUtc;

            public IEnumerable<NodeControl> Nodes => Children.OfType<NodeControl>();
        }

        private ProjectRef _activeProject = new();
        private readonly List<ProjectSession> _backgroundSessions = new();

        /// <summary>這一次執行的節點屬於哪個存檔（在節點開始執行時設定，跟著這次執行的 async 流程走）。</summary>
        private static readonly AsyncLocal<ProjectRef?> _runProject = new();

        /// <summary>工作流程鏈是在哪個存檔開始的（工作流程鏈全域只有一條）。</summary>
        private ProjectRef? _chainProject;

        private DispatcherTimer? _sessionTimer;

        // ── 執行範圍 ──────────────────────────────────────────────

        /// <summary>
        /// 節點開始執行時呼叫：記下它屬於哪個存檔。之後這次執行裡的 GetGeneratedFilesDir() 都用這個存檔的資料夾，
        /// 就算使用者中途切到別的存檔也一樣。在 async 方法裡同步呼叫即可，結束時自動還原。
        /// </summary>
        internal void EnterRunProject(NodeControl node)
            => _runProject.Value = SessionOf(node)?.Project ?? _activeProject;

        /// <summary>這次執行（或目前畫面）所屬存檔的路徑。</summary>
        private string? EffectiveProjectPath => (_runProject.Value ?? _activeProject).Path;

        // ── 查詢 ──────────────────────────────────────────────────

        private ProjectSession? SessionOf(NodeControl? node)
            => node == null ? null : _backgroundSessions.FirstOrDefault(s => s.Children.Contains(node));

        private ProjectSession? SessionFor(string? path)
            => string.IsNullOrEmpty(path) ? null
                : _backgroundSessions.FirstOrDefault(s => string.Equals(s.Project.Path, path, StringComparison.OrdinalIgnoreCase));

        /// <summary>節點還「活著」：在目前畫面上，或在背景執行中的存檔裡。執行流程用它判斷要不要繼續。</summary>
        internal bool IsNodeLive(NodeControl? node)
            => node != null && (MainCanvas.Children.Contains(node) || SessionOf(node) != null);

        /// <summary>目前畫面加上所有背景存檔的連線。只給「依節點查上下游」用：節點物件全域唯一，不會查錯存檔。</summary>
        private IEnumerable<Connection> LiveConnections()
            => _connections.Concat(_backgroundSessions.SelectMany(s => s.Connections));

        /// <summary>這個存檔路徑是否有節點正在執行（目前畫面或背景）。</summary>
        internal bool IsProjectRunning(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (string.Equals(_activeProject.Path, path, StringComparison.OrdinalIgnoreCase))
                return ActiveProjectIsRunning();
            var s = SessionFor(path);
            return s != null && SessionIsRunning(s);
        }

        private bool ActiveProjectIsRunning()
            => MainCanvas.Children.OfType<NodeControl>().Any(n => n.IsGenerating) ||
               (IsWorkflowChainRunning && ReferenceEquals(_chainProject, _activeProject));

        private bool SessionIsRunning(ProjectSession s)
            => s.Nodes.Any(n => n.IsGenerating) ||
               (IsWorkflowChainRunning && ReferenceEquals(_chainProject, s.Project));

        // ── 切走：留在背景 ────────────────────────────────────────

        /// <summary>
        /// 要離開目前的存檔（開別的存檔、開新存檔）之前呼叫。有節點在跑就把整包留在背景，回傳 true；
        /// 沒有在跑就什麼都不做（照原本的方式清空、之後從 JSON 讀）。
        /// </summary>
        private bool StashActiveProjectIfRunning()
        {
            if (!_hasStarted || string.IsNullOrEmpty(_activeProject.Path) || !ActiveProjectIsRunning())
                return false;

            SaveState();   // 先存一次目前的樣子

            var s = new ProjectSession
            {
                Project = _activeProject,
                Children = MainCanvas.Children.Cast<UIElement>().ToList(),
                Connections = _connections.ToList(),
                ZIndexCounter = _zIndexCounter,
                InitialNode = _initialNode,
                FileNameLocked = _fileNameLockedByUser,
                LastAppliedAutoKeyword = _lastAppliedAutoKeyword ?? "",
                LastInitialTopSnapshot = _lastInitialTopSnapshot ?? "",
            };

            // 決策面板不要停在背景節點上
            if (_lastDecisionNode != null && s.Children.Contains(_lastDecisionNode)) _lastDecisionNode = null;
            if (_hoveredDecisionNode != null && s.Children.Contains(_hoveredDecisionNode)) _hoveredDecisionNode = null;

            MainCanvas.Children.Clear();
            _connections.Clear();
            _backgroundSessions.Add(s);
            _activeProject = new ProjectRef();   // 之後的「目前存檔」是新的物件；背景存檔保有原本的那個

            AppLog.Info("Project", $"「{DisplayNameFromPath(s.Project.Path!)}」有節點在執行，切換後留在背景繼續（{s.Nodes.Count()} 個節點）");
            EnsureSessionTimer();
            return true;
        }

        // ── 切回：掛回畫面 ────────────────────────────────────────

        /// <summary>要開的存檔正在背景執行：把同一批節點掛回畫布（不從 JSON 重建），回傳 true。</summary>
        private bool TryRestoreBackgroundSession(string path)
        {
            var s = SessionFor(path);
            if (s == null) return false;

            _suppressSave = true;
            try
            {
                StartUI.Visibility = Visibility.Collapsed;
                MainUI.Visibility = Visibility.Visible;
                _hasStarted = true;

                // 先清掉目前畫面（s 還在背景清單裡，它的節點設定會保留），再把 s 移出清單、掛回畫布
                ClearAll();
                _backgroundSessions.Remove(s);

                _activeProject = s.Project;
                foreach (var child in s.Children)
                    MainCanvas.Children.Add(child);
                _connections.AddRange(s.Connections);
                _zIndexCounter = s.ZIndexCounter;
                _initialNode = s.InitialNode;
                _fileNameLockedByUser = s.FileNameLocked;
                _lastAppliedAutoKeyword = s.LastAppliedAutoKeyword;
                _lastInitialTopSnapshot = s.LastInitialTopSnapshot;

                RefreshCurrentFileLabel();
                SyncDownstreamAutoModeRadios();
                SyncPresentationEngineRadios();
                SyncDocumentEngineRadios();
                EnsureApiStatusRows();
                UpdateAdvancedAutoResolverVisibility();
                UpdateDecisionPanelForCurrentMode();
            }
            finally
            {
                _suppressSave = false;
            }

            // 節點不在畫面上時沒有版面，連線的形狀要等掛回去量好之後重算
            RefreshConnectionsAfterLayout(s.Nodes.ToList());
            RefreshAllNodeAttachmentUIs();
            RefreshFileList();
            SelectFileInList(path);
            RefreshAllNodeModelSelectionUIs();
            Dispatcher.BeginInvoke(new Action(RestoreDecisionPanelAfterLoad), DispatcherPriority.Loaded);

            AppLog.Info("Project", $"切回「{DisplayNameFromPath(path)}」：沿用背景中的節點（仍在執行 {s.Nodes.Count(n => n.IsGenerating)} 個）");
            return true;
        }

        // ── 背景存檔的存檔與釋放 ──────────────────────────────────

        /// <summary>把背景存檔目前的樣子寫回它自己的檔案。</summary>
        private void SaveBackgroundSession(ProjectSession s)
        {
            if (string.IsNullOrEmpty(s.Project.Path)) return;
            try
            {
                var state = BuildState(s.Nodes.ToList(), s.Connections, s.InitialNode, s.FileNameLocked);
                Store.Save(state, s.Project.Path!);
            }
            catch (Exception ex)
            {
                AppLog.Warn("Project", $"背景存檔寫入失敗：{s.Project.Path}", ex);
            }
        }

        /// <summary>背景存檔全部跑完：最後存一次，從記憶體釋放（之後再開就照常從 JSON 讀）。</summary>
        private void ReleaseBackgroundSession(ProjectSession s)
        {
            SaveBackgroundSession(s);
            _backgroundSessions.Remove(s);

            var ids = s.Nodes.Select(n => n.Id).ToHashSet();
            RemoveNodeState(ids);

            AppLog.Info("Project", $"背景中的「{DisplayNameFromPath(s.Project.Path ?? "")}」已全部跑完，已存檔");
        }

        /// <summary>程式關閉時：背景存檔都存一次（節點會隨程式結束而停止，存下的是目前的進度）。</summary>
        private void SaveAllBackgroundSessions()
        {
            foreach (var s in _backgroundSessions.ToList())
                SaveBackgroundSession(s);
        }

        // ── 每節點設定表 ──────────────────────────────────────────

        /// <summary>
        /// 清掉「目前畫面」的每節點設定（切換存檔時用），背景存檔的節點設定保留。
        /// 原本是整張表 Clear()——背景節點會在執行到一半時查不到自己的附件、模型與執行紀錄。
        /// </summary>
        private void ClearPerNodeStateExceptBackground()
        {
            var keep = _backgroundSessions.SelectMany(s => s.Nodes).Select(n => n.Id).ToHashSet();
            RemoveNodeStateWhere(id => !keep.Contains(id));
        }

        private void RemoveNodeState(HashSet<Guid> ids) => RemoveNodeStateWhere(ids.Contains);

        private void RemoveNodeStateWhere(Func<Guid, bool> remove)
        {
            static void Prune<T>(Dictionary<Guid, T> d, Func<Guid, bool> r)
            {
                foreach (var k in d.Keys.Where(r).ToList()) d.Remove(k);
            }

            Prune(_attachmentsByNode, remove);
            Prune(_nodeModelsById, remove);
            Prune(_nodeTaskModesById, remove);
            Prune(_nodeAgentsById, remove);
            Prune(_autoFlowTemplatesByNode, remove);
            Prune(_autoFlowPoliciesByNode, remove);
            Prune(_collaborationModelsByNode, remove);
            _unsupportedDownstreamNodeIds.RemoveWhere(id => remove(id));
            _generatedDownstreamNodeIds.RemoveWhere(id => remove(id));
            _executionLogService.RemoveWhere(nodeId => Guid.TryParse(nodeId, out var g) && remove(g));
        }

        // ── 定時檢查：釋放跑完的背景存檔、更新清單的流光 ──────────

        private void EnsureSessionTimer()
        {
            if (_sessionTimer != null) return;
            _sessionTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };
            _sessionTimer.Tick += (_, __) => SessionTimer_Tick();
            _sessionTimer.Start();
        }

        /// <summary>
        /// 背景存檔要連續閒置多久才釋放。一個節點跑完、下一個自動執行的節點還沒開始之間有短暫空檔，
        /// 那時釋放的話，下一個節點就找不到自己的存檔（產出會寫到目前畫面的存檔）。
        /// </summary>
        private static readonly TimeSpan SessionIdleGrace = TimeSpan.FromSeconds(3);

        private void SessionTimer_Tick()
        {
            var now = DateTime.UtcNow;
            foreach (var s in _backgroundSessions.ToList())
            {
                if (SessionIsRunning(s))
                {
                    s.IdleSinceUtc = null;
                    continue;
                }

                s.IdleSinceUtc ??= now;
                if (now - s.IdleSinceUtc.Value >= SessionIdleGrace)
                    ReleaseBackgroundSession(s);
            }

            UpdateRunningFlagsInFileList();
        }

        /// <summary>存檔清單上，有節點在跑的存檔名稱顯示流光（目前畫面或背景都算）。</summary>
        private void UpdateRunningFlagsInFileList()
        {
            if (FileList?.ItemsSource is not IEnumerable<FileItem> items) return;
            foreach (var item in items)
                item.IsRunning = IsProjectRunning(item.FullPath);
        }
    }
}
