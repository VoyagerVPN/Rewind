namespace Rewind.Preview;

public sealed class AudioMix
{
    private readonly double[] _sources = [1, 1, 1];
    public double Master { get; set; } = 0.25;
    public void SetSource(int index, double value) => _sources[index] = value;
    public double EffectiveGain(int index) => _sources[index] * Master;
}
