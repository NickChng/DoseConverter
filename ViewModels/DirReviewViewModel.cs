using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static DoseConverter.Model;

namespace DoseConverter.ViewModels
{
    /// <summary>
    /// Drives the post-DIR quality review tab.
    ///
    /// The viewport alpha-blends the warped (deformed) source CT against the target CT.
    /// Three optional overlays can be toggled independently:
    ///   â€¢ Jacobian determinant heatmap (blue = compression, white = 1.0, red = expansion)
    ///   â€¢ Deformation grid (regular grid warped by the displacement field)
    ///   â€¢ Deformed dose colourmap (red-yellow, semi-transparent, with its own W/L slider)
    ///
    /// Window/level for each CT image is expressed as a [LowerHU, UpperHU] pair driven by
    /// <see cref="Views.RangeSlider"/> controls.  Slice navigation is via mouse-wheel
    /// (forwarded from the view via <see cref="ScrollSlice"/>).  Clicking the image calls
    /// <see cref="ToggleBlend"/> which mirrors the blend symmetrically (Blend â†’ 1 - Blend).
    /// </summary>
    public class DirReviewViewModel : ObservableObject
    {
        // Raw review volumes (flat [x + y*nx + z*nx*ny] layout), null until a DIR completes.
        private float[] _warpedMoving;
        private float[] _fixedCt;
        private float[] _jacobianDet;        // one float per voxel, same grid
        private float[] _dispField;          // interleaved [dx,dy,dz] per voxel (deformable-only for the grid overlay)
        private float[] _deformedDose;       // Gy, same grid as CT
        private float   _deformedDoseMaxGy  = 1f;
        private int _nx, _ny, _nz;
        private double[] _spacing   = { 1.0, 1.0, 1.0 };                       // [sx, sy, sz] mm
        private double[] _direction = { 1, 0, 0, 0, 1, 0, 0, 0, 1 };           // row-major 3x3 direction cosines

        // -----------------------------------------------------------------------
        // Availability
        // -----------------------------------------------------------------------

        private bool _hasData;
        public bool HasData
        {
            get => _hasData;
            private set { _hasData = value; RaisePropertyChangedEvent(nameof(HasData)); }
        }

        // -----------------------------------------------------------------------
        // Rendered images
        // -----------------------------------------------------------------------

        private ImageSource _displayImage;
        public ImageSource DisplayImage
        {
            get => _displayImage;
            private set { _displayImage = value; RaisePropertyChangedEvent(nameof(DisplayImage)); }
        }

        private ImageSource _jacobianOverlay;
        public ImageSource JacobianOverlay
        {
            get => _jacobianOverlay;
            private set { _jacobianOverlay = value; RaisePropertyChangedEvent(nameof(JacobianOverlay)); }
        }

        private ImageSource _gridOverlay;
        public ImageSource GridOverlay
        {
            get => _gridOverlay;
            private set { _gridOverlay = value; RaisePropertyChangedEvent(nameof(GridOverlay)); }
        }

        private ImageSource _doseOverlay;
        public ImageSource DoseOverlay
        {
            get => _doseOverlay;
            private set { _doseOverlay = value; RaisePropertyChangedEvent(nameof(DoseOverlay)); }
        }

        // -----------------------------------------------------------------------
        // Overlay toggles
        // -----------------------------------------------------------------------

        private bool _showJacobian;
        public bool ShowJacobian
        {
            get => _showJacobian;
            set
            {
                _showJacobian = value;
                RaisePropertyChangedEvent(nameof(ShowJacobian));
                if (value && _jacobianOverlay == null) RenderJacobian();
                RaisePropertyChangedEvent(nameof(JacobianOverlayVisibility));
            }
        }

        public Visibility JacobianOverlayVisibility =>
            (_showJacobian && _jacobianOverlay != null && HasData)
                ? Visibility.Visible : Visibility.Collapsed;

        private bool _showGrid;
        public bool ShowGrid
        {
            get => _showGrid;
            set
            {
                _showGrid = value;
                RaisePropertyChangedEvent(nameof(ShowGrid));
                if (value) RenderGrid();   // always re-render on toggle (slice may have changed)
                RaisePropertyChangedEvent(nameof(GridOverlayVisibility));
            }
        }

        public Visibility GridOverlayVisibility =>
            (_showGrid && _gridOverlay != null && HasData)
                ? Visibility.Visible : Visibility.Collapsed;

        private bool _showDose;
        public bool ShowDose
        {
            get => _showDose;
            set
            {
                _showDose = value;
                RaisePropertyChangedEvent(nameof(ShowDose));
                if (value && _deformedDose != null) RenderDose();
                RaisePropertyChangedEvent(nameof(DoseOverlayVisibility));
                RaisePropertyChangedEvent(nameof(DoseSliderVisibility));
            }
        }

        public Visibility DoseOverlayVisibility =>
            (_showDose && _doseOverlay != null && HasData)
                ? Visibility.Visible : Visibility.Collapsed;

        public Visibility DoseSliderVisibility =>
            (_showDose && _deformedDose != null && HasData)
                ? Visibility.Visible : Visibility.Collapsed;

        // -----------------------------------------------------------------------
        // Dose W/L
        // -----------------------------------------------------------------------

        private double _doseLowerGy = 0;
        public double DoseLowerGy
        {
            get => _doseLowerGy;
            set { _doseLowerGy = value; RaisePropertyChangedEvent(nameof(DoseLowerGy)); RenderDose(); }
        }

        private double _doseUpperGy = 70;
        public double DoseUpperGy
        {
            get => _doseUpperGy;
            set { _doseUpperGy = value; RaisePropertyChangedEvent(nameof(DoseUpperGy)); RenderDose(); }
        }

        public double DoseMaxGy => _deformedDoseMaxGy;

        // -----------------------------------------------------------------------
        // Slice navigation (driven by mouse wheel from the view)
        // -----------------------------------------------------------------------

        private int _sliceIndex;
        public int SliceIndex
        {
            get => _sliceIndex;
            private set
            {
                int clamped = Math.Max(0, Math.Min(value, _nz - 1));
                if (_sliceIndex == clamped) return;
                _sliceIndex = clamped;
                RaisePropertyChangedEvent(nameof(SliceIndex));
                RaisePropertyChangedEvent(nameof(SliceLabel));
                Render();
            }
        }

        public string SliceLabel => HasData ? $"Slice {_sliceIndex + 1} / {_nz}" : "";

        /// <summary>
        /// Called by the view's PreviewMouseWheel handler. Positive delta = scroll up = next slice.
        /// </summary>
        public void ScrollSlice(int delta)
        {
            if (!HasData) return;
            SliceIndex = _sliceIndex + (delta > 0 ? 1 : -1);
        }

        // -----------------------------------------------------------------------
        // Blend (0 = target only, 1 = DIR result only)
        // -----------------------------------------------------------------------

        private double _blend = 0.5;
        public double Blend
        {
            get => _blend;
            set { _blend = Math.Max(0, Math.Min(1, value)); RaisePropertyChangedEvent(nameof(Blend)); Render(); }
        }

        /// <summary>Mirrors the blend symmetrically: Blend â†’ 1 - Blend.</summary>
        public void ToggleBlend()
        {
            if (!HasData) return;
            Blend = 1.0 - _blend;
        }

        // -----------------------------------------------------------------------
        // Window bounds â€” DIR result
        // -----------------------------------------------------------------------

        private double _dirLowerHU = -100;
        public double DirLowerHU
        {
            get => _dirLowerHU;
            set { _dirLowerHU = value; RaisePropertyChangedEvent(nameof(DirLowerHU)); Render(); }
        }

        private double _dirUpperHU = 300;
        public double DirUpperHU
        {
            get => _dirUpperHU;
            set { _dirUpperHU = value; RaisePropertyChangedEvent(nameof(DirUpperHU)); Render(); }
        }

        // -----------------------------------------------------------------------
        // Window bounds â€” Target
        // -----------------------------------------------------------------------

        private double _targetLowerHU = -100;
        public double TargetLowerHU
        {
            get => _targetLowerHU;
            set { _targetLowerHU = value; RaisePropertyChangedEvent(nameof(TargetLowerHU)); Render(); }
        }

        private double _targetUpperHU = 300;
        public double TargetUpperHU
        {
            get => _targetUpperHU;
            set { _targetUpperHU = value; RaisePropertyChangedEvent(nameof(TargetUpperHU)); Render(); }
        }

        // -----------------------------------------------------------------------
        // Data load
        // -----------------------------------------------------------------------

        /// <summary>
        /// Loads a new DIR review payload and renders the central slice.
        /// Safe to call from the UI thread.
        /// </summary>
        public void Load(DeformableRegistrationService.DirReviewData data)
        {
            if (data == null || data.Size == null || data.Size.Length < 3
                || data.WarpedMovingCt == null || data.FixedCt == null)
            {
                HasData = false;
                DisplayImage = null;
                JacobianOverlay = null;
                GridOverlay = null;
                DoseOverlay = null;
                return;
            }

            _warpedMoving       = data.WarpedMovingCt;
            _fixedCt            = data.FixedCt;
            _jacobianDet        = data.JacobianDet;
            // Grid overlay uses the deformable-ONLY field so the rigid shift doesn't dominate the
            // picture; fall back to the composite field if the deformable-only field is unavailable.
            _dispField          = data.DeformableDisplacementField ?? data.DisplacementField;
            _deformedDose       = data.DeformedDoseGy;
            _deformedDoseMaxGy  = data.DeformedDoseMaxGy > 0 ? data.DeformedDoseMaxGy : 1f;

            if (data.Spacing   != null && data.Spacing.Length   == 3) _spacing   = data.Spacing;
            if (data.Direction != null && data.Direction.Length == 9) _direction = data.Direction;

            _nx = (int)data.Size[0];
            _ny = (int)data.Size[1];
            _nz = (int)data.Size[2];

            // Initialise dose W/L to full range
            _doseLowerGy = 0;
            _doseUpperGy = _deformedDoseMaxGy;
            RaisePropertyChangedEvent(nameof(DoseLowerGy));
            RaisePropertyChangedEvent(nameof(DoseUpperGy));
            RaisePropertyChangedEvent(nameof(DoseMaxGy));

            // Reset overlay images so stale data isn't shown
            JacobianOverlay = null;
            GridOverlay = null;
            DoseOverlay = null;

            _sliceIndex = _nz / 2;
            HasData = true;

            RaisePropertyChangedEvent(nameof(SliceIndex));
            RaisePropertyChangedEvent(nameof(SliceLabel));
            RaisePropertyChangedEvent(nameof(DoseSliderVisibility));

            Render();
        }

        // -----------------------------------------------------------------------
        // Rendering â€” base CT blend
        // -----------------------------------------------------------------------

        private void Render()
        {
            if (!HasData || _warpedMoving == null || _fixedCt == null) return;
            if (_sliceIndex < 0 || _sliceIndex >= _nz) return;

            int sliceSize = _nx * _ny;
            int baseIdx   = _sliceIndex * sliceSize;

            var    bmp    = new WriteableBitmap(_nx, _ny, 96, 96, PixelFormats.Bgr32, null);
            byte[] pixels = new byte[sliceSize * 4];

            double dirWindow = Math.Max(1, _dirUpperHU    - _dirLowerHU);
            double tgtWindow = Math.Max(1, _targetUpperHU - _targetLowerHU);
            double a = _blend;
            double b = 1.0 - _blend;

            for (int i = 0; i < sliceSize; i++)
            {
                int  src  = baseIdx + i;
                byte dir  = WindowMap(_warpedMoving[src], _dirLowerHU,    dirWindow);
                byte tgt  = WindowMap(_fixedCt[src],      _targetLowerHU, tgtWindow);
                byte gray = (byte)Math.Max(0, Math.Min(255, a * dir + b * tgt));

                int p = i * 4;
                pixels[p]     = gray;
                pixels[p + 1] = gray;
                pixels[p + 2] = gray;
                pixels[p + 3] = 255;
            }

            bmp.WritePixels(new Int32Rect(0, 0, _nx, _ny), pixels, _nx * 4, 0);
            bmp.Freeze();
            DisplayImage = bmp;

            // Refresh slice-dependent overlays
            if (_showGrid)   RenderGrid();
            if (_showDose)   RenderDose();
            // Jacobian is slice-indexed too
            if (_showJacobian) RenderJacobian();
        }

        // -----------------------------------------------------------------------
        // Rendering â€” Jacobian determinant heatmap
        // -----------------------------------------------------------------------

        private void RenderJacobian()
        {
            if (!HasData || _jacobianDet == null) { JacobianOverlay = null; return; }

            int sliceSize = _nx * _ny;
            int baseIdx   = _sliceIndex * sliceSize;

            var    bmp    = new WriteableBitmap(_nx, _ny, 96, 96, PixelFormats.Bgra32, null);
            byte[] pixels = new byte[sliceSize * 4];

            // Colour map: compress(<1) â†’ blue, 1.0 â†’ transparent white, expand(>1) â†’ red
            // Meaningful range roughly [0.5, 1.5]; saturate outside.
            const float low  = 0.5f;
            const float mid  = 1.0f;
            const float high = 1.5f;
            const byte  alpha = 180; // semi-transparent

            for (int i = 0; i < sliceSize; i++)
            {
                float jac = _jacobianDet[baseIdx + i];
                byte r, g, bl;

                if (jac <= mid)
                {
                    // blue (compression) â†’ white at mid
                    float t = Math.Max(0f, (jac - low) / (mid - low)); // 0=blue, 1=white
                    bl = 255;
                    g  = (byte)(t * 255);
                    r  = (byte)(t * 255);
                }
                else
                {
                    // white at mid â†’ red (expansion)
                    float t = Math.Min(1f, (jac - mid) / (high - mid)); // 0=white, 1=red
                    r  = 255;
                    g  = (byte)((1 - t) * 255);
                    bl = (byte)((1 - t) * 255);
                }

                // Fade to transparent near mid (value â‰ˆ 1) so the CT shows through clearly
                float deviation = Math.Abs(jac - 1.0f);
                byte a = (byte)Math.Min(alpha, deviation * 2f * alpha / (high - mid) * 2);

                int p = i * 4;
                pixels[p]     = bl;  // B
                pixels[p + 1] = g;   // G
                pixels[p + 2] = r;   // R
                pixels[p + 3] = a;   // A
            }

            bmp.WritePixels(new Int32Rect(0, 0, _nx, _ny), pixels, _nx * 4, 0);
            bmp.Freeze();
            JacobianOverlay = bmp;
            RaisePropertyChangedEvent(nameof(JacobianOverlayVisibility));
        }

        // -----------------------------------------------------------------------
        // Rendering â€” deformation grid
        // -----------------------------------------------------------------------

        /// <summary>
        /// Renders a regular grid (every <c>GridSpacing</c> pixels) warped by the
        /// 2-D slice of the displacement field.  Grid lines are drawn in green with
        /// partial transparency so the underlying image remains visible.
        /// </summary>
        private const int GridSpacingPx = 20;

        private void RenderGrid()
        {
            if (!HasData || _dispField == null) { GridOverlay = null; return; }

            int sliceSize  = _nx * _ny;
            int baseIdx    = _sliceIndex * sliceSize;   // voxel offset for this slice
            // Displacement field is interleaved [dx,dy,dz]; stride per voxel = 3
            int fieldBase  = baseIdx * 3;

            var    bmp    = new WriteableBitmap(_nx, _ny, 96, 96, PixelFormats.Bgra32, null);
            byte[] pixels = new byte[sliceSize * 4];   // initialised to fully transparent

            // Helper: draw a line segment in the pixel buffer using Bresenham's line algorithm
            void DrawLine(int x0, int y0, int x1, int y1, byte r, byte g, byte b, byte a)
            {
                int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
                int dy = Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
                int err = (dx > dy ? dx : -dy) / 2;
                while (true)
                {
                    if (x0 >= 0 && x0 < _nx && y0 >= 0 && y0 < _ny)
                    {
                        int p = (y0 * _nx + x0) * 4;
                        pixels[p]     = b;
                        pixels[p + 1] = g;
                        pixels[p + 2] = r;
                        pixels[p + 3] = a;
                    }
                    if (x0 == x1 && y0 == y1) break;
                    int e2 = err;
                    if (e2 > -dx) { err -= dy; x0 += sx; }
                    if (e2 <  dy) { err += dx; y0 += sy; }
                }
            }

            // Convert a world-frame displacement (mm) at grid node (gx,gy) to a warped pixel
            // position.  SimpleITK displacement fields are in PHYSICAL space, so project the vector
            // onto the in-plane image axes (columns of the direction matrix) and divide by spacing to
            // get index-space offsets.  Reduces to (dx/sx, dy/sy) for an identity direction; using the
            // direction makes the overlay orientation-correct instead of mirrored on non-axial frames.
            void WarpNode(int gx, int gy, int fi, out int wx, out int wy)
            {
                double dwx = _dispField[fi], dwy = _dispField[fi + 1], dwz = _dispField[fi + 2];
                double diX = (dwx * _direction[0] + dwy * _direction[3] + dwz * _direction[6]) / _spacing[0];
                double diY = (dwx * _direction[1] + dwy * _direction[4] + dwz * _direction[7]) / _spacing[1];
                wx = gx + (int)Math.Round(diX);
                wy = gy + (int)Math.Round(diY);
            }

            // Draw horizontal grid lines: for each row that is a multiple of GridSpacingPx,
            // connect consecutive warped nodes along that row.
            for (int gy = 0; gy < _ny; gy += GridSpacingPx)
            {
                int prevWx = -1, prevWy = -1;
                for (int gx = 0; gx < _nx; gx++)
                {
                    int voxelIdx = gy * _nx + gx;
                    int fi = fieldBase + voxelIdx * 3;
                    WarpNode(gx, gy, fi, out int wx, out int wy);
                    if (prevWx >= 0)
                        DrawLine(prevWx, prevWy, wx, wy, 0, 220, 0, 200);
                    prevWx = wx; prevWy = wy;
                }
            }

            // Draw vertical grid lines
            for (int gx = 0; gx < _nx; gx += GridSpacingPx)
            {
                int prevWx = -1, prevWy = -1;
                for (int gy = 0; gy < _ny; gy++)
                {
                    int voxelIdx = gy * _nx + gx;
                    int fi = fieldBase + voxelIdx * 3;
                    WarpNode(gx, gy, fi, out int wx, out int wy);
                    if (prevWx >= 0)
                        DrawLine(prevWx, prevWy, wx, wy, 0, 220, 0, 200);
                    prevWx = wx; prevWy = wy;
                }
            }

            bmp.WritePixels(new Int32Rect(0, 0, _nx, _ny), pixels, _nx * 4, 0);
            bmp.Freeze();
            GridOverlay = bmp;
            RaisePropertyChangedEvent(nameof(GridOverlayVisibility));
        }

        // -----------------------------------------------------------------------
        // Rendering â€” deformed dose colourmap
        // -----------------------------------------------------------------------

        private void RenderDose()
        {
            if (!HasData || _deformedDose == null) { DoseOverlay = null; return; }

            int sliceSize = _nx * _ny;
            int baseIdx   = _sliceIndex * sliceSize;

            var    bmp    = new WriteableBitmap(_nx, _ny, 96, 96, PixelFormats.Bgra32, null);
            byte[] pixels = new byte[sliceSize * 4];

            double doseWindow = Math.Max(0.01, _doseUpperGy - _doseLowerGy);

            for (int i = 0; i < sliceSize; i++)
            {
                float dose = _deformedDose[baseIdx + i];
                if (dose <= 0f) { /* leave fully transparent */ continue; }

                double t = Math.Max(0, Math.Min(1, (dose - _doseLowerGy) / doseWindow));

                // Red-yellow colourmap: low â†’ dark red â†’ orange â†’ yellow
                // Eclipse-style rainbow: blue -> cyan -> green -> yellow -> red
                byte r, g, bl;
                if (t < 0.25)
                {
                    double s = t / 0.25;
                    r  = 0;
                    g  = (byte)(s * 255);
                    bl = 255;
                }
                else if (t < 0.5)
                {
                    double s = (t - 0.25) / 0.25;
                    r  = 0;
                    g  = 255;
                    bl = (byte)((1 - s) * 255);
                }
                else if (t < 0.75)
                {
                    double s = (t - 0.5) / 0.25;
                    r  = (byte)(s * 255);
                    g  = 255;
                    bl = 0;
                }
                else
                {
                    double s = (t - 0.75) / 0.25;
                    r  = 255;
                    g  = (byte)((1 - s) * 255);
                    bl = 0;
                }
                byte a = (byte)(160 + t * 80);  // 160-240: more opaque at high dose

                int p = i * 4;
                pixels[p]     = bl;  // B
                pixels[p + 1] = g;   // G
                pixels[p + 2] = r;   // R
                pixels[p + 3] = a;   // A
            }

            bmp.WritePixels(new Int32Rect(0, 0, _nx, _ny), pixels, _nx * 4, 0);
            bmp.Freeze();
            DoseOverlay = bmp;
            RaisePropertyChangedEvent(nameof(DoseOverlayVisibility));
        }

        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>Maps a voxel intensity to 0â€“255 given a window defined by its lower bound and width.</summary>
        private static byte WindowMap(double value, double low, double window)
        {
            double t = (value - low) / window;
            if (t <= 0) return 0;
            if (t >= 1) return 255;
            return (byte)(t * 255.0);
        }
    }
}
