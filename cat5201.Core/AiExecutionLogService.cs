using System;
using System.Collections.Generic;
using System.Linq;

namespace Cat5201
{
    public sealed class AiExecutionLogService
    {
        private readonly Dictionary<string, List<AiExecutionLogEntry>> _logsByNodeId = new();

        public void Add(AiExecutionLogEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.NodeId))
                return;

            if (!_logsByNodeId.TryGetValue(entry.NodeId, out var list))
            {
                list = new List<AiExecutionLogEntry>();
                _logsByNodeId[entry.NodeId] = list;
            }

            list.Add(entry);

            // 先保留最近 20 筆，避免一直長大
            if (list.Count > 20)
            {
                int removeCount = list.Count - 20;
                list.RemoveRange(0, removeCount);
            }
        }

        public IReadOnlyList<AiExecutionLogEntry> GetLogs(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                return Array.Empty<AiExecutionLogEntry>();

            if (_logsByNodeId.TryGetValue(nodeId, out var list))
                return list.ToList();

            return Array.Empty<AiExecutionLogEntry>();
        }

        public AiExecutionLogEntry? GetLatest(string nodeId)
        {
            return GetLogs(nodeId).LastOrDefault();
        }

        public void ClearNode(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                return;

            _logsByNodeId.Remove(nodeId);
        }

        public void ClearAll()
        {
            _logsByNodeId.Clear();
        }

        /// <summary>
        /// 移除符合條件的節點的紀錄。切換存檔時只清掉目前畫面的節點，背景仍在執行的存檔的紀錄要留著
        /// （2026-11-04：原本 ClearAll，背景節點跑完寫進來的紀錄不見了）。
        /// </summary>
        public void RemoveWhere(Func<string, bool> removeNodeId)
        {
            if (removeNodeId == null) return;
            foreach (var key in _logsByNodeId.Keys.Where(removeNodeId).ToList())
                _logsByNodeId.Remove(key);
        }
    }
}