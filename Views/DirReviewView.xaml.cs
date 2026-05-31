using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DoseConverter.ViewModels;

namespace DoseConverter.Views
{
    public partial class DirReviewView : UserControl
    {
        // Current zoom scale (1.0 = 100%).
        private double _zoom = 1.0;
        private const double ZoomStep = 1.20;
        private const double MaxZoom = 3.0;

        public DirReviewView()
        {
            InitializeComponent();

            // Mouse wheel anywhere over the image border scrolls through slices.
            ImageBorder.PreviewMouseWheel += OnImageMouseWheel;

            // Left-click on the image toggles the blend symmetrically.
            ReviewImage.MouseLeftButtonDown += OnImageClick;

            // Right-click magnifies 20% around the clicked point.
            ImageBorder.MouseRightButtonDown += OnImageRightClick;

            // Reset Zoom button click.
            ResetZoomButton.Click += OnResetZoomClick;
        }

        private void OnImageMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (DataContext is DirReviewViewModel vm)
                vm.ScrollSlice(e.Delta);
            e.Handled = true;
        }

        private void OnImageClick(object sender, MouseButtonEventArgs e)
        {
            if (DataContext is DirReviewViewModel vm)
                vm.ToggleBlend();
            e.Handled = true;
        }

        private void OnImageRightClick(object sender, MouseButtonEventArgs e)
        {
            if (_zoom >= MaxZoom)
                return;

            double newZoom = System.Math.Min(_zoom * ZoomStep, MaxZoom);

            // The point the user clicked relative to the ImageStack.
            Point clickInStack = e.GetPosition(ImageStack);

            // After scaling, we want that same logical point to stay under the cursor.
            // New translate so that:  clickInStack * newZoom + translate = clickInStack * _zoom + currentTranslate
            double tx = ZoomTranslate.X + clickInStack.X * (_zoom - newZoom);
            double ty = ZoomTranslate.Y + clickInStack.Y * (_zoom - newZoom);

            // Clamp so we don't pan the content fully off-screen.
            double stackW = ImageStack.ActualWidth;
            double stackH = ImageStack.ActualHeight;
            tx = System.Math.Max(tx, stackW * (1 - newZoom));
            tx = System.Math.Min(tx, 0);
            ty = System.Math.Max(ty, stackH * (1 - newZoom));
            ty = System.Math.Min(ty, 0);

            _zoom = newZoom;
            ApplyZoom(tx, ty);
            e.Handled = true;
        }

        private void OnResetZoomClick(object sender, RoutedEventArgs e)
        {
            _zoom = 1.0;
            ApplyZoom(0, 0);
        }

        private void ApplyZoom(double tx, double ty)
        {
            ZoomScale.ScaleX = _zoom;
            ZoomScale.ScaleY = _zoom;
            ZoomTranslate.X  = tx;
            ZoomTranslate.Y  = ty;

            // Show/hide the Reset Zoom button.
            ResetZoomButton.Visibility = _zoom > 1.001 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
