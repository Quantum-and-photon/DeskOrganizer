using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Microsoft.VisualBasic;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using DeskOrganizer.Model;

namespace DeskOrganizer;

public partial class StickyNoteWindow : Window
{
    private readonly Model.StickyNote _note;
    public string NoteId => _note.Id;
    private DispatcherTimer? _autoSaveTimer;
    private bool _isDirty;
    private bool _isMarkdownMode;
    private bool _isSnapping;
    private bool _barPinned;          // ☰ 按钮固定显示
    private DispatcherTimer? _hideBarTimer;

    /// <summary>提供其他便签窗口列表的回调（由 MainWindow 设置）。</summary>
    public static Func<StickyNoteWindow, IEnumerable<StickyNoteWindow>>? GetOtherNotes { get; set; }

    private static readonly string[] ThemeColors =
    {
        "#FFFFE066", "#FFB4D455", "#FF87CEEB", "#FFDDA0DD",
        "#FFFFA07A", "#FFF0F0F0", "#FFADD8E6", "#FFD4A5A5",
        "#FFA8D8B9", "#FFDAB8EB", "#FFF5C7A9", "#FFB0C4DE",
        "#FF98D8C8", "#FFE6C89C", "#FFC9B1D0"
    };

    private const double SnapThreshold = 15;

    public StickyNoteWindow(Model.StickyNote note)
    {
        _note = note;

        InitializeComponent();

        // Set position
        Left = _note.X;
        Top = _note.Y;
        Width = _note.Width;
        Height = _note.Height;

        // Set theme
        ApplyTheme(_note.BackgroundColor);
        // 初始化 ColorCombo 选中项
        for (int i = 0; i < ColorCombo.Items.Count; i++)
        {
            if (ColorCombo.Items[i] is ComboBoxItem ci && ci.Tag is string tag && tag == _note.BackgroundColor)
            {
                ColorCombo.SelectedIndex = i;
                break;
            }
        }

        // Set opacity（用 alpha 通道，不影响文字清晰度）
        ApplyBackgroundWithAlpha(_note.Opacity);
        // 初始化 OpacityCombo 选中项
        var opacityPercent = (int)(_note.Opacity * 100);
        for (int i = 0; i < OpacityCombo.Items.Count; i++)
        {
            if (OpacityCombo.Items[i] is ComboBoxItem item &&
                int.TryParse(item.Content?.ToString()?.Replace("%", ""), out var pct) &&
                pct == opacityPercent)
            {
                OpacityCombo.SelectedIndex = i;
                break;
            }
        }

        // Set font
        ContentEditor.FontSize = _note.FontSize;
        if (!string.IsNullOrEmpty(_note.FontFamily))
            ContentEditor.FontFamily = new FontFamily(_note.FontFamily);

        // Set blur
        ApplyBlurEffect(_note.BlurEnabled);
        if (_note.BlurEnabled)
        {
            BtnBlur.Background = new SolidColorBrush(Color.FromArgb(0x50, 0x42, 0xA5, 0xF5));
            BtnBlur.Foreground = new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2));
            BtnBlur.FontWeight = FontWeights.Bold;
        }

        // Set content (try load from .md file first)
        var mdContent = ConfigService.Instance.LoadNoteContent(_note.Id);
        SetContent(mdContent ?? _note.Content);
        _isDirty = false; // 确保加载完成后不触发立即保存

        // Set title
        TitleText.Text = string.IsNullOrEmpty(_note.Title) ? "便签" : _note.Title;
        Title = TitleText.Text;

        // Init font family combo
        InitFontFamilyCombo();

        // Update word count
        UpdateWordCount();

        // Auto-save timer (every 3 seconds)
        _autoSaveTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _autoSaveTimer.Tick += (_, _) =>
        {
            if (_isDirty) Save();
        };
        _autoSaveTimer.Start();

        // Bottom bar toggle button
        // 工具栏默认显示，通过标题栏 ☰ 按钮切换

        // Location changed for snapping
        LocationChanged += Window_LocationChanged;

        // 嵌入桌面底层 + 添加缩放边框
        SourceInitialized += Window_SourceInitialized;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var helper = new System.Windows.Interop.WindowInteropHelper(this);
        var hwnd = helper.Handle;

        // 桌面底层
        helper.Owner = Win32.Win32Helper.GetDesktopWindow();
        Win32.Win32Helper.SetWindowLong(hwnd, Win32.Win32Helper.GWL_EXSTYLE,
            Win32.Win32Helper.GetWindowLong(hwnd, Win32.Win32Helper.GWL_EXSTYLE) | 0x80);
        Win32.Win32Helper.SetBottomWindow(hwnd);

        // 拦截 WM_NCHITTEST 实现边缘缩放
        var source = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        source?.AddHook(WndProcHook);
    }

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
    private const int ResizeBorder = 6;

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST)
        {
            // 提取鼠标屏幕坐标（lParam 低字=x，高字=y）
            int lParamInt = lParam.ToInt32();
            short x = (short)(lParamInt & 0xFFFF);
            short y = (short)((lParamInt >> 16) & 0xFFFF);

            // 转换为窗口相对坐标
            var screenPoint = new Point(x, y);
            var windowPoint = PointFromScreen(screenPoint);
            int relX = (int)windowPoint.X;
            int relY = (int)windowPoint.Y;

            int w = (int)ActualWidth;
            int h = (int)ActualHeight;

            bool onLeft = relX >= 0 && relX < ResizeBorder;
            bool onRight = relX > w - ResizeBorder && relX <= w;
            bool onTop = relY >= 0 && relY < ResizeBorder;
            bool onBottom = relY > h - ResizeBorder && relY <= h;

            bool onTopLeft = onTop && onLeft;
            bool onTopRight = onTop && onRight;
            bool onBottomLeft = onBottom && onLeft;
            bool onBottomRight = onBottom && onRight;

            if (onTopLeft) { handled = true; return (IntPtr)HTTOPLEFT; }
            if (onTopRight) { handled = true; return (IntPtr)HTTOPRIGHT; }
            if (onBottomLeft) { handled = true; return (IntPtr)HTBOTTOMLEFT; }
            if (onBottomRight) { handled = true; return (IntPtr)HTBOTTOMRIGHT; }
            if (onLeft) { handled = true; return (IntPtr)HTLEFT; }
            if (onRight) { handled = true; return (IntPtr)HTRIGHT; }
            if (onTop) { handled = true; return (IntPtr)HTTOP; }
            if (onBottom) { handled = true; return (IntPtr)HTBOTTOM; }
        }
        return IntPtr.Zero;
    }

    private void InitFontFamilyCombo()
    {
        var family = _note.FontFamily;
        for (int i = 0; i < FontFamilyCombo.Items.Count; i++)
        {
            if (FontFamilyCombo.Items[i] is ComboBoxItem item &&
                item.Tag is string tag && tag == family)
            {
                FontFamilyCombo.SelectedIndex = i;
                return;
            }
        }
    }

    // ---- Window Events ----

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_isDirty) Save();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 关闭=隐藏，重启后还在；不弹删除确认
        Save();
        _autoSaveTimer?.Stop();
        _autoSaveTimer = null;

        // 从主窗口的便签列表中移除引用（窗口对象），但保留配置数据
        try { MainWindow.Instance?.UnregisterStickyNoteWindow(this); } catch { }
    }

    private void Window_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _note.X = Left;
        _note.Y = Top;
        _note.Width = ActualWidth;
        _note.Height = ActualHeight;
        _note.ModifiedAt = DateTime.Now;
    }

    private DispatcherTimer? _snapTimer;

    private void Window_LocationChanged(object? sender, EventArgs e)
    {
        // 实时更新坐标，延迟执行吸附（避免拖动中频繁 SetLocation 导致闪屏）
        _note.X = Left;
        _note.Y = Top;
        _note.ModifiedAt = DateTime.Now;

        _snapTimer?.Stop();
        _snapTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _snapTimer.Tick -= SnapTimer_Tick;
        _snapTimer.Tick += SnapTimer_Tick;
        _snapTimer.Start();
    }

    private void SnapTimer_Tick(object? sender, EventArgs e)
    {
        _snapTimer?.Stop();
        if (_isSnapping) return;
        _isSnapping = true;
        try
        {
            SnapToEdges();
            _note.X = Left;
            _note.Y = Top;
        }
        catch { }
        finally { _isSnapping = false; }
    }

    // ---- Content ----

    private bool _suppressTextChanged;

    private void ContentEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        _isDirty = true;
        _note.ModifiedAt = DateTime.Now;
        UpdateWordCount();
    }

    private void ContentEditor_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            double delta = e.Delta > 0 ? 2 : -2;
            double newSize = Math.Max(8, Math.Min(48, ContentEditor.FontSize + delta));
            ApplyFontSize(newSize);
        }
    }

    private void UpdateWordCount()
    {
        try
        {
            var range = new TextRange(ContentEditor.Document.ContentStart, ContentEditor.Document.ContentEnd);
            var text = range.Text.Trim();
            int charCount = text.Length;
            // 统计中文和英文单词
            int wordCount = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                if (c > 0x4E00) // CJK 字符
                {
                    wordCount++;
                    inWord = false;
                }
                else if (char.IsLetterOrDigit(c))
                {
                    if (!inWord) { wordCount++; inWord = true; }
                }
                else
                {
                    inWord = false;
                }
            }
            WordCountText.Text = $"{charCount}字 / {wordCount}词";
            FontInfoText.Text = $"{ContentEditor.FontSize:F0}px";
        }
        catch { }
    }

    private void SetContent(string content)
    {
        // 临时抑制 TextChanged，避免程序设置内容时误标记 _isDirty
        _suppressTextChanged = true;
        try
        {
            if (string.IsNullOrEmpty(content))
            {
                ContentEditor.Document.Blocks.Clear();
                return;
            }

            // 规范化换行：统一为 \r\n（RichTextBox 内部格式）
            content = content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
            // 去除末尾多余换行（TextRange.Text setter 会自动添加）
            content = content.TrimEnd('\r', '\n');

            var range = new TextRange(ContentEditor.Document.ContentStart, ContentEditor.Document.ContentEnd);
            range.Text = content;
        }
        finally
        {
            _suppressTextChanged = false;
        }
    }

    private string GetContent()
    {
        var range = new TextRange(ContentEditor.Document.ContentStart, ContentEditor.Document.ContentEnd);
        // RichTextBox 的 TextRange.Text 会在末尾添加 \r\n，且每段后也有 \r\n
        // 彻底清理：去除末尾所有空白换行，中间换行统一为 \n（节省存储，避免重复规范化）
        var text = range.Text;
        // 去除末尾多余的 \r\n（RichTextBox 总会添加）
        text = text.TrimEnd('\r', '\n', ' ');
        // 统一换行为 \n（存储用 \n，显示时 SetContent 会转回 \r\n）
        text = text.Replace("\r\n", "\n").Replace("\r", "\n");
        return text;
    }

    // ---- Theme ----

    private void ApplyTheme(string colorHex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(colorHex);

            // Darken border slightly
            var border = Color.FromRgb(
                (byte)Math.Max(0, color.R - 30),
                (byte)Math.Max(0, color.G - 30),
                (byte)Math.Max(0, color.B - 30));
            NoteBorder.BorderBrush = new SolidColorBrush(border);
        }
        catch { }

        _note.BackgroundColor = colorHex;
        // 重新应用当前不透明度（用 alpha 通道设置背景色）
        ApplyBackgroundWithAlpha(_note.Opacity);
    }

    private void ColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ColorCombo.SelectedItem is ComboBoxItem item && item.Tag is string colorHex)
        {
            ApplyTheme(colorHex);
        }
    }

    // ---- Opacity ----

    private void OpacityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpacityCombo.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content?.ToString()?.Replace("%", ""), out var pct))
        {
            double opacity = pct / 100.0;
            // 用 alpha 通道实现真正半透明（而非 Opacity 变浅效果）
            ApplyBackgroundWithAlpha(opacity);
            _note.Opacity = opacity;
        }
    }

    /// <summary>根据不透明度设置背景色的 alpha 通道。</summary>
    private void ApplyBackgroundWithAlpha(double opacity)
    {
        try
        {
            var baseColor = (Color)ColorConverter.ConvertFromString(_note.BackgroundColor);
            byte alpha = (byte)(opacity * 255);
            var bgColor = Color.FromArgb(alpha, baseColor.R, baseColor.G, baseColor.B);

            if (_note.BlurEnabled)
            {
                // 毛玻璃模式：半透明白色
                BlurLayer.Background = new SolidColorBrush(Color.FromArgb(alpha, 0xFF, 0xFF, 0xFF));
                BgLayer.Background = new SolidColorBrush(Colors.Transparent);
            }
            else
            {
                BgLayer.Background = new SolidColorBrush(bgColor);
            }
        }
        catch { }
    }

    // ---- Font ----

    private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontFamilyCombo.SelectedItem is ComboBoxItem item && item.Tag is string family)
        {
            try
            {
                ContentEditor.FontFamily = new FontFamily(family);
                _note.FontFamily = family;
            }
            catch { }
        }
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontSizeCombo.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Content?.ToString(), out var size))
        {
            ContentEditor.FontSize = size;
            _note.FontSize = size;
            FontInfoText.Text = $"{size:F0}px";
        }
    }

    private void ApplyFontSize(double size)
    {
        ContentEditor.FontSize = size;
        _note.FontSize = size;
        FontInfoText.Text = $"{size:F0}px";
        // 同步下拉框
        for (int i = 0; i < FontSizeCombo.Items.Count; i++)
        {
            if (FontSizeCombo.Items[i] is ComboBoxItem item &&
                double.TryParse(item.Content?.ToString(), out var s) &&
                Math.Abs(s - size) < 0.1)
            {
                FontSizeCombo.SelectedIndex = i;
                return;
            }
        }
    }

    // ---- Blur ----

    private void BtnBlur_Click(object sender, RoutedEventArgs e)
    {
        _note.BlurEnabled = !_note.BlurEnabled;
        ApplyBlurEffect(_note.BlurEnabled);

        // 高亮效果
        BtnBlur.Background = _note.BlurEnabled
            ? new SolidColorBrush(Color.FromArgb(0x50, 0x42, 0xA5, 0xF5))
            : new SolidColorBrush(Colors.Transparent);
        BtnBlur.Foreground = _note.BlurEnabled
            ? new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2))
            : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        BtnBlur.FontWeight = _note.BlurEnabled ? FontWeights.Bold : FontWeights.Normal;
    }

    private void ApplyBlurEffect(bool enabled)
    {
        if (enabled)
        {
            BlurLayer.Visibility = Visibility.Visible;
            if (BlurLayer.Effect is BlurEffect be)
                be.Radius = 12;
        }
        else
        {
            BlurLayer.Visibility = Visibility.Collapsed;
        }
        // 重新应用背景（含 alpha 通道）
        ApplyBackgroundWithAlpha(_note.Opacity);
    }

    // ---- Markdown Toggle ----

    private void BtnMarkdown_Click(object sender, RoutedEventArgs e)
    {
        _isMarkdownMode = !_isMarkdownMode;

        // 高亮效果：激活时背景变为半透明蓝色
        BtnMarkdown.Background = _isMarkdownMode
            ? new SolidColorBrush(Color.FromArgb(0x50, 0x42, 0xA5, 0xF5))
            : new SolidColorBrush(Colors.Transparent);
        BtnMarkdown.Foreground = _isMarkdownMode
            ? new SolidColorBrush(Color.FromRgb(0x19, 0x76, 0xD2))
            : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        BtnMarkdown.FontWeight = _isMarkdownMode ? FontWeights.Bold : FontWeights.Normal;

        if (_isMarkdownMode)
        {
            var content = GetContent();
            var rendered = RenderMarkdown(content);
            SetContent(rendered);
        }
        else
        {
            SetContent(_note.Content);
        }
        _isDirty = false; // 模式切换是程序行为，不应触发自动保存
    }

    private static string RenderMarkdown(string markdown)
    {
        return markdown
            .Replace("### ", "[H3] ")
            .Replace("## ", "[H2] ")
            .Replace("# ", "[H1] ")
            .Replace("**", "[B]")
            .Replace("*", "[I]")
            .Replace("`", "[Code]");
    }

    // ---- Drag & Drop ----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var file in files)
            {
                if (!File.Exists(file)) continue;
                var fileName = Path.GetFileName(file);
                var content = GetContent();
                var attachment = $"\n[附件: {fileName}](file:///{file.Replace('\\', '/')})\n";
                SetContent(content + attachment);
                _isDirty = true; // 拖放附件是用户操作，需触发保存
            }
        }
    }

    // ---- Buttons ----

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnToggleBar_Click(object sender, RoutedEventArgs e)
    {
        _barPinned = !_barPinned;
        if (_barPinned)
            ShowBottomBar();
        else
            HideBottomBar();
    }

    /// <summary>底部热区鼠标进入 → 显示底栏。</summary>
    private void BottomHotZone_MouseEnter(object sender, MouseEventArgs e)
    {
        ShowBottomBar();
    }

    private void ShowBottomBar()
    {
        _hideBarTimer?.Stop();
        BottomBar.Visibility = Visibility.Visible;
    }

    private void HideBottomBar()
    {
        if (_barPinned) return;
        _hideBarTimer?.Stop();
        _hideBarTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _hideBarTimer.Tick += (_, _) =>
        {
            BottomBar.Visibility = Visibility.Collapsed;
            _hideBarTimer?.Stop();
        };
        _hideBarTimer.Start();
    }

    /// <summary>底栏鼠标进入 → 保持显示。</summary>
    private void BottomBar_MouseEnter(object sender, MouseEventArgs e)
    {
        ShowBottomBar();
    }

    /// <summary>底栏鼠标离开 → 延迟隐藏。</summary>
    private void BottomBar_MouseLeave(object sender, MouseEventArgs e)
    {
        HideBottomBar();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void DeleteStickyNote_Click(object sender, RoutedEventArgs e)
    {
        var result = ConfirmDialog.Show(
            "删除便签",
            $"确定要永久删除便签 \"{TitleText.Text}\" 吗？",
            "删除后重启软件将无法恢复此便签。如只需临时隐藏，请选择\"关闭便签\"。",
            "永久删除",
            isDanger: true);

        if (result)
        {
            MainWindow.Instance?.DeleteStickyNote(_note.Id);
        }
    }

    private void TitleBar_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 检测双击标题栏进入内联编辑
        if (e.ClickCount == 2)
        {
            StartInlineEditTitle();
        }
    }

    private void StartInlineEditTitle()
    {
        TitleEditBox.Text = TitleText.Text;
        TitleText.Visibility = Visibility.Collapsed;
        TitleEditBox.Visibility = Visibility.Visible;
        TitleEditBox.Focus();
        TitleEditBox.SelectAll();
    }

    private void EndInlineEditTitle(bool save)
    {
        if (TitleEditBox.Visibility != Visibility.Visible) return;

        var newText = TitleEditBox.Text.Trim();
        TitleEditBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;

        if (save && !string.IsNullOrWhiteSpace(newText) && newText != TitleText.Text)
        {
            TitleText.Text = newText;
            _note.Title = newText;
            _note.ModifiedAt = DateTime.Now;
            Save();
        }
    }

    private void TitleEditBox_LostFocus(object sender, RoutedEventArgs e)
    {
        EndInlineEditTitle(true); // 失焦自动保存
    }

    private void TitleEditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            EndInlineEditTitle(true);
        }
        else if (e.Key == Key.Escape)
        {
            EndInlineEditTitle(false); // ESC 取消
        }
    }

    // ---- Snap to Edges ----

    private void SnapToEdges()
    {
        const double snap = 6;
        var screen = SystemParameters.WorkArea;

        // 屏幕边缘吸附
        if (Math.Abs(Left - screen.Left) < snap) Left = screen.Left;
        if (Math.Abs(Left + ActualWidth - screen.Right) < snap) Left = screen.Right - ActualWidth;
        if (Math.Abs(Top - screen.Top) < snap) Top = screen.Top;
        if (Math.Abs(Top + ActualHeight - screen.Bottom) < snap) Top = screen.Bottom - ActualHeight;

        // 便签间吸附 + 防重叠
        var others = GetOtherNotes?.Invoke(this);
        if (others == null) return;

        double myLeft = Left, myTop = Top;
        double myRight = Left + ActualWidth, myBottom = Top + ActualHeight;

        foreach (var other in others)
        {
            if (other == null || !other.IsLoaded) continue;

            double oLeft = other.Left, oTop = other.Top;
            double oRight = oLeft + other.ActualWidth, oBottom = oTop + other.ActualHeight;

            // 检查是否重叠
            bool overlap = myLeft < oRight && myRight > oLeft && myTop < oBottom && myBottom > oTop;

            if (overlap)
            {
                // 计算最小推移方向
                double pushLeft = oRight - myLeft;    // 向左推
                double pushRight = myRight - oLeft;   // 向右推
                double pushUp = oBottom - myTop;       // 向上推
                double pushDown = myBottom - oTop;     // 向下推

                double minPush = Math.Min(Math.Min(pushLeft, pushRight), Math.Min(pushUp, pushDown));

                if (minPush == pushLeft) myLeft = oRight + 2;
                else if (minPush == pushRight) myLeft = oLeft - ActualWidth - 2;
                else if (minPush == pushUp) myTop = oBottom + 2;
                else myTop = oTop - ActualHeight - 2;

                myRight = myLeft + ActualWidth;
                myBottom = myTop + ActualHeight;
            }
            else
            {
                // 边缘吸附（间距 < snap 时对齐）
                // 水平吸附：右边缘靠近左边缘
                if (Math.Abs(myRight - oLeft) < snap && !(myTop >= oBottom || myBottom <= oTop))
                {
                    myLeft = oLeft - ActualWidth;
                    // 垂直对齐
                    if (Math.Abs(myTop - oTop) < snap) myTop = oTop;
                }
                // 左边缘靠近右边缘
                else if (Math.Abs(myLeft - oRight) < snap && !(myTop >= oBottom || myBottom <= oTop))
                {
                    myLeft = oRight;
                    if (Math.Abs(myTop - oTop) < snap) myTop = oTop;
                }

                // 垂直吸附：下边缘靠近上边缘
                if (Math.Abs(myBottom - oTop) < snap && !(myLeft >= oRight || myRight <= oLeft))
                {
                    myTop = oTop - ActualHeight;
                    if (Math.Abs(myLeft - oLeft) < snap) myLeft = oLeft;
                }
                // 上边缘靠近下边缘
                else if (Math.Abs(myTop - oBottom) < snap && !(myLeft >= oRight || myRight <= oLeft))
                {
                    myTop = oBottom;
                    if (Math.Abs(myLeft - oLeft) < snap) myLeft = oLeft;
                }
            }
        }

        Left = myLeft;
        Top = myTop;
    }

    // ---- Save ----

    private bool _isSaving;

    public void Save()
    {
        if (_isSaving) return; // 防重入：Deactivated 和自动保存定时器可能并发调用
        _isSaving = true;
        try
        {
            _note.Content = GetContent();
            _note.Title = TitleText.Text;
            _note.X = Left;
            _note.Y = Top;
            _note.Width = ActualWidth;
            _note.Height = ActualHeight;
            _note.ModifiedAt = DateTime.Now;

            var notes = ConfigService.Instance.Config.StickyNotes;
            if (notes != null)
            {
                var index = notes.FindIndex(n => n.Id == _note.Id);
                if (index >= 0)
                {
                    notes[index] = _note;
                }
            }

            ConfigService.Instance.Save();
            // 持久化便签内容到 .md 文件
            ConfigService.Instance.SaveNoteContent(_note);
            _isDirty = false;
        }
        catch (Exception ex)
        {
            App.Log($"[StickyNoteWindow] Save failed for '{_note.Id}': {ex.Message}");
        }
        finally
        {
            _isSaving = false;
        }
    }
}
