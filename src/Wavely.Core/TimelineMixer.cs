namespace Wavely.Core;

public sealed class TimelineMixer
{
    private readonly float[][] Samples;
    private readonly long[][] Tags;
    private readonly int Capacity;
    public long WrittenFrames { get; private set; }
    public int Sources => Samples.Length;
    public long LateFrames { get; private set; }

    public TimelineMixer(int SampleRate, int SourceCount)
    {
        Capacity = SampleRate * 4;
        Samples = Enumerable.Range(0, SourceCount).Select(_ => new float[Capacity * 2]).ToArray();
        Tags = Enumerable.Range(0, SourceCount).Select(_ => Enumerable.Repeat(-1L, Capacity).ToArray()).ToArray();
    }

    public void Add(int Source, long StartFrame, ReadOnlySpan<float> Data)
    {
        for (var Frame = 0; Frame < Data.Length / 2; Frame++)
        {
            var Position = StartFrame + Frame;
            if (Position < WrittenFrames) { LateFrames++; continue; }
            if (Position >= WrittenFrames + Capacity) throw new IOException("Audio processing fell behind. The captured portion is preserved.");
            var Slot = (int)(Position % Capacity);
            Samples[Source][Slot * 2] = float.IsFinite(Data[Frame * 2]) ? Data[Frame * 2] : 0;
            Samples[Source][Slot * 2 + 1] = float.IsFinite(Data[Frame * 2 + 1]) ? Data[Frame * 2 + 1] : 0;
            Tags[Source][Slot] = Position;
        }
    }

    public float Render(Span<byte> Output)
    {
        var Peak = 0f;
        for (var Frame = 0; Frame < Output.Length / 4; Frame++, WrittenFrames++)
        {
            var Slot = (int)(WrittenFrames % Capacity);
            for (var Channel = 0; Channel < 2; Channel++)
            {
                var Value = 0f;
                for (var Source = 0; Source < Sources; Source++)
                    if (Tags[Source][Slot] == WrittenFrames) Value += Samples[Source][Slot * 2 + Channel] / Sources;
                Value = Math.Clamp(Value, -1f, 1f);
                Peak = Math.Max(Peak, Math.Abs(Value));
                System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(Output.Slice(Frame * 4 + Channel * 2, 2), (short)Math.Round(Value * 32767));
            }
        }
        return Peak;
    }
}
