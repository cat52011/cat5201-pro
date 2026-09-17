using System.Text.Json;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 專案檔格式版本（#4 便宜保險）：舊檔（無 SchemaVersion 欄位）載入必須得到 0、
    /// 新存檔寫出 ProjectStore.CurrentSchemaVersion —— 之後格式演進靠這個欄位分流，不用猜版本。
    /// </summary>
    public class ProjectSchemaTests
    {
        [Fact]
        public void OldProjectJson_WithoutSchemaVersion_LoadsAsVersionZero()
        {
            const string oldJson = """
            {
                "CreatedAt": "2026-01-01T00:00:00",
                "InitialNodeId": null,
                "Nodes": [],
                "Connections": [],
                "Attachments": []
            }
            """;

            var state = JsonSerializer.Deserialize<AppState>(oldJson);

            Assert.NotNull(state);
            Assert.Equal(0, state!.SchemaVersion);   // 舊檔＝0（record 預設），不會炸
        }

        [Fact]
        public void CurrentSchemaVersion_IsOne()
        {
            // 釘住目前版本號：升版是「有意識的決定」（要配載入分流），不該被順手改掉。
            Assert.Equal(1, ProjectStore.CurrentSchemaVersion);
        }

        [Fact]
        public void SchemaVersion_RoundTrips()
        {
            var state = new AppState(
                System.DateTime.Now, null,
                new(), new(), new(),
                SchemaVersion: ProjectStore.CurrentSchemaVersion);

            var restored = JsonSerializer.Deserialize<AppState>(JsonSerializer.Serialize(state));

            Assert.Equal(ProjectStore.CurrentSchemaVersion, restored!.SchemaVersion);
        }
    }
}
