using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32.SafeHandles;
using UsnExplorer.Core;

namespace UsnExplorer.ViewModels;

/// <summary>分区筛选条目 (可勾选)</summary>
public sealed class VolumeFilterItem : INotifyPropertyChanged
{
    private bool _isSelected = true;

    public required string DriveLetter { get; init; }
    public required string DisplayName { get; init; }
    public string SizeText { get; set; } = "";
    public bool JournalEnabled { get; set; }
    public string? JournalError { get; set; }
    public int RecordCount { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public string CountText => RecordCount > 0 ? RecordCount.ToString("N0") : "";

    public void RaiseCountChanged() => OnPropertyChanged(nameof(CountText));

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>操作类型筛选条目 (可勾选)</summary>
public sealed class OperationFilterItem : INotifyPropertyChanged
{
    private bool _isSelected;
    public required UsnOperation Operation { get; init; }
    public int Count { get; set; }

    public string DisplayName => Operation.DisplayName;
    public string Description => Operation.Description;
    public string Accent => Operation.Accent;
    public string CountText => Count > 0 ? Count.ToString("N0") : "";

    public void RaiseCountChanged() => OnPropertyChanged(nameof(CountText));

    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

/// <summary>主视图模型: 扫描编排、筛选、统计。</summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly UsnStore _store = new();

    public MainViewModel()
    {
        Rows = new UsnRowList(_store);

        foreach (var op in UsnOperation.All)
            Operations.Add(new OperationFilterItem { Operation = op });

        foreach (var op in Operations)
            op.PropertyChanged += (_, _) => { if (AutoApply) ScheduleRefresh(); };

        Volumes.CollectionChanged += (_, _) => { if (AutoApply) ScheduleRefresh(); };

        ScanCommand = new RelayCommand(() => _ = ScanAsync(), () => CanScan);
        CancelCommand = new RelayCommand(CancelScan, () => IsScanning);
        ApplyFilterCommand = new RelayCommand(ApplyFilterNow, () => HasData);
        ClearFilterCommand = new RelayCommand(ClearAllFilters, () => HasData);
        ExportCommand = new RelayCommand(() => _ = RequestExportAsync(), () => HasData);
        ToggleAllOperationsCommand = new RelayCommand(ToggleAllOperations, () => HasData);
    }

    // ---------------------------------------------------------------- 命令

    public ICommand ScanCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand ApplyFilterCommand { get; }
    public ICommand ClearFilterCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ToggleAllOperationsCommand { get; }

    /// <summary>由 View 指定的导出回调 (View 负责弹保存对话框)。</summary>
    public Func<Task>? ExportRequested { get; set; }

    private Task RequestExportAsync() => ExportRequested?.Invoke() ?? Task.CompletedTask;

    private bool _allOpsSelected;
    private void ToggleAllOperations()
    {
        _allOpsSelected = !_allOpsSelected;
        foreach (var o in Operations)
            o.IsSelected = _allOpsSelected;
        ApplyFilterNow();
    }

    /// <summary>筛选条上显示的条件摘要。</summary>
    public string ActiveFilterText
    {
        get
        {
            if (_store.TotalCount == 0) return "";

            var parts = new List<string>(4);

            var vols = Volumes.Where(v => v.IsSelected).Select(v => v.DriveLetter).ToList();
            if (vols.Count > 0 && vols.Count < Volumes.Count)
                parts.Add("分区 " + string.Join("/", vols));

            var ops = Operations.Where(o => o.IsSelected).Select(o => o.DisplayName).ToList();
            if (ops.Count > 0)
                parts.Add("类型 " + string.Join("/", ops));

            if (DateFrom is not null || DateTo is not null)
            {
                var a = DateFrom?.ToString("MM-dd HH:mm") ?? "最早";
                var b = DateTo?.ToString("MM-dd HH:mm") ?? "现在";
                parts.Add($"{a} → {b}");
            }

            if (!string.IsNullOrWhiteSpace(FileNameSearch))
                parts.Add($"文件名~\"{FileNameSearch}\"");

            if (!string.IsNullOrWhiteSpace(PathSearch))
                parts.Add($"路径~\"{PathSearch}\"");

            if (ExcludeDirectories) parts.Add("排除目录");

            return parts.Count == 0 ? "无筛选" : string.Join("  ·  ", parts);
        }
    }

    public bool HasData => _store.TotalCount > 0;

    // ---------------------------------------------------------------- 排序

    private UsnStore.SortColumn _sortColumn = UsnStore.SortColumn.Time;
    // 日志查看器的自然默认是"最新在前"。初始为降序, 与扫描后的自动排序保持一致,
    // 这样徽标显示的排序状态和表格里的实际顺序不会互相矛盾。
    private bool _sortAscending;
    private bool _isSorting;

    /// <summary>当前排序状态文本, 显示在统计条上。</summary>
    public string SortText
    {
        get
        {
            var name = _sortColumn switch
            {
                UsnStore.SortColumn.Time => "时间",
                UsnStore.SortColumn.Volume => "分区",
                UsnStore.SortColumn.Operation => "操作",
                UsnStore.SortColumn.Name => "文件名",
                UsnStore.SortColumn.Folder => "所在文件夹",
                UsnStore.SortColumn.Usn => "USN",
                _ => "时间",
            };
            return $"{name} {(_sortAscending ? "↑" : "↓")}";
        }
    }

    public bool IsSorting
    {
        get => _isSorting;
        private set { _isSorting = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 由 View 在用户点击列头时调用。返回 false 表示正在排序中, 应忽略这次点击。
    ///
    /// 为什么不直接用 DataGrid 自带的排序: 它走 ListCollectionView, 必须枚举全部条目
    /// —— 我们按需创建行的索引器会被调用几百万次, 直接卡死。这里改为只对 int 索引排序。
    /// </summary>
    public async Task<bool> SortByAsync(string columnKey)
    {
        if (IsSorting || _store.ViewCount < 2) return false;

        var col = columnKey switch
        {
            "Volume" => UsnStore.SortColumn.Volume,
            "ReasonRaw" => UsnStore.SortColumn.Operation,
            "Name" => UsnStore.SortColumn.Name,
            "Folder" => UsnStore.SortColumn.Folder,
            "Usn" => UsnStore.SortColumn.Usn,
            _ => UsnStore.SortColumn.Time,
        };

        // 同一列再点一次 -> 反向; 换列 -> 重新升序
        if (col == _sortColumn) _sortAscending = !_sortAscending;
        else { _sortColumn = col; _sortAscending = true; }

        IsSorting = true;
        StatusText = $"正在按「{SortText}」排序…";

        try
        {
            var prog = new Progress<string>(msg => Report(() => ProgressText = msg));
            var (ms, ok) = await Task.Run(() =>
                _store.SortView(_sortColumn, _sortAscending, prog, CancellationToken.None));

            // 只在排序完成后刷新一次。排序期间保留旧数据可见 ——
            // Store 用双缓冲换引用, 不会暴露半成品状态, 所以不必提前清空表格。
            Rows.NotifyReset();
            StatusText = ok ? $"排序完成 ({ms}ms)" : "排序已取消";
            ProgressText = "";
            OnPropertyChanged(nameof(SortText));
            return ok;
        }
        catch (Exception ex)
        {
            StatusText = $"排序出错: {ex.Message}";
            return false;
        }
        finally
        {
            IsSorting = false;
        }
    }

    /// <summary>筛选变化时让文件夹键缓存失效, 并把排序状态复位提示。</summary>
    private void ResetSortState()
    {
        _store.InvalidateSortCache();
    }

    public UsnRowList Rows { get; }

    public ObservableCollection<VolumeFilterItem> Volumes { get; } = new();
    public ObservableCollection<OperationFilterItem> Operations { get; } = new();

    // ---------------------------------------------------------------- 状态

    private bool _isScanning;
    public bool IsScanning
    {
        get => _isScanning;
        private set { _isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanScan)); }
    }

    public bool CanScan => !IsScanning;

    private string _statusText = "就绪 —— 点击「扫描全部」开始";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _progressText = "";
    public string ProgressText
    {
        get => _progressText;
        set { _progressText = value; OnPropertyChanged(); }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set { _progressValue = value; OnPropertyChanged(); }
    }

    private bool _isIndeterminate;
    public bool IsIndeterminate
    {
        get => _isIndeterminate;
        set { _isIndeterminate = value; OnPropertyChanged(); }
    }

    /// <summary>筛选结果条数 (显示用)</summary>
    private string _resultSummary = "尚未扫描";
    public string ResultSummary
    {
        get => _resultSummary;
        set { _resultSummary = value; OnPropertyChanged(); }
    }

    private string _timingText = "";
    public string TimingText
    {
        get => _timingText;
        set { _timingText = value; OnPropertyChanged(); }
    }

    /// <summary>时间范围的实际边界 (扫描后从数据推导)</summary>
    private string _timeRangeText = "";
    public string TimeRangeText
    {
        get => _timeRangeText;
        set { _timeRangeText = value; OnPropertyChanged(); }
    }

    // ---------------------------------------------------------------- 筛选条件

    private string _fileNameSearch = "";
    /// <summary>文件名关键词</summary>
    public string FileNameSearch
    {
        get => _fileNameSearch;
        set
        {
            if (_fileNameSearch == value) return;
            _fileNameSearch = value;
            OnPropertyChanged();
            if (AutoApply) ScheduleRefresh();
        }
    }

    private string _pathSearch = "";
    /// <summary>路径 (所在文件夹) 关键词</summary>
    public string PathSearch
    {
        get => _pathSearch;
        set
        {
            if (_pathSearch == value) return;
            _pathSearch = value;
            OnPropertyChanged();
            if (AutoApply) ScheduleRefresh();
        }
    }

    private DateTime? _dateFrom;
    public DateTime? DateFrom
    {
        get => _dateFrom;
        set
        {
            if (_dateFrom == value) return;
            _dateFrom = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateFromOnly));
            if (AutoApply) ScheduleRefresh();
        }
    }

    private DateTime? _dateTo;
    public DateTime? DateTo
    {
        get => _dateTo;
        set
        {
            if (_dateTo == value) return;
            _dateTo = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateToOnly));
            if (AutoApply) ScheduleRefresh();
        }
    }

    /// <summary>
    /// 绑给 DatePicker 用 —— 它只认日期不认时刻。点选日期时保留已填的时刻部分。
    /// (DatePicker.SelectedDate 带上时间也可以, 但显示格式会带上时分秒, 很难看)
    /// </summary>
    public DateTime? DateFromOnly
    {
        get => _dateFrom?.Date;
        set
        {
            if (value is null) { DateFrom = null; return; }
            var time = _dateFrom?.TimeOfDay ?? TimeSpan.Zero;
            DateFrom = value.Value.Date + time;
        }
    }

    public DateTime? DateToOnly
    {
        get => _dateTo?.Date;
        set
        {
            if (value is null) { DateTo = null; return; }
            var time = _dateTo?.TimeOfDay ?? new TimeSpan(23, 59, 59);
            DateTo = value.Value.Date + time;
        }
    }

    private bool _excludeDirectories;
    public bool ExcludeDirectories
    {
        get => _excludeDirectories;
        set
        {
            if (_excludeDirectories == value) return;
            _excludeDirectories = value;
            OnPropertyChanged();
            if (AutoApply) ScheduleRefresh();
        }
    }

    /// <summary>输入时自动筛选 (大数据量下可关掉)</summary>
    private bool _autoApply = true;
    public bool AutoApply
    {
        get => _autoApply;
        set { _autoApply = value; OnPropertyChanged(); }
    }

    /// <summary>选中行的详情</summary>
    private UsnRow? _selectedRow;
    public UsnRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            _selectedRow = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
        }
    }

    public bool HasSelection => _selectedRow is not null;

    // 常用时间范围快捷选项
    public string[] QuickRanges { get; } =
        ["不限", "最近 1 小时", "最近 24 小时", "最近 7 天", "最近 30 天", "今天", "自定义"];

    private int _quickRangeIndex;
    public int QuickRangeIndex
    {
        get => _quickRangeIndex;
        set
        {
            if (_quickRangeIndex == value) return;
            _quickRangeIndex = value;
            OnPropertyChanged();
            ApplyQuickRange(value);
        }
    }

    private bool _suppressQuickRange;
    private void ApplyQuickRange(int idx)
    {
        if (_suppressQuickRange) return;
        var now = DateTime.Now;
        switch (idx)
        {
            case 0: DateFrom = null; DateTo = null; break;
            case 1: DateFrom = now.AddHours(-1); DateTo = null; break;
            case 2: DateFrom = now.AddHours(-24); DateTo = null; break;
            case 3: DateFrom = now.AddDays(-7); DateTo = null; break;
            case 4: DateFrom = now.AddDays(-30); DateTo = null; break;
            case 5: DateFrom = now.Date; DateTo = null; break;
            case 6: break;   // 自定义: 不动, 让用户填
        }
    }

    // ---------------------------------------------------------------- 扫描

    private CancellationTokenSource? _cts;
    private DispatcherTimer? _debounce;

    public void CancelScan() => _cts?.Cancel();

    /// <summary>扫描选中的分区 (没勾选就扫全部)。在后台线程执行, 保持 UI 响应。</summary>
    public async Task ScanAsync(bool includeMft = true)
    {
        if (IsScanning) return;

        IsScanning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        _store.Clear();
        Rows.NotifyReset();

        var targets = Volumes.Where(v => v.IsSelected && v.JournalEnabled).ToList();
        if (targets.Count == 0)
        {
            StatusText = "没有可扫描的分区 —— 请勾选至少一个已启用 USN 日志的分区";
            IsScanning = false;
            return;
        }

        var totalSw = Stopwatch.StartNew();
        StatusText = "正在扫描…";
        IsIndeterminate = true;
        ProgressValue = 0;

        try
        {
            long grandTotal = 0;

            foreach (var vol in targets)
            {
                ct.ThrowIfCancellationRequested();

                ProgressText = $"{vol.DriveLetter} 正在查询日志…";

                // 每个卷独立处理, 一个卷失败不影响其他卷
                var ok = await Task.Run(() => ScanOneVolume(vol, ct), ct);
                grandTotal += ok.records;
            }

            totalSw.Stop();

            // 扫描完推导实际时间边界
            UpdateTimeBounds();

            // 重建操作类型计数
            RefreshOperationCounts();

            var sw2 = Stopwatch.StartNew();
            ApplyFilterCore();
            sw2.Stop();

            int mftTotal = Volumes.Where(v => _store.HasResolver(v.DriveLetter))
                                  .Sum(v => _store.GetResolver(v.DriveLetter)?.MftEntryCount ?? 0);
            ResultSummary = $"共 {_store.TotalCount:N0} 条记录";
            TimingText = $"读取 {totalSw.Elapsed.TotalSeconds:0.00}s · 筛选 {sw2.ElapsedMilliseconds:N0}ms · MFT 索引 {mftTotal:N0} 项";
            StatusText = "扫描完成";

            RefreshVolumeCounts();
        }
        catch (OperationCanceledException)
        {
            StatusText = "扫描已取消";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描出错: {ex.Message}";
        }
        finally
        {
            IsScanning = false;
            IsIndeterminate = false;
            ProgressText = "";
            ProgressValue = 0;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private (bool ok, long records) ScanOneVolume(VolumeFilterItem vol, CancellationToken ct)
    {
        // 找到对应的 VolumeInfo
        var info = _volumesRaw.FirstOrDefault(v =>
            v.DriveLetter.Equals(vol.DriveLetter, StringComparison.OrdinalIgnoreCase));
        if (info is null) return (false, 0);

        var journal = UsnReader.QueryJournal(info.VolumePath, out var err, out var handle);
        if (journal is null || handle is null)
        {
            Report(() =>
            {
                vol.JournalEnabled = false;
                vol.JournalError = err;
                vol.IsSelected = false;
            });
            return (false, 0);
        }

        long count = 0;
        using (handle)
        {
            var j = journal.Value;

            // 进度报告节流: 每次进度都往 UI 线程发会导致队列爆炸。
            // 注意 Progress<T> 在后台线程构造时没有 SynchronizationContext,
            // 回调会跑在线程池线程上 —— 所以内部必须显式走 Report() 切回 UI 线程。
            var lastReport = DateTime.UtcNow;
            var progress = new Progress<UsnReadProgress>(p =>
            {
                var now = DateTime.UtcNow;
                if ((now - lastReport).TotalMilliseconds < 120) return;
                lastReport = now;
                Report(() =>
                {
                    ProgressValue = p.Fraction * 100;
                    ProgressText = $"{p.Volume} 已读 {p.RecordsRead:N0} 条";
                });
            });

            // 读日志 (后台线程)
            var collected = new List<UsnEntry>(1 << 16);
            count = UsnReader.ReadJournal(info, handle, j, e => collected.Add(e), progress, ct);

            // 一次性并入 store (在后台线程, 避免 UI 线程做百万次插入)
            lock (_storeLock)
            {
                foreach (var e in collected)
                    _store.Add(e);
            }

            if (ct.IsCancellationRequested) return (true, count);

            // 枚举 MFT 建路径解析表
            Report(() =>
            {
                ProgressText = $"{vol.DriveLetter} 正在建立 MFT 路径索引…";
                IsIndeterminate = true;
            });

            var mftProgress = new Progress<(long count, string volume)>(p =>
            {
                Report(() => ProgressText = $"{p.volume} MFT 索引 {p.count:N0} 项…");
            });
            var mft = UsnReader.EnumerateMft(handle, mftProgress, vol.DriveLetter, ct);
            lock (_storeLock)
            {
                _store.SetResolver(vol.DriveLetter, new PathResolver(mft, vol.DriveLetter));
            }
            Report(() => IsIndeterminate = false);
        }

        return (true, count);
    }

    private readonly object _storeLock = new();

    /// <summary>把工作丢回 UI 线程并等待完成 (用于更新绑定属性)。</summary>
    private void Report(Action a)
    {
        if (Application.Current?.Dispatcher is { } d && !d.CheckAccess())
            d.Invoke(a);
        else
            a();
    }

    private List<VolumeInfo> _volumesRaw = new();

    /// <summary>
    /// 枚举分区并填充筛选列表。
    /// 注意: ObservableCollection 只能在 UI 线程改 —— 枚举和日志探测放后台,
    /// 结果拿回 UI 线程再填集合, 否则会抛 "不支持从调度程序线程以外的线程更改"。
    /// </summary>
    public async Task LoadVolumesAsync()
    {
        // 后台: 枚举卷 + 逐个探测 USN 日志是否可用
        var probed = await Task.Run(() =>
        {
            var vols = UsnReader.EnumerateVolumes();
            var list = new List<(VolumeInfo info, bool enabled, string? err)>(vols.Count);
            foreach (var v in vols)
            {
                var journal = UsnReader.QueryJournal(v.VolumePath, out var err, out var handle);
                handle?.Dispose();
                list.Add((v, journal is not null, err));
            }
            return list;
        });

        _volumesRaw = probed.Select(p => p.info).ToList();

        // UI 线程: 填集合
        Volumes.Clear();
        foreach (var (info, enabled, err) in probed)
        {
            var item = new VolumeFilterItem
            {
                DriveLetter = info.DriveLetter,
                DisplayName = info.DisplayName,
                SizeText = info.SizeText,
                JournalEnabled = enabled,
                JournalError = err,
                IsSelected = enabled,
            };
            item.PropertyChanged += (_, _) => { if (AutoApply) ScheduleRefresh(); };
            Volumes.Add(item);
        }

        var usable = Volumes.Count(v => v.JournalEnabled);
        StatusText = usable > 0
            ? $"发现 {Volumes.Count} 个 NTFS 分区, 其中 {usable} 个启用了 USN 日志"
            : "未找到启用 USN 日志的 NTFS 分区";
    }

    private void RefreshVolumeCounts()
    {
        foreach (var v in Volumes)
        {
            v.RecordCount = _store.CountByVolume.GetValueOrDefault(v.DriveLetter);
            v.RaiseCountChanged();
        }
    }

    private void RefreshOperationCounts()
    {
        foreach (var o in Operations)
        {
            o.Count = _store.CountByOperation.GetValueOrDefault(o.Operation.DisplayName);
            o.RaiseCountChanged();
        }
    }

    private void UpdateTimeBounds()
    {
        if (_store.TotalCount == 0)
        {
            TimeRangeText = "";
            return;
        }
        var from = DateTime.FromFileTimeUtc(_store.EarliestTicks).ToLocalTime();
        var to = DateTime.FromFileTimeUtc(_store.LatestTicks).ToLocalTime();
        TimeRangeText = $"数据时间范围: {from:yyyy-MM-dd HH:mm} → {to:yyyy-MM-dd HH:mm}";

        // 扫描完把日期/时刻框填成实际数据范围 —— 空的输入框会让人误以为是坏的,
        // 而且填上之后用户拖动日期就能直观地缩小范围。
        // 用 _suppressQuickRange 避免又被 QuickRange 的回调覆盖。
        _suppressQuickRange = true;
        DateFrom = from;
        DateTo = to;
        _suppressQuickRange = false;

        QuickRangeIndex = 0;   // 下拉框显示「不限」—— 当前范围就是全量
        SyncTimeBoxes?.Invoke();
    }

    /// <summary>由 View 提供: 把 DateFrom/DateTo 的时刻部分同步到两个文本框。</summary>
    public Action? SyncTimeBoxes { get; set; }

    // ---------------------------------------------------------------- 筛选

    private void ScheduleRefresh()
    {
        // 去抖: 连续输入时不要每次都跑全量筛选
        _debounce ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(180),
            DispatcherPriority.Background,
            (_, _) =>
            {
                _debounce!.Stop();
                if (IsScanning) return;
                ApplyFilterCore();
            },
            Application.Current.Dispatcher);
        _debounce.Stop();
        _debounce.Start();
    }

    public void ApplyFilterNow()
    {
        _debounce?.Stop();
        ApplyFilterCore();
    }

    private void ApplyFilterCore()
    {
        if (_store.TotalCount == 0)
        {
            Rows.NotifyReset();
            ResultSummary = "尚未扫描";
            return;
        }

        if (IsSorting) return;   // 排序进行中, 等它读完再筛, 避免读半成品视图

        var filter = new UsnFilter
        {
            FileNameContains = FileNameSearch,
            PathContains = PathSearch,
            From = DateFrom,
            To = DateTo,
        };

        foreach (var v in Volumes.Where(v => v.IsSelected))
            filter.Volumes.Add(v.DriveLetter);

        foreach (var o in Operations.Where(o => o.IsSelected))
            filter.Operations.Add(o.Operation);

        try
        {
            var (count, ms, truncated) = _store.ApplyFilter(filter);
            _store.InvalidateSortCache();   // _view 已重建, 文件夹排序键缓存失效

            // 扫描后的默认视图: 按时间降序 (最新在前)。
            // USN 日志本身就是时间序的, 但这个排序保证了徽标状态与表格实际顺序一致。
            _sortColumn = UsnStore.SortColumn.Time;
            _sortAscending = false;
            _store.SortView(_sortColumn, _sortAscending);

            Rows.NotifyReset();

            if (truncated)
            {
                // 绝不静默截断 —— 用户必须知道还有多少条没显示
                ResultSummary = $"显示 {count:N0} 条 (已达上限, 实际匹配更多) / 共 {_store.TotalCount:N0} 条";
            }
            else
            {
                ResultSummary = count == _store.TotalCount
                    ? $"共 {_store.TotalCount:N0} 条记录"
                    : $"筛出 {count:N0} / {_store.TotalCount:N0} 条";
            }

            var mftTotal = Volumes.Where(v => _store.HasResolver(v.DriveLetter))
                                  .Sum(v => _store.GetResolver(v.DriveLetter)?.MftEntryCount ?? 0);
            TimingText = $"筛选 {ms}ms · MFT 索引 {mftTotal:N0} 项";

            OnPropertyChanged(nameof(ActiveFilterText));
            OnPropertyChanged(nameof(HasData));
            OnPropertyChanged(nameof(SortText));
        }
        catch (Exception ex)
        {
            StatusText = $"筛选出错: {ex.Message}";
        }
    }

    public void ClearAllFilters()
    {
        _suppressQuickRange = true;
        FileNameSearch = "";
        PathSearch = "";
        ExcludeDirectories = false;
        QuickRangeIndex = 0;

        // 时间回到「全量数据范围」而不是留空 —— 留空会让日期框看起来是坏的
        if (_store.TotalCount > 0)
        {
            DateFrom = DateTime.FromFileTimeUtc(_store.EarliestTicks).ToLocalTime();
            DateTo = DateTime.FromFileTimeUtc(_store.LatestTicks).ToLocalTime();
        }
        else
        {
            DateFrom = null;
            DateTo = null;
        }
        _suppressQuickRange = false;
        SyncTimeBoxes?.Invoke();

        foreach (var v in Volumes) v.IsSelected = v.JournalEnabled;
        foreach (var o in Operations) o.IsSelected = false;

        ApplyFilterNow();
    }

    /// <summary>只勾选某一种操作类型</summary>
    public void SelectOnlyOperation(OperationFilterItem item)
    {
        foreach (var o in Operations)
            o.IsSelected = ReferenceEquals(o, item);
        ApplyFilterNow();
    }

    public void SelectAllOperations()
    {
        foreach (var o in Operations) o.IsSelected = false;
        ApplyFilterNow();
    }

    // ---------------------------------------------------------------- 导出

    /// <summary>把当前筛选结果导出为 CSV。</summary>
    public async Task<(bool ok, string message)> ExportCsvAsync(string path, bool fullPath = true)
    {
        if (_store.ViewCount == 0)
            return (false, "当前没有可导出的记录");

        var count = _store.ViewCount;
        try
        {
            await Task.Run(() =>
            {
                using var w = new StreamWriter(path, false, new System.Text.UTF8Encoding(true));
                w.WriteLine("时间,分区,操作,文件名,所在文件夹,完整路径,USN,MFT记录号,属性,原因位图");
                for (int i = 0; i < count; i++)
                {
                    var e = _store.At(i);
                    var row = new UsnRow(_store, _store.View[i]);
                    w.Write(Csv(row.TimeText)); w.Write(',');
                    w.Write(Csv(e.Volume)); w.Write(',');
                    w.Write(Csv(row.ReasonText)); w.Write(',');
                    w.Write(Csv(e.Name)); w.Write(',');
                    w.Write(Csv(row.Folder)); w.Write(',');
                    w.Write(Csv(fullPath ? row.FullPath : "")); w.Write(',');
                    w.Write(e.Usn); w.Write(',');
                    w.Write($"0x{e.FileId:X}"); w.Write(',');
                    w.Write(row.KindText); w.Write(',');
                    w.Write(row.ReasonHex);
                    w.WriteLine();
                }
            });
            return (true, $"已导出 {count:N0} 条到 {path}");
        }
        catch (Exception ex)
        {
            return (false, $"导出失败: {ex.Message}");
        }
    }

    private static string Csv(string? s)
    {
        s ??= "";
        // 字段含逗号/引号/换行时按 RFC4180 加引号转义
        if (s.Contains(',') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    // ---------------------------------------------------------------- INotifyPropertyChanged

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? n = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}
