using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace Cat5201
{
    /// <summary>
    /// 決策窗 Timeline 渲染器(第 2 刀,拆上帝物件):步驟卡/技術細節抽屜/Workspace 產出卡/
    /// 複製與匯出動作,自 MainWindow 原樣搬出(~1,300 行)。
    /// 依賴以建構子注入:host 面板、對話框 owner、開檔動作、產出資料夾——不回頭抓 MainWindow。
    /// </summary>
    internal sealed class DecisionTimelineRenderer
    {
        private readonly Panel _host;
        private readonly Window _owner;
        private readonly Action<string> _openGeneratedFile;
        private readonly Func<string> _getGeneratedFilesDir;
        private readonly Action _refreshDecisionView;                 // 步驟卡展開/收合後,請宿主重繪當前節點的決策視圖
        private readonly Action<string> _materializeDownstreamPlan;   // downstream_node_plan 卡片的「生成節點」動作(業務,留宿主)

        // 步驟卡展開狀態(面板級,不隨節點切換重置)
        private readonly HashSet<string> _expandedDecisionStepKeys = new();

        public DecisionTimelineRenderer(
            Panel host, Window owner, Action<string> openGeneratedFile, Func<string> getGeneratedFilesDir,
            Action refreshDecisionView, Action<string> materializeDownstreamPlan)
        {
            _host = host;
            _owner = owner;
            _openGeneratedFile = openGeneratedFile;
            _getGeneratedFilesDir = getGeneratedFilesDir;
            _refreshDecisionView = refreshDecisionView;
            _materializeDownstreamPlan = materializeDownstreamPlan;
        }

        // 同 MainWindow.CreateBrush:hex 轉刷,錯誤退 fallback。
        private static SolidColorBrush CreateBrush(string? hex, string fallbackHex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            }
            catch { }

            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex)!);
        }

        private void ApplyActiveTimelineVisual(
    Border cardBorder,
    Border dotOuter,
    Border dotInner,
    NodeDecisionStepState state)
        {
            if (cardBorder == null || dotOuter == null || dotInner == null)
                return;

            var pulseBrush = GetStepBrush(state);

            cardBorder.BorderThickness = new Thickness(1.6);

            var shadow = cardBorder.Effect as DropShadowEffect;
            if (shadow == null)
            {
                shadow = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.18,
                    Color = Colors.Black
                };
                cardBorder.Effect = shadow;
            }

            shadow.Color = pulseBrush.Color;
            shadow.BlurRadius = 22;
            shadow.Opacity = 0.22;

            var borderAnim = new ThicknessAnimation
            {
                From = new Thickness(1.6),
                To = new Thickness(2.4),
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            cardBorder.BeginAnimation(Border.BorderThicknessProperty, borderAnim);

            var shadowBlurAnim = new DoubleAnimation
            {
                From = 18,
                To = 28,
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, shadowBlurAnim);

            var shadowOpacityAnim = new DoubleAnimation
            {
                From = 0.14,
                To = 0.28,
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, shadowOpacityAnim);

            var dotScale = new ScaleTransform(1.0, 1.0);
            dotOuter.RenderTransformOrigin = new Point(0.5, 0.5);
            dotOuter.RenderTransform = dotScale;

            var dotScaleAnim = new DoubleAnimation
            {
                From = 1.0,
                To = 1.18,
                Duration = TimeSpan.FromMilliseconds(700),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            dotScale.BeginAnimation(ScaleTransform.ScaleXProperty, dotScaleAnim);
            dotScale.BeginAnimation(ScaleTransform.ScaleYProperty, dotScaleAnim);

            var innerOpacityAnim = new DoubleAnimation
            {
                From = 0.65,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(700),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            dotInner.BeginAnimation(UIElement.OpacityProperty, innerOpacityAnim);
        }

        private void ClearTimelineAnimations(Border cardBorder, Border dotOuter, Border dotInner)
        {
            if (cardBorder != null)
            {
                cardBorder.BeginAnimation(Border.BorderThicknessProperty, null);

                if (cardBorder.Effect is DropShadowEffect shadow)
                {
                    shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
                    shadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                }
            }

            if (dotOuter?.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
            }

            if (dotInner != null)
            {
                dotInner.BeginAnimation(UIElement.OpacityProperty, null);
                dotInner.Opacity = 1.0;
            }
        }

        // #16 資訊三層化：技術步驟預設收進抽屜。面板級狀態（不隨節點切換重置，使用者開過就維持開）。
        private bool _isDecisionTechExpanded;
        private IReadOnlyList<NodeDecisionStepViewData>? _lastTimelineSteps;

        public void Render(IReadOnlyList<NodeDecisionStepViewData> steps)
        {
            _host.Dispatcher.Invoke(() =>
            {
                if (_host == null)
                    return;

                _lastTimelineSteps = steps;
                _host.Children.Clear();

                if (steps == null || steps.Count == 0)
                {
                    var emptyBorder = new Border
                    {
                        Background = CreateBrush("#FAFAFA", "#FAFAFA"),
                        BorderBrush = CreateBrush("#E9E9E9", "#E9E9E9"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(12),
                        Padding = new Thickness(12),
                        Child = new TextBlock
                        {
                            Text = "尚無 Decision Timeline",
                            FontSize = 12,
                            Foreground = CreateBrush("#7A7A7A", "#7A7A7A")
                        }
                    };

                    _host.Children.Add(emptyBorder);
                    return;
                }

                // 第 1+2 層（人看的：執行摘要 / Model Selection / 參與的 AI…）直接渲染；
                // 第 3 層（IsTechnical）收進「技術細節」抽屜，點標題列展開/收合。
                // 注意：Warning/Error 的技術步驟不收（例如 fallback 警告使用者必須看見）。
                var visible = new List<NodeDecisionStepViewData>();
                var technical = new List<NodeDecisionStepViewData>();
                foreach (var s in steps)
                {
                    if (s.IsTechnical && s.State != NodeDecisionStepState.Warning && s.State != NodeDecisionStepState.Error && !s.IsActive)
                        technical.Add(s);
                    else
                        visible.Add(s);
                }

                int renderIndex = 0;
                int totalVisible = visible.Count;
                for (int i = 0; i < visible.Count; i++)
                {
                    var item = CreateDecisionTimelineItem(
                        step: visible[i],
                        index: renderIndex++,
                        isLast: i == totalVisible - 1 && technical.Count == 0,
                        isFirst: i == 0);

                    _host.Children.Add(item);
                }

                if (technical.Count == 0)
                    return;

                // 抽屜標題列
                var drawerHeader = new Border
                {
                    Background = CreateBrush("#F6F7FA", "#F6F7FA"),
                    BorderBrush = CreateBrush("#E5E8EF", "#E5E8EF"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 4, 0, 4),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock
                    {
                        Text = _isDecisionTechExpanded
                            ? $"🔧 技術細節（{technical.Count} 步）　收合 ▲"
                            : $"🔧 技術細節（{technical.Count} 步）　展開 ▼",
                        FontSize = 12,
                        FontWeight = FontWeights.Medium,
                        Foreground = CreateBrush("#5A6478", "#5A6478")
                    }
                };
                drawerHeader.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    _isDecisionTechExpanded = !_isDecisionTechExpanded;
                    if (_lastTimelineSteps != null)
                        Render(_lastTimelineSteps); // 重繪同一批步驟
                };
                _host.Children.Add(drawerHeader);

                if (_isDecisionTechExpanded)
                {
                    for (int i = 0; i < technical.Count; i++)
                    {
                        var item = CreateDecisionTimelineItem(
                            step: technical[i],
                            index: renderIndex++,
                            isLast: i == technical.Count - 1,
                            isFirst: false);

                        _host.Children.Add(item);
                    }
                }
            });
        }

        private FrameworkElement CreateDecisionTimelineItem(
    NodeDecisionStepViewData step,
    int index,
    bool isLast,
    bool isFirst)
        {
            string safeTitle = step?.Title ?? "";
            string safeDetail = step?.Detail ?? "";
            var safeState = step?.State ?? NodeDecisionStepState.Info;
            bool safeHighlight = step?.Highlight == true;
            bool safeIsActive = step?.IsActive == true;
            bool safeIsExpandable = step?.IsExpandable == true;
            var safeDetailLines = step?.DetailLines ?? Array.Empty<string>();
            string stepKey = $"{index}:{safeTitle}:{safeDetail}";
            bool isExpanded = _expandedDecisionStepKeys.Contains(stepKey);

            var root = new Grid
            {
                Margin = new Thickness(0, 0, 0, isLast ? 0 : 12)
            };

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ===== 左側 timeline 區 =====
            var timelineGrid = new Grid
            {
                Width = 20
            };
            timelineGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            timelineGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            if (!isFirst)
            {
                var topLine = new Border
                {
                    Width = 2,
                    Height = 10,
                    Background = CreateBrush("#D9DDE4", "#D9DDE4"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetRow(topLine, 0);
                timelineGrid.Children.Add(topLine);
            }

            var dotOuter = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(999),
                Background = CreateBrush("#FFFFFF", "#FFFFFF"),
                BorderBrush = GetStepBrush(safeState),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var dotInner = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(999),
                Background = GetStepBrush(safeState),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            dotOuter.Child = dotInner;
            Grid.SetRow(dotOuter, 0);
            timelineGrid.Children.Add(dotOuter);

            if (!isLast)
            {
                var bottomLine = new Border
                {
                    Width = 2,
                    Background = CreateBrush("#D9DDE4", "#D9DDE4"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                Grid.SetRow(bottomLine, 1);
                timelineGrid.Children.Add(bottomLine);
            }

            Grid.SetColumn(timelineGrid, 0);
            root.Children.Add(timelineGrid);

            // ===== 右側卡片 =====
            var cardBorder = new Border
            {
                Background = safeHighlight
                    ? CreateBrush("#F6FAFF", "#F6FAFF")
                    : CreateBrush("#FFFFFF", "#FFFFFF"),
                BorderBrush = GetStepBorderBrush(safeState, safeHighlight || safeIsActive),
                BorderThickness = new Thickness(safeIsActive ? 1.6 : 1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 10, 12, 10),
                Cursor = safeIsExpandable ? Cursors.Hand : Cursors.Arrow
            };

            var shadow = new DropShadowEffect
            {
                BlurRadius = safeIsActive ? 20 : (safeHighlight ? 14 : 10),
                ShadowDepth = 0,
                Opacity = safeIsActive ? 0.18 : (safeHighlight ? 0.14 : 0.08),
                Color = safeIsActive ? GetStepBrush(safeState).Color : Colors.Black
            };
            cardBorder.Effect = shadow;

            var contentPanel = new StackPanel();

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var indexBadge = new Border
            {
                Background = GetStepSoftBrush(safeState),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = (index + 1).ToString(),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = GetStepBrush(safeState),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var titleText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(safeTitle) ? "-" : safeTitle,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#232323", "#232323"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            titleStack.Children.Add(indexBadge);
            titleStack.Children.Add(titleText);

            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var stateBadge = new Border
            {
                Background = GetStepSoftBrush(safeState),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = safeIsActive ? "Running" : GetStepStateLabel(safeState),
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = GetStepBrush(safeState)
                }
            };

            Grid.SetColumn(stateBadge, 1);
            headerGrid.Children.Add(stateBadge);

            contentPanel.Children.Add(headerGrid);

            var detailText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(safeDetail) ? "-" : safeDetail,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = CreateBrush("#5D5D5D", "#5D5D5D"),
                TextWrapping = TextWrapping.Wrap
            };
            contentPanel.Children.Add(detailText);

            if (safeIsExpandable)
            {
                var actionRow = new DockPanel
                {
                    Margin = new Thickness(0, 8, 0, 0),
                    LastChildFill = false
                };

                var expandHint = new TextBlock
                {
                    Text = isExpanded ? "收合詳細資訊 ▲" : "展開詳細資訊 ▼",
                    FontSize = 11.5,
                    Foreground = CreateBrush("#6F6F6F", "#6F6F6F"),
                    FontWeight = FontWeights.Medium
                };

                DockPanel.SetDock(expandHint, Dock.Right);
                actionRow.Children.Add(expandHint);
                contentPanel.Children.Add(actionRow);
            }

            if (isExpanded && safeDetailLines.Count > 0)
            {
                var detailHost = new StackPanel
                {
                    Margin = new Thickness(0, 10, 0, 0)
                };

                var separator = new Border
                {
                    Height = 1,
                    Background = CreateBrush("#ECEFF4", "#ECEFF4"),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                detailHost.Children.Add(separator);

                if (IsWorkspaceStep(step))
                {
                    var workspaceArtifacts = step?.WorkspaceArtifacts;
                    if (workspaceArtifacts != null && workspaceArtifacts.Count > 0)
                        detailHost.Children.Add(CreateWorkspaceProductSurface(workspaceArtifacts));
                    else
                        detailHost.Children.Add(CreateWorkspaceInspector(safeDetailLines));
                }
                else
                {
                    foreach (var line in safeDetailLines)
                    {
                        detailHost.Children.Add(new Border
                        {
                            Background = CreateBrush("#FAFBFD", "#FAFBFD"),
                            BorderBrush = CreateBrush("#EEF1F4", "#EEF1F4"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(8),
                            Padding = new Thickness(8, 6, 8, 6),
                            Margin = new Thickness(0, 0, 0, 6),
                            Child = new TextBlock
                            {
                                Text = string.IsNullOrWhiteSpace(line) ? "-" : line,
                                FontSize = 11.5,
                                Foreground = CreateBrush("#666666", "#666666"),
                                TextWrapping = TextWrapping.Wrap
                            }
                        });
                    }
                }

                contentPanel.Children.Add(detailHost);
            }

            cardBorder.Child = contentPanel;

            AttachCopyContextMenu(
                cardBorder,
                "複製此決策區塊",
                () => BuildDecisionStepCopyText(step, index));

            if (safeIsExpandable)
            {
                cardBorder.MouseLeftButtonUp += (_, __) =>
                {
                    if (_expandedDecisionStepKeys.Contains(stepKey))
                        _expandedDecisionStepKeys.Remove(stepKey);
                    else
                        _expandedDecisionStepKeys.Add(stepKey);

                    _refreshDecisionView();
                };

                cardBorder.MouseEnter += (_, __) =>
                {
                    if (!safeIsActive)
                    {
                        cardBorder.Background = safeHighlight
                            ? CreateBrush("#F0F7FF", "#F0F7FF")
                            : CreateBrush("#FAFAFA", "#FAFAFA");
                    }
                };

                cardBorder.MouseLeave += (_, __) =>
                {
                    if (!safeIsActive)
                    {
                        cardBorder.Background = safeHighlight
                            ? CreateBrush("#F6FAFF", "#F6FAFF")
                            : CreateBrush("#FFFFFF", "#FFFFFF");
                    }
                };
            }

            if (safeIsActive)
            {
                ApplyActiveTimelineVisual(cardBorder, dotOuter, dotInner, safeState);

                cardBorder.Background = safeHighlight
                    ? CreateBrush("#EEF6FF", "#EEF6FF")
                    : CreateBrush("#F8FBFF", "#F8FBFF");
            }
            else
            {
                ClearTimelineAnimations(cardBorder, dotOuter, dotInner);
            }

            Grid.SetColumn(cardBorder, 2);
            root.Children.Add(cardBorder);

            return root;
        }

        private static bool IsWorkspaceStep(NodeDecisionStepViewData? step)
        {
            return string.Equals(step?.Title, "Workspace", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDecisionStepCopyText(
            NodeDecisionStepViewData? step,
            int index)
        {
            if (step == null)
                return "";

            var lines = new List<string>
            {
                $"{index + 1}. {SafeCopy(step.Title)}",
                $"State: {step.State}" + (step.IsActive ? " / Running" : ""),
                $"Detail: {SafeCopy(step.Detail)}"
            };

            if (step.DetailLines != null && step.DetailLines.Count > 0)
            {
                lines.Add("");
                lines.Add("Details:");
                lines.AddRange(step.DetailLines.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string SafeCopy(string? text)
            => string.IsNullOrWhiteSpace(text) ? "-" : text.Trim();

        // Workspace v2：從結構化 artifact 紀錄渲染產品化卡片（非 re-parse 文字行）。
        private FrameworkElement CreateWorkspaceProductSurface(IReadOnlyList<AgentWorkspaceArtifactRecord> records)
        {
            var root = new StackPanel();

            var safe = (records ?? Array.Empty<AgentWorkspaceArtifactRecord>())
                .Where(x => x != null)
                .ToList();

            if (safe.Count == 0)
            {
                root.Children.Add(CreateWorkspaceTextCard("本次沒有產出物。", muted: true));
                return root;
            }

            var visible = safe.Where(x => x.IsUserVisible).ToList();
            var internalItems = safe.Where(x => !x.IsUserVisible).ToList();

            root.Children.Add(new TextBlock
            {
                Text = $"共 {safe.Count} 項產出物，{visible.Count} 項對使用者可見。",
                FontSize = 11.5,
                Foreground = CreateBrush("#57606A", "#57606A"),
                Margin = new Thickness(0, 2, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });

            foreach (var r in visible)
                root.Children.Add(CreateProductArtifactCard(r, dimmed: false));

            if (internalItems.Count > 0)
            {
                root.Children.Add(CreateWorkspaceSectionLabel($"內部中繼資料（{internalItems.Count}）"));
                foreach (var r in internalItems)
                    root.Children.Add(CreateProductArtifactCard(r, dimmed: true));
            }

            return root;
        }

        private Border CreateProductArtifactCard(AgentWorkspaceArtifactRecord r, bool dimmed)
        {
            var (emoji, accentHex, accentSoftHex) = ArtifactKindVisual(r.ArtifactKind, r.ContentFormat, r.FormatLabel);

            var panel = new StackPanel();

            // ── 標題列：種類圖示 chip + 標題/小標 + 狀態徽章（靠右）──
            var header = new DockPanel { LastChildFill = true };

            var (statusBg, statusFg) = ArtifactStatus.Colors(r.Status);
            var statusBadge = CreateWorkspaceBadge(
                string.IsNullOrWhiteSpace(r.StatusLabel) ? ArtifactStatus.ToLabel(r.Status) : r.StatusLabel,
                statusBg, statusFg);
            statusBadge.Margin = new Thickness(6, 1, 0, 0);
            statusBadge.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(statusBadge, Dock.Right);
            header.Children.Add(statusBadge);

            var iconChip = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(9),
                Background = CreateBrush(accentSoftHex, accentSoftHex),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = emoji,
                    FontSize = 17,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            DockPanel.SetDock(iconChip, Dock.Left);
            header.Children.Add(iconChip);

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(r.Title) ? r.KindLabel : r.Title,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush(dimmed ? "#6B7280" : "#1A2333", "#1A2333"),
                TextWrapping = TextWrapping.Wrap
            });

            // 小標：種類 · 格式 · 模型（淡色一行）
            var subParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.KindLabel)) subParts.Add(r.KindLabel);
            if (!string.IsNullOrWhiteSpace(r.FormatLabel)) subParts.Add(r.FormatLabel);
            if (!string.IsNullOrWhiteSpace(r.ModelId)) subParts.Add(GetArtifactModelDisplay(r.ModelId));
            if (subParts.Count > 0)
            {
                titleStack.Children.Add(new TextBlock
                {
                    Text = string.Join("   ·   ", subParts),
                    FontSize = 11,
                    Foreground = CreateBrush("#8A94A6", "#8A94A6"),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }
            header.Children.Add(titleStack);
            panel.Children.Add(header);

            // ── 次要徽章（事實數 / 內部）──
            if (r.FactCount > 0 || !r.IsUserVisible)
            {
                var badges = new WrapPanel { Margin = new Thickness(0, 9, 0, 0) };
                if (r.FactCount > 0)
                    badges.Children.Add(CreateWorkspaceBadge(
                        string.IsNullOrWhiteSpace(r.VerificationLabel) ? $"{r.FactCount} 項事實" : r.VerificationLabel,
                        "#FFF6E5", "#9A6700"));
                if (!r.IsUserVisible)
                    badges.Children.Add(CreateWorkspaceBadge("內部中繼", "#F2F4F7", "#888888"));
                panel.Children.Add(badges);
            }

            // ── 預覽：嵌入式淡底卡 ──
            if (!string.IsNullOrWhiteSpace(r.Preview))
            {
                panel.Children.Add(new Border
                {
                    Background = CreateBrush("#F8FAFC", "#F8FAFC"),
                    BorderBrush = CreateBrush("#EDF1F6", "#EDF1F6"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 9, 0, 0),
                    Child = new TextBlock
                    {
                        Text = r.Preview,
                        FontSize = 11.5,
                        Foreground = CreateBrush(dimmed ? "#8A94A6" : "#43536C", "#43536C"),
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }

            // ── 來源 / 時間：footer 淡字 ──
            var metaParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.SourceAgentId))
                metaParts.Add($"代理 {r.SourceAgentId}");
            if (!string.IsNullOrWhiteSpace(r.CapabilityId))
                metaParts.Add($"能力 {r.CapabilityId}");
            if (!string.IsNullOrWhiteSpace(r.CreatedAtLocalText))
                metaParts.Add(r.CreatedAtLocalText);

            if (metaParts.Count > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = string.Join("   ·   ", metaParts),
                    FontSize = 10.5,
                    Foreground = CreateBrush("#9AA4B2", "#9AA4B2"),
                    Margin = new Thickness(0, 8, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            // ── 依賴 ──
            if (r.DependsOn != null && r.DependsOn.Count > 0)
            {
                var dep = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
                dep.Children.Add(new TextBlock
                {
                    Text = "依賴",
                    FontSize = 10.5,
                    Foreground = CreateBrush("#9AA4B2", "#9AA4B2"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 5)
                });
                foreach (var d in r.DependsOn.Where(x => !string.IsNullOrWhiteSpace(x)))
                    dep.Children.Add(CreateWorkspaceBadge(ArtifactDependencyLabel(d), "#F4F0FF", "#6B4FBB"));
                panel.Children.Add(dep);
            }

            // ── 動作 chips：複製 / 匯出 / 開啟檔案 ──
            var actions = new WrapPanel { Margin = new Thickness(0, 11, 0, 0) };
            actions.Children.Add(CreateWorkspaceActionButton("📋 複製", accentHex, () => CopyTextToClipboard(BuildArtifactCopyText(r))));
            actions.Children.Add(CreateWorkspaceActionButton("💾 匯出", accentHex, () => ExportArtifactRecord(r)));
            if (!string.IsNullOrWhiteSpace(r.FilePath) && File.Exists(r.FilePath))
                actions.Children.Add(CreateWorkspaceActionButton("📂 開啟檔案", accentHex, () => _openGeneratedFile(r.FilePath)));
            panel.Children.Add(actions);

            var card = new Border
            {
                Background = CreateBrush(dimmed ? "#FBFCFE" : "#FFFFFF", "#FFFFFF"),
                BorderBrush = CreateBrush(dimmed ? "#EDF0F4" : "#E4EAF2", "#E4EAF2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(13),
                Margin = new Thickness(0, 0, 0, 10),
                Child = panel
            };

            if (!dimmed)
            {
                card.Effect = new DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 10,
                    ShadowDepth = 1,
                    Direction = 270,
                    Opacity = 0.07
                };
            }

            AttachCopyContextMenu(card, "複製此產出物", () => BuildArtifactCopyText(r));

            return card;
        }

        // 依產出物種類給一個視覺識別（emoji + 主色 + 淡底色），讓 Workspace 一眼分辨是簡報/圖片/影片/文件…
        private static (string Emoji, string Accent, string AccentSoft) ArtifactKindVisual(
            string? kind, string? format, string? formatLabel)
        {
            string k = (kind ?? "").Trim().ToLowerInvariant();
            string f = (format ?? "").Trim().ToLowerInvariant();
            string fl = formatLabel ?? "";

            bool Has(params string[] needles) =>
                needles.Any(n => k.Contains(n) || f.Contains(n));

            if (Has("present", "pptx", "slide", "deck") || fl.Contains("簡報"))
                return ("📊", "#7C3AED", "#F1ECFE");
            if (Has("image", "png", "jpg", "jpeg") || fl.Contains("圖"))
                return ("🖼", "#DB2777", "#FCE7F3");
            if (Has("video", "media", "mp4") || fl.Contains("影片"))
                return ("🎬", "#4F46E5", "#EAEBFE");
            if (Has("doc", "pdf", "report", "word") || fl.Contains("文件") || fl.Contains("報告"))
                return ("📄", "#2563EB", "#E6F0FE");
            if (Has("fact", "valid", "verify") || fl.Contains("事實") || fl.Contains("驗證"))
                return ("✅", "#059669", "#E4F6EE");
            if (Has("code") || fl.Contains("程式"))
                return ("💻", "#475569", "#EEF1F5");
            if (Has("search", "research") || fl.Contains("搜尋"))
                return ("🔍", "#0891B2", "#E2F5F9");
            if (Has("plan", "workflow") || fl.Contains("計畫") || fl.Contains("流程"))
                return ("🗂", "#B45309", "#FCEFDD");

            return ("📦", "#475467", "#F1F4F8");
        }

        // 動作 chip：用 Border 自繪（避免預設 Button 灰底 chrome），帶 hover。
        private FrameworkElement CreateWorkspaceActionButton(string label, string accentHex, Action onClick)
        {
            var bg = CreateBrush("#F4F7FC", "#F4F7FC");
            var bgHover = CreateBrush("#E7EEFA", "#E7EEFA");

            var chip = new Border
            {
                Background = bg,
                BorderBrush = CreateBrush("#DCE5F2", "#DCE5F2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(0, 0, 7, 0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock
                {
                    Text = label,
                    FontSize = 11.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = CreateBrush(accentHex, accentHex)
                }
            };

            chip.MouseEnter += (_, __) => chip.Background = bgHover;
            chip.MouseLeave += (_, __) => chip.Background = bg;
            chip.MouseLeftButtonUp += (_, __) =>
            {
                try { onClick?.Invoke(); }
                catch { /* 動作失敗不應讓決策窗崩潰 */ }
            };

            return chip;
        }

        private static string GetArtifactModelDisplay(string modelId)
        {
            var def = AiModelHelper.GetDefinition(modelId);
            if (!string.IsNullOrWhiteSpace(def.DisplayName))
                return def.DisplayName;
            return string.IsNullOrWhiteSpace(modelId) ? "-" : modelId;
        }

        private static string ArtifactDependencyLabel(string itemType)
        {
            return (itemType ?? "").Trim().ToLowerInvariant() switch
            {
                "verified_facts" => "事實",
                "search_summary" => "搜尋",
                "final_synthesis" => "最終答案",
                "reasoning_analysis" or "code_analysis" => "分析",
                _ => string.IsNullOrWhiteSpace(itemType) ? "上游" : itemType
            };
        }

        private void CopyTextToClipboard(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            try { Clipboard.SetText(text); }
            catch { /* Clipboard 可能被其他程式暫時鎖住 */ }
        }

        private static string BuildArtifactCopyText(AgentWorkspaceArtifactRecord r)
        {
            if (r == null)
                return "";

            var lines = new List<string>
            {
                $"{r.Title}",
                $"類型：{r.KindLabel} / {r.FormatLabel} / 狀態：{(string.IsNullOrWhiteSpace(r.StatusLabel) ? ArtifactStatus.ToLabel(r.Status) : r.StatusLabel)}"
            };

            var meta = new List<string>();
            if (!string.IsNullOrWhiteSpace(r.SourceAgentId)) meta.Add($"代理 {r.SourceAgentId}");
            if (!string.IsNullOrWhiteSpace(r.ModelId)) meta.Add($"模型 {r.ModelId}");
            if (!string.IsNullOrWhiteSpace(r.CapabilityId)) meta.Add($"能力 {r.CapabilityId}");
            if (!string.IsNullOrWhiteSpace(r.CreatedAtLocalText)) meta.Add(r.CreatedAtLocalText);
            if (meta.Count > 0)
                lines.Add("來源：" + string.Join(" · ", meta));

            if (r.DependsOn != null && r.DependsOn.Count > 0)
                lines.Add("依賴：" + string.Join(", ", r.DependsOn));

            if (!string.IsNullOrWhiteSpace(r.FilePath))
                lines.Add("檔案：" + r.FilePath);

            if (!string.IsNullOrWhiteSpace(r.Preview))
            {
                lines.Add("");
                lines.Add(r.Preview);
            }

            return string.Join(Environment.NewLine, lines);
        }

        // 把單一 artifact 匯出成 _generated 內的 .txt（產品化「匯出」動作）。
        private void ExportArtifactRecord(AgentWorkspaceArtifactRecord r)
        {
            if (r == null)
                return;

            // 已落地成檔的 artifact：直接開啟既有檔案，不重複匯出。
            if (!string.IsNullOrWhiteSpace(r.FilePath) && File.Exists(r.FilePath))
            {
                _openGeneratedFile(r.FilePath);
                return;
            }

            try
            {
                Directory.CreateDirectory(_getGeneratedFilesDir());

                string baseName = string.IsNullOrWhiteSpace(r.Title) ? r.KindLabel : r.Title;
                string safeName = SanitizeArtifactFileName(baseName);
                string fileName = $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
                string fullPath = System.IO.Path.Combine(_getGeneratedFilesDir(), fileName);

                File.WriteAllText(fullPath, BuildArtifactCopyText(r), new System.Text.UTF8Encoding(true));

                _openGeneratedFile(fullPath);
            }
            catch (Exception ex)
            {
                MenuConfirmDialog.ShowMessage(_owner, "錯誤", $"匯出產出物失敗：{ex.Message}", _owner);
            }
        }

        private static string SanitizeArtifactFileName(string name)
        {
            string s = (name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(s))
                s = "artifact";

            foreach (var c in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(c, '_');

            return s.Length <= 40 ? s : s.Substring(0, 40);
        }

        private FrameworkElement CreateWorkspaceInspector(IReadOnlyList<string> lines)
        {
            var root = new StackPanel();

            if (lines == null || lines.Count == 0)
            {
                root.Children.Add(CreateWorkspaceTextCard("-", muted: true));
                return root;
            }

            var currentArtifactFacts = new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 0)
            };

            Border? currentArtifactCard = null;
            List<string>? currentArtifactLines = null;

            foreach (var raw in lines)
            {
                string trimmed = (raw ?? "").Trim();

                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (trimmed.Equals("--- Artifacts ---", StringComparison.OrdinalIgnoreCase))
                {
                    root.Children.Add(CreateWorkspaceSectionLabel("Artifacts"));
                    continue;
                }

                if (trimmed.StartsWith("Artifact:", StringComparison.OrdinalIgnoreCase))
                {
                    var artifactLines = new List<string> { trimmed };
                    currentArtifactLines = artifactLines;

                    currentArtifactFacts = new StackPanel
                    {
                        Margin = new Thickness(0, 8, 0, 0)
                    };

                    currentArtifactCard = CreateArtifactCard(
                        trimmed,
                        currentArtifactFacts,
                        () => string.Join(Environment.NewLine, artifactLines),
                        trimmed.Contains("Type: downstream_node_plan", StringComparison.OrdinalIgnoreCase)
                            ? () => _materializeDownstreamPlan(
                                string.Join(Environment.NewLine, artifactLines))
                            : null);

                    root.Children.Add(currentArtifactCard);
                    continue;
                }

                if (trimmed.StartsWith("VerifiedFacts:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateWorkspaceTextCard(trimmed, muted: true));
                    continue;
                }

                if (trimmed.StartsWith("Fact:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateFactCard(trimmed));
                    continue;
                }

                if (trimmed.StartsWith("Snapshot:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("SnapshotFile:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("SnapshotPreview:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateCodeSnapshotCard(trimmed));
                    continue;
                }

                if (trimmed.StartsWith("Diff:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("DiffBase:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("DiffFile:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("UnifiedDiff:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("DiffNote:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateDiffTextCard(trimmed));
                    continue;
                }

                if (trimmed.StartsWith("OwnerAgent:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateOwnershipTags(trimmed));
                    continue;
                }

                currentArtifactLines?.Add(trimmed);
                root.Children.Add(CreateWorkspaceTextCard(trimmed, muted: true));
            }

            return root;
        }

        private FrameworkElement CreateWorkspaceSectionLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#57606A", "#57606A"),
                Margin = new Thickness(0, 2, 0, 8)
            };
        }

        private Border CreateArtifactCard(
            string line,
            StackPanel factsHost,
            Func<string> copyTextProvider,
            Action? applyDownstreamPlan = null)
        {
            var parts = line.Substring("Artifact:".Length).Trim()
                .Split('/')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            string kind = parts.Count > 0 ? parts[0] : "artifact";
            string format = parts.Count > 1 ? parts[1] : "text";
            string visibility = parts.Count > 2 ? parts[2] : "visible";
            string meta = parts.Count > 3 ? string.Join(" / ", parts.Skip(3)) : "";

            var panel = new StackPanel();

            var header = new WrapPanel();
            header.Children.Add(CreateWorkspaceBadge(kind, "#EAF4FF", "#245A9B"));
            header.Children.Add(CreateWorkspaceBadge(format, "#F2F4F7", "#475467"));
            header.Children.Add(CreateWorkspaceBadge(visibility, "#F7F7F7", "#666666"));
            panel.Children.Add(header);

            if (!string.IsNullOrWhiteSpace(meta))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = meta,
                    FontSize = 11.5,
                    Foreground = CreateBrush("#4A4A4A", "#4A4A4A"),
                    Margin = new Thickness(0, 6, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            panel.Children.Add(factsHost);

            var card = new Border
            {
                Background = CreateBrush("#FFFFFF", "#FFFFFF"),
                BorderBrush = CreateBrush("#DDE7F2", "#DDE7F2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = panel
            };

            AttachCopyContextMenu(
                card,
                "複製此主題區塊",
                copyTextProvider,
                applyDownstreamPlan);

            return card;
        }

        private Border CreateCodeSnapshotCard(string line)
        {
            string label = "Snapshot";
            string body = line;

            int colon = line.IndexOf(':');
            if (colon >= 0)
            {
                label = line.Substring(0, colon).Trim();
                body = line.Substring(colon + 1).Trim();
            }

            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#3B4A66", "#3B4A66"),
                TextWrapping = TextWrapping.Wrap
            });

            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(body) ? "-" : body,
                FontSize = 11.5,
                Foreground = CreateBrush("#43536C", "#43536C"),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            return new Border
            {
                Background = CreateBrush("#F4F7FC", "#F4F7FC"),
                BorderBrush = CreateBrush("#D7E1F0", "#D7E1F0"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = panel
            };
        }

        private Border CreateDiffTextCard(string line)
        {
            string label = "Diff";
            string body = line;

            int colon = line.IndexOf(':');
            if (colon >= 0)
            {
                label = line.Substring(0, colon).Trim();
                body = line.Substring(colon + 1).Trim();
            }

            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#2F5F52", "#2F5F52"),
                TextWrapping = TextWrapping.Wrap
            });

            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(body) ? "-" : body,
                FontSize = 11.5,
                Foreground = CreateBrush("#365950", "#365950"),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            return new Border
            {
                Background = CreateBrush("#F0FAF6", "#F0FAF6"),
                BorderBrush = CreateBrush("#BFE5D7", "#BFE5D7"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = panel
            };
        }

        private Border CreateFactCard(string line)
        {
            string content = line.Substring("Fact:".Length).Trim();
            string subject = content;
            string value = "";

            int equalsIndex = content.IndexOf('=');
            if (equalsIndex >= 0)
            {
                subject = content.Substring(0, equalsIndex).Trim();
                value = content.Substring(equalsIndex + 1).Trim();
            }

            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = subject,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#252525", "#252525"),
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(value))
            {
                var valueParts = value
                    .Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (valueParts.Count == 0)
                    valueParts.Add(value);

                panel.Children.Add(new TextBlock
                {
                    Text = valueParts[0],
                    FontSize = 12,
                    Foreground = CreateBrush("#245A9B", "#245A9B"),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });

                foreach (var meta in valueParts.Skip(1))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = meta,
                        FontSize = 11,
                        Foreground = CreateBrush("#51606F", "#51606F"),
                        Margin = new Thickness(0, 2, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                }
            }

            return new Border
            {
                Background = CreateBrush("#F8FBFF", "#F8FBFF"),
                BorderBrush = CreateBrush("#D8E8F8", "#D8E8F8"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = panel
            };
        }

        private FrameworkElement CreateOwnershipTags(string line)
        {
            var wrap = new WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 6)
            };

            var parts = line
                .Split('|')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            foreach (var part in parts)
            {
                bool numeric = part.Contains("numeric_fact_source", StringComparison.OrdinalIgnoreCase);
                bool official = part.Contains("official", StringComparison.OrdinalIgnoreCase);
                bool background = part.Contains("background_context", StringComparison.OrdinalIgnoreCase);

                string bg = numeric ? "#EAF7EF" : official ? "#EEF4FF" : background ? "#FFF7E8" : "#F2F4F7";
                string fg = numeric ? "#1F7A3A" : official ? "#245A9B" : background ? "#9A5A00" : "#475467";

                wrap.Children.Add(CreateWorkspaceBadge(part, bg, fg));
            }

            return wrap;
        }

        private Border CreateWorkspaceBadge(string text, string bgHex, string fgHex)
        {
            return new Border
            {
                Background = CreateBrush(bgHex, bgHex),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 5, 5),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(text) ? "-" : text,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = CreateBrush(fgHex, fgHex),
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private Border CreateWorkspaceTextCard(string text, bool muted)
        {
            return new Border
            {
                Background = CreateBrush(muted ? "#FAFBFD" : "#FFFFFF", "#FAFBFD"),
                BorderBrush = CreateBrush("#EEF1F4", "#EEF1F4"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(text) ? "-" : text,
                    FontSize = 11.5,
                    Foreground = CreateBrush(muted ? "#666666" : "#333333", "#666666"),
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private void AttachCopyContextMenu(
            FrameworkElement target,
            string menuText,
            Func<string> copyTextProvider,
            Action? applyDownstreamPlan = null)
        {
            if (target == null || copyTextProvider == null)
                return;

            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(menuText) ? "複製" : menuText,
                Style = _owner.TryFindResource("FileMenuItemStyle") as Style
            };

            item.Click += (_, __) =>
            {
                string text = copyTextProvider() ?? "";
                if (string.IsNullOrWhiteSpace(text))
                    return;

                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
                    // Clipboard may be temporarily locked by another process.
                }
            };

            var menu = new ContextMenu
            {
                Style = _owner.TryFindResource("FileContextMenuStyle") as Style
            };

            menu.Items.Add(item);

            if (applyDownstreamPlan != null)
            {
                var sepStyle = _owner.TryFindResource("FileSeparatorStyle") as Style;
                var applyItem = new MenuItem
                {
                    Header = "套用到畫布",
                    Style = _owner.TryFindResource("FileMenuItemStyle") as Style
                };

                applyItem.Click += (_, __) => applyDownstreamPlan();

                menu.Items.Add(new Separator { Style = sepStyle });
                menu.Items.Add(applyItem);
            }

            target.ContextMenu = menu;
        }

        public static SolidColorBrush GetStepBrush(NodeDecisionStepState state)
        {
            return state switch
            {
                NodeDecisionStepState.Success => CreateBrush("#2E9B52", "#2E9B52"),
                NodeDecisionStepState.Warning => CreateBrush("#D48A00", "#D48A00"),
                NodeDecisionStepState.Error => CreateBrush("#C93C3C", "#C93C3C"),
                _ => CreateBrush("#4F7EF7", "#4F7EF7")
            };
        }

        public static SolidColorBrush GetStepSoftBrush(NodeDecisionStepState state)
        {
            return state switch
            {
                NodeDecisionStepState.Success => CreateBrush("#EAF7EF", "#EAF7EF"),
                NodeDecisionStepState.Warning => CreateBrush("#FFF5E7", "#FFF5E7"),
                NodeDecisionStepState.Error => CreateBrush("#FDECEC", "#FDECEC"),
                _ => CreateBrush("#EDF3FF", "#EDF3FF")
            };
        }

        public static SolidColorBrush GetStepBorderBrush(NodeDecisionStepState state, bool highlight)
        {
            if (highlight)
            {
                return state switch
                {
                    NodeDecisionStepState.Success => CreateBrush("#BFE3CB", "#BFE3CB"),
                    NodeDecisionStepState.Warning => CreateBrush("#F1D39B", "#F1D39B"),
                    NodeDecisionStepState.Error => CreateBrush("#E9B0B0", "#E9B0B0"),
                    _ => CreateBrush("#C9D9FF", "#C9D9FF")
                };
            }

            return state switch
            {
                NodeDecisionStepState.Success => CreateBrush("#D7EBDD", "#D7EBDD"),
                NodeDecisionStepState.Warning => CreateBrush("#F3E2BA", "#F3E2BA"),
                NodeDecisionStepState.Error => CreateBrush("#F0CCCC", "#F0CCCC"),
                _ => CreateBrush("#E8ECF3", "#E8ECF3")
            };
        }

        public static string GetStepStateLabel(NodeDecisionStepState state)
        {
            return state switch
            {
                NodeDecisionStepState.Success => "Success",
                NodeDecisionStepState.Warning => "Warning",
                NodeDecisionStepState.Error => "Error",
                _ => "Info"
            };
        }
    }
}
