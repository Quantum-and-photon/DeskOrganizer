using System;
using System.Drawing;
using System.Windows.Forms;

namespace DeskOrganizer.NoFences;

/// <summary>
/// 不透明度调整对话框（使用 TrackBar 滑块）。
/// </summary>
public class OpacityDialog : Form
{
    private TrackBar _trackBar = null!;
    private Label _valueLabel = null!;
    private Button _okButton = null!;
    private Button _cancelButton = null!;

    public double Value => _trackBar.Value / 100.0;

    public OpacityDialog(double currentOpacity)
    {
        InitializeComponents(currentOpacity);
    }

    private void InitializeComponents(double currentOpacity)
    {
        Text = "调整不透明度";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        ClientSize = new Size(300, 160);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;

        var label = new Label
        {
            Text = "不透明度：",
            Location = new Point(12, 14),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(label);

        _valueLabel = new Label
        {
            Text = $"{(int)(currentOpacity * 100)}%",
            Location = new Point(240, 14),
            AutoSize = true,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold)
        };
        Controls.Add(_valueLabel);

        _trackBar = new TrackBar
        {
            Minimum = 10,
            Maximum = 100,
            Value = (int)(currentOpacity * 100),
            TickFrequency = 10,
            Location = new Point(12, 40),
            Size = new Size(265, 45),
            LargeChange = 10
        };
        _trackBar.ValueChanged += (_, _) =>
        {
            _valueLabel.Text = $"{_trackBar.Value}%";
        };
        Controls.Add(_trackBar);

        _okButton = new Button
        {
            Text = "确定",
            DialogResult = DialogResult.OK,
            Location = new Point(110, 95),
            Size = new Size(80, 30),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(_okButton);

        _cancelButton = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(198, 95),
            Size = new Size(80, 30),
            Font = new Font("Segoe UI", 9f)
        };
        Controls.Add(_cancelButton);

        AcceptButton = _okButton;
        CancelButton = _cancelButton;
    }
}
