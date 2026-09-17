@echo off
REM ============================================================
REM  UsnExplorer 构建脚本
REM  产出: publish\UsnExplorer.exe (单文件, 需已安装 .NET 8 桌面运行时)
REM ============================================================
setlocal
cd /d "%~dp0"

echo [1/3] 编译...
dotnet build -c Release -v q --nologo
if errorlevel 1 (
    echo 编译失败
    exit /b 1
)

echo [2/3] 发布单文件...
dotnet publish -c Release -r win-x64 --self-contained false ^
    -p:PublishSingleFile=true -o publish -v q --nologo
if errorlevel 1 (
    echo 发布失败
    exit /b 1
)

echo [3/3] 引擎自检 (D: 盘)...
set USN_SELFTEST_OUT=%TEMP%\usnexplorer_selftest.txt
bin\Release\net8.0-windows\UsnExplorer.exe --selftest D >nul 2>&1
if exist "%TEMP%\usnexplorer_selftest.txt" (
    type "%TEMP%\usnexplorer_selftest.txt"
) else (
    echo 自检未产出报告 (可能未以管理员运行)
)

echo.
echo 完成。可执行文件: %~dp0publish\UsnExplorer.exe
echo 直接双击运行会请求管理员权限。
endlocal
