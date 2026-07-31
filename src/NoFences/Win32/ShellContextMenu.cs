using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DeskOrganizer.NoFences.Win32;

/// <summary>
/// Shell 右键菜单工具：获取文件/文件夹的系统右键菜单（与 Windows 桌面一致）。
/// 通过 IShellFolder + IContextMenu 接口实现，支持 IContextMenu3 消息转发。
///
/// 性能优化（对照桌面右键的速度）：
/// 1. 缓存桌面 IShellFolder 与父文件夹 IShellFolder（围栏内条目通常同目录，BindToObject 只做一次）；
/// 2. 用 SHParseDisplayName + ILFindLastID/ILClone/ILRemoveLastID 拆分 PIDL，省掉 2 次 ParseDisplayName；
/// 3. GetUIObjectOf 只调一次（请求 IContextMenu），IContextMenu2/3 通过 QueryInterface 获取，
///    避免重复创建 COM 菜单对象；
/// 4. 消息转发窗口只注册/创建一次，全程复用；
/// 5. CMF_EXTENDEDVERBS 仅在按住 Shift 时传入（与桌面行为一致，且减少动词枚举开销）；
/// 6. 启动时后台 STA 线程 Warmup，预加载外壳扩展 DLL，消除首次右键的卡顿。
/// </summary>
public static class ShellContextMenu
{
    private const uint CMF_NORMAL = 0x00000000;
    private const uint CMF_ITEMMENU = 0x00000080;
    private const uint CMF_CANRENAME = 0x00000010;
    private const uint CMF_EXTENDEDVERBS = 0x00000100;

    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_VERTICAL = 0x0040;

    private const uint WM_NULL = 0x0000;
    private const int SW_SHOWNORMAL = 1;
    private const uint CMIC_MASK_UNICODE = 0x00004000;
    private const int VK_SHIFT = 0x10;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    private static readonly Guid IID_IShellFolder = new("000214E6-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu = new("000214E4-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu2 = new("000214F4-0000-0000-C000-000000000046");
    private static readonly Guid IID_IContextMenu3 = new("BCFCE0A0-EC17-11D0-8D3B-00A0C9099F11");

    #region COM 接口

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc, [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, int grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, [In] ref Guid riid, out IntPtr ppv);
        void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, [In] ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl, IntPtr apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl, [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            [In] ref Guid riid, ref uint rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, int uFlags, out IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl, [MarshalAs(UnmanagedType.LPWStr)] string pszName, int uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        void QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(IntPtr pici);
        void GetCommandString(uint idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F4-0000-0000-C000-000000000046")]
    private interface IContextMenu2
    {
        void QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(IntPtr pici);
        void GetCommandString(uint idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        void HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("BCFCE0A0-EC17-11D0-8D3B-00A0C9099F11")]
    private interface IContextMenu3
    {
        void QueryContextMenu(IntPtr hmenu, uint indexMenu, uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(IntPtr pici);
        void GetCommandString(uint idCmd, uint uType, IntPtr pReserved, IntPtr pszName, uint cchMax);
        void HandleMenuMsg(uint uMsg, IntPtr wParam, IntPtr lParam);
        void HandleMenuMsg2(uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr plResult);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CMINVOKECOMMANDINFOEX
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public int dwHotKey;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTitle;
        public IntPtr lpVerbW;
        public IntPtr lpParametersW;
        public IntPtr lpDirectoryW;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpTitleW;
        public uint dwHotKeyFlag;
    }

    #endregion

    #region Win32

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetDesktopFolder(out IShellFolder ppshf);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILFindLastID(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILClone(IntPtr pidl);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ILRemoveLastID(IntPtr pidl);

    /// <summary>判断 PIDL 是否为空（等价 ILIsEmpty：空指针或首个 ITEMIDLIST 的 cb 为 0）。</summary>
    private static bool IsEmptyPidl(IntPtr pidl)
    {
        return pidl == IntPtr.Zero || Marshal.ReadInt16(pidl) == 0;
    }

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern IntPtr SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
    }

    #endregion

    // 当前活动的 IContextMenu3/IContextMenu2 引用（用于消息转发）
    [ThreadStatic]
    private static IContextMenu2? _activeContextMenu2;
    [ThreadStatic]
    private static IContextMenu3? _activeContextMenu3;

    // ---- 缓存（仅在调用 ShowContextMenu 的 UI 线程上使用，RCW 线程亲和）----
    private static readonly object _cacheLock = new();
    private static IShellFolder? _desktopFolder;
    private static readonly Dictionary<string, IShellFolder> _parentFolderCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Queue<string> _parentFolderCacheOrder = new();
    private const int MaxCachedParentFolders = 8;

    // ---- 消息窗口（每线程创建一次，全程复用）----
    private const string MsgWndClassName = "DeskOrganizer_ShellCmMsgWnd";
    private static bool _wndClassRegistered;
    [ThreadStatic]
    private static IntPtr _msgWindow;

    /// <summary>
    /// 系统右键菜单的执行结果。
    /// </summary>
    public enum ContextMenuResult
    {
        /// <summary>完全失败，菜单未显示。</summary>
        Failed,
        /// <summary>菜单显示了但用户取消（未选择命令）。</summary>
        Cancelled,
        /// <summary>菜单显示了且用户执行了命令。</summary>
        Executed,
    }

    /// <summary>
    /// 显示文件/文件夹的系统右键菜单（与 Windows 桌面一致，包含所有菜单项）。
    /// </summary>
    /// <returns>Failed=完全失败；Cancelled=用户取消；Executed=执行了命令。</returns>
    public static ContextMenuResult ShowContextMenu(string filePath, IntPtr hWnd, System.Drawing.Point screenPoint)
    {
        if (string.IsNullOrEmpty(filePath)) return ContextMenuResult.Failed;
        bool isDir = System.IO.Directory.Exists(filePath);
        if (!isDir && !System.IO.File.Exists(filePath))
            return ContextMenuResult.Failed;

        IntPtr pidlFull = IntPtr.Zero;
        IntPtr pContextMenu = IntPtr.Zero;
        IntPtr hMenu = IntPtr.Zero;
        IShellFolder? parentFolder = null;
        IContextMenu? contextMenu = null;

        try
        {
            // 1. 解析完整路径为 PIDL（一次调用）
            int hr = SHParseDisplayName(filePath, IntPtr.Zero, out pidlFull, 0, out _);
            if (hr != 0 || pidlFull == IntPtr.Zero)
            {
                App.Log($"[ShellContextMenu] SHParseDisplayName failed (hr={hr}) for '{filePath}'");
                return ContextMenuResult.Failed;
            }

            // 2. 子项 PIDL 指向 pidlFull 内部，无需单独释放
            IntPtr pidlChild = ILFindLastID(pidlFull);
            if (pidlChild == IntPtr.Zero) return ContextMenuResult.Failed;

            // 3. 获取父文件夹（优先缓存；未命中则 BindToObject 一次并缓存）
            string? parentKey = GetParentPathKey(filePath);
            parentFolder = GetCachedParentFolder(parentKey);
            if (parentFolder == null)
            {
                parentFolder = BindParentFolder(pidlFull, parentKey == null);
                if (parentFolder == null) return ContextMenuResult.Failed;
                CacheParentFolder(parentKey, parentFolder);
            }

            // 4. 获取 IContextMenu（只调一次 GetUIObjectOf；缓存文件夹失败时逐出缓存重试一次）
            pContextMenu = GetContextMenuPtr(parentFolder, pidlChild);
            if (pContextMenu == IntPtr.Zero && parentKey != null)
            {
                EvictParentFolder(parentKey);
                parentFolder = BindParentFolder(pidlFull, false);
                if (parentFolder == null) return ContextMenuResult.Failed;
                CacheParentFolder(parentKey, parentFolder);
                pContextMenu = GetContextMenuPtr(parentFolder, pidlChild);
            }
            if (pContextMenu == IntPtr.Zero)
            {
                App.Log("[ShellContextMenu] GetUIObjectOf(IContextMenu) failed");
                return ContextMenuResult.Failed;
            }

            // 5. 转换为托管对象；IContextMenu2/3 在同一个 COM 对象上 QueryInterface（零额外创建开销）
            contextMenu = Marshal.GetObjectForIUnknown(pContextMenu) as IContextMenu;
            if (contextMenu == null)
            {
                App.Log("[ShellContextMenu] Failed to cast to IContextMenu");
                return ContextMenuResult.Failed;
            }

            _activeContextMenu3 = null;
            _activeContextMenu2 = null;
            Guid iid3 = IID_IContextMenu3;
            if (Marshal.QueryInterface(pContextMenu, ref iid3, out IntPtr p3) == 0 && p3 != IntPtr.Zero)
            {
                _activeContextMenu3 = Marshal.GetObjectForIUnknown(p3) as IContextMenu3;
                Marshal.Release(p3);
            }
            if (_activeContextMenu3 == null)
            {
                Guid iid2 = IID_IContextMenu2;
                if (Marshal.QueryInterface(pContextMenu, ref iid2, out IntPtr p2) == 0 && p2 != IntPtr.Zero)
                {
                    _activeContextMenu2 = Marshal.GetObjectForIUnknown(p2) as IContextMenu2;
                    Marshal.Release(p2);
                }
            }

            // 6. 创建并填充菜单（CMF_EXTENDEDVERBS 仅在按住 Shift 时传入，与桌面一致）
            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return ContextMenuResult.Failed;

            const uint idCmdFirst = 1;
            const uint idCmdLast = 0x7FFF;
            uint flags = CMF_NORMAL | CMF_ITEMMENU | CMF_CANRENAME;
            if ((GetKeyState(VK_SHIFT) & 0x8000) != 0)
                flags |= CMF_EXTENDEDVERBS;
            contextMenu.QueryContextMenu(hMenu, 0, idCmdFirst, idCmdLast, flags);

            // 7. 复用消息窗口转发菜单消息
            IntPtr msgWindow = GetOrCreateMessageWindow();
            IntPtr ownerWnd = msgWindow != IntPtr.Zero ? msgWindow : hWnd;

            // 8. 显示菜单
            SetForegroundWindow(ownerWnd);
            uint cmd = (uint)TrackPopupMenuEx(hMenu,
                TPM_LEFTALIGN | TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_VERTICAL,
                screenPoint.X, screenPoint.Y, ownerWnd, IntPtr.Zero);
            PostMessage(ownerWnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd == 0) return ContextMenuResult.Cancelled;

            // 9. 执行命令
            uint cmdOffset = cmd - idCmdFirst;
            var info = new CMINVOKECOMMANDINFOEX
            {
                cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFOEX>(),
                fMask = CMIC_MASK_UNICODE,
                hwnd = hWnd,
                lpVerb = new IntPtr((int)cmdOffset),
                lpVerbW = new IntPtr((int)cmdOffset),
                lpParameters = IntPtr.Zero,
                lpParametersW = IntPtr.Zero,
                lpDirectory = IntPtr.Zero,
                lpDirectoryW = IntPtr.Zero,
                lpTitle = null!,
                lpTitleW = null!,
                nShow = SW_SHOWNORMAL,
                dwHotKey = 0,
                hIcon = IntPtr.Zero,
                dwHotKeyFlag = 0,
            };

            IntPtr pInfo = Marshal.AllocHGlobal(info.cbSize);
            try
            {
                Marshal.StructureToPtr(info, pInfo, false);
                contextMenu.InvokeCommand(pInfo);
                return ContextMenuResult.Executed;
            }
            finally
            {
                Marshal.FreeHGlobal(pInfo);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ShellContextMenu] ShowContextMenu failed: {ex.Message}");
            return ContextMenuResult.Failed;
        }
        finally
        {
            _activeContextMenu3 = null;
            _activeContextMenu2 = null;
            if (hMenu != IntPtr.Zero) DestroyMenu(hMenu);
            if (pContextMenu != IntPtr.Zero) Marshal.Release(pContextMenu);
            if (pidlFull != IntPtr.Zero) ILFree(pidlFull);
            // parentFolder / desktopFolder / msgWindow 均为缓存复用对象，此处不释放
        }
    }

    /// <summary>
    /// 启动时在后台 STA 线程预热：完整走一遍 PIDL 解析 + GetUIObjectOf + QueryContextMenu，
    /// 让外壳扩展 DLL 提前加载进进程（DLL 加载是进程级的），消除首次右键卡顿。
    /// </summary>
    public static void Warmup()
    {
        if (System.Threading.Interlocked.Exchange(ref _warmupStarted, 1) != 0) return;
        var t = new System.Threading.Thread(WarmupWorker)
        {
            IsBackground = true,
            Name = "ShellCmWarmup",
        };
        t.SetApartmentState(System.Threading.ApartmentState.STA);
        t.Start();
    }

    private static int _warmupStarted;

    private static void WarmupWorker()
    {
        try
        {
            // 取样：桌面上第一个 .lnk（围栏里最常见的条目类型），没有则用桌面文件夹
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string sample = desktop;
            try
            {
                var lnks = System.IO.Directory.GetFiles(desktop, "*.lnk");
                if (lnks.Length > 0) sample = lnks[0];
            }
            catch { }

            int hr = SHParseDisplayName(sample, IntPtr.Zero, out IntPtr pidlFull, 0, out _);
            if (hr != 0 || pidlFull == IntPtr.Zero) return;
            try
            {
                IntPtr pidlChild = ILFindLastID(pidlFull);
                IntPtr pidlParent = ILClone(pidlFull);
                if (pidlParent == IntPtr.Zero) return;
                try
                {
                    IShellFolder? parent = null;
                    int hr2 = SHGetDesktopFolder(out var desktopFolder);
                    if (hr2 == 0 && desktopFolder != null)
                    {
                        if (!ILRemoveLastID(pidlParent) || IsEmptyPidl(pidlParent))
                        {
                            parent = desktopFolder;
                        }
                        else
                        {
                            Guid iid = IID_IShellFolder;
                            IntPtr pUnk = IntPtr.Zero;
                            try { desktopFolder.BindToObject(pidlParent, IntPtr.Zero, ref iid, out pUnk); } catch { }
                            if (pUnk != IntPtr.Zero)
                            {
                                parent = Marshal.GetObjectForIUnknown(pUnk) as IShellFolder;
                                Marshal.Release(pUnk);
                            }
                        }
                    }
                    if (parent == null) return;

                    IntPtr pCm = GetContextMenuPtr(parent, pidlChild);
                    if (pCm == IntPtr.Zero) return;
                    try
                    {
                        if (Marshal.GetObjectForIUnknown(pCm) is IContextMenu cm)
                        {
                            IntPtr hMenu = CreatePopupMenu();
                            if (hMenu != IntPtr.Zero)
                            {
                                // 触发外壳扩展加载（进程级，一次即可）
                                cm.QueryContextMenu(hMenu, 0, 1, 0x7FFF, CMF_NORMAL | CMF_ITEMMENU | CMF_CANRENAME);
                                DestroyMenu(hMenu);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.Release(pCm);
                    }
                    App.Log("[ShellContextMenu] Warmup completed");
                }
                finally
                {
                    ILFree(pidlParent);
                }
            }
            finally
            {
                ILFree(pidlFull);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[ShellContextMenu] Warmup failed: {ex.Message}");
        }
    }

    // ---- 缓存辅助 ----

    private static IShellFolder? GetDesktopFolder()
    {
        if (_desktopFolder != null) return _desktopFolder;
        lock (_cacheLock)
        {
            if (_desktopFolder == null)
            {
                int hr = SHGetDesktopFolder(out var folder);
                if (hr == 0) _desktopFolder = folder;
            }
            return _desktopFolder;
        }
    }

    /// <summary>父目录路径作为缓存键；根目录（如 C:\）返回 null 表示父级是桌面。</summary>
    private static string? GetParentPathKey(string filePath)
    {
        try
        {
            string trimmed = filePath.TrimEnd('\\');
            if (trimmed.Length <= 2 && trimmed.EndsWith(":")) return null; // 驱动器根，父级=桌面
            return System.IO.Path.GetDirectoryName(trimmed);
        }
        catch
        {
            return null;
        }
    }

    private static IShellFolder? GetCachedParentFolder(string? key)
    {
        if (key == null) return GetDesktopFolder();
        lock (_cacheLock)
        {
            return _parentFolderCache.TryGetValue(key, out var folder) ? folder : null;
        }
    }

    private static void CacheParentFolder(string? key, IShellFolder folder)
    {
        if (key == null) return; // 桌面文件夹已由 _desktopFolder 缓存
        lock (_cacheLock)
        {
            if (_parentFolderCache.ContainsKey(key)) return;
            _parentFolderCache[key] = folder;
            _parentFolderCacheOrder.Enqueue(key);
            while (_parentFolderCacheOrder.Count > MaxCachedParentFolders)
            {
                string old = _parentFolderCacheOrder.Dequeue();
                if (_parentFolderCache.Remove(old, out var oldFolder))
                {
                    try { Marshal.ReleaseComObject(oldFolder); } catch { }
                }
            }
        }
    }

    private static void EvictParentFolder(string key)
    {
        lock (_cacheLock)
        {
            if (_parentFolderCache.Remove(key, out var folder))
            {
                try { Marshal.ReleaseComObject(folder); } catch { }
            }
        }
    }

    /// <summary>通过完整 PIDL 绑定父文件夹；parentIsDesktop 时直接返回桌面文件夹。</summary>
    private static IShellFolder? BindParentFolder(IntPtr pidlFull, bool parentIsDesktop)
    {
        var desktop = GetDesktopFolder();
        if (desktop == null) return null;
        if (parentIsDesktop) return desktop;

        IntPtr pidlParent = ILClone(pidlFull);
        if (pidlParent == IntPtr.Zero) return null;
        try
        {
            if (!ILRemoveLastID(pidlParent) || IsEmptyPidl(pidlParent))
                return desktop; // 父级是桌面

            Guid iid = IID_IShellFolder;
            IntPtr pUnk = IntPtr.Zero;
            try { desktop.BindToObject(pidlParent, IntPtr.Zero, ref iid, out pUnk); }
            catch { pUnk = IntPtr.Zero; }
            if (pUnk == IntPtr.Zero) return null;
            var folder = Marshal.GetObjectForIUnknown(pUnk) as IShellFolder;
            Marshal.Release(pUnk);
            return folder;
        }
        finally
        {
            ILFree(pidlParent);
        }
    }

    private static IntPtr GetContextMenuPtr(IShellFolder parentFolder, IntPtr pidlChild)
    {
        Guid iid = IID_IContextMenu;
        uint reserved = 0;
        try
        {
            parentFolder.GetUIObjectOf(IntPtr.Zero, 1, new IntPtr[] { pidlChild }, ref iid, ref reserved, out IntPtr p);
            return p;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    // ---- 消息窗口（复用）----

    private static IntPtr _wndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // 转发菜单消息给 IContextMenu3/IContextMenu2
        if (_activeContextMenu3 != null)
        {
            try
            {
                _activeContextMenu3.HandleMenuMsg2(msg, wParam, lParam, out IntPtr result);
                if (result != IntPtr.Zero) return result;
            }
            catch { }
        }
        else if (_activeContextMenu2 != null)
        {
            try { _activeContextMenu2.HandleMenuMsg(msg, wParam, lParam); }
            catch { }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static IntPtr GetOrCreateMessageWindow()
    {
        if (_msgWindow != IntPtr.Zero) return _msgWindow;

        IntPtr hInstance = GetModuleHandle(null);
        if (!_wndClassRegistered)
        {
            var wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate.Value!),
                lpszClassName = MsgWndClassName,
                hInstance = hInstance,
            };
            ushort atom = RegisterClassW(ref wc);
            if (atom == 0 && Marshal.GetLastWin32Error() != ERROR_CLASS_ALREADY_EXISTS)
                return IntPtr.Zero;
            _wndClassRegistered = true;
        }

        _msgWindow = CreateWindowEx(0, MsgWndClassName, "", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        return _msgWindow;
    }

    private static readonly System.Threading.ThreadLocal<WndProcDelegate> _wndProcDelegate =
        new(() => new WndProcDelegate(_wndProc));
}
