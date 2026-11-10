using System;
using System.Windows;
using System.Windows.Controls;

namespace PlayNext.UI
{
    /// <summary>
    /// Wraps children onto rows like a WrapPanel. When <see cref="IsCollapsed"/> is true only the first row is shown;
    /// children that would land on later rows are hidden entirely (never cut in half).
    /// </summary>
    public class CollapsibleWrapPanel : Panel
    {
        public static readonly DependencyProperty IsCollapsedProperty = DependencyProperty.Register(
            nameof(IsCollapsed), typeof(bool), typeof(CollapsibleWrapPanel),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsArrange));

        public bool IsCollapsed
        {
            get => (bool)GetValue(IsCollapsedProperty);
            set => SetValue(IsCollapsedProperty, value);
        }

        protected override Size MeasureOverride(Size available)
        {
            double rowWidth = 0, rowHeight = 0, totalHeight = 0, maxWidth = 0;
            int row = 0;
            foreach (UIElement child in InternalChildren)
            {
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var size = child.DesiredSize;
                if (rowWidth > 0 && rowWidth + size.Width > available.Width)
                {
                    if (row == 0 || !IsCollapsed) totalHeight += rowHeight;
                    maxWidth = Math.Max(maxWidth, rowWidth);
                    row++;
                    rowWidth = 0;
                    rowHeight = 0;
                }
                rowWidth += size.Width;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
            if (row == 0 || !IsCollapsed) totalHeight += rowHeight;
            maxWidth = Math.Max(maxWidth, rowWidth);
            var width = double.IsInfinity(available.Width) ? maxWidth : Math.Min(maxWidth, available.Width);
            return new Size(width, totalHeight);
        }

        protected override Size ArrangeOverride(Size final)
        {
            double x = 0, y = 0, rowHeight = 0;
            int row = 0;
            foreach (UIElement child in InternalChildren)
            {
                var size = child.DesiredSize;
                if (x > 0 && x + size.Width > final.Width)
                {
                    row++;
                    y += rowHeight;
                    x = 0;
                    rowHeight = 0;
                }
                if (IsCollapsed && row > 0)
                {
                    child.Arrange(new Rect(0, 0, 0, 0)); // hidden: doesn't fit on the first row
                    continue;
                }
                child.Arrange(new Rect(x, y, size.Width, size.Height));
                x += size.Width;
                rowHeight = Math.Max(rowHeight, size.Height);
            }
            return final;
        }
    }
}
