using UsnExplorer.Interop;

namespace UsnExplorer.Core;

/// <summary>
/// 筛选条件。UI 上所有筛选都归结到这里, 再编译成对 UsnEntry 的判定。
/// </summary>
public sealed class UsnFilter
{
    /// <summary>只在「文件名」里匹配的关键字 (不区分大小写)。空 = 不限。</summary>
    public string? FileNameContains { get; set; }

    /// <summary>只在「完整路径(含文件夹)」里匹配的关键字。空 = 不限。</summary>
    public string? PathContains { get; set; }

    /// <summary>选中的分区, 如 {"C:", "D:"}。空集 = 不限。</summary>
    public HashSet<string> Volumes { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>选中的操作类型。空集 = 不限。</summary>
    public HashSet<UsnOperation> Operations { get; } = new();

    /// <summary>时间下限 (本地时间, 含)。null = 不限。</summary>
    public DateTime? From { get; set; }

    /// <summary>时间上限 (本地时间, 含)。null = 不限。</summary>
    public DateTime? To { get; set; }

    /// <summary>最小文件大小 (字节)。0 = 不限。仅对有大小信息的记录有意义。</summary>
    public long MinSize { get; set; }

    /// <summary>只显示对可读文件的变更, 过滤掉纯目录/系统元数据噪音。</summary>
    public bool ExcludeDirectories { get; set; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(FileNameContains)
        && string.IsNullOrWhiteSpace(PathContains)
        && Volumes.Count == 0
        && Operations.Count == 0
        && From is null
        && To is null;

    public int ActiveCount
    {
        get
        {
            int n = 0;
            if (!string.IsNullOrWhiteSpace(FileNameContains)) n++;
            if (!string.IsNullOrWhiteSpace(PathContains)) n++;
            if (Volumes.Count > 0) n++;
            if (Operations.Count > 0) n++;
            if (From is not null || To is not null) n++;
            if (ExcludeDirectories) n++;
            return n;
        }
    }

    /// <summary>判定单条记录是否通过。path 为该记录的完整路径 (可能为 null)。</summary>
    public bool Matches(UsnEntry e, string? path)
    {
        // 文件名关键字: 只比对 FileName
        if (!string.IsNullOrWhiteSpace(FileNameContains))
        {
            if (e.Name.IndexOf(FileNameContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        // 路径关键字: 只比对完整路径。需要路径, 所以放最后 (调用方按需先做便宜判定)
        if (!string.IsNullOrWhiteSpace(PathContains))
        {
            if (path is null ||
                path.IndexOf(PathContains.Trim(), StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }

        // 分区
        if (Volumes.Count > 0 && !Volumes.Contains(e.Volume))
            return false;

        // 时间范围 (用本地时间比较, 与 UI 显示一致)
        if (From is not null || To is not null)
        {
            var t = e.LocalTime;
            if (From is not null && t < From.Value) return false;
            if (To is not null && t > To.Value) return false;
        }

        // 操作类型
        if (Operations.Count > 0 && !Operations.Any(op => op.Matches(e.Reason)))
            return false;

        // 目录过滤
        if (ExcludeDirectories && (e.FileAttributes & 0x10) != 0)
            return false;

        return true;
    }

    public UsnFilter Clone()
    {
        var f = new UsnFilter
        {
            FileNameContains = FileNameContains,
            PathContains = PathContains,
            From = From,
            To = To,
            MinSize = MinSize,
            ExcludeDirectories = ExcludeDirectories,
        };
        foreach (var v in Volumes) f.Volumes.Add(v);
        foreach (var o in Operations) f.Operations.Add(o);
        return f;
    }
}

/// <summary>
/// 面向 UI 的操作类型。USN 的 reason 位图很细 (23 个位), 直接铺给用户太碎,
/// 这里归成人类能用的粒度, 每个类别覆盖若干原始位。
/// </summary>
public sealed class UsnOperation
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required uint Mask { get; init; }
    public required string Description { get; init; }
    public string Accent { get; init; } = "#6B7280";

    public bool Matches(uint reason) => (reason & Mask) != 0;

    public static readonly UsnOperation Create = new()
    {
        Name = nameof(Create),
        DisplayName = "创建",
        Mask = (uint)UsnReason.FileCreate,
        Description = "文件或目录被创建",
        Accent = "#22C55E",
    };

    public static readonly UsnOperation Delete = new()
    {
        Name = nameof(Delete),
        DisplayName = "删除",
        Mask = (uint)UsnReason.FileDelete,
        Description = "文件或目录被删除",
        Accent = "#EF4444",
    };

    public static readonly UsnOperation Rename = new()
    {
        Name = nameof(Rename),
        DisplayName = "重命名/移动",
        Mask = (uint)(UsnReason.RenameOldName | UsnReason.RenameNewName),
        Description = "改名或移动位置 (成对出现: 旧名 + 新名)",
        Accent = "#F59E0B",
    };

    public static readonly UsnOperation Write = new()
    {
        Name = nameof(Write),
        DisplayName = "写入/修改",
        Mask = (uint)(UsnReason.DataOverwrite | UsnReason.DataExtend | UsnReason.DataTruncation
                      | UsnReason.NamedDataOverwrite | UsnReason.NamedDataExtend
                      | UsnReason.NamedDataTruncation),
        Description = "文件内容被写入、追加或截断",
        Accent = "#3B82F6",
    };

    public static readonly UsnOperation AttributeChange = new()
    {
        Name = nameof(AttributeChange),
        DisplayName = "属性变更",
        Mask = (uint)(UsnReason.BasicInfoChange | UsnReason.IndexableChange
                      | UsnReason.CompressionChange | UsnReason.EncryptionChange
                      | UsnReason.ObjectIdChange | UsnReason.ReparsePointChange
                      | UsnReason.IntegrityChange),
        Description = "基本属性、压缩、加密、重解析点等变更",
        Accent = "#8B5CF6",
    };

    public static readonly UsnOperation Security = new()
    {
        Name = nameof(Security),
        DisplayName = "权限变更",
        Mask = (uint)(UsnReason.SecurityChange | UsnReason.EaChange),
        Description = "ACL / 安全描述符 / 扩展属性被修改",
        Accent = "#EC4899",
    };

    public static readonly UsnOperation StreamChange = new()
    {
        Name = nameof(StreamChange),
        DisplayName = "数据流变更",
        Mask = (uint)UsnReason.StreamChange,
        Description = "命名的备用数据流 (ADS) 被修改",
        Accent = "#14B8A6",
    };

    public static readonly UsnOperation HardLink = new()
    {
        Name = nameof(HardLink),
        DisplayName = "硬链接变更",
        Mask = (uint)UsnReason.HardLinkChange,
        Description = "硬链接被添加或删除",
        Accent = "#A855F7",
    };

    public static readonly UsnOperation Transacted = new()
    {
        Name = nameof(Transacted),
        DisplayName = "事务变更",
        Mask = (uint)UsnReason.TransactedChange,
        Description = "经 TxF 事务提交的变更",
        Accent = "#64748B",
    };

    public static readonly UsnOperation Close = new()
    {
        Name = nameof(Close),
        DisplayName = "关闭句柄",
        Mask = (uint)UsnReason.Close,
        Description = "文件句柄关闭 —— 记录真正的写入结束时刻",
        Accent = "#9CA3AF",
    };

    /// <summary>全部类型, UI 按此顺序展示。</summary>
    public static readonly UsnOperation[] All =
    [
        Create, Delete, Rename, Write, AttributeChange,
        Security, StreamChange, HardLink, Transacted, Close,
    ];

    public override string ToString() => DisplayName;

    /// <summary>把一条记录的 reason 位图翻译成可读标签 (可能多个, 用 + 连接)。</summary>
    public static string DescribeReason(uint reason)
    {
        if (reason == 0) return "无";

        var parts = new List<string>(4);
        // 按语义优先级排, 避免出现 "关闭句柄+创建" 这种别扭顺序
        foreach (var op in new[] { Create, Rename, Delete, Write, Security, StreamChange,
                                    HardLink, AttributeChange, Transacted, Close })
        {
            if (op.Matches(reason))
                parts.Add(op.DisplayName);
        }
        return parts.Count > 0 ? string.Join(" + ", parts) : $"0x{reason:X8}";
    }
}
