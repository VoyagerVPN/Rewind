namespace Rewind.Preview;

public sealed class AudioLevelEnvelope
{
    private double _peak;
    private TimeSpan _peakTime;

    public void Observe(double amplitude, TimeSpan now)
    {
        double peak = double.IsFinite(amplitude) ? Math.Clamp(amplitude, 0, 1) : 0;
        if (peak < Read(now)) return;
        _peak = peak; _peakTime = now;
    }

    public double Read(TimeSpan now)
    {
        double releaseSeconds = Math.Max(0, (now - _peakTime).TotalSeconds - 0.06);
        double amplitude = _peak * Math.Pow(10, -24 * releaseSeconds / 20);
        return amplitude < 0.0005 ? 0 : amplitude;
    }

    public void Reset() { _peak = 0; _peakTime = TimeSpan.Zero; }

    public static double Fill(double amplitude) => !double.IsFinite(amplitude) || amplitude <= 0 ? 0 :
        Math.Clamp((20 * Math.Log10(amplitude) + 60) / 60, 0, 1);
}
