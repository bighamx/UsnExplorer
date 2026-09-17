namespace UsnExplorer.Core;

/// <summary>
/// 把 MFT 记录号还原成完整路径。
///
/// 为什么要专门做一层: USN 记录只给出 FileReferenceNumber + ParentFileReferenceNumber,
/// 没有路径。必须靠 MFT 全量枚举建一张 FileId -> (ParentId, Name) 的表, 再向上回溯到根。
/// 回溯要处理两种情况 —— 中间目录已经删除 (表里查不到), 和环 (损坏数据)。
/// </summary>
public sealed class PathResolver
{
    private readonly Dictionary<ulong, (ulong ParentId, string Name)> _mft;
    private readonly string _root;   // 如 "C:"

    // 解析结果缓存 —— 同一目录下大量记录共享父链, 缓存能省掉绝大部分回溯
    private readonly Dictionary<ulong, string> _cache = new(1 << 16);

    /// <summary>MFT 记录号 5 = 根目录, 其自身无父路径</summary>
    private const ulong MftRootFileId = 5;

    public PathResolver(Dictionary<ulong, (ulong ParentId, string Name)> mft, string root)
    {
        _mft = mft;
        _root = root;
        _cache[MftRootFileId] = _root + "\\";
    }

    public int MftEntryCount => _mft.Count;

    /// <summary>
    /// 解析一条记录的完整路径。给出记录自身的名字和父目录 ID。
    /// 找不到父链时返回可读的退化形式, 不抛异常 —— 日志里存在已删除文件是常态。
    /// </summary>
    public string Resolve(ulong fileId, ulong parentId, string name)
    {
        // 父目录路径 (优先走缓存)
        var parentPath = ResolveDirectory(parentId, depth: 0);

        return parentPath.Length == 1 || parentPath.EndsWith('\\')
            ? parentPath + name
            : parentPath + "\\" + name;
    }

    /// <summary>回溯目录路径。depth 用于切断损坏数据造成的环。</summary>
    private string ResolveDirectory(ulong dirId, int depth)
    {
        if (_cache.TryGetValue(dirId, out var cached))
            return cached;

        if (depth > 256)
            return $"{_root}\\<层级过深>\\";     // 环保护

        if (!_mft.TryGetValue(dirId, out var node))
            return $"{_root}\\<已删除>\\";        // 目录已被删除, MFT 表里没有了

        var ancestor = ResolveDirectory(node.ParentId, depth + 1);
        var path = ancestor.EndsWith('\\') ? ancestor + node.Name : ancestor + "\\" + node.Name;

        // 缓存里存目录路径, 末尾补反斜杠, 便于拼接
        _cache[dirId] = path + "\\";
        return _cache[dirId];
    }

    /// <summary>
    /// 只取父目录路径 (不拼文件名), 用于「所在文件夹」列。
    /// </summary>
    public string ResolveParent(ulong parentId)
    {
        var p = ResolveDirectory(parentId, 0);
        return p.Length > 1 ? p.TrimEnd('\\') : p;
    }
}
