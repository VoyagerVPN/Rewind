namespace Rewind.Preview;

public readonly record struct PanelBounds(int Left, int Top, int Width, int Height);

public static class PanelGeometry
{
    public static int PlayerSurfaceHeight(int width, int height, double scale) =>
        Math.Max(1, (int)Math.Round(Math.Min(height * .88, Math.Max(1, width - 64 * scale) * 9 / 16 + 128 * scale)));
    public static PanelBounds PlayerSurfaceBounds(int width, int height, double scale)
    {
        int sidebar = ForMonitor(0, 0, width, height, scale, false).Width;
        int margin = (int)Math.Round(16 * scale);
        int playerHeight = PlayerSurfaceHeight(width - sidebar, height, scale);
        return new(sidebar + margin, (int)Math.Round((height - playerHeight) / 2d), Math.Max(1, width - sidebar - 2 * margin), playerHeight);
    }
    public static PanelBounds ForMonitor(int left, int top, int width, int height, double dpiScale, bool playerVisible)
    {
        double logicalWidth = Math.Clamp(width / dpiScale * 0.26, 360, 520);
        int panelWidth = Math.Min(width, (int)Math.Round(logicalWidth * dpiScale));
        return new PanelBounds(left, top, playerVisible ? width : panelWidth, height);
    }
}
