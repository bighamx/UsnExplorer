using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UsnExplorer.Interop;

namespace UsnExplorer.Core;

/// <summary>
/// USN 日志读取引擎。所有读取都走卷句柄 + FSCTL:
///
///   $Extend\$UsnJrnl:$J 用 CreateFileW 打不开 (文件系统层拦截, 连
///   SeBackupPrivilege 都无效), 只能:
///     FSCTL_QUERY_USN_JOURNAL  读日志配置
///     FSCTL_READ_USN_JOURNAL   顺序读记录流
///     FSCTL_ENUM_USN_DATA      枚举全盘 MFT (路径解析用)
/// </summary>
public sealed class UsnReader
{
    private const int BufferSize = 1 << 20;          // 1 MB

    // USN_RECORD_V2 固定头 60 字节
    private const int RecordHeaderSize = 60;

    public static bool HasBackupPrivilege { get; private set; }

    public static void Initialize() => HasBackupPrivilege = NativeMethods.TryEnableBackupPrivilege();

    // -----------------------------------------------------------------------
    // 分区枚举
    // -----------------------------------------------------------------------

    /// <summary>列出所有固定/可移动卷 (跳过光驱、网络盘、只读介质)。</summary>
    public static List<VolumeInfo> EnumerateVolumes()
    {
        var result = new List<VolumeInfo>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                    continue;
                if (!drive.IsReady)
                    continue;
                // 必须是 NTFS —— USN 是 NTFS 特性, exFAT/FAT32 上 fsutil 直接不支持
                if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
                    continue;

                var letter = drive.Name.TrimEnd('\\');       // "C:"
                result.Add(new VolumeInfo
                {
                    DriveLetter = letter,
                    VolumePath = $@"\\.\{letter}",
                    DisplayName = BuildDisplayName(letter, drive),
                    FileSystem = drive.DriveFormat,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                });
            }
            catch (IOException) { /* 介质不可用, 跳过 */ }
            catch (UnauthorizedAccessException) { }
        }
        return result;
    }

    private static string BuildDisplayName(string letter, DriveInfo drive)
    {
        try
        {
            var label = drive.VolumeLabel;
            var kind = drive.DriveType == DriveType.Removable ? "可移动" : "本地";
            return string.IsNullOrWhiteSpace(label)
                ? $"{letter} ({kind})"
                : $"{letter} {label} ({kind})";
        }
        catch { return letter; }
    }

    // -----------------------------------------------------------------------
    // 日志配置
    // -----------------------------------------------------------------------

    /// <summary>查询单个卷的 USN 日志配置。失败时返回 null 并给出原因。</summary>
    public static UsnJournalDataV0? QueryJournal(
        string volumePath, out string? error, out SafeFileHandle? handle)
    {
        handle = null;
        error = null;
        try
        {
            handle = NativeMethods.OpenVolume(volumePath);
            if (!DeviceIoControl<UsnJournalDataV0>(
                    handle, NativeMethods.FSCTL_QUERY_USN_JOURNAL,
                    IntPtr.Zero, 0, out var data, out var err))
            {
                error = DescribeError(err);
                handle.Dispose();
                handle = null;
                return null;
            }
            return data;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            handle?.Dispose();
            handle = null;
            return null;
        }
    }

    /// <summary>把 Win32 错误码翻译成人话。</summary>
    public static string DescribeError(uint err) => err switch
    {
        5 => "拒绝访问 —— 需要以管理员身份运行",
        2 => "系统找不到指定的文件",
        NativeMethods.ERROR_JOURNAL_NOT_ACTIVE => "该卷未启用 USN 日志",
        NativeMethods.ERROR_JOURNAL_DELETE_IN_PROGRESS => "USN 日志正在删除中",
        NativeMethods.ERROR_HANDLE_EOF => "已读到日志末尾",
        NativeMethods.ERROR_INVALID_PARAMETER => "参数无效 —— 日志可能已被重建, 请刷新",
        _ => $"Win32 错误 {err} ({new System.ComponentModel.Win32Exception((int)err).Message})",
    };

    // -----------------------------------------------------------------------
    // 读日志记录
    // -----------------------------------------------------------------------

    /// <summary>
    /// 顺序读取一个卷的 USN 日志。记录通过 sink 流式交给调用方,
    /// 读到的总条数由返回值给出 —— 不在这里缓存, 避免千万级记录撑爆内存。
    /// </summary>
    public static long ReadJournal(
        VolumeInfo volume,
        SafeFileHandle handle,
        UsnJournalDataV0 journal,
        Action<UsnEntry> sink,
        IProgress<UsnReadProgress>? progress = null,
        CancellationToken ct = default)
    {
        long start = journal.FirstUsn;
        long next = journal.NextUsn;
        long span = next - start;
        long total = 0;
        var lastReport = 0L;

        // 缓冲区用 Marshal.AllocHGlobal —— USN 记录是紧凑二进制,
        // 用 byte[] 读没问题, 但解析要避免每条约一个 Span 切片分配。
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            while (start < next)
            {
                ct.ThrowIfCancellationRequested();

                var input = new ReadUsnJournalDataV0
                {
                    StartUsn = (ulong)start,
                    ReasonMask = 0xFFFFFFFF,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalId = journal.UsnJournalId,
                };

                if (!DeviceIoControlRaw(handle, NativeMethods.FSCTL_READ_USN_JOURNAL,
                        input, 40, buffer, BufferSize, out var bytes))
                {
                    var err = (uint)Marshal.GetLastWin32Error();
                    // 读完尾部会返回 EOF, 属于正常结束
                    if (err is NativeMethods.ERROR_HANDLE_EOF or NativeMethods.ERROR_INVALID_PARAMETER)
                        break;
                    throw new IOException($"读取 USN 日志失败: {DescribeError(err)}");
                }

                if (bytes <= 8)
                    break;   // 只剩 8 字节的 next-USN, 没有记录

                // 输出前 8 字节是下一轮的 StartUsn
                long newStart = Marshal.ReadInt64(buffer, 0);

                int offset = 8;
                while (offset + RecordHeaderSize <= bytes)
                {
                    uint recordLength = (uint)Marshal.ReadInt32(buffer, offset);
                    // 长度非法就停, 防止死循环
                    if (recordLength < RecordHeaderSize || offset + recordLength > bytes)
                        break;

                    var header = Marshal.PtrToStructure<UsnRecordV2Header>(buffer + offset);

                    // 只处理 V2/V3 (V3 多了 8 字节尾, 但文件名偏移仍可用)
                    if (header.MajorVersion is 2 or 3)
                    {
                        var name = ReadName(buffer + offset, header);
                        if (name is not null)
                        {
                            sink(new UsnEntry
                            {
                                Usn = header.Usn,
                                FileId = header.FileReferenceNumber & 0x0000FFFFFFFFFFFF,
                                ParentId = header.ParentFileReferenceNumber & 0x0000FFFFFFFFFFFF,
                                Name = name,
                                Reason = header.Reason,
                                FileAttributes = header.FileAttributes,
                                SourceInfo = header.SourceInfo,
                                FileTimeUtc = header.TimeStamp,
                                Volume = volume.DriveLetter,
                            });
                            total++;
                        }
                    }

                    offset += (int)recordLength;
                }

                if (newStart <= start)
                    break;   // 没有前进, 防止死循环
                start = newStart;

                if (progress is not null && total - lastReport >= 20_000)
                {
                    lastReport = total;
                    progress.Report(new UsnReadProgress
                    {
                        Volume = volume.DriveLetter,
                        RecordsRead = total,
                        TotalUsnSpan = span,
                        UsnPosition = start - journal.FirstUsn,
                    });
                }
            }

            progress?.Report(new UsnReadProgress
            {
                Volume = volume.DriveLetter,
                RecordsRead = total,
                TotalUsnSpan = span,
                UsnPosition = span,
            });
            return total;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>从记录里取出 UTF-16 文件名。</summary>
    private static string? ReadName(IntPtr recordPtr, UsnRecordV2Header header)
    {
        if (header.FileNameLength == 0 || header.FileNameOffset == 0)
            return null;
        // 防御: 偏移+长度必须落在记录内 (记录长度由调用方校验过)
        if (header.FileNameOffset + header.FileNameLength > header.RecordLength)
            return null;
        return Marshal.PtrToStringUni(
            recordPtr + header.FileNameOffset,
            header.FileNameLength / 2);
    }

    // -----------------------------------------------------------------------
    // MFT 枚举 (路径解析用)
    // -----------------------------------------------------------------------

    /// <summary>
    /// 枚举卷上所有 MFT 条目, 返回 FileId -> (ParentId, Name) 映射。
    /// 这是唯一能拿到「历史文件名」的途径: USN 记录里的名字是变更当时的,
    /// 文件后来改名了只能靠这里补全。
    /// </summary>
    public static Dictionary<ulong, (ulong ParentId, string Name)> EnumerateMft(
        SafeFileHandle handle,
        IProgress<(long count, string volume)>? progress = null,
        string volumeLetter = "",
        CancellationToken ct = default)
    {
        var map = new Dictionary<ulong, (ulong, string)>(1 << 20);
        var buffer = Marshal.AllocHGlobal(BufferSize);
        try
        {
            ulong startFrn = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                var input = new MftEnumDataV0
                {
                    StartFileReferenceNumber = startFrn,
                    LowUsn = 0,
                    HighUsn = long.MaxValue,
                };

                if (!DeviceIoControlRaw(handle, NativeMethods.FSCTL_ENUM_USN_DATA,
                        input, 24, buffer, BufferSize, out var bytes))
                    break;
                if (bytes <= 8)
                    break;

                ulong nextFrn = (ulong)Marshal.ReadInt64(buffer, 0);

                int offset = 8;
                ulong lastFrn = startFrn;
                while (offset + RecordHeaderSize <= bytes)
                {
                    uint recordLength = (uint)Marshal.ReadInt32(buffer, offset);
                    if (recordLength < RecordHeaderSize || offset + recordLength > bytes)
                        break;

                    var header = Marshal.PtrToStructure<UsnRecordV2Header>(buffer + offset);
                    if (header.MajorVersion is 2 or 3)
                    {
                        var name = ReadName(buffer + offset, header);
                        var frn = header.FileReferenceNumber & 0x0000FFFFFFFFFFFF;
                        if (name is not null)
                            map[frn] = (header.ParentFileReferenceNumber & 0x0000FFFFFFFFFFFF, name);
                        if (frn > lastFrn) lastFrn = frn;
                    }
                    offset += (int)recordLength;
                }

                if (progress is not null)
                    progress.Report((map.Count, volumeLetter));

                if (nextFrn <= lastFrn)
                    break;
                startFrn = nextFrn;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return map;
    }

    // -----------------------------------------------------------------------
    // DeviceIoControl 辅助
    // -----------------------------------------------------------------------

    /// <summary>调 DeviceIoControl, 把输出结构体直接 marshal 回来。</summary>
    private static bool DeviceIoControl<T>(
        SafeFileHandle h, uint code, IntPtr inBuf, uint inSize, out T output, out uint error)
        where T : struct
    {
        int size = Marshal.SizeOf<T>();
        var outPtr = Marshal.AllocHGlobal(size);
        try
        {
            bool ok = NativeMethods.DeviceIoControl(h, code, inBuf, inSize,
                outPtr, (uint)size, out _, IntPtr.Zero);
            error = ok ? 0 : (uint)Marshal.GetLastWin32Error();
            output = ok ? Marshal.PtrToStructure<T>(outPtr) : default;
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(outPtr);
        }
    }

    /// <summary>调 DeviceIoControl, 输出给裸缓冲区 (大批量记录走这条)。</summary>
    private static bool DeviceIoControlRaw(
        SafeFileHandle h, uint code, object input, uint inSize,
        IntPtr outBuf, uint outSize, out int bytesReturned)
    {
        int structSize = Marshal.SizeOf(input);
        var inPtr = Marshal.AllocHGlobal(structSize);
        try
        {
            Marshal.StructureToPtr(input, inPtr, false);
            bool ok = NativeMethods.DeviceIoControl(h, code, inPtr, inSize,
                outBuf, outSize, out var ret, IntPtr.Zero);
            bytesReturned = (int)ret;
            return ok;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
        }
    }
}
