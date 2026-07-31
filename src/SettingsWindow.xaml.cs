using System;
using System.Windows;
using System.Windows.Controls;
using DeskOrganizer.Model;
using DeskOrganizer.Win32;

namespace DeskOrganizer;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadSettings();
        LoadDataInfo();
    }

    // ---- Load ----

    private void LoadSettings()
    {
        var config = ConfigService.Instance.Config;

        ChkStartWithWindows.IsChecked = config.StartWithWindows;
        ChkMinimizeToTray.IsChecked = config.MinimizeToTray;
        ChkEnableBlur.IsChecked = config.EnableBlur;
        ChkShowShadow.IsChecked = config.ShowShadow;
        ChkAutoSave.IsChecked = config.AutoSave;
        ChkMarkdownRender.IsChecked = config.MarkdownRender;

        SliderIconSize.Value = config.IconSize;
        SliderTitleHeight.Value = config.TitleHeight;
        SliderFontSize.Value = config.FontSize;

        // 数据管理配置
        ChkAutoCleanBackups.IsChecked = config.AutoCleanBackups;
        TxtMaxBackupCount.Text = config.MaxBackupCount.ToString();
        TxtStorageLimit.Text = config.StorageLimitMB.ToString();
        TxtSearchIndexLimit.Text = config.SearchIndexLimit.ToString();

        // 搜索热键配置
        SelectHotkeyItem(CmbHotkeyMod, config.SearchHotkeyModifiers > 0 ? config.SearchHotkeyModifiers : 1);
        SelectHotkeyItem(CmbHotkeyKey, config.SearchHotkeyKey > 0 ? config.SearchHotkeyKey : 0x20);

        // 关于 Tab
        TxtVersion.Text = $"版本: v{Model.UpdateService.GetCurrentVersion()}";
        ChkAutoCheckUpdate.IsChecked = config.AutoCheckUpdate;

        UpdateSliderLabels();
    }

    /// <summary>按 Tag 值选中 ComboBox 项。</summary>
    private static void SelectHotkeyItem(System.Windows.Controls.ComboBox combo, int value)
    {
        foreach (var item in combo.Items)
        {
            if (item is System.Windows.Controls.ComboBoxItem cbi && cbi.Tag is string tagStr && int.TryParse(tagStr, out var tagVal) && tagVal == value)
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private void LoadDataInfo()
    {
        TxtConfigPath.Text = ConfigService.Instance.ConfigFilePath ?? "未知";
        TxtBackupPath.Text = ConfigService.Instance.BackupDirectoryPath ?? "未知";
        TxtLastSaved.Text = ConfigService.Instance.Config.LastSavedAt.ToString("yyyy-MM-dd HH:mm:ss");
        TxtBackupCount.Text = ConfigService.Instance.GetBackupCount().ToString();
    }

    // ---- Slider Labels ----

    private void UpdateSliderLabels()
    {
        TxtIconSize.Text = ((int)SliderIconSize.Value).ToString();
        TxtTitleHeight.Text = ((int)SliderTitleHeight.Value).ToString();
        TxtFontSize.Text = ((int)SliderFontSize.Value).ToString();
    }

    // ---- Buttons ----

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        // 验证数值输入范围
        int iconSize = (int)SliderIconSize.Value;
        int titleHeight = (int)SliderTitleHeight.Value;
        int fontSize = (int)SliderFontSize.Value;

        var errors = new System.Collections.Generic.List<string>();
        if (iconSize < 16 || iconSize > 128)
            errors.Add($"图标大小 ({iconSize}px) 超出有效范围 (16-128)。");
        if (titleHeight < 20 || titleHeight > 100)
            errors.Add($"标题栏高度 ({titleHeight}px) 超出有效范围 (20-100)。");
        if (fontSize < 8 || fontSize > 72)
            errors.Add($"字体大小 ({fontSize}px) 超出有效范围 (8-72)。");

        // 解析数据管理配置
        int maxBackupCount = 20;
        if (!int.TryParse(TxtMaxBackupCount.Text, out maxBackupCount) || maxBackupCount < 1 || maxBackupCount > 100)
            errors.Add("备份保留数量需为 1-100 之间的整数。");

        int storageLimit = 10;
        if (!int.TryParse(TxtStorageLimit.Text, out storageLimit) || storageLimit < 0 || storageLimit > 10240)
            errors.Add("存储上限需为 0-10240 之间的整数 (MB)。");

        int searchIndexLimit = 200000;
        if (!int.TryParse(TxtSearchIndexLimit.Text, out searchIndexLimit) || searchIndexLimit < 1000 || searchIndexLimit > 1000000)
            errors.Add("搜索索引上限需为 1000-1000000 之间的整数。");

        if (errors.Count > 0)
        {
            MessageBox.Show(string.Join("\n", errors), "输入验证失败",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var config = ConfigService.Instance.Config;

        config.StartWithWindows = ChkStartWithWindows.IsChecked ?? true;
        config.MinimizeToTray = ChkMinimizeToTray.IsChecked ?? true;
        config.EnableBlur = ChkEnableBlur.IsChecked ?? false;
        config.ShowShadow = ChkShowShadow.IsChecked ?? true;
        config.AutoSave = ChkAutoSave.IsChecked ?? true;
        config.MarkdownRender = ChkMarkdownRender.IsChecked ?? false;

        config.IconSize = iconSize;
        config.TitleHeight = titleHeight;
        config.FontSize = fontSize;

        // 保存数据管理配置
        config.AutoCleanBackups = ChkAutoCleanBackups.IsChecked ?? true;
        config.MaxBackupCount = maxBackupCount;
        config.StorageLimitMB = storageLimit;
        config.SearchIndexLimit = searchIndexLimit;

        // 保存热键配置
        config.SearchHotkeyModifiers = (CmbHotkeyMod.SelectedItem is System.Windows.Controls.ComboBoxItem modItem && modItem.Tag is string modTag && int.TryParse(modTag, out var mv)) ? mv : 1;
        config.SearchHotkeyKey = (CmbHotkeyKey.SelectedItem is System.Windows.Controls.ComboBoxItem keyItem && keyItem.Tag is string keyTag && int.TryParse(keyTag, out var kv)) ? kv : 0x20;

        // 保存自动更新配置
        config.AutoCheckUpdate = ChkAutoCheckUpdate.IsChecked ?? true;

        // Sync auto-start registry
        AutoStartHelper.SyncAutoStartState(config.StartWithWindows);

        ConfigService.Instance.Save();

        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // ---- 关于 Tab ----

    private void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var updateWindow = new UpdateWindow();
        updateWindow.CheckOnLoad();
        updateWindow.ShowDialog();
    }

    private void LinkGithub_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { }
    }

    private void BtnBackupNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ConfigService.Instance.CreateBackup();
            TxtBackupCount.Text = ConfigService.Instance.GetBackupCount().ToString();
            MessageBox.Show("备份成功。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"备份失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnRestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var result = MessageBox.Show(
            "确定要从最近的备份恢复吗？当前配置将被覆盖。",
            "确认恢复",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result == MessageBoxResult.Yes)
        {
            try
            {
                if (ConfigService.Instance.TryRestoreFromBackup() != null)
                {
                    LoadSettings();
                    LoadDataInfo();
                    MessageBox.Show("恢复成功。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("未找到可恢复的备份。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"恢复失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
