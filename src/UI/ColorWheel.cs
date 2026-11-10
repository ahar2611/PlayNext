using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace PlayNext.UI
{
    /// <summary>
    /// A small HSV color picker: click or drag on the wheel for hue/saturation, the slider sets brightness.
    /// Two-way bound to a "#RRGGBB" string through <see cref="Hex"/>.
    /// </summary>
    public class ColorWheel : UserControl
    {
        public static readonly DependencyProperty HexProperty = DependencyProperty.Register(
            nameof(Hex), typeof(string), typeof(ColorWheel),
            new FrameworkPropertyMetadata("#E60023", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnHexChanged));

        public string Hex
        {
            get => (string)GetValue(HexProperty);
            set => SetValue(HexProperty, value);
        }

        private const int WheelSize = 150;
        private readonly Grid wheelHost;
        private readonly Ellipse dimmer;
        private readonly Ellipse marker;
        private readonly TranslateTransform markerPosition = new TranslateTransform();
        private readonly Slider brightness;
        private readonly Border brightnessTrack;
        private double hue, saturation, value = 1;
        private bool updating;

        public ColorWheel()
        {
            wheelHost = new Grid { Width = WheelSize, Height = WheelSize, Cursor = Cursors.Cross, Background = Brushes.Transparent, HorizontalAlignment = HorizontalAlignment.Left };
            wheelHost.Children.Add(new Image { Source = BuildWheel(WheelSize), Width = WheelSize, Height = WheelSize });
            dimmer = new Ellipse { Fill = Brushes.Black, Opacity = 0, IsHitTestVisible = false };
            wheelHost.Children.Add(dimmer);
            marker = new Ellipse
            {
                Width = 16,
                Height = 16,
                Stroke = Brushes.White,
                StrokeThickness = 2.5,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                RenderTransform = markerPosition,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Opacity = 0.8 }
            };
            wheelHost.Children.Add(marker);

            wheelHost.MouseLeftButtonDown += (s, e) => { wheelHost.CaptureMouse(); Pick(e.GetPosition(wheelHost)); };
            wheelHost.MouseMove += (s, e) => { if (wheelHost.IsMouseCaptured) Pick(e.GetPosition(wheelHost)); };
            wheelHost.MouseLeftButtonUp += (s, e) => wheelHost.ReleaseMouseCapture();

            brightnessTrack = new Border { Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 0, 0, 2), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            brightness = new Slider { Minimum = 0.2, Maximum = 1, Value = 1, Width = WheelSize, ToolTip = "Brightness" };
            brightness.ValueChanged += (s, e) =>
            {
                if (updating) return;
                value = brightness.Value;
                Commit();
            };
            var sliderHost = new Grid { Width = WheelSize, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
            sliderHost.Children.Add(brightnessTrack);
            sliderHost.Children.Add(brightness);

            var root = new StackPanel();
            root.Children.Add(wheelHost);
            root.Children.Add(sliderHost);
            Content = root;
            Loaded += (s, e) => LoadFromHex(Hex);
        }

        private static void OnHexChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var wheel = (ColorWheel)d;
            if (!wheel.updating) wheel.LoadFromHex(e.NewValue as string);
        }

        private void LoadFromHex(string hex)
        {
            if (!AccentColors.TryParse(hex, out var color)) return;
            AccentColors.ToHsv(color, out hue, out saturation, out value);
            updating = true;
            brightness.Value = Math.Max(brightness.Minimum, value);
            updating = false;
            UpdateVisuals();
        }

        private void Pick(Point p)
        {
            var c = WheelSize / 2.0;
            double dx = p.X - c, dy = p.Y - c;
            hue = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
            saturation = Math.Min(1, Math.Sqrt(dx * dx + dy * dy) / c);
            Commit();
        }

        private void Commit()
        {
            updating = true;
            Hex = AccentColors.ToHex(AccentColors.FromHsv(hue, saturation, value));
            updating = false;
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            var c = WheelSize / 2.0;
            var r = saturation * c;
            var rad = hue * Math.PI / 180;
            markerPosition.X = c + Math.Cos(rad) * r - marker.Width / 2;
            markerPosition.Y = c + Math.Sin(rad) * r - marker.Height / 2;
            marker.Fill = new SolidColorBrush(AccentColors.FromHsv(hue, saturation, value));
            dimmer.Opacity = 1 - value;
            brightnessTrack.Background = new LinearGradientBrush(Colors.Black, AccentColors.FromHsv(hue, saturation, 1), 0);
        }

        /// <summary>Hue around the circle, saturation from the center out, full brightness; soft anti-aliased edge.</summary>
        private static BitmapSource BuildWheel(int size)
        {
            var bmp = new WriteableBitmap(size, size, 96, 96, PixelFormats.Bgra32, null);
            var pixels = new byte[size * size * 4];
            var c = (size - 1) / 2.0;
            var radius = size / 2.0 - 1;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    double dx = x - c, dy = y - c;
                    var dist = Math.Sqrt(dx * dx + dy * dy);
                    var alpha = Math.Max(0, Math.Min(1, radius + 0.5 - dist));
                    if (alpha <= 0) continue;
                    var h = (Math.Atan2(dy, dx) * 180 / Math.PI + 360) % 360;
                    var color = AccentColors.FromHsv(h, Math.Min(1, dist / radius), 1);
                    var i = (y * size + x) * 4;
                    pixels[i] = color.B;
                    pixels[i + 1] = color.G;
                    pixels[i + 2] = color.R;
                    pixels[i + 3] = (byte)(alpha * 255);
                }
            }
            bmp.WritePixels(new Int32Rect(0, 0, size, size), pixels, size * 4, 0);
            bmp.Freeze();
            return bmp;
        }
    }
}
