using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Interop;
using MultiDesk.Native;

namespace MultiDesk.UI
{
    /// <summary>
    /// 全局快捷键管理（RegisterHotKey）。默认快捷键：
    ///   Ctrl+Alt+1..9      切换到第 N 个桌面
    ///   Ctrl+Alt+Left/Right 上一个 / 下一个桌面
    ///   Ctrl+Alt+Shift+Left/Right  把当前窗口移到上一个 / 下一个桌面
    ///   Ctrl+Alt+S         打开设置
    ///   Ctrl+Alt+Shift+R   恢复所有窗口
    /// </summary>
    public class HotkeyManager : IDisposable
    {
        public event Action<int> SwitchToIndexRequested;
        public event Action PreviousRequested;
        public event Action NextRequested;
        public event Action MoveWindowPreviousRequested;
        public event Action MoveWindowNextRequested;
        public event Action OpenSettingsRequested;
        public event Action RestoreAllRequested;

        private HwndSource _source;
        private readonly Dictionary<int, string> _registered = new Dictionary<int, string>();
        private readonly List<string> _failed = new List<string>();
        private bool _disposed;

        private const int IdBaseSwitch = 100;      // 100+0..8
        private const int IdPrev = 200;
        private const int IdNext = 201;
        private const int IdMovePrev = 210;
        private const int IdMoveNext = 211;
        private const int IdOpenSettings = 220;
        private const int IdRestoreAll = 221;

        public string Report
        {
            get
            {
                if (_failed.Count == 0) return "快捷键全部注册成功（共 " + _registered.Count + " 个）";
                return "有 " + _failed.Count + " 个快捷键注册失败（可能被其他程序占用）：" + string.Join("，", _failed.ToArray());
            }
        }

        public void Register()
        {
            if (_disposed) return;
            try
            {
                HwndSourceParameters parameters = new HwndSourceParameters("MultiDeskHotkeySink");
                parameters.WindowStyle = 0; // 隐藏窗口
                parameters.Width = 0;
                parameters.Height = 0;
                _source = new HwndSource(parameters);
                _source.AddHook(WndProc);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HotkeyManager] create message window failed: " + ex.Message);
                _failed.Add("消息窗口创建失败");
                return;
            }

            // Ctrl+Alt+1..9
            for (int i = 0; i < 9; i++)
            {
                int index = i;
                Register(IdBaseSwitch + i, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT,
                    (uint)(0x31 + i), "Ctrl+Alt+" + (i + 1),
                    delegate { RaiseIndex(SwitchToIndexRequested, index); });
            }
            Register(IdPrev, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x25, "Ctrl+Alt+←", delegate { Raise(PreviousRequested); });
            Register(IdNext, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x27, "Ctrl+Alt+→", delegate { Raise(NextRequested); });
            Register(IdMovePrev, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, 0x25, "Ctrl+Alt+Shift+←", delegate { Raise(MoveWindowPreviousRequested); });
            Register(IdMoveNext, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, 0x27, "Ctrl+Alt+Shift+→", delegate { Raise(MoveWindowNextRequested); });
            Register(IdOpenSettings, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x53, "Ctrl+Alt+S", delegate { Raise(OpenSettingsRequested); });
            Register(IdRestoreAll, NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT | NativeMethods.MOD_SHIFT, 0x52, "Ctrl+Alt+Shift+R", delegate { Raise(RestoreAllRequested); });
        }

        private void Register(int id, uint modifiers, uint vk, string displayName, Action action)
        {
            try
            {
                bool ok = NativeMethods.RegisterHotKey(_source.Handle, id, modifiers | NativeMethods.MOD_NOREPEAT, vk);
                if (ok)
                {
                    _registered[id] = displayName;
                    _actions[id] = action;
                }
                else
                {
                    _failed.Add(displayName);
                }
            }
            catch (Exception ex)
            {
                _failed.Add(displayName + "(" + ex.Message + ")");
            }
        }

        private readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == NativeMethods.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                Action action;
                if (_actions.TryGetValue(id, out action))
                {
                    handled = true;
                    try { action(); }
                    catch (Exception ex) { Debug.WriteLine("[HotkeyManager] action failed: " + ex.Message); }
                }
            }
            return IntPtr.Zero;
        }

        private static void Raise(Action action)
        {
            if (action != null) action();
        }

        private static void RaiseIndex(Action<int> action, int index)
        {
            if (action != null) action(index);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_source != null)
            {
                foreach (int id in _registered.Keys)
                {
                    try { NativeMethods.UnregisterHotKey(_source.Handle, id); }
                    catch { }
                }
                _registered.Clear();
                _actions.Clear();
                try
                {
                    _source.RemoveHook(WndProc);
                    _source.Dispose();
                }
                catch { }
                _source = null;
            }
        }
    }
}
