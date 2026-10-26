using System;
using System.Collections.Generic;
using System.Windows;
using MultiDesk.Core;

namespace MultiDesk.UI
{
    /// <summary>卸载窗口（由 MultiDesk.exe --uninstall 复制到临时目录后以 --uninstall-run 启动）。</summary>
    public partial class UninstallWindow : Window
    {
        private readonly string _targetDir;
        private readonly string _targetExe;
        private readonly int _ownerProcessId;
        private readonly string _tempDir;
        private bool _running;

        public UninstallWindow(string targetDir, string targetExe, int ownerProcessId, string tempDir)
        {
            InitializeComponent();
            _targetDir = targetDir;
            _targetExe = targetExe;
            _ownerProcessId = ownerProcessId;
            _tempDir = tempDir;
            TargetPathText.Text = string.IsNullOrEmpty(targetExe) ? targetDir : targetExe;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            try { Uninstaller.ScheduleTempCleanup(_tempDir); } catch { }
            Close();
        }

        private void Uninstall_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            if (MessageBox.Show(
                    "将删除多桌面助手程序、快捷方式和全部配置数据（含桌面图标数据）。" + Environment.NewLine +
                    "删除后不可恢复，确定继续吗？",
                    "卸载确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }
            _running = true;
            UninstallButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            LogText.Text = "正在卸载，请稍候…";
            try
            {
                // 固定行为：删除程序文件、快捷方式/自启/托盘设置，并删除配置数据（干净卸载）
                List<string> log = Uninstaller.Execute(_targetDir, _targetExe, _ownerProcessId,
                    true, true, 6000);
                Uninstaller.WriteLog(log);
                LogText.Text = string.Join(Environment.NewLine, log.ToArray());
                MessageBox.Show("卸载完成。" + Environment.NewLine + Environment.NewLine
                        + "多桌面助手已从本机移除，感谢使用！",
                    "多桌面助手", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                LogText.Text = "卸载失败：" + ex.Message;
                MessageBox.Show("卸载过程中出现错误：" + ex.Message, "多桌面助手",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { Uninstaller.ScheduleTempCleanup(_tempDir); } catch { }
                Close();
            }
        }
    }
}
