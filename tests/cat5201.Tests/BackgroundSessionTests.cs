using System;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 切換存檔時，執行中的存檔留在背景繼續（2026-11-04 使用者要求）：
    /// 執行紀錄只清掉目前畫面的節點；存檔清單的名稱在執行中流光。
    /// 主要流程在 MainWindow.Sessions.cs（WPF 主視窗），這裡測得到的是它依賴的兩個元件。
    /// </summary>
    public class BackgroundSessionTests
    {
        private static AiExecutionLogEntry Log(string nodeId) => new() { NodeId = nodeId };

        [Fact]
        public void 執行紀錄只移除指定的節點_背景存檔節點的紀錄保留()
        {
            var service = new AiExecutionLogService();
            string active = Guid.NewGuid().ToString(), background = Guid.NewGuid().ToString();
            service.Add(Log(active));
            service.Add(Log(background));
            service.Add(Log(background));

            // 切換存檔：只清目前畫面的節點（原本是 ClearAll，背景節點的紀錄會不見）
            service.RemoveWhere(id => id == active);

            Assert.Empty(service.GetLogs(active));
            Assert.Equal(2, service.GetLogs(background).Count);
        }

        [Fact]
        public void 流光文字_開啟時文字是移動中的漸層_關閉時回到原本的顏色()
        {
            Exception? failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    var text = new ShimmerTextBlock { Text = "Review Agent test 4" };
                    var original = text.Foreground;

                    text.IsShimmering = true;
                    var brush = Assert.IsType<LinearGradientBrush>(text.Foreground);
                    Assert.False(brush.IsFrozen);                                    // 程式建立的筆刷，動畫才播得了
                    var shift = Assert.IsType<TranslateTransform>(brush.RelativeTransform);
                    Assert.True(shift.HasAnimatedProperties);                        // 亮帶在移動
                    Assert.Equal(5, brush.GradientStops.Count);

                    text.IsShimmering = false;
                    Assert.Same(original, text.Foreground);                          // 跑完恢復原本的文字顏色
                    Assert.False(shift.HasAnimatedProperties);
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure != null) throw new Xunit.Sdk.XunitException(failure.ToString());
        }
    }
}
