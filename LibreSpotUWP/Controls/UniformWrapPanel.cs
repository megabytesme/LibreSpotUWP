using System;
using Windows.Foundation;
using Windows.UI.Xaml.Controls;

namespace LibreSpotUWP.Controls
{
    public class UniformWrapPanel : Panel
    {
        public double ItemWidth { get; set; } = 120;
        public double ItemMargin { get; set; } = 8;

        protected override Size MeasureOverride(Size availableSize)
        {
            double x = 0;
            double rowHeight = 0;
            double totalHeight = 0;

            foreach (var child in Children)
            {
                child.Measure(new Size(ItemWidth, double.PositiveInfinity));

                if (x + ItemWidth > availableSize.Width)
                {
                    totalHeight += rowHeight + ItemMargin;
                    x = 0;
                    rowHeight = 0;
                }

                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);
                x += ItemWidth + ItemMargin;
            }

            totalHeight += rowHeight;

            return new Size(availableSize.Width, totalHeight);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double x = 0;
            double y = 0;
            double rowHeight = 0;

            foreach (var child in Children)
            {
                if (x + ItemWidth > finalSize.Width)
                {
                    y += rowHeight + ItemMargin;
                    x = 0;
                    rowHeight = 0;
                }

                rowHeight = Math.Max(rowHeight, child.DesiredSize.Height);

                child.Arrange(new Rect(x, y, ItemWidth, child.DesiredSize.Height));

                x += ItemWidth + ItemMargin;
            }

            return finalSize;
        }
    }
}