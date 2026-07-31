using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeskOrganizer.NoFences;

/// <summary>
/// 栅栏重命名对话框。
/// </summary>
public class EditDialog : Form
{
    private TextBox _textBox = null!;
    private Button _okButton = null!;
    private Button _cancelButton = null!;

    /// <summary>
    /// 用户输入的新名称。
    /// </summary>
    public string Value => _textBox.Text;

    /// <summary>
    /// 创建重命名对话框。
    /// </summary>
    /// <param name="title">对话框标题。</param>
    /// <param name="label">输入框标签文字。</param>
    /// <param name="defaultValue">默认值。</param>
    public EditDialog(string title, string label, string defaultValue = "")
    {
        InitializeComponents(title, label, defaultValue);
    }

    private void InitializeComponents(string title, string label, string defaultValue)
    {
        // 窗体设置
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new System.Drawing.Size(320, 120);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;

        // 标签
        var lbl = new Label
        {
            Text = label,
            Location = new System.Drawing.Point(12, 12),
            AutoSize = true
        };
        Controls.Add(lbl);

        // 文本框
        _textBox = new TextBox
        {
            Text = defaultValue,
            Location = new System.Drawing.Point(12, 36),
            Width = 290,
            SelectionStart = 0,
            SelectionLength = defaultValue.Length
        };
        Controls.Add(_textBox);

        // 确认按钮
        _okButton = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new System.Drawing.Point(140, 72),
            Width = 80
        };
        Controls.Add(_okButton);

        // 取消按钮
        _cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new System.Drawing.Point(228, 72),
            Width = 80
        };
        Controls.Add(_cancelButton);

        // 设定 AcceptButton 和 CancelButton
        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _textBox.Focus();
        _textBox.SelectAll();
    }
}
