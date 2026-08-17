#if DEBUG
using System.IO;
#endif

namespace CPURacer.Capture;

/// <summary>
/// 轻量诊断日志：Debug 构建下写入 %TEMP%\CPURacer-diag.log，进程启动时清空。
/// Release 构建下 Write 为 no-op（零开销）。
/// 用于在非调试器环境（管理员直跑 exe）下收集跳变预测/周期学习诊断。
/// </summary>
public static class DiagLog
{
#if DEBUG
    private static readonly string Path = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "CPURacer-diag.log");

    static DiagLog()
    {
        try
        {
            File.Delete(Path);
        }
        catch
        {
            // 清理失败不影响后续写入。
        }
    }
#endif

    public static void Write(string line)
    {
#if DEBUG
        try
        {
            File.AppendAllText(
                Path,
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响游戏。
        }
#endif
    }
}
