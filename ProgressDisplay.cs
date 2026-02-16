using System.Diagnostics;
using System.Globalization;

namespace AndroidSparseTools;

internal sealed class ProgressDisplay : IDisposable
{
    private readonly string _label;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private int _lastPercent = -1;
    private DateTime _lastUpdate = DateTime.MinValue;
    private int _lastRenderedLength;
    private long _lastSampleDone;
    private double _lastSampleSeconds;
    private double _speedBytesPerSec;
    private bool _ended;

    public ProgressDisplay(string label)
    {
        _label = label;
    }

    public void Report(long done, long total)
    {
        if (total <= 0)
        {
            return;
        }

        if (done < 0)
        {
            done = 0;
        }

        if (done > total)
        {
            done = total;
        }

        int percent = (int)(done * 100 / total);
        DateTime now = DateTime.UtcNow;
        if (done != total && percent == _lastPercent && (now - _lastUpdate).TotalMilliseconds < 80)
        {
            return;
        }

        _lastPercent = percent;
        _lastUpdate = now;

        UpdateSpeed(done);
        double displaySpeed = _speedBytesPerSec;
        if (displaySpeed <= 0 && done > 0)
        {
            double elapsed = _stopwatch.Elapsed.TotalSeconds;
            if (elapsed > 0)
            {
                displaySpeed = done / elapsed;
            }
        }

        string speedText = displaySpeed > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "{0}/s",
                FormatBytes((long)displaySpeed))
            : "--";

        string line = string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1,3}% ({2}/{3}) {4}",
            _label,
            percent,
            FormatBytes(done),
            FormatBytes(total),
            speedText);

        int renderLength = Math.Max(line.Length, _lastRenderedLength);
        Console.Error.Write("\r");
        Console.Error.Write(line.PadRight(renderLength));
        _lastRenderedLength = line.Length;

        if (done == total)
        {
            Console.Error.WriteLine();
            _ended = true;
        }
    }

    public void Dispose()
    {
        if (!_ended && _lastPercent >= 0)
        {
            Console.Error.WriteLine();
            _ended = true;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Format(CultureInfo.InvariantCulture, "{0:0.##}{1}", value, units[unit]);
    }

    private void UpdateSpeed(long done)
    {
        double nowSeconds = _stopwatch.Elapsed.TotalSeconds;
        if (done < _lastSampleDone || nowSeconds <= _lastSampleSeconds)
        {
            _lastSampleDone = done;
            _lastSampleSeconds = nowSeconds;
            return;
        }

        long deltaBytes = done - _lastSampleDone;
        double deltaSeconds = nowSeconds - _lastSampleSeconds;
        if (deltaBytes == 0 || deltaSeconds < 0.02)
        {
            return;
        }

        double instantSpeed = deltaBytes / deltaSeconds;
        _speedBytesPerSec = _speedBytesPerSec <= 0
            ? instantSpeed
            : (0.25 * instantSpeed + 0.75 * _speedBytesPerSec);

        _lastSampleDone = done;
        _lastSampleSeconds = nowSeconds;
    }
}
