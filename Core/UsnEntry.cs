using System.Runtime.InteropServices;

namespace UsnExplorer.Core;

/// <summary>
/// 一条 USN 记录的紧凑表示。刻意做成 struct 并让字符串默认不填充 ——
/// List&lt;T&gt; 里 T 是值类型时元素连续存放, 千万级也只有 ~400MB,
/// 引用类型会变成几百万个堆对象直接把 GC 拖死。
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct UsnEntry
{
    /// <summary>USN 序号 (块内单调递增, 用于排序和定位)</summary>
    public long Usn;

    /// <summary>MFT 记录号 = FileReferenceNumber 的低 48 位</summary>
    public ulong FileId;

    /// <summary>父目录 MFT 记录号。路径解析靠它向上回溯。</summary>
    public ulong ParentId;

    /// <summary>完整路径。由 PathResolver 惰性填充; 未解析时为 null。</summary>
    public string? ResolvedPath;

    /// <summary>该记录的文件名 (来自 USN 记录本身, 长度通常 &lt; 100 字符)</summary>
    public string Name;

    public uint Reason;
    public uint FileAttributes;
    public uint SourceInfo;

    /// <summary>
    /// 变更时刻, 保留 USN 记录的原始 FILETIME (自 1601-01-01 起的 100ns)。
    /// 注意不要塞进 DateTime.Ticks —— 那是自 0001-01-01 起算, 两者差 1600 年。
    /// 取值请用 LocalTime / UtcTime。
    /// </summary>
    public long FileTimeUtc;

    /// <summary>所属分区, 如 "C:"。用于分区筛选。</summary>
    public string Volume;

    /// <summary>UTC 时刻 (经 DateTime.FromFileTimeUtc 正确换算)</summary>
    public DateTime UtcTime => DateTime.FromFileTimeUtc(FileTimeUtc).ToUniversalTime();

    /// <summary>本地时刻, 用于显示和日期范围筛选</summary>
    public DateTime LocalTime => UtcTime.ToLocalTime();
}

/// <summary>分区信息</summary>
public sealed class VolumeInfo
{
    public required string DriveLetter { get; init; }        // 如 "C:"
    public required string VolumePath { get; init; }         // 如 @"\\.\C:"
    public required string DisplayName { get; init; }        // 如 "C: (系统)"
    public string? FileSystem { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }

    /// <summary>该卷的 USN 日志是否可用</summary>
    public bool JournalEnabled { get; set; }

    /// <summary>日志不可用时的原因</summary>
    public string? JournalError { get; set; }

    public string SizeText => TotalBytes > 0
        ? $"{FormatBytes(TotalBytes - FreeBytes)} / {FormatBytes(TotalBytes)}"
        : "";

    public override string ToString() => DisplayName;

    internal static string FormatBytes(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }
}

/// <summary>读取进度回调</summary>
public sealed class UsnReadProgress
{
    public string Volume { get; init; } = "";
    public long RecordsRead { get; init; }
    public long TotalUsnSpan { get; init; }
    public long UsnPosition { get; init; }

    public double Fraction => TotalUsnSpan > 0
        ? Math.Clamp((double)UsnPosition / TotalUsnSpan, 0, 1)
        : 0;
}
