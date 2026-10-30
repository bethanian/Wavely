using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Wavely.App;

public sealed class WaveformView : Canvas
{
    private readonly List<Rectangle> Bars = [];
    private readonly float[] Values = new float[144];
    private readonly SolidColorBrush ActiveBrush = new(ColorHelper.FromArgb(255, 217, 65, 86));
    private readonly SolidColorBrush QuietBrush = new(ColorHelper.FromArgb(120, 135, 141, 154));
    private bool IsLive;
    private double Progress;
    public WaveformView()
    {
        IsHitTestVisible = false;
        for (var Index = 0; Index < Values.Length; Index++)
        {
            var Bar = new Rectangle { RadiusX = 1, RadiusY = 1, Fill = QuietBrush, Width = 2 };
            Bars.Add(Bar);
            Children.Add(Bar);
        }
        SizeChanged += (_, _) => Draw();
    }
    public void Push(float Peak)
    {
        Array.Copy(Values, 1, Values, 0, Values.Length - 1);
        Values[^1] = Peak;
        IsLive = true;
        Draw();
    }
    public void SetPeaks(float[] Peaks)
    {
        for (var Index = 0; Index < Values.Length; Index++) Values[Index] = Peaks.Length > 0 ? Peaks[Math.Min(Peaks.Length - 1, Index * Peaks.Length / Values.Length)] : 0;
        IsLive = false;
        Progress = 0;
        Draw();
    }
    public void SetProgress(double Value) { if ((int)(Progress * Bars.Count) == (int)(Value * Bars.Count)) return; Progress = Value; Draw(); }
    private void Draw()
    {
        var Width = ActualWidth;
        var Height = ActualHeight;
        for (var Index = 0; Index < Bars.Count; Index++)
        {
            var Bar = Bars[Index];
            Bar.Width = Math.Max(1, Width / Bars.Count * 0.48);
            Bar.Height = Math.Max(2, Math.Sqrt(Values[Index]) * Math.Max(0, Height - 50));
            SetLeft(Bar, Index * Width / Bars.Count);
            SetTop(Bar, (Height - Bar.Height) / 2);
            Bar.Fill = IsLive || Index / (double)Bars.Count < Progress ? ActiveBrush : QuietBrush;
        }
    }
}
