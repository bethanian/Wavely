namespace Wavely.Core;

public sealed class CaptureTimeline(int SampleRate, int DeviceSampleRate = 0)
{
    private bool Initialized;
    private long ActiveSegmentQpc;
    private long PreviousDeviceFrame;
    private int PreviousFrameCount;
    private long NextOutputFrame;

    public long Place(long DeviceFrame, long PacketQpc, int FrameCount, long SegmentFrame, long SegmentQpc)
    {
        if (FrameCount == 0) return NextOutputFrame;
        long OutputFrame;
        if (!Initialized || ActiveSegmentQpc != SegmentQpc || DeviceFrame < PreviousDeviceFrame)
        {
            OutputFrame = SegmentFrame + (long)Math.Round((PacketQpc - SegmentQpc) * SampleRate / 10_000_000d);
            ActiveSegmentQpc = SegmentQpc;
            Initialized = true;
        }
        else
        {
            var Gap = (long)Math.Round((DeviceFrame - PreviousDeviceFrame) * (double)SampleRate / (DeviceSampleRate > 0 ? DeviceSampleRate : SampleRate)) - PreviousFrameCount;
            OutputFrame = checked(NextOutputFrame + (Gap > SampleRate / 500 ? Gap : 0));
        }
        PreviousDeviceFrame = DeviceFrame;
        PreviousFrameCount = FrameCount;
        NextOutputFrame = checked(OutputFrame + FrameCount);
        return OutputFrame;
    }
}
