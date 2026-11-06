namespace Ponte;

// Slow recovery and quick degradation keep a temporary congestion spike from repeatedly restarting the encoder.
internal sealed class AutomaticQuality
{
    public int Level { get; private set; }
    private int _bad, _good;
    private double _baseline = double.MaxValue;
    public void Reset() { Level = _bad = _good = 0; _baseline = double.MaxValue; }
    public bool Observe(double rtt, double paintMilliseconds)
    {
        if (!double.IsFinite(rtt) || rtt <= 0) return false;
        _baseline = Math.Min(_baseline, rtt);
        bool congested = rtt > Math.Max(300, _baseline + 180) || paintMilliseconds > 45;
        bool healthy = rtt < Math.Max(180, _baseline + 60) && paintMilliseconds < 25;
        _bad = congested ? _bad + 1 : 0; _good = healthy ? _good + 1 : 0;
        int old = Level;
        if (_bad >= 2) { Level = Math.Min(4, Level + 1); _bad = _good = 0; }
        else if (_good >= 10) { Level = Math.Max(0, Level - 1); _bad = _good = 0; }
        return old != Level;
    }
    public (int Quality, VideoOptions Video) Apply(int quality, VideoOptions video)
    {
        if (Level == 0) return (quality, video);
        int ceiling = Level switch { 1 => 60, 2 => 30, _ => 20 };
        int fps = Math.Min(video.Fps, ceiling);
        int height = Level switch { 1 => 1080, 2 => 900, 3 => 720, _ => 480 };
        return (Math.Max(10, (int)(quality * (1 - Level * .13))), video with { Fps = fps,
            BitrateMbps = Math.Max(1, video.BitrateMbps / (1 << Level)), MaxHeight = height });
    }
}
