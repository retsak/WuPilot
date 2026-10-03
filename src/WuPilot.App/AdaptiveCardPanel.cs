using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace WuPilot.App;

// Measure each row using its tallest card so wrapped status text is never clipped.
public sealed class AdaptiveCardPanel : Panel
{
    private const double Gap = 12;
    public double MinimumItemWidth { get; set; } = 320;
    private int Columns(double width) => double.IsInfinity(width) ? 1 : Math.Clamp((int)((width + Gap) / (MinimumItemWidth + Gap)), 1, 2);

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var width = double.IsInfinity(availableSize.Width) ? MinimumItemWidth : availableSize.Width;
        var cardWidth = Math.Max(0, (width - Gap * (columns - 1)) / columns);
        var height = 0d;
        for (var index = 0; index < Children.Count; index += columns)
        {
            var rowHeight = 0d;
            for (var column = 0; column < columns && index + column < Children.Count; column++)
            {
                var child = Children[index + column];
                child.Measure(new Size(cardWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
            }
            height += rowHeight + (index == 0 ? 0 : Gap);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var cardWidth = Math.Max(0, (finalSize.Width - Gap * (columns - 1)) / columns);
        var top = 0d;
        for (var index = 0; index < Children.Count; index += columns)
        {
            var rowHeight = Children.Skip(index).Take(columns).Max(child => child.DesiredSize.Height);
            for (var column = 0; column < columns && index + column < Children.Count; column++)
                Children[index + column].Arrange(new Rect(column * (cardWidth + Gap), top, cardWidth, rowHeight));
            top += rowHeight + Gap;
        }
        return finalSize;
    }
}
