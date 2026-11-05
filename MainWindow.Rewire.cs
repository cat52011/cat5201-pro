using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PathShape = System.Windows.Shapes.Path;

namespace Cat5201
{
    // MainWindow 部分類別 — 變更上游節點（rewire）流程（自 MainWindow.xaml.cs 原樣搬出）
    public partial class MainWindow
    {
        // ===== 變更上游節點（rewire）流程 =====
        // 1) 右鍵「變更上游節點」→ BeginRewireUpstream：找出主上游連線，隱藏它、改用 ghost 跟滑鼠。
        // 2) 滑鼠移動 → UpdateRewireGhost。
        // 3) 左鍵點到合法節點 → TryCompleteRewire：依滑鼠在新節點中線左右決定接左孔/右孔，改 StartNode。
        // 4) ESC / 失敗 → CancelRewire：原連線資料未被改過，直接還原顯示。
        public void BeginRewireUpstream(NodeControl child)
        {
            if (child == null || _rewireMode)
                return;
            if (IsWorkflowChainRunning)
                return;

            var conn = _connections.FirstOrDefault(c => ReferenceEquals(c.EndNode, child));
            if (conn == null)
                return;

            _rewireMode = true;
            _rewireConn = conn;
            _rewireChild = child;

            // 兩條都要藏：只藏看得到的那條，透明的命中線還留在原地攔滑鼠。
            if (conn.Path != null)
                conn.Path.Visibility = Visibility.Collapsed;
            if (conn.HitPath != null)
                conn.HitPath.Visibility = Visibility.Collapsed;

            _rewireGhost = new PathShape
            {
                Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#5B6CFF")!,
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                IsHitTestVisible = false
            };
            Canvas.SetZIndex(_rewireGhost, GetNextZIndex());
            MainCanvas.Children.Add(_rewireGhost);

            Mouse.OverrideCursor = Cursors.Cross;

            // 初始 ghost 起點畫在原上游孔位，避免閃一下。
            UpdateRewireGhost(GetThumbCenterOnCanvas(conn.StartNode, conn.StartThumb));
        }

        public void UpdateRewireGhost(Point mouseCanvas)
        {
            if (!_rewireMode || _rewireGhost == null || _rewireChild == null || _rewireConn == null)
                return;

            var end = GetThumbCenterOnCanvas(_rewireChild, _rewireConn.EndThumb);
            var start = mouseCanvas;

            var geometry = new System.Windows.Media.PathGeometry();
            var figure = new System.Windows.Media.PathFigure { StartPoint = start };
            figure.Segments.Add(new System.Windows.Media.BezierSegment
            {
                Point1 = new Point((start.X + end.X) / 2, start.Y),
                Point2 = new Point((start.X + end.X) / 2, end.Y),
                Point3 = end
            });
            geometry.Figures.Add(figure);
            _rewireGhost.Data = geometry;
        }

        // 嘗試把主上游接到 target。不合法（自己 / 自己的下游後代）→ 擋下、提示音、保持 rewire 狀態等待重選。
        public void TryCompleteRewire(NodeControl target)
        {
            if (!_rewireMode || _rewireConn == null || _rewireChild == null || target == null)
                return;

            if (ReferenceEquals(target, _rewireChild))
            {
                System.Media.SystemSounds.Asterisk.Play();   // 不能接自己
                return;
            }

            // 不能接到自己的下游後代（會形成循環）。
            if (GetDescendantIds(_rewireChild).Contains(target.Id.ToString()))
            {
                System.Media.SystemSounds.Asterisk.Play();
                return;
            }

            // 需求：依滑鼠在新節點「中線」的左右，決定接左孔(ThumbTL)或右孔(ThumbTR)。
            double centerX = SafeFinite(Canvas.GetLeft(target), 0) + SafePositiveFinite(target.Width, 300) / 2.0;
            Point mouse = Mouse.GetPosition(MainCanvas);
            string startThumb = mouse.X < centerX ? "ThumbTL" : "ThumbTR";

            // 成功——此刻才動連線資料：改上游節點與其接孔，下游端(EndNode/EndThumb)維持不變。
            var conn = _rewireConn;
            conn.StartNode = target;
            conn.StartThumb = startThumb;

            EndRewire();
            UpdateConnectionGeometry(conn);
            HookNode(target);
            SaveState();
        }

        public void CancelRewire() => EndRewire();

        // 收尾：移除 ghost、把原連線顯示回來、清狀態。成功與取消共用——
        // 取消時連線資料根本沒被改過所以等同還原；成功時資料已指向新上游，顯示回來即生效。
        private void EndRewire()
        {
            if (_rewireGhost != null)
            {
                MainCanvas.Children.Remove(_rewireGhost);
                _rewireGhost = null;
            }

            if (_rewireConn?.Path != null)
                _rewireConn.Path.Visibility = Visibility.Visible;
            if (_rewireConn?.HitPath != null)
                _rewireConn.HitPath.Visibility = Visibility.Visible;

            Mouse.OverrideCursor = null;

            _rewireMode = false;
            _rewireConn = null;
            _rewireChild = null;
        }

        // 此節點的所有下游後代 ID（沿出邊 BFS，不含自己）。用於 rewire 防循環。
        private HashSet<string> GetDescendantIds(NodeControl node)
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (node == null)
                return ids;

            var visited = new HashSet<Guid> { node.Id };
            var queue = new Queue<NodeControl>();
            queue.Enqueue(node);
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                foreach (var c in _connections)
                {
                    if (ReferenceEquals(c.StartNode, cur) && c.EndNode != null && visited.Add(c.EndNode.Id))
                    {
                        ids.Add(c.EndNode.Id.ToString());
                        queue.Enqueue(c.EndNode);
                    }
                }
            }

            return ids;
        }

        public bool IsInitialNode(NodeControl node)
            => ReferenceEquals(_initialNode, node);
    }
}
