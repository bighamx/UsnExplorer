using System.Collections.ObjectModel;
using System.Diagnostics;
using UsnExplorer.Interop;

namespace UsnExplorer.Core;

/// <summary>
/// 已加载记录的内存存储 + 筛选执行。
///
/// 记录以 List&lt;UsnEntry&gt; 保存 (值类型连续存储); 筛选结果另存一份索引列表,
/// 这样 DataGrid 虚拟化只需按索引取行, 不必复制记录本身。
/// </summary>
public sealed class UsnStore
{
    private readonly List<UsnEntry> _all = new(1 << 20);
    /// <summary>当前视图的索引。排序时整个换引用 (双缓冲), 不原地改动。</summary>
    private List<int> _view = new(1 << 16);
    private readonly Dictionary<string, PathResolver> _resolvers = new(StringComparer.OrdinalIgnoreCase);

    // 每条记录解析后的路径缓存 —— 懒解析, 按需生成后存回
    private readonly Dictionary<int, string> _pathCache = new();

    public IReadOnlyList<UsnEntry> All => _all;
    public IReadOnlyList<int> View => _view;

    public int TotalCount => _all.Count;
    public int ViewCount => _view.Count;

    public long EarliestTicks { get; private set; } = long.MaxValue;
    public long LatestTicks { get; private set; } = long.MinValue;

    public Dictionary<string, int> CountByVolume { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> CountByOperation { get; } = new();

    public void Clear()
    {
        _all.Clear();
        _view.Clear();
        _pathCache.Clear();
        _displayFolderCache.Clear();
        _resolvers.Clear();
        CountByVolume.Clear();
        CountByOperation.Clear();
        EarliestTicks = long.MaxValue;
        LatestTicks = long.MinValue;
    }

    public void SetResolver(string volume, PathResolver resolver) => _resolvers[volume] = resolver;

    public bool HasResolver(string volume) => _resolvers.ContainsKey(volume);

    public PathResolver? GetResolver(string volume) =>
        _resolvers.TryGetValue(volume, out var r) ? r : null;

    /// <summary>追加一条记录 (读取线程调用)。</summary>
    public void Add(in UsnEntry e)
    {
        _all.Add(e);
        CountByVolume[e.Volume] = CountByVolume.GetValueOrDefault(e.Volume) + 1;

        foreach (var op in UsnOperation.All)
        {
            if (op.Matches(e.Reason))
                CountByOperation[op.DisplayName] = CountByOperation.GetValueOrDefault(op.DisplayName) + 1;
        }

        if (e.FileTimeUtc < EarliestTicks) EarliestTicks = e.FileTimeUtc;
        if (e.FileTimeUtc > LatestTicks) LatestTicks = e.FileTimeUtc;
    }

    /// <summary>
    /// 解析某条记录的完整路径 (带缓存)。已删除的中间目录会退化成 &lt;已删除&gt; 占位。
    /// </summary>
    public string GetPath(int index)
    {
        if (_pathCache.TryGetValue(index, out var cached))
            return cached;

        var e = _all[index];
        var resolver = GetResolver(e.Volume);
        string path = resolver is null
            ? $"{e.Volume}\\<未解析>\\{e.Name}"
            : resolver.Resolve(e.FileId, e.ParentId, e.Name);

        _pathCache[index] = path;
        return path;
    }

    /// <summary>
    /// 该记录所在的文件夹 (完整路径, 不含文件名)。
    /// 早期版本这里只保留「盘符 + 末两级」做所谓简化 —— 那是错的, 会让人误以为
    /// 文件在浅层目录。现在返回真实完整路径; 列宽不够时由 UI 侧做截断提示。
    /// </summary>
    public string GetDisplayFolder(int index)
    {
        // 按原始记录索引缓存: 切片会分配新字符串, 走文件夹排序时是 260 万次分配,
        // 缓存后重复排序几乎零成本
        if (_displayFolderCache.TryGetValue(index, out var cached))
            return cached;

        var e = _all[index];
        var path = GetPath(index);

        string result;
        if (path.Length > e.Name.Length && path.EndsWith(e.Name, StringComparison.Ordinal))
            result = path[..^(e.Name.Length + 1)];   // 去掉末尾文件名
        else
            result = path;                            // 名字对不上就返回路径本身

        _displayFolderCache[index] = result;
        return result;
    }

    private readonly Dictionary<int, string> _displayFolderCache = new();

    /// <summary>
    /// 应用筛选, 重建 _view 索引列表。返回 (通过条数, 耗时ms, 是否被上限截断)。
    /// 筛选在索引层做, 不复制记录 —— _view 只是 int 索引, 千万级也只有几十 MB。
    /// </summary>
    public (int count, long elapsedMs, bool truncated) ApplyFilter(
        UsnFilter filter, int maxResults = 20_000_000)
    {
        var sw = Stopwatch.StartNew();
        _view.Clear();
        bool truncated = false;

        for (int i = 0; i < _all.Count; i++)
        {
            var e = _all[i];

            // 便宜的判定优先 (不碰路径解析)
            if (filter.Volumes.Count > 0 && !filter.Volumes.Contains(e.Volume))
                continue;

            if (filter.From is not null || filter.To is not null)
            {
                var t = e.LocalTime;
                if (filter.From is not null && t < filter.From.Value) continue;
                if (filter.To is not null && t > filter.To.Value) continue;
            }

            if (filter.Operations.Count > 0)
            {
                bool hit = false;
                foreach (var op in filter.Operations)
                {
                    if (op.Matches(e.Reason)) { hit = true; break; }
                }
                if (!hit) continue;
            }

            if (filter.ExcludeDirectories && (e.FileAttributes & 0x10) != 0)
                continue;

            // 文件名关键字 (无需路径解析)
            if (!string.IsNullOrWhiteSpace(filter.FileNameContains))
            {
                if (e.Name.IndexOf(filter.FileNameContains.Trim(),
                        StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }

            // 路径关键字最后做 —— 只有前面都通过才付出路径解析的代价
            if (!string.IsNullOrWhiteSpace(filter.PathContains))
            {
                if (GetPath(i).IndexOf(filter.PathContains.Trim(),
                        StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
            }

            _view.Add(i);
            if (_view.Count >= maxResults)
            {
                truncated = true;
                break;
            }
        }

        _folderKeys = null;   // _view 内容已变, 与它平行的文件夹键数组必须丢弃
        sw.Stop();
        return (_view.Count, sw.ElapsedMilliseconds, truncated);
    }

    public UsnEntry At(int viewIndex) => _all[_view[viewIndex]];

    // -----------------------------------------------------------------------
    // 排序
    // -----------------------------------------------------------------------

    /// <summary>可排序的列</summary>
    public enum SortColumn { Time, Volume, Operation, Name, Folder, Usn }

    /// <summary>
    /// 文件夹排序键, 与 _view **位置一一对应** (_folderKeys[i] 是 _view[i] 的文件夹)。
    ///
    /// 必须平行维护、随 _view 一起置换, 而不是按位置缓存后按长度判断复用 ——
    /// 排序改变了顺序但没改变长度, 那种缓存会返回错位的键, 排出错误结果。
    /// 筛选重建 _view 时置 null。
    /// </summary>
    private string[]? _folderKeys;

    /// <summary>
    /// 对当前筛选结果排序。
    ///
    /// 关键: 只对 int 索引排序, 绝不触碰行对象。
    /// 让 DataGrid 走 SortMemberPath 会经过 ListCollectionView, 那条路必须枚举全部条目 ——
    /// 对我们按需创建行的索引器来说就是物化几百万个 UsnRow + 反射读属性, 直接卡死。
    /// 这里改为抽出键数组后用 Array.Sort(keys, indices) 排序, 无委托调用, 快一个数量级。
    /// </summary>
    public (long elapsedMs, bool sorted) SortView(
        SortColumn column, bool ascending,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        int n = _view.Count;
        if (n < 2) return (0, true);

        var sw = Stopwatch.StartNew();

        // 本地位置数组 —— 排序它, 而不是 _view 本身
        var idx = new int[n];
        for (int i = 0; i < n; i++) idx[i] = i;

        switch (column)
        {
            case SortColumn.Folder:
                {
                    // 唯一需要路径解析的列。
                    // 关键: 必须排序**副本**。Array.Sort(keys, idx) 是原地排序, 直接排
                    // 缓存数组会把它打乱 —— 既污染缓存, 又让后面的 idx 置换变成双重置换,
                    // 结果就是 _view 排错 (表现为降序后大范围逆序)。
                    var cached = EnsureFolderKeys(progress, ct);
                    if (ct.IsCancellationRequested) return (sw.ElapsedMilliseconds, false);

                    var keys = (string[])cached.Clone();
                    Array.Sort(keys, idx, StringComparer.OrdinalIgnoreCase);

                    // 用同一个置换把缓存数组重排到新顺序, 保持与 _view 平行
                    var reordered = new string[n];
                    for (int i = 0; i < n; i++) reordered[i] = cached[idx[i]];
                    _pendingFolderReorder = reordered;
                    break;
                }
            case SortColumn.Name:
                {
                    var keys = new string[n];
                    for (int i = 0; i < n; i++) keys[i] = _all[_view[i]].Name;
                    Array.Sort(keys, idx, StringComparer.OrdinalIgnoreCase);
                    break;
                }
            case SortColumn.Volume:
                {
                    var keys = new string[n];
                    for (int i = 0; i < n; i++) keys[i] = _all[_view[i]].Volume;
                    Array.Sort(keys, idx, StringComparer.OrdinalIgnoreCase);
                    break;
                }
            case SortColumn.Operation:
                {
                    // 按"主要操作"的优先级序号排, 让同类型记录聚在一起
                    var keys = new int[n];
                    for (int i = 0; i < n; i++)
                        keys[i] = OperationOrdinal(_all[_view[i]].Reason);
                    Array.Sort(keys, idx);
                    break;
                }
            case SortColumn.Usn:
                {
                    var keys = new long[n];
                    for (int i = 0; i < n; i++) keys[i] = _all[_view[i]].Usn;
                    Array.Sort(keys, idx);
                    break;
                }
            case SortColumn.Time:
            default:
                {
                    var keys = new long[n];
                    for (int i = 0; i < n; i++) keys[i] = _all[_view[i]].FileTimeUtc;
                    Array.Sort(keys, idx);
                    break;
                }
        }

        if (ct.IsCancellationRequested) return (sw.ElapsedMilliseconds, false);

        // 双缓冲: 先把结果装进**新**列表, 最后一次性换引用。
        // 单次引用赋值是原子的, 所以 UI 线程要么看到旧的完整数据, 要么看到新的完整数据,
        // 绝不会读到排到一半的状态 —— 也就不必在排序前清空表格 (那样用户会盯着空表等几秒)。
        var sortedView = new int[n];
        for (int i = 0; i < n; i++) sortedView[i] = _view[idx[i]];

        // 文件夹列: 上面的 switch 已经用 idx 把缓存重排好了, 不能再用 idx 置换一次
        var sortedFolders = _pendingFolderReorder;
        _pendingFolderReorder = null;

        if (!ascending)
        {
            Array.Reverse(sortedView);
            if (sortedFolders is not null) Array.Reverse(sortedFolders);
        }

        var newView = new List<int>(n);
        for (int i = 0; i < n; i++) newView.Add(sortedView[i]);

        _view = newView;                 // 原子换引用
        if (sortedFolders is not null) _folderKeys = sortedFolders;

        sw.Stop();
        return (sw.ElapsedMilliseconds, true);
    }

    /// <summary>文件夹列排序时算好的重排结果, 交给统一的收尾逻辑应用。</summary>
    private string[]? _pendingFolderReorder;

    /// <summary>取/建与 _view 平行的文件夹键数组 (路径解析贵, 所以只建一次)。</summary>
    private string[] EnsureFolderKeys(IProgress<string>? progress, CancellationToken ct)
    {
        if (_folderKeys is not null && _folderKeys.Length == _view.Count)
            return _folderKeys;

        int n = _view.Count;
        var keys = new string[n];
        int lastReport = 0;
        for (int i = 0; i < n; i++)
        {
            if (ct.IsCancellationRequested) break;
            keys[i] = GetDisplayFolder(_view[i]);   // 内部还有一层按原始索引的缓存

            if (progress is not null && i - lastReport >= 200_000)
            {
                lastReport = i;
                progress.Report($"解析路径 {i:N0} / {n:N0}");
            }
        }

        _folderKeys = keys;
        return keys;
    }

    /// <summary>筛选条件变化后调用 —— _view 内容已换, 文件夹键数组必须丢弃。</summary>
    public void InvalidateSortCache() => _folderKeys = null;

    /// <summary>把 reason 位图映射成一个排序用的序号 (与 UI 里操作类型的展示顺序一致)</summary>
    private static int OperationOrdinal(uint reason) => reason switch
    {
        _ when UsnOperation.Create.Matches(reason) => 0,
        _ when UsnOperation.Delete.Matches(reason) => 1,
        _ when UsnOperation.Rename.Matches(reason) => 2,
        _ when UsnOperation.Write.Matches(reason) => 3,
        _ when UsnOperation.Security.Matches(reason) => 4,
        _ when UsnOperation.StreamChange.Matches(reason) => 5,
        _ when UsnOperation.HardLink.Matches(reason) => 6,
        _ when UsnOperation.AttributeChange.Matches(reason) => 7,
        _ when UsnOperation.Transacted.Matches(reason) => 8,
        _ => 9,
    };

    /// <summary>
    /// 取每个可见行的文件夹路径作为排序键。
    /// 注意不能在排序后 bump 版本号 —— 那会把自己刚建立的缓存废掉,
    /// 导致第二次按同列排序重新走一遍 260 万次字符串处理。
    /// 缓存只在 _view 的**内容**变化时失效 (筛选), 顺序变化不影响键的正确性。
    /// </summary>
}
