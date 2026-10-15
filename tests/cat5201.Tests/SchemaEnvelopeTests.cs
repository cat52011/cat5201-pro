using System.Collections.Generic;
using Cat5201;
using Xunit;

namespace Cat5201.Tests
{
    /// <summary>
    /// 落地檔案的版本信封。真正要擋的是 2026-10 那次事故：
    /// 格式換了、檔案裡卻沒有版本標記，於是硬碟上的既有檔案全部失效。
    /// 所以這裡最重要的一條是「讀得懂沒有信封的舊檔」。
    /// </summary>
    public class SchemaEnvelopeTests
    {
        private sealed record Entry(double Remaining);

        [Fact]
        public void LegacyFilesWithoutAnEnvelopeStillLoad()
        {
            // 升級前寫下的裸字典。
            string legacy = "{\"openai\":{\"Remaining\":12.5}}";

            var (schema, data) = SchemaEnvelope.Read<Dictionary<string, Entry>>(legacy);

            Assert.Equal(SchemaEnvelope.LegacySchema, schema);
            Assert.Equal(12.5, data!["openai"].Remaining);
        }

        [Fact]
        public void RoundTripKeepsBothVersionAndContent()
        {
            var payload = new Dictionary<string, Entry> { ["openai"] = new Entry(3.25) };

            var (schema, data) = SchemaEnvelope.Read<Dictionary<string, Entry>>(
                SchemaEnvelope.Write(payload, 2));

            Assert.Equal(2, schema);
            Assert.Equal(3.25, data!["openai"].Remaining);
        }

        [Fact]
        public void ListsWorkToo()
        {
            var (_, data) = SchemaEnvelope.Read<List<string>>(SchemaEnvelope.Write(new List<string> { "a", "b" }, 1));
            Assert.Equal(new[] { "a", "b" }, data!);
        }

        [Fact]
        public void BrokenOrEmptyFilesAreTreatedAsEmptyNotAsACrash()
        {
            Assert.Null(SchemaEnvelope.Read<List<string>>("這不是 JSON").Data);
            Assert.Null(SchemaEnvelope.Read<List<string>>("").Data);
            Assert.Null(SchemaEnvelope.Read<List<string>>(null).Data);
        }
    }
}
