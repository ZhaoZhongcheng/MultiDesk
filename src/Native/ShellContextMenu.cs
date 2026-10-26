using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace MultiDesk.Native
{
    /// <summary>
    /// 为任意文件/文件夹显示与 Windows 资源管理器完全一致的原生右键菜单
    /// （剪切/复制/重命名/共享/删除/发送到/压缩/属性/第三方扩展等）。
    ///
    /// 实现方式：SHParseDisplayName → SHBindToParent → IShellFolder.GetUIObjectOf(IContextMenu)
    /// → QueryContextMenu → TrackPopupMenuEx(TPM_RETURNCMD) → InvokeCommand。
    /// 对需要子菜单/自绘的扩展（IContextMenu2/3）通过 HwndSource 钩子转发菜单消息。
    /// </summary>
    public static class ShellContextMenu
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct CMINVOKECOMMANDINFO
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            public IntPtr lpParameters;
            public IntPtr lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
        }

        [ComImport]
        [Guid("000214E6-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellFolder
        {
            void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
                out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
            void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
            void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
            void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
            void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
            void GetAttributesOf(uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
            void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
                ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
            void GetDisplayNameOf(IntPtr pidl, uint uFlags, IntPtr pName);
            void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName,
                uint uFlags, out IntPtr ppidlOut);
        }

        [ComImport]
        [Guid("000214E4-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu
        {
            [PreserveSig]
            int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
            void InvokeCommand(ref CMINVOKECOMMANDINFO pici);
            void GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, StringBuilder pszName, int cchMax);
        }

        [ComImport]
        [Guid("000214F4-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu2
        {
            [PreserveSig]
            int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
            void InvokeCommand(ref CMINVOKECOMMANDINFO pici);
            void GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, StringBuilder pszName, int cchMax);
            [PreserveSig]
            int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        }

        [ComImport]
        [Guid("BCFCE0A0-EC17-11D0-8D10-00A0C90F2719")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IContextMenu3
        {
            [PreserveSig]
            int QueryContextMenu(IntPtr hMenu, uint indexMenu, int idCmdFirst, int idCmdLast, uint uFlags);
            void InvokeCommand(ref CMINVOKECOMMANDINFO pici);
            void GetCommandString(IntPtr idCmd, uint uType, IntPtr pReserved, StringBuilder pszName, int cchMax);
            [PreserveSig]
            int HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
            [PreserveSig]
            int HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
        }

        private static readonly Guid IID_IShellFolder = new Guid("000214E6-0000-0000-C000-000000000046");
        private static readonly Guid IID_IContextMenu = new Guid("000214E4-0000-0000-C000-000000000046");

        private const uint CMF_NORMAL = 0x00000000;
        private const uint CMF_EXTENDEDVERBS = 0x00000100;
        private const uint TPM_RETURNCMD = 0x0100;
        private const uint TPM_RIGHTBUTTON = 0x0002;
        private const uint TPM_LEFTALIGN = 0x0000;
        private const uint TPM_TOPALIGN = 0x0000;
        private const int SW_SHOWNORMAL = 1;

        private const uint WM_INITMENUPOPUP = 0x0117;
        private const uint WM_DRAWITEM = 0x002B;
        private const uint WM_MEASUREITEM = 0x002C;
        private const uint WM_MENUCHAR = 0x0120;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [DllImport("shell32.dll")]
        private static extern int SHBindToParent(IntPtr pidl, ref Guid riid, out IntPtr ppv, out IntPtr ppidlLast);

        [DllImport("shell32.dll")]
        private static extern void ILFree(IntPtr pidl);

        [DllImport("user32.dll")]
        private static extern IntPtr CreatePopupMenu();

        [DllImport("user32.dll")]
        private static extern bool DestroyMenu(IntPtr hMenu);

        [DllImport("user32.dll")]
        private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lpmt);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>在屏幕坐标 (x,y) 处显示 path 对应的 Windows 原生上下文菜单；成功返回 true。</summary>
        public static bool Show(string path, IntPtr ownerHwnd, int x, int y)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            if (!System.IO.File.Exists(path) && !System.IO.Directory.Exists(path)) return false;
            if (ownerHwnd == IntPtr.Zero) return false;

            IntPtr pidl = IntPtr.Zero;
            object folderObject = null;
            object menuObject = null;
            IntPtr hMenu = IntPtr.Zero;
            HwndSource source = null;
            HwndSourceHook hook = null;
            try
            {
                uint attrs;
                if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out attrs) != 0 || pidl == IntPtr.Zero) return false;

                Guid iidFolder = IID_IShellFolder;
                IntPtr parentFolderPtr = IntPtr.Zero;
                IntPtr childPidl = IntPtr.Zero;
                if (SHBindToParent(pidl, ref iidFolder, out parentFolderPtr, out childPidl) != 0 || parentFolderPtr == IntPtr.Zero)
                {
                    return false;
                }
                folderObject = Marshal.GetObjectForIUnknown(parentFolderPtr);
                Marshal.Release(parentFolderPtr);

                IShellFolder folder = (IShellFolder)folderObject;
                Guid iidMenu = IID_IContextMenu;
                IntPtr contextMenuPtr;
                try
                {
                    folder.GetUIObjectOf(ownerHwnd, 1, new IntPtr[] { childPidl }, ref iidMenu, IntPtr.Zero, out contextMenuPtr);
                }
                catch
                {
                    return false;
                }
                if (contextMenuPtr == IntPtr.Zero) return false;
                menuObject = Marshal.GetObjectForIUnknown(contextMenuPtr);
                Marshal.Release(contextMenuPtr);

                IContextMenu contextMenu = menuObject as IContextMenu;
                if (contextMenu == null) return false;

                hMenu = CreatePopupMenu();
                if (hMenu == IntPtr.Zero) return false;

                int result = contextMenu.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_EXTENDEDVERBS);
                if (result < 0) return false;

                IContextMenu2 menu2 = menuObject as IContextMenu2;
                IContextMenu3 menu3 = menuObject as IContextMenu3;
                if (menu2 != null || menu3 != null)
                {
                    source = HwndSource.FromHwnd(ownerHwnd);
                    if (source != null)
                    {
                        IContextMenu2 cm2 = menu2;
                        IContextMenu3 cm3 = menu3;
                        hook = delegate(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
                        {
                            try
                            {
                                if (msg == (int)WM_INITMENUPOPUP || msg == (int)WM_DRAWITEM || msg == (int)WM_MEASUREITEM)
                                {
                                    if (cm3 != null)
                                    {
                                        IntPtr ignored;
                                        cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out ignored);
                                        handled = true;
                                        return IntPtr.Zero;
                                    }
                                    if (cm2 != null)
                                    {
                                        cm2.HandleMenuMsg((uint)msg, wParam, lParam);
                                        handled = true;
                                        return IntPtr.Zero;
                                    }
                                }
                                if (msg == (int)WM_MENUCHAR && cm3 != null)
                                {
                                    IntPtr extra;
                                    cm3.HandleMenuMsg2((uint)msg, wParam, lParam, out extra);
                                    handled = true;
                                    return IntPtr.Zero;
                                }
                            }
                            catch
                            {
                            }
                            return IntPtr.Zero;
                        };
                        source.AddHook(hook);
                    }
                }

                try { SetForegroundWindow(ownerHwnd); } catch { }
                int cmd = TrackPopupMenuEx(hMenu,
                    TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_LEFTALIGN | TPM_TOPALIGN, x, y, ownerHwnd, IntPtr.Zero);
                try { PostMessage(ownerHwnd, 0x0000, IntPtr.Zero, IntPtr.Zero); } catch { }

                if (cmd > 0)
                {
                    CMINVOKECOMMANDINFO info = new CMINVOKECOMMANDINFO();
                    info.cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFO));
                    info.fMask = 0;
                    info.hwnd = ownerHwnd;
                    info.lpVerb = new IntPtr(cmd - 1);
                    info.nShow = SW_SHOWNORMAL;
                    try { contextMenu.InvokeCommand(ref info); }
                    catch { return false; }
                }
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { if (source != null && hook != null) source.RemoveHook(hook); } catch { }
                try { if (hMenu != IntPtr.Zero) DestroyMenu(hMenu); } catch { }
                try { if (menuObject != null && Marshal.IsComObject(menuObject)) Marshal.ReleaseComObject(menuObject); } catch { }
                try { if (folderObject != null && Marshal.IsComObject(folderObject)) Marshal.ReleaseComObject(folderObject); } catch { }
                try { if (pidl != IntPtr.Zero) ILFree(pidl); } catch { }
            }
        }
    }
}
