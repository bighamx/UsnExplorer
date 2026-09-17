using System.Collections;
using System.Globalization;
using System.Windows.Media;
using UsnExplorer.Core;

namespace UsnExplorer.ViewModels;

/// <summary>表格里的一行。由 UsnRowList 按需创建, 只有可见行才会存在。</summary>
public sealed class UsnRow
{
    private readonly UsnStore _store;
    private readonly int _index;   // _store.All 中的下标

    public UsnRow(UsnStore store, int index)
    {
        _store = store;
        _index = index;
        var e = store.All[index];

        Usn = e.Usn;
        FileId = e.FileId;
        ParentId = e.ParentId;
        Name = e.Name;
        Volume = e.Volume;
        ReasonRaw = e.Reason;
        SourceInfo = e.SourceInfo;
        Attributes = e.FileAttributes;
        LocalTime = e.LocalTime;
        UtcTime = e.UtcTime;
        ReasonText = UsnOperation.DescribeReason(e.Reason);

        // 路径是懒解析的 —— 解析要回溯 MFT, 对百万行全量做会卡住 UI。
        // 只在这一行真的要显示时才解析。
        _pathLazy = new Lazy<string>(() => store.GetPath(index));
        _folderLazy = new Lazy<string>(() => store.GetDisplayFolder(index));
    }

    private readonly Lazy<string> _pathLazy;
    private readonly Lazy<string> _folderLazy;

    public long Usn { get; }
    public ulong FileId { get; }
    public ulong ParentId { get; }
    public string Name { get; }
    public string Volume { get; }
    public uint ReasonRaw { get; }
    public uint SourceInfo { get; }
    public uint Attributes { get; }
    public DateTime LocalTime { get; }
    public DateTime UtcTime { get; }
    public string ReasonText { get; }

    public string FullPath => _pathLazy.Value;
    public string Folder => _folderLazy.Value;

    public string TimeText => LocalTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public bool IsDirectory => (Attributes & 0x10) != 0;

    public string KindText => IsDirectory ? "目录" : "文件";

    /// <summary>主要操作类型 —— 用于行左侧的色条和类型胶囊。</summary>
    public UsnOperation PrimaryOperation
    {
        get
        {
            // 按语义优先级挑一个"最主要的"操作
            foreach (var op in new[] { UsnOperation.Create, UsnOperation.Delete,
                                       UsnOperation.Rename, UsnOperation.Write,
                                       UsnOperation.Security, UsnOperation.StreamChange,
                                       UsnOperation.HardLink, UsnOperation.AttributeChange,
                                       UsnOperation.Transacted, UsnOperation.Close })
            {
                if (op.Matches(ReasonRaw))
                    return op;
            }
            return UsnOperation.Close;
        }
    }

    // 静态冻结画刷: 避免每次行渲染都 new Brush (WPF 画刷有线程亲和性,
    // 每行新建还会把渲染线程压垮)
    private static readonly Brush BrushCreate = Freeze(Color.FromRgb(0x22, 0xC5, 0x5E));
    private static readonly Brush BrushDelete = Freeze(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly Brush BrushRename = Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly Brush BrushWrite = Freeze(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly Brush BrushAttr = Freeze(Color.FromRgb(0x8B, 0x5C, 0xF6));
    private static readonly Brush BrushSecurity = Freeze(Color.FromRgb(0xEC, 0x48, 0x99));
    private static readonly Brush BrushStream = Freeze(Color.FromRgb(0x14, 0xB8, 0xA6));
    private static readonly Brush BrushHardLink = Freeze(Color.FromRgb(0xA8, 0x55, 0xF7));
    private static readonly Brush BrushTransacted = Freeze(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly Brush BrushClose = Freeze(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private static Brush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();          // 冻结后可跨线程共享, 且渲染更快
        return b;
    }

    public Brush OperationBrush => PrimaryOperation switch
    {
        var op when op == UsnOperation.Create => BrushCreate,
        var op when op == UsnOperation.Delete => BrushDelete,
        var op when op == UsnOperation.Rename => BrushRename,
        var op when op == UsnOperation.Write => BrushWrite,
        var op when op == UsnOperation.AttributeChange => BrushAttr,
        var op when op == UsnOperation.Security => BrushSecurity,
        var op when op == UsnOperation.StreamChange => BrushStream,
        var op when op == UsnOperation.HardLink => BrushHardLink,
        var op when op == UsnOperation.Transacted => BrushTransacted,
        _ => BrushClose,
    };

    /// <summary>路径不完整 (父目录已删除) 时提示</summary>
    public bool PathIncomplete
    {
        get
        {
            var p = FullPath;
            return p.Contains("<已删除>") || p.Contains("<未解析>");
        }
    }

    public string FileIdHex => $"0x{FileId:X}";
    public string ReasonHex => $"0x{ReasonRaw:X8}";
}

/// <summary>
/// DataGrid 数据源。实现 IList 让 WPF 的虚拟化生效, 且 indexer 按需构造 UsnRow ——
/// 250 万行不会产生 250 万个对象, 只有屏幕上那几十行 + 缓冲区会被创建。
/// </summary>
public sealed class UsnRowList : IList, IList<UsnRow>, System.Collections.Specialized.INotifyCollectionChanged
{
    private readonly UsnStore _store;

    public UsnRowList(UsnStore store) => _store = store;

    public event System.Collections.Specialized.NotifyCollectionChangedEventHandler? CollectionChanged;

    public void NotifyReset() =>
        CollectionChanged?.Invoke(this, new System.Collections.Specialized.NotifyCollectionChangedEventArgs(
            System.Collections.Specialized.NotifyCollectionChangedAction.Reset));

    public int Count => _store.ViewCount;

    public UsnRow this[int index] => new(_store, _store.View[index]);

    UsnRow IList<UsnRow>.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    object? IList.this[int index]
    {
        get => this[index];
        set => throw new NotSupportedException();
    }

    public bool Contains(object? value) => value is UsnRow r && Contains(r);

    public int IndexOf(object? value) => value is UsnRow r ? IndexOf(r) : -1;

    public bool Contains(UsnRow item) => false;   // 行对象不持有自身位置

    public int IndexOf(UsnRow item) => -1;        // 仅需支持 WPF 内部探测

    bool ICollection<UsnRow>.Remove(UsnRow item) => throw new NotSupportedException();

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++)
            array.SetValue(this[i], index + i);
    }

    public IEnumerator<UsnRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // --- 只读集合, 以下全部不支持 ---
    public void Add(UsnRow item) => throw new NotSupportedException();
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public void Insert(int index, UsnRow item) => throw new NotSupportedException();
    public void Insert(int index, object? value) => throw new NotSupportedException();

    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(UsnRow[] array, int arrayIndex) => throw new NotSupportedException();
}
