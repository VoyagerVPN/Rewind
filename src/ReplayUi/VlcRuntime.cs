using LibVLCSharp.Shared;

namespace Rewind.Preview;

internal static class VlcRuntime
{
    private static readonly Lazy<LibVLC> Runtime = new(() =>
    {
        Core.Initialize();
        return new LibVLC("--ignore-config", "--no-audio", "--no-video-title-show", "--no-osd", "--no-sub-autodetect-file");
    });
    public static LibVLC Instance => Runtime.Value;
    private static readonly Lazy<LibVLC> HeadlessRuntime = new(() => { Core.Initialize(); return new LibVLC("--ignore-config", "--no-audio", "--vout=dummy", "--avcodec-hw=none", "--no-video-title-show", "--no-osd", "--no-sub-autodetect-file"); });
    public static LibVLC Headless => HeadlessRuntime.Value;
    private static readonly Lazy<LibVLC> SoftwareRuntime = new(() => { Core.Initialize(); return new LibVLC("--ignore-config", "--no-audio", "--avcodec-hw=none", "--verbose=" + (Environment.GetEnvironmentVariable("REWIND_VIDEO_TRACE_FOLDER") is null ? "0" : "2"), "--no-video-title-show", "--no-osd", "--no-sub-autodetect-file"); });
    public static LibVLC Software => SoftwareRuntime.Value;
}
