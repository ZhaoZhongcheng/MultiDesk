using System;
using System.IO;
using System.Text;
using MultiDesk.Core;

namespace MultiDesk.Native
{
    /// <summary>
    /// 会话日志文件实现：记录「被隐藏」的窗口，用于崩溃后由看门狗恢复。
    /// 文件位于数据目录下 session.json。
    /// </summary>
    public class WindowsJournal : IWindowJournal
    {
        public string FilePath { get; private set; }

        public WindowsJournal(string filePath)
        {
            FilePath = filePath;
        }

        public SessionJournal Load()
        {
            try
            {
                if (!File.Exists(FilePath)) return null;
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                if (string.IsNullOrEmpty(json)) return null;
                return JsonIo.Deserialize<SessionJournal>(json);
            }
            catch
            {
                return null;
            }
        }

        public void Save(SessionJournal journal)
        {
            if (journal == null) return;
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                string json = JsonIo.Serialize(journal);
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Delete(FilePath);
                File.Move(tmp, FilePath);
            }
            catch
            {
                // 日志失败不影响核心功能
            }
        }

        public void Clear()
        {
            try
            {
                if (File.Exists(FilePath)) File.Delete(FilePath);
                string tmp = FilePath + ".tmp";
                if (File.Exists(tmp)) File.Delete(tmp);
            }
            catch
            {
            }
        }
    }
}
