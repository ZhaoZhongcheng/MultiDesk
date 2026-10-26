using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization.Json;
using System.Text;

namespace MultiDesk.Core
{
    /// <summary>配置文件的读写（JSON，位于 %APPDATA%\MultiDesk\config.json）。</summary>
    public class ConfigStore
    {
        public string DataDir { get; private set; }
        public string ConfigPath { get; private set; }
        public string BackupPath { get; private set; }

        public ConfigStore(string dataDir)
        {
            if (string.IsNullOrEmpty(dataDir))
            {
                dataDir = DefaultDataDir();
            }
            DataDir = dataDir;
            ConfigPath = Path.Combine(dataDir, "config.json");
            BackupPath = Path.Combine(dataDir, "config.json.bak");
        }

        public static string DefaultDataDir()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "MultiDesk");
        }

        /// <summary>加载配置；文件不存在或损坏时返回默认配置（不写盘）。</summary>
        public AppConfig Load()
        {
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    if (!string.IsNullOrEmpty(json))
                    {
                        AppConfig cfg = JsonIo.Deserialize<AppConfig>(json);
                        if (cfg != null)
                        {
                            cfg.Sanitize();
                            return cfg;
                        }
                    }
                }
            }
            catch
            {
                // 尝试备份
                try
                {
                    if (File.Exists(BackupPath))
                    {
                        string json = File.ReadAllText(BackupPath, Encoding.UTF8);
                        AppConfig cfg = JsonIo.Deserialize<AppConfig>(json);
                        if (cfg != null)
                        {
                            cfg.Sanitize();
                            return cfg;
                        }
                    }
                }
                catch
                {
                }
            }
            AppConfig def = AppConfig.CreateDefault();
            def.Sanitize();
            return def;
        }

        public bool Exists
        {
            get { return File.Exists(ConfigPath); }
        }

        /// <summary>保存配置（原子写入，并保留上一版本备份）。</summary>
        public void Save(AppConfig config)
        {
            if (config == null) throw new ArgumentNullException("config");
            config.Sanitize();
            Directory.CreateDirectory(DataDir);
            string json = JsonIo.Serialize(config);
            string tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(ConfigPath))
            {
                try { File.Copy(ConfigPath, BackupPath, true); }
                catch { }
                File.Delete(ConfigPath);
            }
            File.Move(tmp, ConfigPath);
        }
    }

    /// <summary>基于 DataContractJsonSerializer 的 JSON 工具（.NET Framework 内置，无第三方依赖）。</summary>
    public static class JsonIo
    {
        public static string Serialize<T>(T value)
        {
            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream ms = new MemoryStream())
            {
                ser.WriteObject(ms, value);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static T Deserialize<T>(string json) where T : class
        {
            if (string.IsNullOrEmpty(json)) return null;
            DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(T));
            using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
            {
                object obj = ser.ReadObject(ms);
                return obj as T;
            }
        }
    }
}
