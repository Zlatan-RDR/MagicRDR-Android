namespace MagicRDR.Core;

/// <summary>
/// A progress notification. <see cref="Fraction"/> is 0..1 (ready to bind to a ProgressBar);
/// <see cref="Message"/> describes the current step.
/// </summary>
public readonly record struct RpfProgress(double Fraction, string Message)
{
    public int Percent => (int)Math.Round(Math.Clamp(Fraction, 0d, 1d) * 100d);
}

/// <summary>Byte-based progress accumulator with throttling so UI threads are not flooded.</summary>
internal sealed class ProgressTracker
{
    private readonly IProgress<RpfProgress>? _progress;
    private readonly long _total;
    private long _done;
    private string _message = string.Empty;
    private long _lastReportTick = long.MinValue;

    public ProgressTracker(IProgress<RpfProgress>? progress, long totalBytes)
    {
        _progress = progress;
        _total = totalBytes;
    }

    public void SetMessage(string message)
    {
        _message = message;
        Report(force: true);
    }

    public void Advance(long bytes)
    {
        _done += bytes;
        Report(force: false);
    }

    public void Complete(string message)
    {
        _message = message;
        _progress?.Report(new RpfProgress(1.0, message));
    }

    private void Report(bool force)
    {
        if (_progress == null)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (!force && now - _lastReportTick < 60)
        {
            return;
        }

        _lastReportTick = now;
        double fraction = _total <= 0 ? 0d : Math.Min(1d, (double)_done / _total);
        _progress.Report(new RpfProgress(fraction, _message));
    }
}
