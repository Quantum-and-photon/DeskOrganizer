using System.Windows;

namespace DeskOrganizer;

public partial class ConfirmDialog : Window
{
    public bool DialogResultValue { get; private set; }

    /// <summary>
    /// 显示确认对话框
    /// </summary>
    /// <param name="title">标题</param>
    /// <param name="message">消息</param>
    /// <param name="warning">警告提示（可选）</param>
    /// <param name="confirmText">确认按钮文字</param>
    /// <param name="isDanger">是否危险操作（红色按钮）</param>
    public static bool Show(string title, string message, string warning = "",
        string confirmText = "确定", bool isDanger = true)
    {
        var dlg = new ConfirmDialog();
        dlg.TitleText.Text = title;
        dlg.MessageText.Text = message;

        if (!string.IsNullOrEmpty(warning))
        {
            dlg.WarningBanner.Visibility = Visibility.Visible;
            dlg.WarningText.Text = warning;
        }

        dlg.ConfirmButton.Content = confirmText;

        var converter = new System.Windows.Media.BrushConverter();
        if (isDanger)
        {
            // 危险操作：黄色图标 + 红色按钮
            dlg.IconCircle.Fill = (System.Windows.Media.Brush)converter.ConvertFromString("#FEF3C7")!;
            dlg.IconText.Text = "⚠";
            dlg.IconText.Foreground = (System.Windows.Media.Brush)converter.ConvertFromString("#F59E0B")!;
        }
        else
        {
            // 非危险：蓝色图标 + 蓝色按钮
            dlg.IconCircle.Fill = (System.Windows.Media.Brush)converter.ConvertFromString("#DBEAFE")!;
            dlg.IconText.Text = "ℹ";
            dlg.IconText.Foreground = (System.Windows.Media.Brush)converter.ConvertFromString("#3B82F6")!;
            dlg.ConfirmButton.Background = (System.Windows.Media.Brush)converter.ConvertFromString("#2563EB")!;
        }

        dlg.ShowDialog();
        return dlg.DialogResultValue;
    }

    public ConfirmDialog()
    {
        InitializeComponent();
        MouseLeftButtonDown += (_, _) => DragMove();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResultValue = false;
        Close();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResultValue = true;
        Close();
    }
}
