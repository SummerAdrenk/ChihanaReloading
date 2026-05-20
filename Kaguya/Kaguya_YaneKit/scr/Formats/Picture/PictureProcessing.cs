// ============================================================================
// PictureProcessing.cs
// 图片批处理的并行基础设施与进度汇报工具
//
// 职责:
//   - 提供线程安全的控制台输出 (WriteLine)
//   - 提供统一的进度条显示 (StartProgress / IProgressScope)
//     进度格式: [TAG] N/M (X%)
//   - 定义全局并行参数 (MaxDegreeOfParallelism)
//
// 依赖: 无外部依赖, 仅使用 System.Threading
// 被依赖: FileConverter, FileSorter, Restorer, CharacterComposer 等
//         所有涉及并行文件处理的模块都通过此类进行控制台交互
// ============================================================================

namespace Kaguya_YaneKit.Formats.Picture;

internal static class PictureProcessing
{
    public const int MaxDegreeOfParallelism = 128;

    private static readonly object ConsoleLock = new();

    public static ParallelOptions ParallelOptions => new()
    {
        MaxDegreeOfParallelism = MaxDegreeOfParallelism
    };

    // 线程安全的控制台输出, 防止多线程交叉打印
    public static void WriteLine(string message)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine(message);
        }
    }

    // 创建进度追踪器, 输出格式: [label] current/total (percent%)
    public static IProgressScope StartProgress(string label, int total)
    {
        return new ProgressScope(label, total);
    }

    public interface IProgressScope : IDisposable
    {
        void Increment();
    }

    // 进度追踪实现: 按 10% 间隔汇报, 支持多线程安全递增
    private sealed class ProgressScope : IProgressScope
    {
        private readonly string _label;
        private readonly int _total;
        private readonly int _reportStep;
        private int _current;
        private int _nextReportAt;
        private bool _donePrinted;

        public ProgressScope(string label, int total)
        {
            _label = label;
            _total = Math.Max(total, 0);
            _reportStep = _total <= 10 ? 1 : Math.Max(1, _total / 10);
            _nextReportAt = _reportStep;
            Render(0);
        }

        public void Increment()
        {
            var current = Interlocked.Increment(ref _current);
            lock (ConsoleLock)
            {
                if (current >= _total)
                {
                    if (!_donePrinted)
                    {
                        _donePrinted = true;
                        Render(current);
                    }

                    return;
                }

                if (current >= _nextReportAt)
                {
                    Render(current);
                    while (_nextReportAt <= current)
                    {
                        _nextReportAt += _reportStep;
                    }
                }
            }
        }

        private void Render(int current)
        {
            var percent = _total == 0 ? 100 : (int)Math.Round(current * 100.0 / _total);
            Console.WriteLine($"  [{_label}] {current}/{_total} ({percent}%)");
        }

        public void Dispose()
        {
            lock (ConsoleLock)
            {
                if (!_donePrinted)
                {
                    _donePrinted = true;
                    Render(_current);
                }
            }
        }
    }
}
