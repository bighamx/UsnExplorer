using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UsnExplorer.ViewModels;

namespace UsnExplorer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.ExportRequested = ExportAsync;
        _vm.PropertyChanged += Vm_PropertyChanged;

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 权限徽标
        bool admin = IsElevated();
        PrivilegeBadge.Text = admin ? "● 管理员权限" : "▲ 未提权 —— 无法读取 USN";
        PrivilegeBadge.Foreground = admin
            ? new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E))
            : new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));

        // ViewModel 需要把 DateFrom/DateTo 的时刻部分回写到文本框
        _vm.SyncTimeBoxes = SyncTimeBoxesFromVm;

        // 枚举分区 (不需要提权)
        await _vm.LoadVolumesAsync();

        if (admin)
        {
            // 自动开扫, 省掉一次点击
            await _vm.ScanAsync();
        }
        else
        {
            _vm.StatusText = "未以管理员身份运行 —— 请右键使用「以管理员身份运行」重启";
        }
    }

    /// <summary>把 DateFrom/DateTo 的时分秒同步到两个时刻输入框。</summary>
    private void SyncTimeBoxesFromVm()
    {
        _syncingTime = true;
        try
        {
            FromTimeBox.Text = (_vm.DateFrom ?? DateTime.MinValue).ToString("HH:mm:ss");
            ToTimeBox.Text = (_vm.DateTo ?? DateTime.MinValue).ToString("HH:mm:ss");
        }
        finally
        {
            _syncingTime = false;
        }
    }

    /// <summary>防止时间框回写触发循环</summary>
    private bool _syncingTime;

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsScanning))
        {
            ProgressPanel.Visibility = _vm.IsScanning ? Visibility.Visible : Visibility.Collapsed;
        }
        if (e.PropertyName == nameof(MainViewModel.ProgressText) && _vm.IsScanning)
        {
            ProgressPanel.Visibility = string.IsNullOrEmpty(_vm.ProgressText)
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    // ------------------------------------------------------------------ 交互

    /// <summary>点击单条操作类型 = 只看这一类 (再点一次取消)。</summary>
    private void OperationRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: OperationFilterItem item })
        {
            var anySelected = _vm.Operations.Any(o => o.IsSelected);
            if (anySelected && item.IsSelected && _vm.Operations.Count(o => o.IsSelected) == 1)
            {
                // 只选了这一个 -> 取消选择, 恢复全部
                item.IsSelected = false;
                _vm.ApplyFilterNow();
            }
            else
            {
                _vm.SelectOnlyOperation(item);
            }
        }
    }

    /// <summary>
    /// 拦下 DataGrid 的默认排序。
    /// 默认路径走 ListCollectionView, 会枚举全部条目 —— 对按需创建行的索引器就是
    /// 几百万次物化 + 反射读属性, 百万行必然卡死。改成让 ViewModel 只排 int 索引。
    /// </summary>
    private async void RecordsGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;                      // 关键: 阻止 WPF 的默认排序

        var key = e.Column.SortMemberPath;
        if (string.IsNullOrEmpty(key)) return;

        // 同步列头的排序指示箭头
        var dir = e.Column.SortDirection != System.ComponentModel.ListSortDirection.Ascending
            ? System.ComponentModel.ListSortDirection.Ascending
            : System.ComponentModel.ListSortDirection.Descending;

        foreach (var c in RecordsGrid.Columns)
            c.SortDirection = null;
        e.Column.SortDirection = dir;

        await _vm.SortByAsync(key);
    }

    private void RecordsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中行变化时, 详情面板里的路径已由 Lazy 触发解析
        if (RecordsGrid.SelectedItem is UsnRow row)
        {
            _ = row.FullPath;   // 预热, 避免详情面板先闪空白
        }
    }

    /// <summary>双击行 = 复制完整路径。</summary>
    private void RecordsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RecordsGrid.SelectedItem is UsnRow row)
            CopyToClipboard(row.FullPath);
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedRow is { } row)
            CopyToClipboard(row.FullPath);
    }

    private static void CopyToClipboard(string text)
    {
        try
        {
            Clipboard.SetText(text);
        }
        catch (Exception ex)
        {
            // 剪贴板被其他进程占用时会抛, 不致命
            MessageBox.Show($"复制失败: {ex.Message}", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ 时间

    private void FromTimeBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyTimeBoxes();
    private void ToTimeBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyTimeBoxes();

    /// <summary>
    /// 把「日期选择器 (只有日期)」+「时刻文本框」合成精确到秒的边界。
    /// 组合规则: 完整时间 = 所选日期 + 框里的 HH:mm:ss。
    /// 时刻框留空或格式非法时, 起始边界退化为 00:00:00, 结束边界退化为 23:59:59。
    /// </summary>
    private void ApplyTimeBoxes()
    {
        if (!IsLoaded || _syncingTime) return;

        if (_vm.DateFrom is { } from)
            _vm.DateFrom = from.Date + ParseTime(FromTimeBox.Text, TimeSpan.Zero);
        if (_vm.DateTo is { } to)
            _vm.DateTo = to.Date + ParseTime(ToTimeBox.Text, new TimeSpan(23, 59, 59));
    }

    private static TimeSpan ParseTime(string? text, TimeSpan fallback)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return fallback;
        if (TimeSpan.TryParseExact(s, @"hh\:mm\:ss", CultureInfo.InvariantCulture, out var ts))
            return ts;
        if (TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out ts))
            return ts;
        // HH:mm 这种也接受
        if (TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out ts))
            return ts;
        return fallback;
    }

    // ------------------------------------------------------------------ 导出

    private async Task ExportAsync()
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出筛选结果",
            Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
            FileName = $"USN日志_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog(this) != true) return;

        _vm.StatusText = "正在导出…";
        var (ok, message) = await _vm.ExportCsvAsync(dlg.FileName);
        _vm.StatusText = message;
    }

    // ------------------------------------------------------------------ 按键

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            NameSearchBox.Focus();
            NameSearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            PathSearchBox.Focus();
            PathSearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
