# USN 日志浏览器

读取并检索 NTFS USN（Update Sequence Number，更新序列号）变更日志的工具，用来回答一个问题：**这块盘上，谁在什么时候动过哪些文件？**

[English](README.en.md) · 中文

![USN 日志浏览器](docs/screenshot.png)

Windows 10/11 · .NET 8 · WPF · 需要管理员权限

---

## 它读的是什么

NTFS 会为每个卷维护一份变更流水账，路径是 `C:\$Extend\$UsnJrnl:$J`，文件的新建、删除、改名、写入、改属性等操作都会记进去。本工具把它读出来，以表格呈现，支持检索和筛选。

**这个文件用普通文件 API 打不开。** 拦截发生在文件系统层——以管理员身份运行、并且同时启用了 `SeBackupPrivilege` 和 `SeRestorePrivilege`，依然返回 `ERROR_ACCESS_DENIED`。各种路径写法无一例外：

```
C:\$Extend\$UsnJrnl:$J
\\?\C:\$Extend\$UsnJrnl:$J
\\.\C:\$Extend\$UsnJrnl:$J
\\?\Volume{guid}\$Extend\$UsnJrnl:$J
```

旁证：`icacls` 连这个路径都解析不了，而 `GetFileAttributesEx` 却能成功返回这个稀疏文件的逻辑长度。说明拦的不是 DACL。

唯一可行的读法是打开卷句柄再发 FSCTL：

```
CreateFileW("\\.\C:", GENERIC_READ, ...)
FSCTL_QUERY_USN_JOURNAL  0x000900F4   读日志配置
FSCTL_READ_USN_JOURNAL   0x000900BB   顺序读变更记录
FSCTL_ENUM_USN_DATA      0x000900B3   枚举全盘 MFT 条目
```

本工具走的就是这条路。它还负责**还原完整路径**：一条 USN 记录里只有文件引用号、父目录引用号，以及*变更当时*的文件名——没有路径。路径靠枚举 MFT 建一张 `FileId → (ParentId, Name)` 的表，再逐级向上回溯到根目录得到。

## 运行

```
publish\UsnExplorer.exe
```

启动时会通过 UAC 请求管理员权限——打开卷句柄必须要。启动后自动发现 NTFS 分区并扫描。

从源码构建（需要 .NET 8 SDK）：

```
build.cmd
```

产物是 `publish\UsnExplorer.exe`，单文件 270KB，依赖已安装的 .NET 8 桌面运行时。脚本最后一步会自动跑引擎自检。

## 功能

- **分区扫描** — 自动发现所有 NTFS 卷，逐个读取日志
- **路径还原** — 基于 MFT 枚举，带环保护；祖先目录已删除时显示 `<已删除>` 占位而不是抛异常
- **筛选**
  - 文件名关键词、路径关键词，两条独立通道（不区分大小写，输入即筛选）
  - 日期 + 精确到秒的时间范围，带常用区间快捷选项；扫描完自动填入实际数据的时间上下界
  - 操作类型：创建 / 删除 / 重命名 / 写入 / 属性 / 权限 / 数据流 / 硬链接 / 事务 / 关闭句柄
  - 分区，以及「排除目录」开关
- **排序** — 点击任意列标题；排序在后台线程执行，过程中表格保持可用
- **详情面板** — 完整路径、本地与 UTC 时间、MFT 记录号、原始 reason 位图
- **CSV 导出** 当前筛选结果（按 RFC 4180 转义）
- **快捷键** — `F5` 扫描 · `Ctrl+F` 文件名 · `Ctrl+Shift+F` 路径 · `Ctrl+E` 导出 · `Esc` 取消 · 双击行复制路径

## 性能

本机实测（NVMe 卷，264 万条记录 / 138 万条 MFT 条目）：

```
读日志      2,644,894 条  /  886 ms   (~300 万条/秒)
MFT 枚举    1,377,396 项  /  2.3 s
路径解析    100%         (抽样 2000 条)
筛选        全量 < 400 ms
按时间排序   ~200 ms
```

记录全量常驻内存（260 万条约 450MB）。没有分页、没有落盘索引——这个量级下全内存换来的是筛选零延迟。

百万行级别能跑得动，靠两件事：

- 记录用**结构体**的 `List<T>` 保存，行对象由 `DataGrid` 的 `IList` 索引器按需创建。所以 300 万行不会变成 300 万个堆对象。
- 排序被拦下来，只对 **int 索引数组**排序。DataGrid 自带的排序要经过 `ListCollectionView`，它会枚举全部条目——对上按需创建行的索引器，就是几百万次物化加反射，UI 直接卡死。改成后台排索引后，实测窗口全程不阻塞，300 万行按文件名重排 276ms。

## 代码结构

```
Interop/NativeMethods.cs      P/Invoke: CreateFileW、DeviceIoControl、特权启用
                              以及各结构体定义（紧凑排列）
Core/UsnReader.cs             FSCTL 封装: QUERY / READ_USN_JOURNAL / ENUM_USN_DATA
Core/UsnEntry.cs              紧凑的记录结构体表示
Core/PathResolver.cs          MFT 映射表的路径回溯（带缓存和环保护）
Core/UsnFilter.cs             筛选模型 + 操作类型归类
Core/UsnStore.cs              记录存储、筛选、索引排序（双缓冲）
ViewModels/UsnRow.cs          按需物化的 IList 行
ViewModels/MainViewModel.cs   扫描编排、状态、导出
MainWindow.xaml               界面
Themes/Dark.xaml              暗色主题（完整控件模板）
SelfTest.cs                   无头引擎自检
```

## 无头自检

互操作层出错的表现是**「静默返回 0 条」**，而对着界面看，这和一个空日志、或者扫错了卷完全无法区分。自检模式会打印日志配置、记录数、耗时、时间跨度、reason 位分布、MFT 条目数和路径解析成功率：

```
USN_SELFTEST_OUT=%TEMP%\out.txt UsnExplorer.exe --selftest [盘符]
```

它还会拿独立重算的期望值校验引擎：12 项筛选、9 项排序。这确实抓到过真 bug——降序排「所在文件夹」时有 2999 条中 883 条错序，原因是排序把自己的缓存键数组原地打乱了。

`build.cmd` 会自动跑一遍。

## 已知边界

- 仅支持 NTFS。exFAT 和 FAT32 没有 USN 日志。
- 日志是**环形缓冲**：达到 `MaxSize`（通常 256MB）后，最旧的记录被覆盖，所以只有最近一段历史。清空日志（`fsutil usn deletejournal /N X:`）会不可恢复地丢掉全部历史，并让 Everything 这类消费者被迫全盘重扫——这不是一个「清理」操作。
- 文件报告的逻辑长度一直很大（约 2.5GB），实际分配停在 256MB 上限附近（约 262MB）。它是稀疏文件，逻辑长度只增不减；真实磁盘成本是 `MaxSize` 加一个 `AllocationDelta`。
- 路径还原依赖 MFT 的**当前**状态。祖先目录已删除的路径无法还原，会显示为 `<已删除>`。
- 筛选结果超过 2000 万行会截断，界面会明确提示已达上限——绝不静默丢弃。
- 只读。不会修改或清空日志。

## 实现要点

如果你也要自己写一遍，以下几个细节最费时间：

- `READ_USN_JOURNAL_DATA_V0` 恰好 **40 字节**（`<QIIQQQ>`），不是 48——两个 `DWORD` 夹在 `DWORDLONG` 之间。尺寸传错会返回 `ERROR_INVALID_PARAMETER`，输出字节为 0。
- `USN_RECORD_V2` 头恰好 **60 字节**（`<IHHQQQQIIIIHH>`）。major/minor 版本号各是 `WORD` 不是 `DWORD`，写错会让后面所有字段静默错位。
- `TimeStamp` 是 **FILETIME**（自 1601-01-01 起的 100ns），**不是** `DateTime.Ticks`（自 0001-01-01 起）。直接用 `new DateTime(ticks)` 会让每个时间戳整整差 1600 年，而且结果看起来仍然像个正常日期。
- `TOKEN_PRIVILEGES` 是 **16 字节不是 24**：`LUID` 虽 8 字节但按 4 字节对齐。声明成 `long` 会算成 24，`AdjustTokenPrivileges` 静默失败返回 1300。而且它在特权实际不存在时也返回 `true`，所以必须额外检查 `GetLastWin32Error() != 1300`。
- `Array.Sort(keys, indices)` 会**原地**排序 `keys`——不要把你打算复用的缓存数组传进去。
