using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DoseConverter.Views
{
    /// <summary>
    /// Vertical dual-thumb range slider.
    /// The visual top = Maximum (high HU), visual bottom = Minimum (low HU).
    /// Exposes <see cref="LowerValue"/> and <see cref="UpperValue"/> as dependency
    /// properties; the implied window = UpperValue - LowerValue, level = midpoint.
    /// </summary>
    public partial class RangeSlider : UserControl
    {
        // -----------------------------------------------------------------------
        // Dependency properties
        // -----------------------------------------------------------------------

        public static readonly DependencyProperty MinimumProperty =
            DependencyProperty.Register(nameof(Minimum), typeof(double), typeof(RangeSlider),
                new PropertyMetadata(-1024.0, OnRangeChanged));

        public static readonly DependencyProperty MaximumProperty =
            DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RangeSlider),
                new PropertyMetadata(3072.0, OnRangeChanged));

        public static readonly DependencyProperty LowerValueProperty =
            DependencyProperty.Register(nameof(LowerValue), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(-100.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnRangeChanged));

        public static readonly DependencyProperty UpperValueProperty =
            DependencyProperty.Register(nameof(UpperValue), typeof(double), typeof(RangeSlider),
                new FrameworkPropertyMetadata(300.0,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnRangeChanged));

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }
        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }
        public double LowerValue
        {
            get => (double)GetValue(LowerValueProperty);
            set => SetValue(LowerValueProperty, Math.Max(Minimum, Math.Min(value, UpperValue)));
        }
        public double UpperValue
        {
            get => (double)GetValue(UpperValueProperty);
            set => SetValue(UpperValueProperty, Math.Max(LowerValue, Math.Min(value, Maximum)));
        }

        private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((RangeSlider)d).Layout();

        // -----------------------------------------------------------------------
        // Drag state
        // -----------------------------------------------------------------------

        private enum DragTarget { None, Lower, Upper }
        private DragTarget _drag = DragTarget.None;
        private double _dragStartY;
        private double _dragStartValue;
        private const double ThumbR = 8; // half thumb size

        // -----------------------------------------------------------------------
        // Construction
        // -----------------------------------------------------------------------

        public RangeSlider()
        {
            InitializeComponent();
            Loaded += (_, __) => Layout();
            SizeChanged += (_, __) => Layout();

            ThumbUpper.MouseLeftButtonDown += (s, e) => StartDrag(e, DragTarget.Upper);
            ThumbLower.MouseLeftButtonDown += (s, e) => StartDrag(e, DragTarget.Lower);
            TrackCanvas.MouseLeftButtonDown += OnTrackClick;
            TrackCanvas.MouseMove += OnMouseMove;
            TrackCanvas.MouseLeftButtonUp += OnMouseUp;
            TrackCanvas.MouseLeave += (_, __) => { if (_drag != DragTarget.None) { _drag = DragTarget.None; TrackCanvas.ReleaseMouseCapture(); } };
        }

        // -----------------------------------------------------------------------
        // Layout
        // -----------------------------------------------------------------------

        private void Layout()
        {
            if (TrackCanvas == null) return;

            double h = TrackCanvas.ActualHeight;
            if (h <= 0) h = ActualHeight - 60; // approx before first render
            if (h <= 0) return;

            double range = Maximum - Minimum;
            if (range <= 0) return;

            // Position track groove centred horizontally
            double cx = TrackCanvas.ActualWidth > 0 ? TrackCanvas.ActualWidth / 2.0 : 10;

            Canvas.SetLeft(TrackRect, cx - 3);
            Canvas.SetTop(TrackRect, 0);
            TrackRect.Height = h;

            // Map values → Y positions (top = Maximum)
            double yUpper = ValueToY(UpperValue, h);
            double yLower = ValueToY(LowerValue, h);

            // Thumb centres
            Canvas.SetLeft(ThumbUpper, cx - ThumbR);
            Canvas.SetTop(ThumbUpper, yUpper - ThumbR);
            Canvas.SetLeft(ThumbLower, cx - ThumbR);
            Canvas.SetTop(ThumbLower, yLower - ThumbR);

            // Highlight rect between thumbs (yUpper < yLower since up = larger value)
            Canvas.SetLeft(RangeRect, cx - 3);
            Canvas.SetTop(RangeRect, yUpper);
            RangeRect.Height = Math.Max(0, yLower - yUpper);

            // Labels
            LabelUpper.Text = $"{UpperValue:F0}";
            LabelLower.Text = $"{LowerValue:F0}";
            double window = UpperValue - LowerValue;
            double level = (UpperValue + LowerValue) / 2.0;
            LabelWL.Text = $"W:{window:F0}\nL:{level:F0}";
        }

        private double ValueToY(double value, double height)
        {
            double fraction = (value - Minimum) / (Maximum - Minimum);
            return (1.0 - fraction) * height; // inverted: top = max
        }

        private double YToValue(double y, double height)
        {
            if (height <= 0) return Minimum;
            double fraction = 1.0 - y / height;
            return Minimum + fraction * (Maximum - Minimum);
        }

        // -----------------------------------------------------------------------
        // Drag / interaction
        // -----------------------------------------------------------------------

        private void StartDrag(MouseButtonEventArgs e, DragTarget target)
        {
            _drag = target;
            _dragStartY = e.GetPosition(TrackCanvas).Y;
            _dragStartValue = target == DragTarget.Upper ? UpperValue : LowerValue;
            TrackCanvas.CaptureMouse();
            e.Handled = true;
        }

        private void OnTrackClick(object sender, MouseButtonEventArgs e)
        {
            // Click on the track (not on a thumb) — snap the nearest thumb to the click
            double y = e.GetPosition(TrackCanvas).Y;
            double h = TrackCanvas.ActualHeight;
            double clickedValue = YToValue(y, h);

            double distUpper = Math.Abs(clickedValue - UpperValue);
            double distLower = Math.Abs(clickedValue - LowerValue);

            if (distUpper <= distLower)
                UpperValue = Math.Max(LowerValue, Math.Min(Maximum, clickedValue));
            else
                LowerValue = Math.Max(Minimum, Math.Min(UpperValue, clickedValue));

            Layout();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (_drag == DragTarget.None || e.LeftButton != MouseButtonState.Pressed) return;

            double h = TrackCanvas.ActualHeight;
            double currentY = e.GetPosition(TrackCanvas).Y;
            double deltaY = currentY - _dragStartY;
            // Invert because up = higher value
            double deltaValue = -deltaY / h * (Maximum - Minimum);
            double newValue = _dragStartValue + deltaValue;

            if (_drag == DragTarget.Upper)
                UpperValue = Math.Max(LowerValue, Math.Min(Maximum, newValue));
            else
                LowerValue = Math.Max(Minimum, Math.Min(UpperValue, newValue));

            Layout();
        }

        private void OnMouseUp(object sender, MouseButtonEventArgs e)
        {
            _drag = DragTarget.None;
            TrackCanvas.ReleaseMouseCapture();
        }
    }
}
