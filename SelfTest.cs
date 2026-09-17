using System.IO;
using System.Text;
using UsnExplorer.Core;

namespace UsnExplorer;

/// <summary>
/// 无头自检。用途: 在不起 UI 的前提下验证引擎能真正读到 USN 记录 ——
/// 互操作层 (FSCTL / 结构体布局) 出错时症状是"静默返回 0 条", 光看 UI 很难区分
/// 是没数据还是解析错了。用法: UsnExplorer.exe --selftest [盘符]
/// </summary>
internal static class SelfTest
{
    public static int Run(string[] args)
    {
        var outFile = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("USN_SELFTEST_OUT"))
            ? null
            : Environment.GetEnvironmentVariable("USN_SELFTEST_OUT");

        var sb = new StringBuilder();
        void W(string s = "")
        {
            sb.AppendLine(s);
            if (outFile is null) Console.WriteLine(s);
        }

        try
        {
            W("=== UsnExplorer 引擎自检 ===");
            W();

            UsnReader.Initialize();
            W($"[1] SeBackupPrivilege 启用: {UsnReader.HasBackupPrivilege}");
            W($"    管理员进程: {IsElevated()}");
            W();

            var volumes = UsnReader.EnumerateVolumes();
            W($"[2] 发现 NTFS 卷 {volumes.Count} 个:");
            foreach (var v in volumes)
                W($"    {v.DisplayName,-28} {v.SizeText}");
            W();

            var targets = args.Length > 0 && !args[0].StartsWith("--")
                ? volumes.Where(v => v.DriveLetter.StartsWith(args[0], StringComparison.OrdinalIgnoreCase)).ToList()
                : volumes;

            foreach (var vol in targets)
            {
                W($"--- 卷 {vol.DriveLetter} ---");

                var journal = UsnReader.QueryJournal(vol.VolumePath, out var err, out var handle);
                if (journal is null || handle is null)
                {
                    W($"  [X] 查询日志失败: {err}");
                    W();
                    continue;
                }

                using (handle)
                {
                    var j = journal.Value;
                    W($"  日志 ID      : 0x{j.UsnJournalId:X}");
                    W($"  FirstUsn     : 0x{j.FirstUsn:X}  ({j.FirstUsn:N0})");
                    W($"  NextUsn      : 0x{j.NextUsn:X}  ({j.NextUsn:N0})");
                    W($"  MaximumSize  : {j.MaximumSize / 1048576.0:0.#} MB");
                    W($"  USN 跨度     : {(j.NextUsn - j.FirstUsn) / 1048576.0:0.#} MB");
                    W();

                    // --- 读日志 ---
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var entries = new List<UsnEntry>(200_000);
                    long n;
                    try
                    {
                        n = UsnReader.ReadJournal(vol, handle, j, e => entries.Add(e));
                    }
                    catch (Exception ex)
                    {
                        W($"  [X] 读日志异常: {ex.Message}");
                        W();
                        continue;
                    }
                    sw.Stop();
                    W($"  [OK] 读到 {n:N0} 条记录, 用时 {sw.ElapsedMilliseconds:N0} ms" +
                      (n > 0 ? $" ({n * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):N0} 条/秒)" : ""));
                    W($"       内存占用约 {entries.Count * 72 / 1048576.0:0.#} MB (结构体 + 字符串引用)");

                    if (n == 0)
                    {
                        W("  [!] 零记录 —— 检查日志是否刚被清空");
                        W();
                        continue;
                    }

                    // --- 样本 ---
                    W();
                    W("  最新 8 条:");
                    foreach (var e in entries.TakeLast(8))
                        W($"    {e.LocalTime:yyyy-MM-dd HH:mm:ss}  {e.Volume}  0x{e.Reason:X8}  {UsnOperation.DescribeReason(e.Reason),-14}  {Truncate(e.Name, 44)}");

                    var times = entries.Select(x => x.FileTimeUtc).ToList();
                    var minT = DateTime.FromFileTimeUtc(times.Min()).ToLocalTime();
                    var maxT = DateTime.FromFileTimeUtc(times.Max()).ToLocalTime();
                    W();
                    W($"  时间跨度: {minT:yyyy-MM-dd HH:mm:ss}  ->  {maxT:yyyy-MM-dd HH:mm:ss}");

                    W();
                    W("  操作类型分布:");
                    foreach (var op in UsnOperation.All)
                    {
                        int c = entries.Count(e => op.Matches(e.Reason));
                        if (c > 0)
                            W($"    {op.DisplayName,-10} {c,10:N0}  ({c * 100.0 / n:0.0}%)");
                    }

                    // --- MFT 枚举 + 路径解析 ---
                    W();
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    var mft = UsnReader.EnumerateMft(handle, null, vol.DriveLetter);
                    sw2.Stop();
                    W($"  [OK] MFT 枚举 {mft.Count:N0} 个条目, 用时 {sw2.ElapsedMilliseconds:N0} ms");
                    W($"       字典内存约 {mft.Count * 48 / 1048576.0:0.#} MB (估算)");

                    var resolver = new PathResolver(mft, vol.DriveLetter);
                    W();
                    W("  路径解析样本 (最近 5 条):");
                    foreach (var e in entries.TakeLast(5))
                        W($"    {Truncate(resolver.Resolve(e.FileId, e.ParentId, e.Name), 100)}");

                    // 验证解析成功率: 路径里不该出现 <已删除> 占位才算好
                    int delCount = 0, sampled = 0;
                    for (int i = entries.Count - 1; i >= 0 && sampled < 2000; i--, sampled++)
                    {
                        if (resolver.Resolve(entries[i].FileId, entries[i].ParentId, entries[i].Name)
                                .Contains("<已删除>"))
                            delCount++;
                    }
                    W();
                    W("  路径解析: " + sampled + " 条抽样中 " + (sampled - delCount) + " 条完整 " +
                      $"({(sampled - delCount) * 100.0 / Math.Max(1, sampled):0.#}%), {delCount} 条含已删除父目录");

                    // --- 筛选引擎验证 ---
                    W();
                    W("  筛选引擎验证:");
                    var store = new UsnStore();
                    foreach (var e in entries) store.Add(e);
                    store.SetResolver(vol.DriveLetter, resolver);

                    void Check(string label, UsnFilter f, Func<UsnEntry, string, bool> expected)
                    {
                        var (cnt, ms, trunc) = store.ApplyFilter(f);
                        // 用同一条件独立算一遍期望值, 比对是否一致
                        int expect = 0;
                        for (int i = 0; i < store.TotalCount; i++)
                        {
                            var e = store.All[i];
                            if (expected(e, store.GetPath(i))) expect++;
                        }
                        var mark = cnt == expect ? "OK  " : "MISMATCH!";
                        W($"    [{mark}] {label,-34} 引擎={cnt,8:N0}  期望={expect,8:N0}  截断={trunc}  {ms}ms");
                    }

                    Check("无条件", new UsnFilter(), (e, p) => true);

                    var onlyDel = new UsnFilter();
                    onlyDel.Operations.Add(UsnOperation.Delete);
                    Check("仅删除", onlyDel, (e, p) => UsnOperation.Delete.Matches(e.Reason));

                    var onlyCreate = new UsnFilter();
                    onlyCreate.Operations.Add(UsnOperation.Create);
                    Check("仅创建", onlyCreate, (e, p) => UsnOperation.Create.Matches(e.Reason));

                    var byName = new UsnFilter { FileNameContains = ".txt" };
                    Check("文件名含 .txt", byName,
                        (e, p) => e.Name.Contains(".txt", StringComparison.OrdinalIgnoreCase));

                    // 注意: 路径是 "D:\Application\..." 这种形式, 开头没有 "\D\",
                    // 所以测试串必须选真实存在的子串, 否则是测试本身写错。
                    var byPath = new UsnFilter { PathContains = "\\Application\\" };
                    Check("路径含 \\Application\\", byPath,
                        (e, p) => p.Contains("\\Application\\", StringComparison.OrdinalIgnoreCase));

                    // 大小写不敏感验证
                    var byPathCase = new UsnFilter { PathContains = "\\application\\" };
                    Check("路径大小写不敏感", byPathCase,
                        (e, p) => p.Contains("\\application\\", StringComparison.OrdinalIgnoreCase));

                    var byNameCase = new UsnFilter { FileNameContains = ".TXT" };
                    Check("文件名大小写不敏感", byNameCase,
                        (e, p) => e.Name.Contains(".TXT", StringComparison.OrdinalIgnoreCase));

                    var byPathNo = new UsnFilter { PathContains = "zzz_不存在的路径_zzz" };
                    Check("路径含不存在串", byPathNo, (e, p) => false);

                    var both = new UsnFilter { FileNameContains = ".log", PathContains = "\\" };
                    Check("文件名.log + 路径\\", both,
                        (e, p) => e.Name.Contains(".log", StringComparison.OrdinalIgnoreCase)
                                  && p.Contains('\\'));

                    var timeOnly = new UsnFilter
                    {
                        From = maxT.AddMinutes(-10),
                        To = maxT,
                    };
                    Check("最近10分钟", timeOnly,
                        (e, p) => e.LocalTime >= maxT.AddMinutes(-10) && e.LocalTime <= maxT);

                    var combo = new UsnFilter
                    {
                        FileNameContains = ".log",
                        From = minT,
                        To = maxT,
                    };
                    combo.Operations.Add(UsnOperation.Write);
                    Check("写入+.log+全时段", combo,
                        (e, p) => e.Name.Contains(".log", StringComparison.OrdinalIgnoreCase)
                                  && UsnOperation.Write.Matches(e.Reason));

                    var range = new UsnFilter { From = maxT.AddHours(-1), To = maxT.AddHours(-1) };
                    Check("零宽时间窗(应≈0条)", range,
                        (e, p) => false);

                    // --- 排序验证 ---
                    W();
                    W("  排序引擎验证 (百万级关键路径, 只排索引不物化行):");
                    store.ApplyFilter(new UsnFilter());   // 恢复全量视图

                    void CheckSort(string label, UsnStore.SortColumn col, bool asc,
                                   Func<UsnEntry, UsnEntry, int> cmp)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var (ms, ok) = store.SortView(col, asc);
                        sw.Stop();

                        // 抽查前 2000 条是否真的有序
                        int bad = 0, checkedN = 0;
                        for (int i = 1; i < Math.Min(2000, store.ViewCount); i++)
                        {
                            var a = store.At(i - 1);
                            var b = store.At(i);
                            int c = cmp(a, b);
                            if (asc ? c > 0 : c < 0) bad++;
                            checkedN++;
                        }
                        var mark = ok && bad == 0 ? "OK  " : "BAD!";
                        W($"    [{mark}] {label,-22} {ms,7}ms  抽查 {checkedN} 条, 逆序 {bad}");
                    }

                    CheckSort("时间 升序", UsnStore.SortColumn.Time, true,
                        (a, b) => a.FileTimeUtc.CompareTo(b.FileTimeUtc));
                    CheckSort("时间 降序", UsnStore.SortColumn.Time, false,
                        (a, b) => a.FileTimeUtc.CompareTo(b.FileTimeUtc));
                    CheckSort("文件名 升序", UsnStore.SortColumn.Name, true,
                        (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    CheckSort("文件名 降序", UsnStore.SortColumn.Name, false,
                        (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                    CheckSort("USN 升序", UsnStore.SortColumn.Usn, true,
                        (a, b) => a.Usn.CompareTo(b.Usn));
                    CheckSort("分区 升序", UsnStore.SortColumn.Volume, true,
                        (a, b) => string.Compare(a.Volume, b.Volume, StringComparison.OrdinalIgnoreCase));
                    CheckSort("操作 升序", UsnStore.SortColumn.Operation, true,
                        (a, b) => 0);   // 语义分组顺序, 只验证不抛异常+完成

                    // 文件夹列需要路径解析, 是最慢的一列 —— 用真实键做逆序检查,
                    // 不能传空比较函数, 那样任何错误顺序都会"通过"
                    W("    (下面两项用真实文件夹键校验顺序)");
                    CheckSortFolder("所在文件夹 升序", true);
                    CheckSortFolder("所在文件夹 再排(缓存)", false);

                    void CheckSortFolder(string label, bool asc)
                    {
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var (ms, ok) = store.SortView(UsnStore.SortColumn.Folder, asc);
                        sw.Stop();

                        int bad = 0, checkedN = 0;
                        for (int i = 1; i < Math.Min(3000, store.ViewCount); i++)
                        {
                            var pa = store.GetDisplayFolder(store.View[i - 1]);
                            var pb = store.GetDisplayFolder(store.View[i]);
                            int c = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
                            if (asc ? c > 0 : c < 0) bad++;
                            checkedN++;
                        }
                        var mark = ok && bad == 0 ? "OK  " : "BAD!";
                        W($"    [{mark}] {label,-22} {ms,7}ms  抽查 {checkedN} 条, 逆序 {bad}");
                    }
                }
                W();
            }

            W("=== 自检完成 ===");
            return 0;
        }
        catch (Exception ex)
        {
            W($"!!! 自检异常: {ex}");
            return 1;
        }
        finally
        {
            if (outFile is not null)
                File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : "..." + s[^(max - 3)..];

    private static bool IsElevated()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
