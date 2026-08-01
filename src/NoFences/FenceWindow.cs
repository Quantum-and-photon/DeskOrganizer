using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeskOrganizerModel = DeskOrganizer.Model;
using DeskOrganizer.NoFences.Model;
using DeskOrganizer.NoFences.Util;
using DeskOrganizer.NoFences.Win32;

namespace DeskOrganizer.NoFences;

/// <summary>
/// 栅栏窗口 —— 核心类。一个 FenceWindow 实例代表桌面上的一个栅栏区域，
/// 包含标题栏、图标网格、拖放支持、右键菜单等完整功能。
/// </summary>
public class FenceWindow : Form
{
    #region 常量

    private const int DEFAULT_ICON_SIZE = 48;
    private int _iconSize = DEFAULT_ICON_SIZE;
    private const int ICON_PADDING = 4;
    private const int CELL_PADDING = 8;
    private const int TEXT_HEIGHT = 32;
    private const int TEXT_PADDING = 2;
    private const int ACCENT_BAR_HEIGHT = 3;
    private const int SCROLL_STEP = 48;
    private const int RESIZE_BORDER = 6;

    #endregion

    #region 私有字段

    private List<FenceEntry> _entries = new();
    private readonly ThumbnailProvider _thumbnailProvider;
    private ThrottledExecution? _resizeThrottle;
    private ThrottledExecution? _moveThrottle;
    private bool _isMovingOrResizing; // 拖动/调整大小期间禁止频繁保存
    private int _suppressEventCount; // 代码设置位置时彻底禁止 FenceChanged（引用计数，支持嵌套调用）
    private bool _isLoaded; // 防止初始化期间 FenceChanged 事件把位置覆盖为 0
    public bool SuppressFenceChanged { get; set; } // 外部临时禁用 FenceChanged 回调

    /// <summary>开始禁止 FenceChanged 事件（代码设置位置时调用，防止回调覆盖配置）。</summary>
    public void BeginSuppressEvents() { _suppressEventCount++; }

    /// <summary>结束禁止 FenceChanged 事件，延迟清除以确保 throttle 过期。</summary>
    public void EndSuppressEvents(int delayMs = 300)
    {
        var t = new System.Windows.Threading.DispatcherTimer();
        t.Interval = System.TimeSpan.FromMilliseconds(delayMs);
        t.Tick += (_, _) =>
        {
            if (_suppressEventCount > 0) _suppressEventCount--;
            t.Stop();
        };
        t.Start();
    }
    private int _scrollOffset;
    private int _maxScrollOffset;
    private string _fenceName = "Fence";
    private string _fenceId = string.Empty;
    private Point _dragStart;
    private bool _isDragging;
    private DateTime? _lastClickTime;
    private FenceEntry? _lastClickEntry;
    private bool _isResizing;
    private Rectangle _resizeRect;
    private FenceEntry? _hoveredEntry;
    private FenceEntry? _selectedEntry;
    private bool _isDisposed;
    // 标题栏隐藏时，空白区域拖动窗口的自定义拖动状态
    private bool _isCustomDragging;
    private Point _customDragOffset;
    private DateTime? _lastBlankClickTime;
    private ToolStripMenuItem? _miEntryOpen;
    private ToolStripMenuItem? _miEntryOpenTarget;
    private ToolStripMenuItem? _miEntryRename;
    private ToolStripMenuItem? _miProperties;
    private ToolStripMenuItem? _miRunAsAdmin;

    // 外观属性
    private Color _backgroundColor = Color.FromArgb(30, 30, 46);
    private Color _titleTextColor = Color.White;
    private Color _accentColor = Color.FromArgb(100, 149, 237); // Cornflower Blue
    private Color _textColor = Color.FromArgb(205, 214, 244);
    private Color _hoverColor = Color.FromArgb(49, 50, 68);
    private Color _selectedColor = Color.FromArgb(137, 180, 250);

    // 来自 FenceInfo 的配置属性
    private int _cornerRadius = 8;
    private int _titleHeight = 32;
    private double _opacity = 0.85;
    private bool _blurEnabled = true;
    private bool _locked;

    // 内联编辑标题（用浮动 Form 避免父窗口 UserPaint 影响）
    private Form? _titleEditForm;
    private TextBox? _titleEditBox;

    // 字体
    private readonly Font _titleFont;
    private readonly Font _entryNameFont;
    private readonly Font _entryNameSmallFont;

    // 上下文菜单
    private ContextMenuStrip? _contextMenu;
    private GlobalMouseFilter? _globalMouseFilter;

    // 低级鼠标钩子（用于捕获被桌面层拦截的右键消息）
    private IntPtr _mouseHook = IntPtr.Zero;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_NCRBUTTONUP = 0x00A5;
    private const int WH_MOUSE_LL = 14;
    private const int WM_APP_RIGHTCLICK = 0x8000; // 自定义消息

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    // 事件
    private readonly SolidBrush _accentBrush;

    #endregion

    #region 公共属性

    /// <summary>栅栏名称。</summary>
    public string FenceName
    {
        get => _fenceName;
        set { _fenceName = value; Invalidate(); }
    }

    /// <summary>条目列表。</summary>
    public List<FenceEntry> Entries
    {
        get => _entries;
        set
        {
            _entries = value ?? new List<FenceEntry>();
            RecalculateLayout();
            Invalidate();
            _ = LoadThumbnailsAsync();
        }
    }

    /// <summary>背景颜色。</summary>
    public Color BackgroundColor
    {
        get => _backgroundColor;
        set
        {
            // 保持当前 alpha（由 _opacity 控制），只更新 RGB
            _backgroundColor = Color.FromArgb(_backgroundColor.A, value.R, value.G, value.B);
            ApplyEffects(); Invalidate();
        }
    }

    /// <summary>背景不透明度 (0.1 - 1.0)，只影响背景，不影响图标和文字。</summary>
    public double OpacityValue
    {
        get => _opacity;
        set
        {
            _opacity = Math.Clamp(value, 0.1, 1.0);
            UpdateBackgroundAlpha();
        }
    }

    /// <summary>根据 _opacity 更新背景色 alpha 通道，保持 RGB 不变。</summary>
    private void UpdateBackgroundAlpha()
    {
        _backgroundColor = Color.FromArgb((int)(_opacity * 255), _backgroundColor.R, _backgroundColor.G, _backgroundColor.B);
        ApplyEffects();
        Invalidate();
    }

    /// <summary>圆角半径。</summary>
    public int CornerRadius
    {
        get => _cornerRadius;
        set { _cornerRadius = Math.Max(0, value); Invalidate(); }
    }

    /// <summary>标题栏高度。</summary>
    public int TitleHeight
    {
        get => _titleHeight;
        set { _titleHeight = Math.Clamp(value, 20, 100); RecalculateLayout(); Invalidate(); }
    }

    /// <summary>是否锁定栅栏（禁止拖放和移动）。</summary>
    public bool Locked
    {
        get => _locked;
        set { _locked = value; }
    }

    /// <summary>强调色。</summary>
    public Color AccentColor
    {
        get => _accentColor;
        set { _accentColor = value; _accentBrush.Color = value; Invalidate(); }
    }

    /// <summary>当用户请求新建栅栏时触发。</summary>
    public event Action<FenceWindow>? RequestNewFence;

    /// <summary>当用户请求删除此栅栏时触发。</summary>
    public event Action<FenceWindow>? RequestDeleteFence;

    /// <summary>当栅栏位置或大小改变时触发。</summary>
    public event Action<FenceWindow>? FenceChanged;

    /// <summary>当条目被添加或删除时触发。</summary>
    public event Action? EntriesChanged;

    #endregion

    #region 构造函数

    public FenceWindow()
    {
        // 初始化字体
        _titleFont = new Font("Segoe UI", 12f, FontStyle.Regular);
        _entryNameFont = new Font("Segoe UI", 9f, FontStyle.Regular);
        _entryNameSmallFont = new Font("Segoe UI", 8f, FontStyle.Regular);

        // 初始化画笔
        _accentBrush = new SolidBrush(_accentColor);

        // 初始化缩略图提供器
        _thumbnailProvider = new ThumbnailProvider(_iconSize);
        _thumbnailProvider.ThumbnailLoaded += OnThumbnailLoaded;

        // 初始化节流执行器
        _resizeThrottle = new ThrottledExecution(OnResizeCore, 50);
        _moveThrottle = new ThrottledExecution(OnMoveCore, 50);

        InitializeForm();
        InitializeContextMenu();
        InitializeDragDrop();
    }

    public FenceWindow(DeskOrganizerModel.FenceInfo fence) : this()
    {
        LoadFromModelFenceInfo(fence);
    }

    /// <summary>
    /// 重写 CreateParams。
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            return base.CreateParams;
        }
    }

    private void InitializeForm()
    {
        // 基本窗体属性
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(300, 400);
        ClientSize = new Size(300, 400);
        BackColor = _backgroundColor;
        // 窗体样式
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.DoubleBuffer |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);

        // TopMost + ToolWindow + SendToBack 由 FenceManager 控制
        TopMost = true;
        Opacity = 1.0; // 窗口本身不透明，背景不透明度通过 alpha 通道控制

        // 事件绑定
        Paint += OnPaint;
        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        MouseMove += OnMouseMove;
        MouseWheel += OnMouseWheel;
        MouseEnter += (_, _) => { _hoveredEntry = null; };
        MouseLeave += (_, _) =>
        {
            _hoveredEntry = null;
            _isDragging = false;
            _isResizing = false;
            Invalidate();
        };
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragDrop += OnDragDrop;
        DragLeave += (_, _) => Invalidate();
        LocationChanged += (_, _) => _moveThrottle?.Run();
        Resize += (_, _) => _resizeThrottle?.Run();

        // 全局鼠标消息过滤：点击围栏外部时关闭右键菜单
        _globalMouseFilter = new GlobalMouseFilter(this);
        Application.AddMessageFilter(_globalMouseFilter);
        VisibleChanged += OnVisibleChanged;
    }

    private void InitializeContextMenu()
    {
        _contextMenu = new ContextMenuStrip();

        var miLock = new ToolStripMenuItem("锁定栅栏", null, OnLockClicked)
        { CheckOnClick = true };
        _contextMenu.Items.Add(miLock);

        var miMinimize = new ToolStripMenuItem("最小化", null, OnMinimizeClicked);
        _contextMenu.Items.Add(miMinimize);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var miRename = new ToolStripMenuItem("重命名栅栏", null, OnRenameClicked);
        _contextMenu.Items.Add(miRename);

        var miDeleteEntry = new ToolStripMenuItem("删除条目", null, OnDeleteEntryClicked)
        { Enabled = false };
        _contextMenu.Items.Add(miDeleteEntry);

        var miOrganize = new ToolStripMenuItem("整理围栏内条目", null, OnOrganizeFenceEntriesClicked);
        _contextMenu.Items.Add(miOrganize);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 外观设置子菜单
        var miAppearance = new ToolStripMenuItem("外观设置");
        _contextMenu.Items.Add(miAppearance);

        var miBgColor = new ToolStripMenuItem("背景颜色...", null, OnBackgroundColorClicked);
        miAppearance.DropDownItems.Add(miBgColor);

        var miOpacity = new ToolStripMenuItem("不透明度...", null, OnOpacityClicked);
        miAppearance.DropDownItems.Add(miOpacity);

        var miBlur = new ToolStripMenuItem("毛玻璃效果", null, OnBlurClicked)
        { CheckOnClick = true, Checked = true };
        miAppearance.DropDownItems.Add(miBlur);

        var miAccentColor = new ToolStripMenuItem("强调色...", null, OnAccentColorClicked);
        miAppearance.DropDownItems.Add(miAccentColor);

        _contextMenu.Items.Add(new ToolStripSeparator());

        // 条目操作（选中条目时可用）
        _miEntryOpen = new ToolStripMenuItem("打开", null, OnEntryOpenClicked) { Enabled = false };
        _contextMenu.Items.Add(_miEntryOpen);

        _miEntryOpenTarget = new ToolStripMenuItem("打开快捷方式目标", null, OnEntryOpenTargetClicked) { Enabled = false };
        _contextMenu.Items.Add(_miEntryOpenTarget);

        var miShellMenu = new ToolStripMenuItem("打开文件位置", null, OnOpenFileLocationClicked)
        { Enabled = false };
        _contextMenu.Items.Add(miShellMenu);

        var miProperties = new ToolStripMenuItem("属性", null, OnPropertiesClicked)
        { Enabled = false };
        _miProperties = miProperties;
        _contextMenu.Items.Add(miProperties);

        var miRunAsAdmin = new ToolStripMenuItem("以管理员身份运行", null, OnRunAsAdminClicked)
        { Enabled = false };
        _miRunAsAdmin = miRunAsAdmin;
        _contextMenu.Items.Add(miRunAsAdmin);

        _miEntryRename = new ToolStripMenuItem("重命名", null, OnRenameEntryClicked) { Enabled = false };
        _contextMenu.Items.Add(_miEntryRename);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var miNewFence = new ToolStripMenuItem("新建栅栏", null, OnNewFenceClicked);
        _contextMenu.Items.Add(miNewFence);

        var miDeleteFence = new ToolStripMenuItem("删除栅栏", null, OnDeleteFenceClicked);
        _contextMenu.Items.Add(miDeleteFence);

        _contextMenu.Items.Add(new ToolStripSeparator());

        var miCheckLinks = new ToolStripMenuItem("手动检测失效条目", null, OnCheckBrokenLinksClicked);
        _contextMenu.Items.Add(miCheckLinks);

        var miAutoArrange = new ToolStripMenuItem("自动排布所有围栏", null, OnAutoArrangeClicked);
        _contextMenu.Items.Add(miAutoArrange);

        var miHeight = new ToolStripMenuItem("调整标题高度", null, OnAdjustTitleHeightClicked);
        _contextMenu.Items.Add(miHeight);

        // 存储菜单项引用
        _contextMenu.Tag = new Dictionary<string, ToolStripMenuItem>
        {
            ["Lock"] = miLock,
            ["DeleteEntry"] = miDeleteEntry,
            ["ShellMenu"] = miShellMenu,
            ["Blur"] = miBlur,
            ["EntryOpen"] = _miEntryOpen,
            ["EntryOpenTarget"] = _miEntryOpenTarget
        };
    }

    private void InitializeDragDrop()
    {
        AllowDrop = true;
    }

    #endregion

    #region 窗口效果

    /// <summary>
    /// 应用模糊效果和投影阴影。
    /// </summary>
    public void ApplyEffects()
    {
        if (!IsHandleCreated || Disposing || _isDisposed) return;

        var handle = Handle;

        try
        {
            if (_blurEnabled)
            {
                // 毛玻璃模式：启用 DWM Aero Blur
                // GradientColor 用不透明背景色（alpha=255），DWM 负责模糊
                // Form.Opacity 必须为 1.0，否则 GDI+ 绘制的图标可能不显示
                // 透明度由 DrawBackground 中的半透明背景色控制
                uint gradientColor = BlurUtil.ColorToAbgr(_backgroundColor, 255);
                BlurUtil.EnableBlur(handle, gradientColor);
                Opacity = 1.0;
            }
            else
            {
                // 普通模式：用 Form.Opacity 控制整体透明度
                BlurUtil.DisableBlur(handle);
                Opacity = Math.Max(0.1, _opacity);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[FenceWindow] ApplyEffects failed: {ex.Message}");
        }

        try
        {
            DropShadow.Enable(handle);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[FenceWindow] DropShadow failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 设置窗口为工具窗口模式（Alt-Tab 隐藏）并置顶。
    /// </summary>
    public void ApplyWindowStyles()
    {
        if (!IsHandleCreated || Disposing || _isDisposed) return;

        var handle = Handle;
        WindowUtil.EnableToolWindow(handle);
        WindowUtil.SetTopMost(handle, true);
    }

    /// <summary>
    /// 将窗口粘附到桌面。
    /// </summary>
    public void GlueToDesktop()
    {
        if (!IsHandleCreated || Disposing || _isDisposed) return;
        try
        {
            DesktopUtil.GlueToDesktop(Handle);
            // 安装全局鼠标钩子，捕获被桌面层拦截的右键
            InstallMouseHook();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[FenceWindow] GlueToDesktop failed: {ex.Message}");
        }
    }

    private static IntPtr _sharedMouseHook = IntPtr.Zero;
    private static LowLevelMouseProc? _sharedMouseHookProc;
    private static readonly List<FenceWindow> _aliveFences = new();

    private void InstallMouseHook()
    {
        // 所有围栏共享一个全局钩子，避免多钩子冲突
        lock (_aliveFences)
        {
            if (!_aliveFences.Contains(this))
                _aliveFences.Add(this);

            if (_sharedMouseHook != IntPtr.Zero) return;

            _sharedMouseHookProc = SharedMouseHookCallback;
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            using var module = process.MainModule!;
            _sharedMouseHook = SetWindowsHookEx(WH_MOUSE_LL, _sharedMouseHookProc, GetModuleHandle(module.ModuleName), 0);
            App.Log($"[FenceWindow] Shared mouse hook installed: {_sharedMouseHook != IntPtr.Zero}");
        }
    }

    private static IntPtr SharedMouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        int msg = (int)wParam;
        if (nCode >= 0 && (msg == WM_RBUTTONUP || msg == WM_NCRBUTTONUP))
        {
            var hookStruct = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            // 不使用 lock 和 PointToClient（避免跨线程死锁导致系统鼠标卡顿）
            // 直接遍历所有围栏，用 WindowFromPoint 找到目标窗口
            FenceWindow[] fences;
            lock (_aliveFences)
            {
                fences = _aliveFences.ToArray();
            }

            foreach (var f in fences)
            {
                if (f.IsDisposed || !f.Visible) continue;
                var rect = new Rectangle(f.Location, f.Size);
                if (rect.Contains(hookStruct.pt.X, hookStruct.pt.Y))
                {
                    // 用 PostMessage 发送到目标窗口，不在钩子线程做任何 UI 操作
                    int param = ((hookStruct.pt.Y & 0xFFFF) << 16) | (hookStruct.pt.X & 0xFFFF);
                    PostMessage(f.Handle, WM_APP_RIGHTCLICK, IntPtr.Zero, (IntPtr)param);
                    break;
                }
            }
            // 不吃掉消息，让系统正常处理（避免影响其他应用的右键）
        }
        return CallNextHookEx(_sharedMouseHook, nCode, wParam, lParam);
    }

    private void UninstallMouseHook()
    {
        lock (_aliveFences)
        {
            _aliveFences.Remove(this);
            // 所有围栏都关闭后卸载共享钩子
            if (_aliveFences.Count == 0 && _sharedMouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_sharedMouseHook);
                _sharedMouseHook = IntPtr.Zero;
                _sharedMouseHookProc = null;
            }
        }
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        // 已改为共享钩子，此方法保留兼容但不再使用
        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    #endregion

    #region 事件处理

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowStyles();
        ApplyEffects();
        // 安装共享全局鼠标钩子（捕获被桌面层拦截的右键）
        InstallMouseHook();
    }

    protected override void SetVisibleCore(bool value)
    {
        base.SetVisibleCore(value);
    }

    private void OnVisibleChanged(object? sender, EventArgs e)
    {
        if (Visible && IsHandleCreated)
        {
            ApplyEffects();
        }
    }

    protected override void WndProc(ref Message m)
    {
        // 直接在 WndProc 中处理右键抬起（不依赖低级钩子）
        if (m.Msg == 0x0205) // WM_RBUTTONUP
        {
            int x = (int)m.LParam & 0xFFFF;
            int y = ((int)m.LParam >> 16) & 0xFFFF;
            HandleRightClick(new Point(x, y));
            m.Result = IntPtr.Zero;
            return;
        }

        // 处理低级鼠标钩子发送的自定义右键消息（保留兼容）
        if (m.Msg == WM_APP_RIGHTCLICK)
        {
            // lParam 包含屏幕坐标
            int screenX = (int)m.LParam & 0xFFFF;
            int screenY = ((int)m.LParam >> 16) & 0xFFFF;
            var clientPos = PointToClient(new Point(screenX, screenY));
            HandleRightClick(clientPos);
            m.Result = IntPtr.Zero;
            return;
        }

        // 在 base.WndProc 之前拦截拖动开始/结束
        if (m.Msg == 0x0231) // WM_ENTERSIZEMOVE
        {
            _isMovingOrResizing = true;
        }
        else if (m.Msg == 0x0232) // WM_EXITSIZEMOVE
        {
            _isMovingOrResizing = false;
            if (_isLoaded && !SuppressFenceChanged)
            {
                FenceChanged?.Invoke(this);
            }
        }

        // 不在 WM_WINDOWPOSCHANGING 中修改位置 — 修改会破坏 Windows 拖动的鼠标跟踪，导致围栏滑动

        // 在 base.WndProc 之前拦截双击标题栏，阻止最大化并进入内联编辑
        if (m.Msg == 0x00A3) // WM_NCLBUTTONDBLCLK
        {
            var screenPos = new Point((int)((short)(m.LParam.ToInt32() & 0xFFFF)), (int)((short)(m.LParam.ToInt32() >> 16)));
            var clientPos = PointToClient(screenPos);
            if (IsInTitleBar(clientPos))
            {
                StartInlineEditTitle();
                m.Result = IntPtr.Zero;
                return; // 不调用 base.WndProc，阻止最大化
            }
        }

        // 在 base.WndProc 之前拦截右键
        // 不拦截 WM_NCRBUTTONDOWN — 标题栏右键不做处理

        base.WndProc(ref m);

        switch (m.Msg)
        {
            case WindowUtil.WM_NCHITTEST:
                // 让标题栏区域可以被拖动
                if (!Locked && m.Result == (IntPtr)WindowUtil.HTCLIENT)
                {
                    var pos = PointToClient(new Point((int)((short)(m.LParam.ToInt32() & 0xFFFF)), (int)((short)(m.LParam.ToInt32() >> 16))));
                    if (IsInTitleBar(pos))
                    {
                        m.Result = (IntPtr)WindowUtil.HTCAPTION;
                    }
                    else if (IsInResizeBorder(pos))
                    {
                        m.Result = (IntPtr)WindowUtil.HTBOTTOMRIGHT;
                    }
                }
                break;

            case WindowUtil.WM_MOUSEACTIVATE:
                // 编辑标题时强制激活，确保失焦保存
                if (_titleEditForm != null)
                {
                    m.Result = (IntPtr)WindowUtil.MA_ACTIVATE;
                    break;
                }
                // 标题栏拖动需要窗口激活，其他区域不抢焦点
                {
                    var pos = PointToClient(new Point((int)((short)(m.LParam.ToInt32() & 0xFFFF)), (int)((short)(m.LParam.ToInt32() >> 16))));
                    if (IsInTitleBar(pos))
                        m.Result = (IntPtr)WindowUtil.MA_ACTIVATE;
                    else
                        m.Result = (IntPtr)WindowUtil.MA_NOACTIVATE;
                    break;
                }
        }
    }

    private void OnPaint(object? sender, PaintEventArgs e)
    {
        if (Disposing || _isDisposed) return;

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var bounds = ClientRectangle;
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        DrawBackground(g, bounds);
        DrawTitleBar(g, bounds);
        DrawIconGrid(g, bounds);
        DrawBorder(g, bounds);
    }

    private void DrawBackground(Graphics g, Rectangle bounds)
    {
        if (_blurEnabled)
        {
            // 毛玻璃模式：画半透明背景色，让 DWM blur 透过
            // alpha 由 _opacity 控制
            byte bgAlpha = (byte)Math.Clamp(_opacity * 255, 30, 200);
            using var bgBrush = new SolidBrush(Color.FromArgb(bgAlpha, _backgroundColor.R, _backgroundColor.G, _backgroundColor.B));
            g.FillRoundedRectangle(bgBrush, bounds, _cornerRadius);
        }
        else
        {
            // 普通模式：画不透明背景色，整体透明度由 Form.Opacity 控制
            using var bgBrush = new SolidBrush(Color.FromArgb(255, _backgroundColor.R, _backgroundColor.G, _backgroundColor.B));
            g.FillRoundedRectangle(bgBrush, bounds, _cornerRadius);
        }
    }

    private void DrawTitleBar(Graphics g, Rectangle bounds)
    {
        // 标题栏高度为 0 时，不绘制任何标题内容（完全隐藏，不显露字体）
        if (_titleHeight <= 0) return;

        var titleRect = new Rectangle(0, 0, bounds.Width, _titleHeight);

        // 绘制强调色条纹
        using var accentBrush = new SolidBrush(_accentColor);
        g.FillRoundedRectangle(accentBrush, new Rectangle(0, 0, bounds.Width, ACCENT_BAR_HEIGHT), _cornerRadius);

        // 标题居中绘制
        using var textBrush = new SolidBrush(_titleTextColor);
        var titleSize = g.MeasureString(_fenceName, _titleFont);
        var titleText = _fenceName;
        // 如果文字太长，截断显示
        if (titleSize.Width > titleRect.Width - 24)
        {
            while (g.MeasureString(titleText + "...", _titleFont).Width > titleRect.Width - 24 && titleText.Length > 1)
                titleText = titleText.Substring(0, titleText.Length - 1);
            titleText += "...";
            titleSize = g.MeasureString(titleText, _titleFont);
        }
        var titleX = (titleRect.Width - titleSize.Width) / 2f;
        var titleY = ACCENT_BAR_HEIGHT + (titleRect.Height - ACCENT_BAR_HEIGHT - titleSize.Height) / 2f;
        g.DrawString(titleText, _titleFont, textBrush, titleX, titleY);

        // 如果锁定，显示锁图标提示
        if (_locked)
        {
            using var lockBrush = new SolidBrush(Color.FromArgb(180, _titleTextColor));
            var lockText = "(已锁定)";
            var lockSize = g.MeasureString(lockText, _entryNameSmallFont);
            var lockRect = new RectangleF(
                bounds.Width - lockSize.Width - 12,
                ACCENT_BAR_HEIGHT + (titleRect.Height - ACCENT_BAR_HEIGHT - lockSize.Height) / 2,
                lockSize.Width, lockSize.Height);
            g.DrawString(lockText, _entryNameSmallFont, lockBrush, lockRect);
        }
    }

    private void DrawIconGrid(Graphics g, Rectangle bounds)
    {
        var contentRect = new Rectangle(
            0, _titleHeight,
            bounds.Width, bounds.Height - _titleHeight);

        // 仅在 DEBUG 模式下输出绘制日志，避免 OnPaint 中频繁文件 I/O
        #if DEBUG
        App.Log($"[FenceWindow] DrawIconGrid fence={_fenceName}, entries={_entries.Count}, contentRect={contentRect}, blur={_blurEnabled}, opacity={_opacity}");
        #endif

        // 设置裁剪区域以防止溢出
        using var clipPath = CreateContentClipPath(contentRect);
        g.SetClip(clipPath);

        // 计算网格布局
        int cellWidth = _iconSize + ICON_PADDING * 2;
        int cellHeight = _iconSize + TEXT_HEIGHT + TEXT_PADDING;
        int cols = Math.Max(1, contentRect.Width / cellWidth);
        int rows = (_entries.Count + cols - 1) / cols;

        // 计算水平居中偏移
        int totalGridWidth = cols * cellWidth;
        int xOffset = Math.Max(0, (contentRect.Width - totalGridWidth) / 2);

        // 绘制所有可见条目
        for (int i = 0; i < _entries.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;

            int x = contentRect.X + xOffset + col * cellWidth;
            int y = contentRect.Y + row * cellHeight - _scrollOffset;

            // 跳过不可见的条目
            if (y + cellHeight < contentRect.Y || y > contentRect.Bottom)
                continue;

            var entry = _entries[i];
            var cellRect = new Rectangle(x, y, cellWidth, cellHeight);

            // 绘制悬停/选中效果
            if (entry == _hoveredEntry || entry == _selectedEntry)
            {
                using var hoverBrush = new SolidBrush(entry == _selectedEntry ? _selectedColor.WithAlpha(40) : _hoverColor);
                g.FillRoundedRectangle(hoverBrush, cellRect, 4);
            }

            // 绘制图标
            var iconRect = new Rectangle(
                x + (cellWidth - _iconSize) / 2,
                y,
                _iconSize, _iconSize);

            if (entry.Thumbnail != null)
            {
                try
                {
                    // 用 GraphicsUnit.Pixel 绘制，避免 DPI 缩放导致模糊
                    g.DrawImage(entry.Thumbnail, iconRect, 0, 0, entry.Thumbnail.Width, entry.Thumbnail.Height, GraphicsUnit.Pixel);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"DrawImage FAIL: {entry.FilePath} - {ex.Message}");
                    DrawPlaceholderIcon(g, iconRect, entry);
                }
            }
            else
            {
                DrawPlaceholderIcon(g, iconRect, entry);
            }

            // 绘制文件名（支持自动换行，最多两行）
            var nameRect = new RectangleF(
                x,
                y + _iconSize + TEXT_PADDING,
                cellWidth,
                TEXT_HEIGHT);

            using (var nameBrush = new SolidBrush(_textColor))
            using (var format = new StringFormat
            {
                Trimming = StringTrimming.EllipsisCharacter,
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            })
            {
                g.DrawString(entry.DisplayName, _entryNameFont, nameBrush, nameRect, format);
            }
        }

        g.ResetClip();

        // 计算最大滚动偏移
        int totalHeight = rows * cellHeight;
        _maxScrollOffset = Math.Max(0, totalHeight - contentRect.Height);

        // 绘制滚动提示（如果有更多内容）
        if (_maxScrollOffset > 0)
        {
            DrawScrollIndicators(g, contentRect);
        }
    }

    private void DrawPlaceholderIcon(Graphics g, Rectangle iconRect, FenceEntry entry)
    {
        using var placeholderBrush = new SolidBrush(Color.FromArgb(60, 60, 80));
        g.FillRoundedRectangle(placeholderBrush, iconRect, 6);

        // 绘制类型标识（F 或 D）
        using var labelBrush = new SolidBrush(Color.FromArgb(120, 120, 140));
        using var labelFont = new Font("Segoe UI", 14f, FontStyle.Bold);
        string label = entry.EntryType == Model.EntryType.Folder ? "D" : "F";
        g.DrawCenteredString(label, labelFont, labelBrush, iconRect);
    }

    private void DrawBorder(Graphics g, Rectangle bounds)
    {
        // 围栏边框：半透明白色，带柔和光晕
        using var borderPen = new Pen(Color.FromArgb(60, 255, 255, 255), 1.5f);
        g.DrawRoundedRectangle(borderPen, bounds, _cornerRadius);
    }

    private void DrawScrollIndicators(Graphics g, Rectangle contentRect)
    {
        // 顶部渐变遮罩（如果向上滚动了）
        if (_scrollOffset > 0)
        {
            using var topBrush = new LinearGradientBrush(
                new Rectangle(contentRect.X, contentRect.Y, contentRect.Width, 20),
                Color.FromArgb(180, _backgroundColor), Color.FromArgb(0, _backgroundColor),
                LinearGradientMode.Vertical);
            g.FillRectangle(topBrush, contentRect.X, contentRect.Y, contentRect.Width, 20);
        }

        // 底部渐变遮罩（如果还有更多内容）
        if (_scrollOffset < _maxScrollOffset)
        {
            using var bottomBrush = new LinearGradientBrush(
                new Rectangle(contentRect.X, contentRect.Bottom - 20, contentRect.Width, 20),
                Color.FromArgb(0, _backgroundColor), Color.FromArgb(180, _backgroundColor),
                LinearGradientMode.Vertical);
            g.FillRectangle(bottomBrush, contentRect.X, contentRect.Bottom - 20, contentRect.Width, 20);
        }
    }

    private GraphicsPath CreateContentClipPath(Rectangle contentRect)
    {
        var path = new GraphicsPath();
        float r = _cornerRadius;

        // 上方圆角
        if (r > 0)
        {
            path.AddArc(contentRect.X, contentRect.Y, r * 2, r * 2, 180, 90);
            path.AddArc(contentRect.Right - r * 2, contentRect.Y, r * 2, r * 2, 270, 90);
        }
        else
        {
            path.AddLine(contentRect.X, contentRect.Y, contentRect.Right, contentRect.Y);
        }

        // 下方圆角
        if (r > 0)
        {
            path.AddArc(contentRect.Right - r * 2, contentRect.Bottom - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(contentRect.X, contentRect.Bottom - r * 2, r * 2, r * 2, 90, 90);
        }
        else
        {
            path.AddLine(contentRect.Right, contentRect.Bottom, contentRect.X, contentRect.Bottom);
        }

        path.CloseFigure();
        return path;
    }

    #endregion

    #region 鼠标事件

    private void OnMouseDown(object? sender, MouseEventArgs e)
    {
        // 编辑标题时点击任意位置先保存
        if (_titleEditForm != null)
        {
            EndInlineEditTitle(true);
            return;
        }

        if (e.Button == MouseButtons.Left)
        {
            // 检查是否点击了调整大小区域
            if (IsInResizeBorder(e.Location) && !Locked)
            {
                _isResizing = true;
                _resizeRect = ClientRectangle;
                return;
            }

            // 检查是否点击了标题栏（用于拖动，WndProc 中已处理 HTCAPTION）
            if (IsInTitleBar(e.Location))
            {
                return;
            }

            // 检查是否点击了条目
            var entry = GetEntryAtPoint(e.Location);
            if (entry != null)
            {
                if (_selectedEntry != entry)
                {
                    _selectedEntry = entry;
                    Invalidate();
                }

                // 记录拖动起点
                _dragStart = e.Location;
                _isDragging = false;

                // 双击打开文件
                if (_lastClickTime != null && (DateTime.Now - _lastClickTime.Value).TotalMilliseconds < 400
                    && _lastClickEntry == entry)
                {
                    App.Log($"[FenceWindow] DoubleClick detected on '{entry.FilePath}', type={entry.EntryType}");
                    OpenEntry(entry);
                    _lastClickTime = null;
                    _lastClickEntry = null;
                }
                else
                {
                    _lastClickTime = DateTime.Now;
                    _lastClickEntry = entry;
                }
            }
            else
            {
                _selectedEntry = null;
                Invalidate();

                // 标题栏隐藏时，点击空白区域开始自定义拖动窗口
                if (_titleHeight <= 0 && !Locked)
                {
                    _isCustomDragging = true;
                    _customDragOffset = e.Location;
                    _lastBlankClickTime = DateTime.Now;
                }
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            HandleRightClick(e.Location);
        }
    }

    /// <summary>处理右键点击：显示自定义菜单（含 Shell 操作）。</summary>
    private void HandleRightClick(Point clientPos)
    {
        _contextMenu?.Close();

        var entry = GetEntryAtPoint(clientPos);
        _selectedEntry = entry;
        UpdateContextMenuState(entry);

        // 标题栏判定：Y < titleHeight，或者点击位置没有命中任何条目且在围栏上半部分
        bool inTitleArea = IsInTitleBar(clientPos) || (entry == null && clientPos.Y < _titleHeight * 3);
        string area = inTitleArea ? "TitleBar" : (entry != null ? "Entry" : "Blank");
        App.Log($"[FenceWindow] RightClick area={area}, pos=({clientPos.X},{clientPos.Y}), titleHeight={_titleHeight}");

        if (inTitleArea)
            ShowTitleBarContextMenu(clientPos);   // 第一段：标题栏 → 围栏控制
        else if (entry != null)
            ShowEntryContextMenu(clientPos);      // 第二段：快捷方式 → 控制条目
        else
            ShowFenceContextMenu(clientPos);      // 第三段：空白区域 → 围栏显示效果
    }

    /// <summary>第一段：右键标题栏 — 围栏控制。</summary>
    private void ShowTitleBarContextMenu(Point clientPos)
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;
        var actions = new Dictionary<int, Action>();
        try
        {
            int id = 1;

            AppendMenuW(hMenu, MF_STRING | (_locked ? MF_CHECKED : MF_ENABLED), (uint)id, "锁定栅栏");
            actions[id++] = () => ToggleLock();

            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, (uint)id, "最小化");
            actions[id++] = () => { Visible = false; };

            AppendMenuW(hMenu, MF_SEPARATOR, 0, "");

            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, (uint)id, "重命名栅栏");
            actions[id++] = () => OnRenameClicked(this, EventArgs.Empty);

            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, (uint)id, "新建栅栏");
            actions[id++] = () => OnNewFenceClicked(this, EventArgs.Empty);

            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, (uint)id, "删除栅栏");
            actions[id++] = () => OnDeleteFenceClicked(this, EventArgs.Empty);

            AppendMenuW(hMenu, MF_SEPARATOR, 0, "");

            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, (uint)id, "手动检测失效条目");
            actions[id++] = () => OnCheckBrokenLinksClicked(this, EventArgs.Empty);

            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, (uint)id, "自动排布所有围栏");
            actions[id++] = () => OnAutoArrangeClicked(this, EventArgs.Empty);

            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, (uint)id, "自动整理围栏快捷方式");
            actions[id++] = () => OnOrganizeFenceEntriesClicked(this, EventArgs.Empty);

            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, (uint)id, "调整标题高度");
            actions[id++] = () => OnAdjustTitleHeightClicked(this, EventArgs.Empty);

            ShowTrackMenu(hMenu, actions);
        }
        finally { DestroyMenu(hMenu); }
    }

    /// <summary>第二段：右键快捷方式 — 控制条目。</summary>
    private void ShowEntryContextMenu(Point clientPos)
    {
        var entry = _selectedEntry;
        if (entry == null) return;

        // 显示 Windows 系统右键菜单（与桌面一致）
        var screenPos = PointToScreen(clientPos);
        App.Log($"[FenceWindow] ShowEntryContextMenu entry='{entry.DisplayName}', filePath='{entry.FilePath}', fileExists={System.IO.File.Exists(entry.FilePath)}, dirExists={System.IO.Directory.Exists(entry.FilePath)}");
        if (!string.IsNullOrEmpty(entry.FilePath) &&
            (System.IO.File.Exists(entry.FilePath) || System.IO.Directory.Exists(entry.FilePath)))
        {
            try
            {
                var result = Win32.ShellContextMenu.ShowContextMenu(entry.FilePath, Handle, screenPos);
                App.Log($"[FenceWindow] ShellContextMenu.ShowContextMenu returned {result}");
                if (result == Win32.ShellContextMenu.ContextMenuResult.Executed)
                {
                    // 执行了命令，刷新围栏显示（文件可能被删除/重命名）
                    ForceRepaint();
                    FenceChanged?.Invoke(this);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[FenceWindow] System context menu failed: {ex.Message}");
            }
        }
    }

    /// <summary>第三段：右键空白区域 — 围栏显示效果（外观设置）。</summary>
    private void ShowFenceContextMenu(Point clientPos)
    {
        IntPtr hMenu = CreatePopupMenu();
        if (hMenu == IntPtr.Zero) return;
        var actions = new Dictionary<int, Action>();
        try
        {
            // 不透明度子菜单（0% 到 100%，步进 5%）
            IntPtr hOpacity = CreatePopupMenu();
            int[] opacities = { 0, 10, 20, 30, 40, 50, 60, 70, 80, 90, 100 };
            int currentOpacityPercent = (int)Math.Round(_opacity * 100);
            int subId = 100;
            foreach (var op in opacities)
            {
                uint flags = MF_STRING | MF_ENABLED;
                // 精确匹配当前不透明度（容差 2%）
                if (Math.Abs(currentOpacityPercent - op) <= 2) flags |= MF_CHECKED;
                AppendMenuW(hOpacity, flags, (uint)subId, op == 0 ? "0% (完全透明)" : $"{op}%");
                int capturedOp = op;
                actions[subId] = () =>
                {
                    _opacity = capturedOp / 100.0;
                    App.Log($"[FenceWindow] Opacity changed to {_opacity} ({capturedOp}%), blur={_blurEnabled}, FenceChanged invoked");
                    ApplyEffects();
                    ForceRepaint();
                    FenceChanged?.Invoke(this);
                    App.Log($"[FenceWindow] Opacity save triggered, FenceChanged={FenceChanged != null}");
                };
                subId++;
            }
            AppendMenuW(hMenu, MF_STRING | MF_POPUP | MF_ENABLED, (uint)hOpacity, $"不透明度 (当前 {currentOpacityPercent}%)");

            // 毛玻璃效果
            AppendMenuW(hMenu, MF_STRING | (_blurEnabled ? MF_CHECKED : MF_ENABLED), 1, "毛玻璃效果");
            actions[1] = () => ToggleBlur();

            AppendMenuW(hMenu, MF_SEPARATOR, 0, "");

            // 背景颜色
            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, 2, "背景颜色...");
            actions[2] = () => OnBackgroundColorClicked(this, EventArgs.Empty);

            // 强调色
            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, 3, "强调色...");
            actions[3] = () => OnAccentColorClicked(this, EventArgs.Empty);

            AppendMenuW(hMenu, MF_SEPARATOR, 0, "");

            // 显示/隐藏标题栏
            AppendMenuW(hMenu, MF_STRING | (_titleHeight > 0 ? MF_CHECKED : MF_ENABLED), 4, "显示标题栏");
            actions[4] = () =>
            {
                _titleHeight = _titleHeight > 0 ? 0 : 28;
                RecalculateLayout();
                ForceRepaint();
                FenceChanged?.Invoke(this);
            };

            // 恢复默认颜色设置
            AppendMenuW(hMenu, MF_STRING | MF_ENABLED, 5, "恢复默认颜色设置");
            actions[5] = () =>
            {
                // 恢复默认颜色
                _backgroundColor = Color.FromArgb(30, 30, 46);
                _titleTextColor = Color.White;
                _accentColor = Color.FromArgb(100, 149, 237); // Cornflower Blue
                _accentBrush.Color = _accentColor;
                _opacity = 0.75;
                _blurEnabled = false;
                _titleHeight = 28;
                RecalculateLayout();
                ApplyEffects();
                ForceRepaint();
                FenceChanged?.Invoke(this);
            };

            ShowTrackMenu(hMenu, actions);
        }
        finally { DestroyMenu(hMenu); }
    }

    /// <summary>通用的 TrackPopupMenuEx 调用。</summary>
    private void ShowTrackMenu(IntPtr hMenu, Dictionary<int, Action> actions)
    {
        if (actions.Count == 0) return;

        GetCursorPos(out var pt);
        var screenPos = new Point(pt.X, pt.Y);

        using var owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Size = new Size(1, 1),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(screenPos.X, screenPos.Y)
        };
        owner.Show();
        SetForegroundWindow(owner.Handle);
        // 确保 owner 窗口可以接收菜单命令
        owner.BringToFront();

        int cmd = TrackPopupMenuEx(hMenu,
            TPM_LEFTALIGN | TPM_TOPALIGN | TPM_LEFTBUTTON | TPM_RETURNCMD,
            screenPos.X, screenPos.Y, owner.Handle, IntPtr.Zero);

        App.Log($"[FenceWindow] TrackPopupMenu cmd={cmd}, actions count={actions.Count}, hasKey={actions.ContainsKey(cmd)}");

        if (cmd > 0 && actions.TryGetValue(cmd, out var action))
        {
            App.Log($"[FenceWindow] Executing action for cmd={cmd}");
            try { action(); } catch (Exception ex) { App.Log($"[FenceWindow] Action failed: {ex.Message}"); }
        }
    }

    /// <summary>
    /// 打开围栏条目（双击或菜单"打开"共用）。
    /// 对失效快捷方式（目标不存在）给出用户友好提示，并提供清理选项。
    /// </summary>
    private void OpenEntry(FenceEntry entry)
    {
        // 先检查文件是否存在
        if (!System.IO.File.Exists(entry.FilePath) && !System.IO.Directory.Exists(entry.FilePath))
        {
            App.Log($"[FenceWindow] OpenEntry failed: file not found '{entry.FilePath}'");
            var result = System.Windows.Forms.MessageBox.Show(
                $"文件或快捷方式不存在：\n{entry.DisplayName}\n\n路径：{entry.FilePath}\n\n是否从围栏中移除该失效条目？",
                "无法打开", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.Yes)
            {
                _entries.Remove(entry);
                _thumbnailProvider.Invalidate(entry.FilePath);
                _selectedEntry = null;
                RecalculateLayout();
                Invalidate();
                EntriesChanged?.Invoke();
                FenceChanged?.Invoke(this);
            }
            return;
        }

        // 对 .lnk 快捷方式，检查目标是否存在
        if (entry.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var targetPath = entry.TargetPath;
            if (string.IsNullOrEmpty(targetPath))
                targetPath = NoFences.Model.FenceEntry.ResolveShortcut(entry.FilePath);
            if (!string.IsNullOrEmpty(targetPath)
                && !System.IO.File.Exists(targetPath)
                && !System.IO.Directory.Exists(targetPath))
            {
                App.Log($"[FenceWindow] OpenEntry failed: shortcut target not found '{entry.FilePath}' -> '{targetPath}'");
                var result = System.Windows.Forms.MessageBox.Show(
                    $"快捷方式的目标已失效：\n{entry.DisplayName}\n\n目标：{targetPath}\n\n是否从围栏中移除该失效条目？",
                    "快捷方式失效", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (result == DialogResult.Yes)
                {
                    _entries.Remove(entry);
                    _thumbnailProvider.Invalidate(entry.FilePath);
                    _selectedEntry = null;
                    RecalculateLayout();
                    Invalidate();
                    EntriesChanged?.Invoke();
                    FenceChanged?.Invoke(this);
                }
                return;
            }
        }

        try
        {
            if (entry.EntryType == NoFences.Model.EntryType.Folder)
            {
                System.Diagnostics.Process.Start("explorer.exe", $"\"{entry.FilePath}\"");
            }
            else if (entry.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // .lnk 快捷方式：解析目标路径后直接启动目标 exe
                // 比 UseShellExecute 打开 .lnk 更可靠，避免某些安全软件/沙箱静默拦截
                var target = entry.TargetPath;
                if (string.IsNullOrEmpty(target))
                    target = NoFences.Model.FenceEntry.ResolveShortcut(entry.FilePath);

                if (!string.IsNullOrEmpty(target) && (System.IO.File.Exists(target) || System.IO.Directory.Exists(target)))
                {
                    // 读取快捷方式的工作目录和参数
                    string? workingDir = null;
                    string? arguments = null;
                    try
                    {
                        var shellType = Type.GetTypeFromProgID("WScript.Shell");
                        if (shellType != null)
                        {
                            dynamic shell = Activator.CreateInstance(shellType)!;
                            dynamic sc = shell.CreateShortcut(entry.FilePath);
                            workingDir = sc.WorkingDirectory as string;
                            arguments = sc.Arguments as string;
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(sc);
                            System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
                        }
                    }
                    catch { /* 读取工作目录失败不影响启动 */ }

                    var psi = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = target,
                        UseShellExecute = true
                    };
                    if (!string.IsNullOrEmpty(workingDir) && System.IO.Directory.Exists(workingDir))
                        psi.WorkingDirectory = workingDir;
                    if (!string.IsNullOrEmpty(arguments))
                        psi.Arguments = arguments;

                    System.Diagnostics.Process.Start(psi);
                    App.Log($"[FenceWindow] OpenEntry OK: lnk='{entry.FilePath}' -> target='{target}'");
                }
                else
                {
                    // 目标解析失败，回退到直接用 Shell 打开 .lnk
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = entry.FilePath,
                        UseShellExecute = true
                    });
                    App.Log($"[FenceWindow] OpenEntry OK (fallback shell): '{entry.FilePath}'");
                }
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = entry.FilePath,
                    UseShellExecute = true
                });
                App.Log($"[FenceWindow] OpenEntry OK for '{entry.FilePath}'");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[FenceWindow] OpenEntry failed for '{entry.FilePath}': {ex.Message}");
            System.Windows.Forms.MessageBox.Show(
                $"无法打开：{entry.DisplayName}\n\n错误：{ex.Message}",
                "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // 直接执行的菜单动作（不依赖 ContextMenuStrip 事件）
    private void OnEntryOpenClickedExecute(FenceEntry entry)
    {
        OpenEntry(entry);
    }

    private void OnEntryOpenTargetClickedExecute(FenceEntry entry)
    {
        if (string.IsNullOrEmpty(entry.TargetPath))
        {
            // 尝试重新解析目标路径
            entry.TargetPath = NoFences.Model.FenceEntry.ResolveShortcut(entry.FilePath);
        }
        if (string.IsNullOrEmpty(entry.TargetPath))
        {
            System.Windows.Forms.MessageBox.Show(
                $"无法解析快捷方式的目标路径：\n{entry.DisplayName}",
                "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!System.IO.File.Exists(entry.TargetPath) && !System.IO.Directory.Exists(entry.TargetPath))
        {
            App.Log($"[FenceWindow] Open target failed: target not found '{entry.TargetPath}'");
            var result = System.Windows.Forms.MessageBox.Show(
                $"快捷方式的目标已失效：\n{entry.DisplayName}\n\n目标：{entry.TargetPath}\n\n是否从围栏中移除该失效条目？",
                "快捷方式失效", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (result == DialogResult.Yes)
            {
                _entries.Remove(entry);
                _thumbnailProvider.Invalidate(entry.FilePath);
                _selectedEntry = null;
                RecalculateLayout();
                Invalidate();
                EntriesChanged?.Invoke();
                FenceChanged?.Invoke(this);
            }
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(entry.TargetPath) { UseShellExecute = true });
            App.Log($"[FenceWindow] Open target OK for '{entry.TargetPath}'");
        }
        catch (Exception ex)
        {
            App.Log($"[FenceWindow] Open target failed for '{entry.TargetPath}': {ex.Message}");
            System.Windows.Forms.MessageBox.Show(
                $"无法打开目标：{entry.DisplayName}\n\n错误：{ex.Message}",
                "打开失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OnOpenFileLocationClickedExecute(FenceEntry entry)
    {
        OnOpenFileLocationClicked(this, EventArgs.Empty);
    }

    private void OnPropertiesClickedExecute(FenceEntry entry)
    {
        if (File.Exists(entry.FilePath) || Directory.Exists(entry.FilePath))
            Win32.ShellHelper.ShowProperties(entry.FilePath);
    }

    private void OnRunAsAdminClickedExecute(FenceEntry entry)
    {
        if (File.Exists(entry.FilePath))
            Win32.ShellHelper.RunAsAdmin(entry.FilePath);
    }

    private void ToggleBlur()
    {
        _blurEnabled = !_blurEnabled;
        App.Log($"[FenceWindow] ToggleBlur: blur={_blurEnabled}, opacity={_opacity}, calling ApplyEffects");
        ApplyEffects();
        ForceRepaint();
        FenceChanged?.Invoke(this);
    }

    /// <summary>强制重绘窗口。</summary>
    private void ForceRepaint()
    {
        Invalidate(true);
        Update();
        Refresh();
    }

    private void ToggleLock()
    {
        Locked = !Locked;
        FenceChanged?.Invoke(this);
    }

    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_STRING = 0x0000;
    private const uint MF_ENABLED = 0x0000;
    private const uint MF_GRAYED = 0x0001;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_POPUP = 0x0010;
    private const uint TPM_LEFTBUTTON = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_TOPALIGN = 0x0000;

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, [MarshalAs(UnmanagedType.LPWStr)] string lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hmenu, uint fuFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // Per-pixel alpha 分层窗口 API
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref Point pptDst, ref Size psize,
        IntPtr hdcSrc, ref Point pptSrc, uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;        // AC_SRC_OVER = 0
        public byte BlendFlags;     // 必须为 0
        public byte SourceConstantAlpha; // 整体 alpha，per-pixel 模式下为 255
        public byte AlphaFormat;    // AC_SRC_ALPHA = 1
    }

    private const int ULW_ALPHA = 0x00000002;

    private void OnMouseUp(object? sender, MouseEventArgs e)
    {
        _isDragging = false;
        _isResizing = false;
        _isCustomDragging = false;
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        // 处理标题栏隐藏时的自定义拖动窗口
        if (_isCustomDragging && e.Button == MouseButtons.Left)
        {
            var dx = e.X - _customDragOffset.X;
            var dy = e.Y - _customDragOffset.Y;
            if (dx != 0 || dy != 0)
            {
                Location = new Point(Location.X + dx, Location.Y + dy);
            }
            return;
        }

        // 处理调整大小
        if (_isResizing && !Locked)
        {
            int newWidth = Math.Max(150, e.X);
            int newHeight = Math.Max(_titleHeight + 50, e.Y);
            Size = new Size(newWidth, newHeight);
            return;
        }

        // 条目拖动开始（按住左键在条目上移动超过 5px）
        // 注意：双击检测窗口期（400ms）内不启动拖动，否则 DoDragDrop 会阻塞消息泵，
        // 导致第二次点击的 MouseDown 无法收到，双击永远无法触发。
        var inDoubleClickWindow = _lastClickTime != null
            && (DateTime.Now - _lastClickTime.Value).TotalMilliseconds < 400;
        if (e.Button == MouseButtons.Left && _selectedEntry != null && !_locked
            && !inDoubleClickWindow)
        {
            // 超出双击窗口期，清除状态，恢复正常拖动
            if (_lastClickTime != null) _lastClickTime = null;
            var dx = e.X - _dragStart.X;
            var dy = e.Y - _dragStart.Y;
            if (!_isDragging && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5))
            {
                _isDragging = true;
                var data = new DataObject(DataFormats.FileDrop, new[] { _selectedEntry.FilePath });
                data.SetData("FenceEntry", true, _selectedEntry.FilePath);
                data.SetData("FenceEntrySource", true, _fenceId); // 记录源围栏 ID

                // 拖出前先从源围栏移除，如果拖放取消则恢复
                var dragEntry = _selectedEntry;
                _entries.Remove(dragEntry);
                _thumbnailProvider.Invalidate(dragEntry.FilePath);
                RecalculateLayout();
                Invalidate();

                var result = DoDragDrop(data, DragDropEffects.Move);

                if (result != DragDropEffects.Move)
                {
                    // 拖放被取消或目标不接受，恢复条目
                    _entries.Add(dragEntry);
                    RecalculateLayout();
                }
                else
                {
                    EntriesChanged?.Invoke();
                }

                _selectedEntry = null;
                Invalidate();
                return;
            }
        }

        // 更新悬停状态
        var entry = GetEntryAtPoint(e.Location);
        if (entry != _hoveredEntry)
        {
            _hoveredEntry = entry;
            Invalidate();
        }

        // 更新光标
        if (IsInResizeBorder(e.Location) && !Locked)
        {
            Cursor = Cursors.SizeNWSE;
        }
        else if (IsInTitleBar(e.Location) && !Locked)
        {
            Cursor = Cursors.SizeAll;
        }
        else if (_titleHeight <= 0 && entry == null && !Locked)
        {
            // 标题栏隐藏时，空白区域显示移动光标
            Cursor = Cursors.SizeAll;
        }
        else
        {
            Cursor = Cursors.Arrow;
        }
    }

    private void OnMouseWheel(object? sender, MouseEventArgs e)
    {
        if (_maxScrollOffset <= 0) return;

        int delta = e.Delta > 0 ? -SCROLL_STEP : SCROLL_STEP;
        _scrollOffset = Math.Clamp(_scrollOffset + delta, 0, _maxScrollOffset);
        Invalidate();
    }

    #endregion

    #region 拖放事件

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (Locked)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
        {
            // 始终报告 Copy，防止资源管理器拒绝拖放
            // OnDragDrop 中会根据来源决定实际行为
            e.Effect = DragDropEffects.Copy;
            Invalidate();
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (Locked || e.Data?.GetDataPresent(DataFormats.FileDrop) != true)
        {
            e.Effect = DragDropEffects.None;
            return;
        }

        e.Effect = DragDropEffects.Move;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (Locked) return;

        if (e.Data?.GetDataPresent(DataFormats.FileDrop) is true)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            bool fromFence = e.Data.GetDataPresent("FenceEntrySource");

            // 围栏间移动：不重复添加
            var newFiles = files.Where(f => !_entries.Any(en => string.Equals(en.FilePath, f, StringComparison.OrdinalIgnoreCase))).ToArray();

            if (newFiles.Length > 0)
            {
                if (!fromFence)
                {
                    // 从桌面/文件夹拖入：复制文件到 FenceStorage 并删除原文件（移动方式）
                    var storageDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DeskOrganizer", "FenceStorage");
                    Directory.CreateDirectory(storageDir);

                    var storagePaths = new List<string>();
                    foreach (var srcPath in newFiles)
                    {
                        try
                        {
                            if (!File.Exists(srcPath) && !Directory.Exists(srcPath)) continue;

                            var fileName = Path.GetFileName(srcPath);
                            var destPath = Path.Combine(storageDir, fileName);

                            // 避免文件名冲突
                            if (File.Exists(destPath) && !string.Equals(srcPath, destPath, StringComparison.OrdinalIgnoreCase))
                            {
                                var name = Path.GetFileNameWithoutExtension(srcPath);
                                var ext = Path.GetExtension(srcPath);
                                int i = 1;
                                while (File.Exists(destPath))
                                {
                                    destPath = Path.Combine(storageDir, $"{name}_{i}{ext}");
                                    i++;
                                }
                            }

                            File.Copy(srcPath, destPath, true);
                            storagePaths.Add(destPath);

                            // 删除原文件（实现移动效果）
                            try { File.Delete(srcPath); } catch (Exception ex) { App.Log($"[FenceWindow] Delete src after move failed for {srcPath}: {ex.Message}"); }
                        }
                        catch (Exception ex)
                        {
                            App.Log($"[FenceWindow] Move to storage failed for {srcPath}: {ex.Message}");
                            // 复制失败时回退到直接引用
                            storagePaths.Add(srcPath);
                        }
                    }
                    AddEntries(storagePaths.ToArray());
                }
                else
                {
                    AddEntries(newFiles);
                }
            }
        }

        Invalidate();
    }

    #endregion

    #region 上下文菜单

    private void UpdateContextMenuState(FenceEntry? entry)
    {
        if (_contextMenu?.Tag is not Dictionary<string, ToolStripMenuItem> items) return;

        items["Lock"].Checked = _locked;
        bool hasEntry = entry != null;
        items["DeleteEntry"].Enabled = hasEntry;
        items["ShellMenu"].Enabled = hasEntry && File.Exists(entry!.FilePath);

        if (_miEntryOpen != null) _miEntryOpen.Enabled = hasEntry;
        if (_miEntryOpenTarget != null) _miEntryOpenTarget.Enabled = hasEntry && entry!.EntryType == Model.EntryType.Shortcut && !string.IsNullOrEmpty(entry.TargetPath);
        if (_miEntryRename != null) _miEntryRename.Enabled = hasEntry;
        if (_miProperties != null) _miProperties.Enabled = hasEntry;
        if (_miRunAsAdmin != null) _miRunAsAdmin.Enabled = hasEntry && File.Exists(entry!.FilePath);
    }

    private void OnLockClicked(object? sender, EventArgs e)
    {
        if (_contextMenu?.Tag is not Dictionary<string, ToolStripMenuItem> items) return;
        _locked = items["Lock"].Checked;
        Invalidate();
    }

    private void OnMinimizeClicked(object? sender, EventArgs e)
    {
        Visible = false;
    }

    private void StartInlineEditTitle()
    {
        if (_titleEditForm != null) return;

        var boxWidth = Math.Max(140, Width / 2);
        var boxHeight = _titleHeight - 6;

        _titleEditBox = new TextBox
        {
            Text = _fenceName,
            Font = _titleFont,
            BorderStyle = BorderStyle.None,
            TextAlign = HorizontalAlignment.Center,
            Width = boxWidth,
            Height = boxHeight,
            BackColor = Color.White,
            ForeColor = Color.Black,
            Location = new Point(2, 2)
        };

        _titleEditBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { EndInlineEditTitle(true); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { EndInlineEditTitle(false); e.Handled = true; }
        };

        _titleEditForm = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Width = boxWidth + 4,
            Height = boxHeight + 4,
            ShowInTaskbar = false,
            ControlBox = false,
            BackColor = Color.White,
            TopMost = true
        };

        _titleEditForm.Controls.Add(_titleEditBox);
        _titleEditForm.Deactivate += (_, _) => EndInlineEditTitle(true);

        // 定位到屏幕坐标（标题栏居中）
        var screenX = Left + (Width - _titleEditForm.Width) / 2;
        var screenY = Top + (_titleHeight - _titleEditForm.Height) / 2;
        _titleEditForm.Location = new Point(screenX, screenY);

        _titleEditForm.Show(this);
        _titleEditBox.Focus();
        _titleEditBox.SelectAll();
    }

    private void EndInlineEditTitle(bool save)
    {
        if (_titleEditForm == null) return;

        var newText = _titleEditBox?.Text.Trim() ?? "";
        var form = _titleEditForm;
        _titleEditForm = null;
        _titleEditBox = null;

        try { form.Close(); form.Dispose(); } catch { }

        if (save && !string.IsNullOrWhiteSpace(newText) && newText != _fenceName)
        {
            FenceName = newText;
            Invalidate();
            FenceChanged?.Invoke(this);
        }
    }

    private void OnRenameClicked(object? sender, EventArgs e)
    {
        using var dlg = new EditDialog("重命名栅栏", "栅栏名称:", _fenceName);
        using var owner = CreateTempDialogOwner();
        if (dlg.ShowDialog(owner) == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.Value))
        {
            FenceName = dlg.Value;
            FenceChanged?.Invoke(this);
        }
    }

    private void OnPropertiesClicked(object? sender, EventArgs e)
    {
        if (_selectedEntry == null || !_entries.Contains(_selectedEntry)) return;
        if (File.Exists(_selectedEntry.FilePath) || Directory.Exists(_selectedEntry.FilePath))
            Win32.ShellHelper.ShowProperties(_selectedEntry.FilePath);
    }

    private void OnRunAsAdminClicked(object? sender, EventArgs e)
    {
        if (_selectedEntry == null || !_entries.Contains(_selectedEntry)) return;
        if (File.Exists(_selectedEntry.FilePath))
            Win32.ShellHelper.RunAsAdmin(_selectedEntry.FilePath);
    }

    private void OnEntryOpenClicked(object? sender, EventArgs e)
    {
        if (_selectedEntry != null && _entries.Contains(_selectedEntry))
        {
            OpenEntry(_selectedEntry);
        }
    }

    private void OnEntryOpenTargetClicked(object? sender, EventArgs e)
    {
        if (_selectedEntry != null && _entries.Contains(_selectedEntry)
            && _selectedEntry.EntryType == Model.EntryType.Shortcut)
        {
            OnEntryOpenTargetClickedExecute(_selectedEntry);
        }
    }

    private void OnOrganizeFenceEntriesClicked(object? sender, EventArgs e)
    {
        if (_entries.Count == 0) return;

        var result = System.Windows.Forms.MessageBox.Show(
            $"将围栏 \"{_fenceName}\" 中的 {_entries.Count} 个条目按类型分类到对应围栏？",
            "整理围栏内条目", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (result != DialogResult.Yes) return;

        // 收集当前围栏所有条目路径
        var pathsToMove = _entries.Select(e => e.FilePath).ToList();
        // 从当前围栏移除所有条目
        _entries.Clear();
        Invalidate();
        EntriesChanged?.Invoke();

        // 通知 FenceManager 将这些路径分类到对应围栏
        OrganizeFenceEntriesRequested?.Invoke(_fenceId, pathsToMove);
    }

    /// <summary>
    /// 请求将围栏内的条目按分类移动到其他围栏。
    /// </summary>
    public event Action<string, List<string>>? OrganizeFenceEntriesRequested;

    private void OnDeleteEntryClicked(object? sender, EventArgs e)
    {
        if (_selectedEntry != null && _entries.Contains(_selectedEntry))
        {
            _entries.Remove(_selectedEntry);
            _thumbnailProvider.Invalidate(_selectedEntry.FilePath);
            _selectedEntry = null;
            RecalculateLayout();
            Invalidate();
            EntriesChanged?.Invoke();
            // 同步更新 FenceInfo 中的 FilePaths 并持久化
            FenceChanged?.Invoke(this);
        }
    }

    private void OnOpenFileLocationClicked(object? sender, EventArgs e)
    {
        if (_selectedEntry == null || !_entries.Contains(_selectedEntry)) return;

        try
        {
            string? targetLocation = null;

            // 对于快捷方式，先尝试已有的 TargetPath
            if (!string.IsNullOrEmpty(_selectedEntry.TargetPath))
            {
                if (File.Exists(_selectedEntry.TargetPath))
                    targetLocation = _selectedEntry.TargetPath;
                else if (Directory.Exists(_selectedEntry.TargetPath))
                    targetLocation = _selectedEntry.TargetPath;
            }

            // 如果 TargetPath 为空或文件不存在，尝试重新解析 .lnk
            if (targetLocation == null
                && _selectedEntry.FilePath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                && File.Exists(_selectedEntry.FilePath))
            {
                var resolved = ResolveShortcutSTA(_selectedEntry.FilePath);
                if (!string.IsNullOrEmpty(resolved) && (File.Exists(resolved) || Directory.Exists(resolved)))
                {
                    targetLocation = resolved;
                    _selectedEntry.TargetPath = resolved; // 缓存起来
                }
            }

            // 回退到 FilePath
            if (targetLocation == null && File.Exists(_selectedEntry.FilePath))
                targetLocation = _selectedEntry.FilePath;
            else if (targetLocation == null && Directory.Exists(_selectedEntry.FilePath))
                targetLocation = _selectedEntry.FilePath;

            if (targetLocation == null) return;

            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{targetLocation}\"");
        }
        catch { }
    }

    /// <summary>在 STA 线程上解析快捷方式目标路径。</summary>
    private static string? ResolveShortcutSTA(string lnkPath)
    {
        string? result = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { result = Model.FenceEntry.ResolveShortcut(lnkPath); }
            catch { }
        })
        { IsBackground = true };
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join(3000);
        return result;
    }

    private void OnRenameEntryClicked(object? sender, EventArgs e)
    {
        if (_selectedEntry == null || !_entries.Contains(_selectedEntry)) return;

        var entry = _selectedEntry;
        string currentName = entry.DisplayName;

        using var dlg = new EditDialog("重命名", "名称:", currentName);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        string newName = dlg.Value.Trim();
        if (newName == currentName || string.IsNullOrWhiteSpace(newName))
        {
            entry.CustomName = null;
        }
        else
        {
            entry.CustomName = newName;
        }
        RecalculateLayout();
        Invalidate();
        EntriesChanged?.Invoke();
    }

    /// <summary>获取指定条目在围栏窗口中的显示区域。</summary>
    private Rectangle GetEntryBounds(FenceEntry entry)
    {
        int index = _entries.IndexOf(entry);
        if (index < 0) return Rectangle.Empty;

        int cellWidth = _iconSize + ICON_PADDING * 2;
        int cellHeight = _iconSize + TEXT_HEIGHT + TEXT_PADDING;
        var contentRect = new Rectangle(0, _titleHeight, Width, Height - _titleHeight);
        int cols = Math.Max(1, contentRect.Width / cellWidth);
        int xOffset = Math.Max(0, (contentRect.Width - cols * cellWidth) / 2);

        int col = index % cols;
        int row = index / cols;

        int x = contentRect.X + xOffset + col * cellWidth;
        int y = contentRect.Y + row * cellHeight - _scrollOffset;
        return new Rectangle(x, y, cellWidth, cellHeight);
    }

    private void OnNewFenceClicked(object? sender, EventArgs e)
    {
        RequestNewFence?.Invoke(this);
    }

    private void OnDeleteFenceClicked(object? sender, EventArgs e)
    {
        // 使用美化的 WPF 确认对话框（跨线程安全）
        bool confirm = false;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            confirm = ConfirmDialog.Show(
                "删除围栏",
                $"确定要永久删除围栏 \"{_fenceName}\" 吗？",
                "删除后围栏内的快捷方式将释放回桌面，重启软件后此围栏不再存在。",
                "永久删除",
                isDanger: true);
        });

        if (confirm)
        {
            RequestDeleteFence?.Invoke(this);
        }
    }

    private void OnAdjustTitleHeightClicked(object? sender, EventArgs e)
    {
        using var dlg = new HeightDialog(_titleHeight);
        using var owner = CreateTempDialogOwner();
        if (dlg.ShowDialog(owner) == DialogResult.OK)
        {
            TitleHeight = dlg.Value;
            FenceChanged?.Invoke(this);
        }
    }

    /// <summary>检测失效快捷方式/文件，返回失效条目列表。</summary>
    private List<FenceEntry> DetectBrokenEntries()
    {
        var brokenEntries = new List<FenceEntry>();
        foreach (var entry in _entries)
        {
            if (entry.EntryType == Model.EntryType.Shortcut)
            {
                // 快捷方式：检查 .lnk 文件是否存在 + 目标是否存在
                if (!File.Exists(entry.FilePath))
                {
                    brokenEntries.Add(entry);
                }
                else if (!string.IsNullOrEmpty(entry.TargetPath)
                    && !File.Exists(entry.TargetPath)
                    && !Directory.Exists(entry.TargetPath))
                {
                    brokenEntries.Add(entry);
                }
            }
            else
            {
                // 普通文件/文件夹：检查是否存在
                if (!File.Exists(entry.FilePath) && !Directory.Exists(entry.FilePath))
                {
                    brokenEntries.Add(entry);
                }
            }
        }
        return brokenEntries;
    }

    /// <summary>自动清理失效条目（静默模式，不弹窗，仅记录日志）。</summary>
    /// <param name="silent">true=静默清理不弹窗；false=弹窗确认。</param>
    /// <returns>清理的条目数量。</returns>
    private int CleanBrokenEntries(bool silent)
    {
        var brokenEntries = DetectBrokenEntries();

        if (brokenEntries.Count == 0)
        {
            if (!silent)
            {
                MessageBox.Show(
                    "所有条目均有效，未发现失效的快捷方式或文件。",
                    "检测结果",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            return 0;
        }

        if (!silent)
        {
            var result = MessageBox.Show(
                $"发现 {brokenEntries.Count} 个失效条目：\n\n{string.Join("\n", brokenEntries.Select(e => $"  - {e.DisplayName}"))}\n\n是否自动清理这些失效条目？",
                "检测到失效条目",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (result != DialogResult.Yes) return 0;
        }

        foreach (var entry in brokenEntries)
        {
            entry.Thumbnail?.Dispose();
            _entries.Remove(entry);
            _thumbnailProvider.Invalidate(entry.FilePath);
        }
        _selectedEntry = null;
        RecalculateLayout();
        Invalidate();
        EntriesChanged?.Invoke();
        FenceChanged?.Invoke(this);

        App.Log($"[FenceWindow] Cleaned {brokenEntries.Count} broken entries from fence '{_fenceName}' (silent={silent})");

        if (!silent)
        {
            MessageBox.Show($"已清理 {brokenEntries.Count} 个失效条目。", "清理完成",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        return brokenEntries.Count;
    }

    private void OnCheckBrokenLinksClicked(object? sender, EventArgs e)
    {
        CleanBrokenEntries(silent: false);
    }

    private void OnAutoArrangeClicked(object? sender, EventArgs e)
    {
        DeskOrganizer.Model.FenceManager.Instance.AutoArrangeFences();
    }

    private void OnBackgroundColorClicked(object? sender, EventArgs e)
    {
        using var dlg = new ColorDialog
        {
            Color = _backgroundColor,
            FullOpen = true
        };
        // 围栏窗口是 NOACTIVATE，不能作为对话框 owner，用临时窗口
        using var owner = CreateTempDialogOwner();
        if (dlg.ShowDialog(owner) == DialogResult.OK)
        {
            BackgroundColor = dlg.Color;
            FenceChanged?.Invoke(this);
        }
    }

    private void OnOpacityClicked(object? sender, EventArgs e)
    {
        using var dlg = new OpacityDialog(_opacity);
        using var owner = CreateTempDialogOwner();
        if (dlg.ShowDialog(owner) == DialogResult.OK)
        {
            OpacityValue = dlg.Value;
            FenceChanged?.Invoke(this);
        }
    }

    private void OnBlurClicked(object? sender, EventArgs e)
    {
        if (sender is ToolStripMenuItem mi)
        {
            _blurEnabled = mi.Checked;
            ApplyEffects();
            FenceChanged?.Invoke(this);
        }
    }

    private void OnAccentColorClicked(object? sender, EventArgs e)
    {
        using var dlg = new ColorDialog
        {
            Color = _accentColor,
            FullOpen = true
        };
        using var owner = CreateTempDialogOwner();
        if (dlg.ShowDialog(owner) == DialogResult.OK)
        {
            AccentColor = dlg.Color;
            FenceChanged?.Invoke(this);
        }
    }

    /// <summary>创建临时 Form 作为对话框 owner（围栏窗口 NOACTIVATE 无法作为 owner）。</summary>
    private Form CreateTempDialogOwner()
    {
        var owner = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Size = new Size(0, 0),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-10000, -10000),
            Opacity = 0
        };
        owner.Show();
        owner.Hide();
        return owner;
    }

    #endregion

    #region 布局计算

    private void RecalculateLayout()
    {
        // 根据内容高度计算最大滚动偏移
        int cellWidth = _iconSize + ICON_PADDING * 2;
        int cellHeight = _iconSize + TEXT_HEIGHT + TEXT_PADDING;
        int contentHeight = ClientRectangle.Height - _titleHeight;
        int cols = Math.Max(1, ClientRectangle.Width / cellWidth);
        int rows = (int)Math.Ceiling((double)_entries.Count / cols);
        int totalContentHeight = rows * cellHeight;
        _maxScrollOffset = Math.Max(0, totalContentHeight - contentHeight);
        // 如果当前滚动偏移超出新范围，将其钳制
        _scrollOffset = Math.Min(_scrollOffset, _maxScrollOffset);
    }

    private FenceEntry? GetEntryAtPoint(Point p)
    {
        if (p.Y < _titleHeight) return null;

        int cellWidth = _iconSize + ICON_PADDING * 2;
        int cellHeight = _iconSize + TEXT_HEIGHT + TEXT_PADDING;
        int cols = Math.Max(1, ClientRectangle.Width / cellWidth);
        int xOffset = Math.Max(0, (ClientRectangle.Width - cols * cellWidth) / 2);

        for (int i = 0; i < _entries.Count; i++)
        {
            int col = i % cols;
            int row = i / cols;

            int x = xOffset + col * cellWidth;
            int y = _titleHeight + row * cellHeight - _scrollOffset;

            var cellRect = new Rectangle(x, y, cellWidth, cellHeight);
            if (cellRect.Contains(p))
                return _entries[i];
        }

        return null;
    }

    private bool IsInTitleBar(Point p)
    {
        return p.Y >= 0 && p.Y < _titleHeight;
    }

    private bool IsInResizeBorder(Point p)
    {
        return p.X >= ClientRectangle.Width - RESIZE_BORDER &&
               p.Y >= ClientRectangle.Height - RESIZE_BORDER;
    }

    #endregion

    #region 节流回调

    private void OnResizeCore()
    {
        if (!_isLoaded || SuppressFenceChanged || _isMovingOrResizing || _suppressEventCount > 0) return;
        FenceChanged?.Invoke(this);
    }

    /// <summary>实时吸附到其他围栏边缘（在 WM_WINDOWPOSCHANGING 中调用）。使用窗口实时位置而非配置文件坐标。</summary>
    private void SnapToOtherFences(DeskOrganizerModel.AppConfig config, ref int x, ref int y, int width, int height)
    {
        const int SNAP_DISTANCE = 6;
        int bestDx = 0, bestDy = 0;
        int bestDxDist = SNAP_DISTANCE + 1;
        int bestDyDist = SNAP_DISTANCE + 1;

        // 用 FenceManager 中的实时窗口位置做吸附（而不是配置文件中的旧坐标）
        var fenceManager = DeskOrganizer.Model.FenceManager.Instance;
        foreach (var other in config.Boxes)
        {
            if (other.Id == _fenceId) continue;

            // 优先用窗口实时位置
            var liveWindow = fenceManager.GetFenceWindow(other.Id);
            int ox, oy, ow, oh;
            if (liveWindow != null && liveWindow.IsHandleCreated && !liveWindow.IsDisposed)
            {
                ow = liveWindow.Width;
                oh = liveWindow.Height;
                ox = liveWindow.Location.X;
                oy = liveWindow.Location.Y;
            }
            else
            {
                ow = (int)other.Width;
                oh = (int)other.Height;
                ox = (int)other.X;
                oy = (int)other.Y;
            }

            // 水平吸附
            int dx = CheckSnap(x, width, ox, ow, SNAP_DISTANCE);
            if (dx != 0 && Math.Abs(dx) < bestDxDist) { bestDx = dx; bestDxDist = Math.Abs(dx); }

            // 垂直吸附
            int dy = CheckSnap(y, height, oy, oh, SNAP_DISTANCE);
            if (dy != 0 && Math.Abs(dy) < bestDyDist) { bestDy = dy; bestDyDist = Math.Abs(dy); }
        }

        // 吸附到屏幕边缘
        int screenW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
        int screenH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;

        // 左边
        if (Math.Abs(x) <= SNAP_DISTANCE) { bestDx = -x; bestDxDist = Math.Abs(bestDx); }
        // 右边
        if (Math.Abs(x + width - screenW) <= SNAP_DISTANCE) { bestDx = screenW - width - x; bestDxDist = Math.Abs(bestDx); }
        // 上边
        if (Math.Abs(y) <= SNAP_DISTANCE) { bestDy = -y; bestDyDist = Math.Abs(bestDy); }
        // 下边
        if (Math.Abs(y + height - screenH) <= SNAP_DISTANCE) { bestDy = screenH - height - y; bestDyDist = Math.Abs(bestDy); }

        x += bestDx;
        y += bestDy;

        // 屏幕范围限制（不阻止重叠，只保证围栏不会跑到屏幕外）
        x = Math.Max(0, Math.Min(x, screenW - width));
        y = Math.Max(0, Math.Min(y, screenH - height));
    }

    /// <summary>检测单轴吸附。</summary>
    private static int CheckSnap(int pos, int size, int otherPos, int otherSize, int snapDist)
    {
        int dist;
        dist = pos - otherPos;
        if (Math.Abs(dist) <= snapDist) return -dist;
        dist = pos - (otherPos + otherSize);
        if (Math.Abs(dist) <= snapDist) return -dist;
        dist = (pos + size) - otherPos;
        if (Math.Abs(dist) <= snapDist) return -dist;
        dist = (pos + size) - (otherPos + otherSize);
        if (Math.Abs(dist) <= snapDist) return -dist;
        dist = (pos + size / 2) - (otherPos + otherSize / 2);
        if (Math.Abs(dist) <= snapDist) return -dist;
        return 0;
    }

    private void OnMoveCore()
    {
        if (!_isLoaded || SuppressFenceChanged || _isMovingOrResizing || _suppressEventCount > 0) return;
        FenceChanged?.Invoke(this);
    }

    #endregion

    #region 缩略图加载

    private void OnThumbnailLoaded(string filePath, Bitmap? bitmap)
    {
        System.Diagnostics.Debug.WriteLine($"OnThumbnailLoaded: {Path.GetFileName(filePath)} bitmap={bitmap != null}");
        // 在 UI 线程上刷新
        if (IsHandleCreated && !Disposing && !_isDisposed)
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    var entry = _entries.FirstOrDefault(e => e.FilePath == filePath);
                    System.Diagnostics.Debug.WriteLine($"  UI: found={entry != null} entries={_entries.Count} handle={IsHandleCreated}");
                    if (entry != null)
                    {
                        entry.Thumbnail?.Dispose();
                        entry.Thumbnail = bitmap;
                        Invalidate();
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine($"  UI: NOT FOUND in _entries! path={filePath}");
                        foreach (var e in _entries)
                            System.Diagnostics.Debug.WriteLine($"    has: {e.FilePath}");
                    }
                }));
            }
            catch
            {
                // 窗口可能已关闭
            }
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        try
        {
            await _thumbnailProvider.LoadAllAsync(_entries).ConfigureAwait(false);
        }
        catch
        {
            // 静默处理
        }
    }

    /// <summary>
    /// 主动触发所有缩略图重新加载。
    /// </summary>
    public async Task ReloadThumbnailsAsync()
    {
        _thumbnailProvider.ClearCache();
        foreach (var entry in _entries)
        {
            entry.Thumbnail = null;
            entry.ThumbnailRequested = false;
        }
        await LoadThumbnailsAsync();
    }

    #endregion

    #region 公共方法

    /// <summary>
    /// 添加文件到栅栏。
    /// </summary>
    public void AddEntries(string[] filePaths)
    {
        foreach (var path in filePaths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            // 检测是否为 .lnk 快捷方式
            bool isShortcut = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
            string resolvedPath = path;
            string? targetPath = null;

            if (isShortcut)
            {
                targetPath = FenceEntry.ResolveShortcut(path);
                if (!string.IsNullOrEmpty(targetPath))
                    resolvedPath = targetPath;
            }

            var entryType = Directory.Exists(resolvedPath) ? Model.EntryType.Folder :
                            isShortcut ? Model.EntryType.Shortcut :
                            Model.EntryType.File;

            // 避免重复添加
            if (_entries.Any(e => string.Equals(e.FilePath, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            _entries.Add(new FenceEntry
            {
                FilePath = path,
                EntryType = entryType,
                TargetPath = targetPath,
                ThumbnailRequested = false
            });
        }

        RecalculateLayout();
        Invalidate();
        EntriesChanged?.Invoke();
        _ = LoadThumbnailsAsync();
    }

    /// <summary>
    /// 移除指定路径的条目。
    /// </summary>
    public void RemoveEntry(string filePath)
    {
        var entry = _entries.FirstOrDefault(e => string.Equals(e.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
        if (entry != null)
        {
            _entries.Remove(entry);
            _thumbnailProvider.Invalidate(filePath);
            RecalculateLayout();
            Invalidate();
            EntriesChanged?.Invoke();
        }
    }

    /// <summary>
    /// 获取序列化用的 FenceInfo 数据。
    /// </summary>
    public DeskOrganizerModel.FenceInfo GetFenceInfo()
    {
        return new DeskOrganizerModel.FenceInfo
        {
            Name = _fenceName,
            X = Location.X,
            Y = Location.Y,
            PosX = Location.X,
            PosY = Location.Y,
            Width = Width,
            Height = Height,
            BackgroundColor = $"#{_backgroundColor.R:X2}{_backgroundColor.G:X2}{_backgroundColor.B:X2}",
            Opacity = _opacity,
            BlurEnabled = _blurEnabled,
            CornerRadius = _cornerRadius,
            TitleHeight = _titleHeight,
            Locked = _locked,
            FilePaths = _entries.Select(e => e.FilePath).ToList(),
            EntryCustomNames = _entries
                .Where(e => !string.IsNullOrWhiteSpace(e.CustomName))
                .ToDictionary(e => e.FilePath, e => e.CustomName!)
        };
    }

    /// <summary>
    /// 从 FenceInfo 数据恢复状态。
    /// </summary>
    public void LoadFromFenceInfo(DeskOrganizerModel.FenceInfo info)
    {
        LoadFromModelFenceInfo(info);
    }

    /// <summary>
    /// 从 DeskOrganizerModel.FenceInfo 数据恢复状态。
    /// </summary>
    public void LoadFromModelFenceInfo(DeskOrganizerModel.FenceInfo info)
    {
        _fenceName = info.Name ?? "Untitled Fence";
        _fenceId = info.Id ?? string.Empty;

        int posX = info.PosX != 0 ? info.PosX : (int)info.X;
        int posY = info.PosY != 0 ? info.PosY : (int)info.Y;
        int width = (int)info.Width;
        int height = (int)info.Height;

        // 设置位置（0,0 是合法位置，不应跳过）
        Location = new Point(posX, posY);
        if (width > 50 && height > 50)
            Size = new Size(width, height);

        if (!string.IsNullOrEmpty(info.BackgroundColor))
        {
            var hex = info.BackgroundColor.TrimStart('#');
            if (hex.Length == 8)
                _backgroundColor = Extensions.FromHex(hex);
            else if (hex.Length == 6)
                _backgroundColor = Extensions.FromHex(hex);
        }

        // 修正旧版全白/高透明背景（在深色壁纸上看不见）
        if (_backgroundColor.R > 200 && _backgroundColor.G > 200 && _backgroundColor.B > 200)
        {
            _backgroundColor = Color.FromArgb(32, 42, 58); // 不透明深蓝灰，靠 Form.Opacity 控制透明度
        }

        _opacity = info.Opacity;
        _blurEnabled = info.BlurEnabled;
        _cornerRadius = info.CornerRadius;
        _titleHeight = info.TitleHeight;
        _locked = info.Locked;
        _iconSize = info.IconSize > 0 ? info.IconSize : DEFAULT_ICON_SIZE;

        Opacity = 1.0;
        // 根据 _opacity 设置背景 alpha
        _backgroundColor = Color.FromArgb((int)(_opacity * 255), _backgroundColor.R, _backgroundColor.G, _backgroundColor.B);
        _accentBrush.Color = _accentColor;

        // 清理旧条目的 Thumbnail GDI 资源
        foreach (var e in _entries) e.Thumbnail?.Dispose();
        _entries.Clear();
        if (info.FilePaths != null)
        {
            var customNames = info.EntryCustomNames ?? new();
            foreach (var fp in info.FilePaths)
            {
                // 自动检测：跳过不存在的文件/文件夹（快捷方式的 .lnk 文件存在即加载，目标稍后检测）
                bool isShortcut = fp.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(fp);
                if (!isShortcut && !File.Exists(fp) && !Directory.Exists(fp))
                {
                    App.Log($"[FenceWindow] Auto-skip broken entry: {fp}");
                    continue;
                }

                var entry = new FenceEntry
                {
                    FilePath = fp,
                    EntryType = isShortcut ? NoFences.Model.EntryType.Shortcut :
                                Directory.Exists(fp) ? NoFences.Model.EntryType.Folder :
                                NoFences.Model.EntryType.File,
                    ThumbnailRequested = false
                };
                // 恢复自定义名称
                if (customNames.TryGetValue(fp, out var customName))
                    entry.CustomName = customName;
                _entries.Add(entry);
            }
        }

        RecalculateLayout();
        Invalidate();
        _ = LoadThumbnailsAsync();

        // 后台异步解析所有快捷方式的 TargetPath（不阻塞 UI），解析完成后自动检测失效目标
        _ = Task.Run(async () =>
        {
            foreach (var entry in _entries.Where(e => e.EntryType == NoFences.Model.EntryType.Shortcut))
            {
                try
                {
                    var target = await Task.Run(() =>
                    {
                        string? r = null;
                        var t = new System.Threading.Thread(() =>
                        {
                            try { r = Model.FenceEntry.ResolveShortcut(entry.FilePath); }
                            catch { }
                        })
                        { IsBackground = true };
                        t.SetApartmentState(System.Threading.ApartmentState.STA);
                        t.Start();
                        t.Join(3000);
                        return r;
                    }).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(target))
                        entry.TargetPath = target;
                }
                catch { }
            }

            // TargetPath 解析完成后，自动检测并清理失效的快捷方式目标
            try
            {
                var broken = new List<FenceEntry>();
                foreach (var entry in _entries.ToList())
                {
                    if (entry.EntryType == NoFences.Model.EntryType.Shortcut
                        && !string.IsNullOrEmpty(entry.TargetPath)
                        && !File.Exists(entry.TargetPath)
                        && !Directory.Exists(entry.TargetPath))
                    {
                        broken.Add(entry);
                    }
                }

                if (broken.Count > 0)
                {
                    // 回到 UI 线程清理
                    BeginInvoke((Action)(() =>
                    {
                        foreach (var entry in broken)
                        {
                            if (_entries.Contains(entry))
                            {
                                entry.Thumbnail?.Dispose();
                                _entries.Remove(entry);
                                _thumbnailProvider.Invalidate(entry.FilePath);
                            }
                        }
                        _selectedEntry = null;
                        RecalculateLayout();
                        Invalidate();
                        EntriesChanged?.Invoke();
                        FenceChanged?.Invoke(this);
                        App.Log($"[FenceWindow] Auto-cleaned {broken.Count} broken shortcut targets from fence '{_fenceName}'");
                    }));
                }
            }
            catch (Exception ex) { App.Log($"[FenceWindow] Auto-clean broken links error: {ex.Message}"); }
        });

        // 标记加载完成，之后的位置变更才允许触发 FenceChanged
        _isLoaded = true;
    }

    /// <summary>
    /// 追加围栏信息中的新条目（不清空已有条目和缩略图缓存）。
    /// 用于一键整理后只添加新条目而不影响已有图标的显示。
    /// </summary>
    public void AppendEntriesFromFenceInfo(DeskOrganizerModel.FenceInfo info)
    {
        if (info.FilePaths == null) return;

        var existingPaths = new HashSet<string>(_entries.Select(e => e.FilePath), StringComparer.OrdinalIgnoreCase);
        var customNames = info.EntryCustomNames ?? new();

        bool added = false;
        foreach (var fp in info.FilePaths)
        {
            if (existingPaths.Contains(fp)) continue;

            var entry = new FenceEntry
            {
                FilePath = fp,
                EntryType = Directory.Exists(fp) ? NoFences.Model.EntryType.Folder : NoFences.Model.EntryType.File,
                ThumbnailRequested = false
            };
            if (customNames.TryGetValue(fp, out var customName))
                entry.CustomName = customName;
            _entries.Add(entry);
            existingPaths.Add(fp);
            added = true;
        }

        if (added)
        {
            RecalculateLayout();
            Invalidate();
            _ = LoadThumbnailsAsync();
            EntriesChanged?.Invoke();
        }
    }

    #endregion

    #region 清理

    protected override void Dispose(bool disposing)
    {
        if (!_isDisposed)
        {
            _isDisposed = true;

            if (disposing)
            {
                if (_globalMouseFilter != null)
                    Application.RemoveMessageFilter(_globalMouseFilter);
                _contextMenu?.Dispose();
                _accentBrush?.Dispose();
                _titleFont?.Dispose();
                _entryNameFont?.Dispose();
                _entryNameSmallFont?.Dispose();
                _thumbnailProvider.ThumbnailLoaded -= OnThumbnailLoaded;
                _thumbnailProvider?.Dispose();
                // 清理所有条目的 Thumbnail GDI 资源
                foreach (var e in _entries) e.Thumbnail?.Dispose();
                _entries.Clear();
                UninstallMouseHook();
                _resizeThrottle?.Dispose();
                _moveThrottle?.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    #endregion

    /// <summary>
    /// 全局鼠标消息过滤器：当鼠标在围栏窗口和其右键菜单之外按下时，
    /// 自动关闭右键菜单。解决 WS_EX_NOACTIVATE 窗口上 ContextMenuStrip 不会自动关闭的问题。
    /// </summary>
    private class GlobalMouseFilter : IMessageFilter
    {
        private readonly FenceWindow _fenceWindow;

        public GlobalMouseFilter(FenceWindow fenceWindow)
        {
            _fenceWindow = fenceWindow;
        }

        public bool PreFilterMessage(ref Message m)
        {
            const int WM_LBUTTONDOWN = 0x0201;
            const int WM_RBUTTONDOWN = 0x0204;
            const int WM_MBUTTONDOWN = 0x0207;

            if (m.Msg == WM_LBUTTONDOWN || m.Msg == WM_RBUTTONDOWN || m.Msg == WM_MBUTTONDOWN)
            {
                var menu = _fenceWindow._contextMenu;
                if (menu == null || !menu.Visible) return false;

                var pt = new System.Drawing.Point(
                    (int)m.LParam & 0xFFFF,
                    (int)((uint)m.LParam >> 16) & 0xFFFF);

                var hwnd = UnsafeNativeMethods.WindowFromPoint(pt);

                // 点击了围栏窗口本身 → 不关闭
                if (hwnd == _fenceWindow.Handle) return false;

                // 点击了菜单的 drop-down 窗口 → 不关闭
                // ContextMenuStrip 显示时创建的内部窗口类名是 "ToolStripDropDown"
                var className = GetWindowClassName(hwnd);
                if (className == "ToolStripDropDown" || className == "WindowsForms10.Window.8.app.0.3eeb3a_r6_ad1")
                    return false;

                // 其他位置 → 关闭菜单
                menu.Close();
            }
            return false;
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        private static string GetWindowClassName(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            var sb = new System.Text.StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}

internal static class UnsafeNativeMethods
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(System.Drawing.Point point);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    public const int SW_SHOW = 5;
    public const int GWL_STYLE = -16;
    public const int WS_VISIBLE = 0x10000000;
    public const int WM_SHOWWINDOW = 0x0018;

    public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public static int LOWORD(int value) => value & 0xFFFF;

    public static void ForceVisible(IntPtr hWnd)
    {
        // 先设置 WS_VISIBLE 样式位
        int style = GetWindowLong(hWnd, GWL_STYLE);
        SetWindowLong(hWnd, GWL_STYLE, style | WS_VISIBLE);
        // 再调用 ShowWindow
        ShowWindow(hWnd, SW_SHOW);
    }
}
