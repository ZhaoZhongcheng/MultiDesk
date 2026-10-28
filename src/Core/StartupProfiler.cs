using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MultiDesk.Core
{
    /// <summary>
    /// 启动阶段计时（1.2.8）：记录从进程启动到各阶段的时间点，写入数据目录的 startup-perf.log。
    ///
    /// 用途：定位「开机自启慢」到底慢在哪一段（程序集加载 / 托盘 / 桌面图层 / 图标提取 / 看门狗），
    /// 优化前后可直接对比。日志很小（每次启动约 10~20 行），默认开启。
    ///
    /// 设计：
    ///   * 只在内存累积，正常启动完成时一次性落盘，避免启动期间多次小 IO；
    ///   * 记录系统 uptime，可区分「开机后第一次启动（冷机）」与「已运行很久的热启动」；
    ///   * 看门狗（延迟启动）等启动后的阶段会在后续 Flush 时追加到同一 run 下。
    /// </summary>
    public static class StartupProfiler
    {
        private struct MarkEntry
        {
            public long ElapsedMs;
            public string Stage;
            public string Detail;
        }

        private static readonly object Sync = new object();
        private static readonly List<MarkEntry> Marks = new List<MarkEntry>();
        private static Stopwatch _stopwatch;
        private static string _dataDir;
        private static string _runId;
        private static string _args;
        private static long _uptimeMs;
        private static bool _headerWritten;

        /// <summary>进程入口处调用：开始计时并记录启动参数。</summary>
        public static void Begin(string[] args)
        {
            lock (Sync)
            {
                Marks.Clear();
                _stopwatch = Stopwatch.StartNew();
                _headerWritten = false;
                _dataDir = null;
                _runId = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Process.GetCurrentProcess().Id.ToString();
                _args = args == null ? string.Empty : string.Join(" ", args);
                _uptimeMs = SafeUptimeMs();
            }
            Mark("process_begin");
        }

        /// <summary>数据目录解析完成后调用（之前产生的阶段会一起写入该目录）。</summary>
        public static void Configure(string dataDir)
        {
            lock (Sync)
            {
                _dataDir = dataDir;
            }
        }

        public static void Mark(string stage)
        {
            Mark(stage, null);
        }

        /// <summary>记录一个启动阶段（detail 可为路径/耗时等辅助信息）。</summary>
        public static void Mark(string stage, string detail)
        {
            lock (Sync)
            {
                if (_stopwatch == null) return;
                MarkEntry entry = new MarkEntry();
                entry.ElapsedMs = _stopwatch.ElapsedMilliseconds;
                entry.Stage = stage == null ? string.Empty : stage;
                entry.Detail = detail == null ? string.Empty : detail;
                Marks.Add(entry);
            }
        }

        /// <summary>把当前累积的阶段追加写入 startup-perf.log（可多次调用）。</summary>
        public static void Flush()
        {
            try
            {
                string dataDir;
                string runId;
                string args;
                long uptime;
                List<MarkEntry> marks = new List<MarkEntry>();
                lock (Sync)
                {
                    if (_stopwatch == null || Marks.Count == 0) return;
                    dataDir = _dataDir;
                    runId = _runId;
                    args = _args;
                    uptime = _uptimeMs;
                    marks.AddRange(Marks);
                    Marks.Clear();
                }
                if (string.IsNullOrEmpty(dataDir)) return;

                StringBuilder sb = new StringBuilder();
                lock (Sync)
                {
                    if (!_headerWritten)
                    {
                        _headerWritten = true;
                        sb.Append("# startup run=").Append(runId)
                          .Append(" uptime_s=").Append((uptime / 1000.0).ToString("F1"))
                          .Append(" args=").Append(args)
                          .Append(Environment.NewLine);
                    }
                }
                for (int i = 0; i < marks.Count; i++)
                {
                    sb.Append(marks[i].ElapsedMs.ToString()).Append("ms\t").Append(marks[i].Stage);
                    if (!string.IsNullOrEmpty(marks[i].Detail)) sb.Append('\t').Append(marks[i].Detail);
                    sb.Append(Environment.NewLine);
                }
                Directory.CreateDirectory(dataDir);
                File.AppendAllText(Path.Combine(dataDir, "startup-perf.log"), sb.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }

        private static long SafeUptimeMs()
        {
            try { return (long)GetTickCount64(); }
            catch { return 0; }
        }

        [DllImport("kernel32.dll")]
        private static extern ulong GetTickCount64();
    }
}
