using System;
using System.Diagnostics;

namespace DeskOrganizer.NoFences.Util;

/// <summary>
/// 节流/防抖执行器。确保连续调用之间至少间隔指定的最小时间间隔，
/// 适用于窗口移动、调整大小等高频事件的节流处理。
/// </summary>
public class ThrottledExecution : IDisposable
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly int _minimumIntervalMs;
    private readonly Action _action;
    private bool _disposed;

    /// <summary>
    /// 创建节流执行器。
    /// </summary>
    /// <param name="action">需要节流执行的动作。</param>
    /// <param name="minimumIntervalMs">最小执行间隔（毫秒）。</param>
    public ThrottledExecution(Action action, int minimumIntervalMs = 50)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _minimumIntervalMs = Math.Max(1, minimumIntervalMs);
    }

    /// <summary>
    /// 尝试执行动作。仅当距上次执行已超过最小间隔时才真正执行。
    /// </summary>
    public void Run()
    {
        if (_disposed) return;

        if (_stopwatch.ElapsedMilliseconds >= _minimumIntervalMs)
        {
            _stopwatch.Restart();
            _action();
        }
    }

    /// <summary>
    /// 强制立即执行，无视节流间隔。
    /// </summary>
    public void RunNow()
    {
        if (_disposed) return;
        _stopwatch.Restart();
        _action();
    }

    public void Dispose()
    {
        _disposed = true;
    }
}
