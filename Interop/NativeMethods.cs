using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UsnExplorer.Interop;

internal static class NativeMethods
{
    internal const uint GENERIC_READ = 0x80000000;
    internal const uint FILE_SHARE_READ = 0x00000001;
    internal const uint FILE_SHARE_WRITE = 0x00000002;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    internal const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    internal const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    internal const uint FSCTL_ENUM_USN_DATA = 0x000900B3;

    internal const uint ERROR_HANDLE_EOF = 38;
    internal const uint ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    internal const uint ERROR_JOURNAL_NOT_ACTIVE = 1179;
    internal const uint ERROR_INVALID_PARAMETER = 87;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValueW(string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(
        IntPtr TokenHandle, bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    // 注意: LUID 是 8 字节但按 4 字节对齐 -> 整个结构 16 字节, 不是 24。
    // 用 long 会算成 24 字节, AdjustTokenPrivileges 静默失败(1300)。
    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public LUID Luid;
        public uint Attributes;
    }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x00000002;
    private const uint ERROR_NOT_ALL_ASSIGNED = 1300;

    /// <summary>
    /// 启用 SeBackupPrivilege。读取 $MFT / \$Extend 元数据文件需要。
    /// 失败不抛异常 —— 普通卷句柄 + FSCTL 路径不依赖它, 只是少了兜底能力。
    /// </summary>
    internal static bool TryEnableBackupPrivilege()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValueW(null, "SeBackupPrivilege", out var luid))
                return false;

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Luid = new LUID { LowPart = (uint)(luid & 0xFFFFFFFF), HighPart = (int)(luid >> 32) },
                Attributes = SE_PRIVILEGE_ENABLED,
            };
            if (!AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                return false;
            // AdjustTokenPrivileges 返回 true 但 GetLastError==1300 表示特权不在令牌中
            return Marshal.GetLastWin32Error() != ERROR_NOT_ALL_ASSIGNED;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool CloseHandle(IntPtr hObject);

    /// <summary>打开卷句柄, 如 \\.\C: 或 \\?\Volume{guid}\</summary>
    internal static SafeFileHandle OpenVolume(string volumePath)
    {
        var h = CreateFileW(
            volumePath,
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);
        if (h.IsInvalid)
            throw new IOException(
                $"无法打开 {volumePath} (Win32 错误 {Marshal.GetLastWin32Error()})。请以管理员身份运行。");
        return h;
    }
}

// ---------------------------------------------------------------------------
// 与 FSCTL 直接对接的结构体。全部显式 Pack=1 (这些结构在线上是紧凑排列的)
// ---------------------------------------------------------------------------

/// <summary>READ_USN_JOURNAL_DATA_V0 — 固定 40 字节: DWORDLONG, DWORD, DWORD, DWORDLONG, DWORDLONG, DWORDLONG</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct ReadUsnJournalDataV0
{
    public ulong StartUsn;
    public uint ReasonMask;
    public uint ReturnOnlyOnClose;
    public ulong Timeout;
    public ulong BytesToWaitFor;
    public ulong UsnJournalId;
}

/// <summary>MFT_ENUM_DATA_V0 — StartFileReferenceNumber, LowUsn, HighUsn</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct MftEnumDataV0
{
    public ulong StartFileReferenceNumber;
    public long LowUsn;
    public long HighUsn;
}

/// <summary>USN_JOURNAL_DATA_V0 (FSCTL_QUERY_USN_JOURNAL 的输出) — 64 字节</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct UsnJournalDataV0
{
    public ulong UsnJournalId;
    public long FirstUsn;
    public long NextUsn;
    public long LowestValidUsn;
    public long MaxUsn;
    public ulong MaximumSize;
    public ulong AllocationDelta;
}

/// <summary>
/// USN_RECORD_V2 固定头 (不含文件名)。
/// 逻辑字段: RecordLength(4) Major(2) Minor(2) FileReferenceNumber(8)
/// ParentFileReferenceNumber(8) Usn(8) TimeStamp(8) Reason(4) SourceInfo(4)
/// SecurityId(4) FileAttributes(4) FileNameLength(2) FileNameOffset(2) = 60 字节
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct UsnRecordV2Header
{
    public uint RecordLength;
    public ushort MajorVersion;
    public ushort MinorVersion;
    public ulong FileReferenceNumber;
    public ulong ParentFileReferenceNumber;
    public long Usn;
    public long TimeStamp;
    public uint Reason;
    public uint SourceInfo;
    public uint SecurityId;
    public uint FileAttributes;
    public ushort FileNameLength;
    public ushort FileNameOffset;
}

[Flags]
internal enum UsnReason : uint
{
    DataOverwrite = 0x00000001,
    DataExtend = 0x00000002,
    DataTruncation = 0x00000004,
    NamedDataOverwrite = 0x00000010,
    NamedDataExtend = 0x00000020,
    NamedDataTruncation = 0x00000040,
    FileCreate = 0x00000100,
    FileDelete = 0x00000200,
    EaChange = 0x00000400,
    SecurityChange = 0x00000800,
    RenameOldName = 0x00001000,
    RenameNewName = 0x00002000,
    IndexableChange = 0x00004000,
    BasicInfoChange = 0x00008000,
    HardLinkChange = 0x00010000,
    CompressionChange = 0x00020000,
    EncryptionChange = 0x00040000,
    ObjectIdChange = 0x00080000,
    ReparsePointChange = 0x00100000,
    StreamChange = 0x00200000,
    TransactedChange = 0x00400000,
    IntegrityChange = 0x00800000,
    Close = 0x80000000,
}
