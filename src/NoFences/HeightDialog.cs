using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeskOrganizer.NoFences;

/// <summary>
/// 标题栏高度调整对话框。
/// </summary>
public class HeightDialog : Form
{
    private NumericUpDown _numericUpDown = null!;
    private Button _okButton = null!;
    private Button _cancelButton = null!;

    /// <summary>
    /// 用户设定的标题栏高度值。
    /// </summary>
    public int Value => (int)_numericUpDown.Value;

    /// <summary>
    /// 创建标题栏高度调整对话框。
    /// </summary>
    /// <param name="currentHeight">当前高度值。</param>
    public HeightDialog(int currentHeight)
    {
        InitializeComponents(currentHeight);
    }

    private void InitializeComponents(int currentHeight)
    {
        // 窗体设置
        Text = "调整标题栏高度";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new System.Drawing.Size(280, 140);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;

        // 标签
        var label = new Label
        {
            Text = "标题栏高度（像素）:",
            Location = new System.Drawing.Point(12, 16),
            AutoSize = true
        };
        Controls.Add(label);

        // 数字调节器
        _numericUpDown = new NumericUpDown
        {
            Minimum = 20,
            Maximum = 100,
            Value = Math.Clamp(currentHeight, 20, 100),
            Increment = 1,
            Location = new System.Drawing.Point(12, 44),
            Width = 250
        };
        Controls.Add(_numericUpDown);

        // 确认按钮
        _okButton = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new System.Drawing.Point(100, 84),
            Width = 80
        };
        Controls.Add(_okButton);

        // 取消按钮
        _cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new System.Drawing.Point(188, 84),
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
        _numericUpDown.Focus();
        _numericUpDown.Select(0, _numericUpDown.Value.ToString().Length);
    }
}
