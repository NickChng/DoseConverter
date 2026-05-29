using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMS.TPS.Common.Model.Types;

namespace DoseConverter.Tests
{
    // ---------------------------------------------------------------------------
    // Tests for the two internal static mask-rasterization helpers extracted from
    // DeformableRegistrationService:
    //   • ProjectPolygonToPixelSpace  – world-space VVector polygon → pixel coords
    //   • FillPolygonIntoSlice        – scanline even-odd fill into a byte[] mask
    // Both are accessible via [InternalsVisibleTo("DoseConverter.Tests")].
    // ---------------------------------------------------------------------------

    [TestClass]
    public class ProjectPolygonToPixelSpaceTests
    {
        // Convenience: build a VVector from (x,y,z)
        private static VVector V(double x, double y, double z) => new VVector(x, y, z);

        // Identity axes
        private static readonly VVector XAxis = V(1, 0, 0);
        private static readonly VVector YAxis = V(0, 1, 0);
        private static readonly VVector Origin = V(0, 0, 0);

        [TestMethod]
        public void IdentityGeometry_WorldCoordsEqualPixelCoords()
        {
            var poly = new[] { V(0, 0, 0), V(3, 0, 0), V(3, 4, 0) };
            var (px, py) = DeformableRegistrationService.ProjectPolygonToPixelSpace(
                poly, Origin, XAxis, YAxis, dx: 1.0, dy: 1.0);

            Assert.AreEqual(3, px.Length);
            Assert.AreEqual(0.0, px[0], 1e-10);
            Assert.AreEqual(3.0, px[1], 1e-10);
            Assert.AreEqual(3.0, px[2], 1e-10);
            Assert.AreEqual(0.0, py[0], 1e-10);
            Assert.AreEqual(0.0, py[1], 1e-10);
            Assert.AreEqual(4.0, py[2], 1e-10);
        }

        [TestMethod]
        public void NonZeroOrigin_IsSubtractedBeforeProjection()
        {
            // Origin shifted by (10, 20, 0) — pixel coords should ignore that offset
            var origin = V(10, 20, 0);
            var poly = new[] { V(10, 20, 0), V(13, 20, 0), V(13, 24, 0) };
            var (px, py) = DeformableRegistrationService.ProjectPolygonToPixelSpace(
                poly, origin, XAxis, YAxis, dx: 1.0, dy: 1.0);

            Assert.AreEqual(0.0, px[0], 1e-10);
            Assert.AreEqual(3.0, px[1], 1e-10);
            Assert.AreEqual(4.0, py[2], 1e-10);
        }

        [TestMethod]
        public void SpacingScale_DividesPixelCoordinates()
        {
            // dx=2, dy=4 — pixel coords should be halved/quartered
            var poly = new[] { V(0, 0, 0), V(6, 0, 0), V(6, 8, 0) };
            var (px, py) = DeformableRegistrationService.ProjectPolygonToPixelSpace(
                poly, Origin, XAxis, YAxis, dx: 2.0, dy: 4.0);

            Assert.AreEqual(3.0, px[1], 1e-10);   // 6 / 2
            Assert.AreEqual(2.0, py[2], 1e-10);   // 8 / 4
        }

        [TestMethod]
        public void ObliqueXAxis_UsesDotProduct()
        {
            // xDir is aligned with the DICOM Y axis — so X pixel coord comes from world Y
            var xDirAlongY = V(0, 1, 0);
            var yDirAlongX = V(1, 0, 0);
            var poly = new[] { V(5, 3, 0) };   // single vertex for easy checking
            var (px, py) = DeformableRegistrationService.ProjectPolygonToPixelSpace(
                poly, Origin, xDirAlongY, yDirAlongX, dx: 1.0, dy: 1.0);

            // dot((5,3,0), (0,1,0)) / 1 = 3
            Assert.AreEqual(3.0, px[0], 1e-10);
            // dot((5,3,0), (1,0,0)) / 1 = 5
            Assert.AreEqual(5.0, py[0], 1e-10);
        }

        [TestMethod]
        public void SingleVertex_DoesNotThrow()
        {
            var poly = new[] { V(1, 2, 0) };
            var (px, py) = DeformableRegistrationService.ProjectPolygonToPixelSpace(
                poly, Origin, XAxis, YAxis, dx: 1.0, dy: 1.0);
            Assert.AreEqual(1, px.Length);
            Assert.AreEqual(1.0, px[0], 1e-10);
            Assert.AreEqual(2.0, py[0], 1e-10);
        }

        [TestMethod]
        public void OutputArrayLengthMatchesInputPolygonLength()
        {
            var poly = Enumerable.Range(0, 7)
                .Select(i => V(i, i, 0)).ToArray();
            var (px, py) = DeformableRegistrationService.ProjectPolygonToPixelSpace(
                poly, Origin, XAxis, YAxis, dx: 1.0, dy: 1.0);
            Assert.AreEqual(poly.Length, px.Length);
            Assert.AreEqual(poly.Length, py.Length);
        }

        // -----------------------------------------------------------------------
        // Negative direction cosines — simulates Head-First Prone (HFP) where
        // xDir = (-1,0,0) and yDir = (0,-1,0).  The image origin is placed at the
        // maximum patient-X, maximum patient-Y corner so that pixel (0,0) maps to
        // the largest world X/Y, and pixel indices still increase as world X/Y
        // decreases.  ProjectPolygonToPixelSpace must yield the same non-negative
        // pixel indices as the equivalent HFS geometry.
        // -----------------------------------------------------------------------
        [TestMethod]
        public void NegativeXDir_HFP_ProducesCorrectNonNegativePixelCoords()
        {
            // HFP-like: xDir = (-1,0,0), yDir = (0,-1,0).
            // Image has 5 columns (dx=1) and 5 rows (dy=1).
            // Origin placed at patient coords (4, 4, 0) — i.e. the high-x, high-y corner.
            var xDirNeg = V(-1, 0, 0);
            var yDirNeg = V(0, -1, 0);
            var originHFP = V(4, 4, 0);

            // A world point at patient (4,4,0) should be pixel (0,0).
            // A world point at patient (2,1,0) should be pixel (2,3):
            //   ix = dot((2-4, 1-4, 0), (-1,0,0)) / 1 = dot((-2,-3,0),(-1,0,0)) = 2
            //   iy = dot((2-4, 1-4, 0), (0,-1,0)) / 1 = dot((-2,-3,0),(0,-1,0))  = 3
            var poly = new[] { V(4, 4, 0), V(2, 4, 0), V(2, 1, 0), V(4, 1, 0) };
            var (px, py) = DeformableRegistrationService.ProjectPolygonToPixelSpace(
                poly, originHFP, xDirNeg, yDirNeg, dx: 1.0, dy: 1.0);

            Assert.AreEqual(0.0, px[0], 1e-10, "Corner (4,4) → pixel (0,0) ix");
            Assert.AreEqual(0.0, py[0], 1e-10, "Corner (4,4) → pixel (0,0) iy");
            Assert.AreEqual(2.0, px[1], 1e-10, "Point (2,4) → pixel (2,0) ix");
            Assert.AreEqual(0.0, py[1], 1e-10, "Point (2,4) → pixel (2,0) iy");
            Assert.AreEqual(2.0, px[2], 1e-10, "Point (2,1) → pixel (2,3) ix");
            Assert.AreEqual(3.0, py[2], 1e-10, "Point (2,1) → pixel (2,3) iy");
        }

        // -----------------------------------------------------------------------
        // Orientation invariance round-trip: project the same physical square
        // under HFS (xDir=+X, yDir=+Y) and HFP (xDir=-X, yDir=-Y) geometries
        // and verify the resulting pixel-space polygons fill the same pixels.
        // -----------------------------------------------------------------------
        [TestMethod]
        public void HFS_vs_HFP_SamePhysicalSquare_SameFilledPixels()
        {
            int nx = 4, ny = 4, nz = 1;

            // Physical square: world X in [1,3), world Y in [1,3)
            // with 1 mm pixel spacing and a 4×4 image.

            // --- HFS geometry: origin=(0,0,0), xDir=(+1,0,0), yDir=(0,+1,0) ---
            var originHFS = V(0, 0, 0);
            var xHFS = V(1, 0, 0);
            var yHFS = V(0, 1, 0);
            var polyWorld = new[] { V(1, 1, 0), V(3, 1, 0), V(3, 3, 0), V(1, 3, 0) };

            var (pxHFS, pyHFS) = DeformableRegistrationService.ProjectPolygonToPixelSpace(
                polyWorld, originHFS, xHFS, yHFS, dx: 1.0, dy: 1.0);
            byte[] maskHFS = new byte[nx * ny * nz];
            DeformableRegistrationService.FillPolygonIntoSlice(pxHFS, pyHFS, nx, ny, 0, maskHFS);

            // --- HFP geometry: origin=(3,3,0), xDir=(-1,0,0), yDir=(0,-1,0) ---
            // Image spans world X: 3 down to 0; world Y: 3 down to 0.
            // Pixel (0,0) = world (3,3); pixel (1,1) = world (2,2); etc.
            var originHFP = V(3, 3, 0);
            var xHFP = V(-1, 0, 0);
            var yHFP = V(0, -1, 0);

            var (pxHFP, pyHFP) = DeformableRegistrationService.ProjectPolygonToPixelSpace(
                polyWorld, originHFP, xHFP, yHFP, dx: 1.0, dy: 1.0);
            byte[] maskHFP = new byte[nx * ny * nz];
            DeformableRegistrationService.FillPolygonIntoSlice(pxHFP, pyHFP, nx, ny, 0, maskHFP);

            // Both masks must have the same number of set pixels …
            int setHFS = maskHFS.Count(b => b == 1);
            int setHFP = maskHFP.Count(b => b == 1);
            Assert.AreEqual(setHFS, setHFP,
                $"HFS filled {setHFS} pixels but HFP filled {setHFP}.");

            // … and the same total (4 pixels: (1,1),(1,2),(2,1),(2,2) in HFS pixel space)
            Assert.AreEqual(4, setHFS, "Expected 4 pixels inside the 2×2 world square.");
        }
    }

    [TestClass]
    public class FillPolygonIntoSliceTests
    {
        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        /// <summary>Returns the mask value at (ix, iy) in slice z of a flat mask buffer.</summary>
        private static byte Get(byte[] mask, int nx, int ny, int z, int ix, int iy)
            => mask[z * nx * ny + iy * nx + ix];

        /// <summary>Counts set pixels across the whole buffer.</summary>
        private static int CountSet(byte[] mask) => mask.Count(b => b == 1);

        // -----------------------------------------------------------------------
        // Unit square  [0,4) × [0,4)  (pixel-space vertices at corners)
        //    Vertices:  (0,0) (4,0) (4,4) (0,4)
        // -----------------------------------------------------------------------

        [TestMethod]
        public void UnitSquare_InteriorPixelsAreSet()
        {
            int nx = 6, ny = 6, nz = 1;
            byte[] mask = new byte[nx * ny * nz];
            double[] pxX = { 0, 4, 4, 0 };
            double[] pxY = { 0, 0, 4, 4 };

            DeformableRegistrationService.FillPolygonIntoSlice(pxX, pxY, nx, ny, sliceZ: 0, mask);

            // Pixels (0..3) × (0..3) should be set — scanline samples at iy+0.5
            for (int ix = 0; ix < 4; ix++)
                for (int iy = 0; iy < 4; iy++)
                    Assert.AreEqual(1, Get(mask, nx, ny, 0, ix, iy),
                        $"Expected pixel ({ix},{iy}) to be inside the square.");
        }

        [TestMethod]
        public void UnitSquare_PixelsOutsideAreNotSet()
        {
            int nx = 6, ny = 6, nz = 1;
            byte[] mask = new byte[nx * ny * nz];
            double[] pxX = { 0, 4, 4, 0 };
            double[] pxY = { 0, 0, 4, 4 };

            DeformableRegistrationService.FillPolygonIntoSlice(pxX, pxY, nx, ny, sliceZ: 0, mask);

            // Column 5 and row 5 are outside
            for (int iy = 0; iy < ny; iy++)
                Assert.AreEqual(0, Get(mask, nx, ny, 0, 5, iy), $"Column 5, row {iy} should be outside.");
            for (int ix = 0; ix < nx; ix++)
                Assert.AreEqual(0, Get(mask, nx, ny, 0, ix, 5), $"Row 5, col {ix} should be outside.");
        }

        [TestMethod]
        public void UnitSquare_TotalSetPixelCountIsCorrect()
        {
            int nx = 6, ny = 6, nz = 1;
            byte[] mask = new byte[nx * ny * nz];
            double[] pxX = { 0, 4, 4, 0 };
            double[] pxY = { 0, 0, 4, 4 };

            DeformableRegistrationService.FillPolygonIntoSlice(pxX, pxY, nx, ny, sliceZ: 0, mask);

            Assert.AreEqual(16, CountSet(mask));  // 4×4 = 16 pixels
        }

        // -----------------------------------------------------------------------
        // Right triangle:  (0,0) → (4,0) → (0,4)
        // Expected filled pixels: those with ix + iy < 4
        // -----------------------------------------------------------------------

        [TestMethod]
        public void RightTriangle_CorrectPixelsFilled()
        {
            int nx = 6, ny = 6, nz = 1;
            byte[] mask = new byte[nx * ny * nz];
            double[] pxX = { 0, 4, 0 };
            double[] pxY = { 0, 0, 4 };

            DeformableRegistrationService.FillPolygonIntoSlice(pxX, pxY, nx, ny, sliceZ: 0, mask);

            // A pixel at (ix, iy) is inside iff  (ix+0.5) + (iy+0.5) < 4
            for (int iy = 0; iy < 4; iy++)
                for (int ix = 0; ix < 4; ix++)
                {
                    bool expectedInside = (ix + 0.5 + iy + 0.5) < 4.0;
                    byte expected = expectedInside ? (byte)1 : (byte)0;
                    Assert.AreEqual(expected, Get(mask, nx, ny, 0, ix, iy),
                        $"Triangle pixel ({ix},{iy}) expected {expected}.");
                }
        }

        // -----------------------------------------------------------------------
        // Slice-Z offset: same square in slice 1 of a 3-slice buffer
        // -----------------------------------------------------------------------

        [TestMethod]
        public void SliceZ_OnlyTargetSliceIsWritten()
        {
            int nx = 6, ny = 6, nz = 3;
            byte[] mask = new byte[nx * ny * nz];
            double[] pxX = { 0, 4, 4, 0 };
            double[] pxY = { 0, 0, 4, 4 };

            DeformableRegistrationService.FillPolygonIntoSlice(pxX, pxY, nx, ny, sliceZ: 1, mask);

            // Slice 0 and slice 2 must be all zeros
            for (int ix = 0; ix < nx; ix++)
                for (int iy = 0; iy < ny; iy++)
                {
                    Assert.AreEqual(0, Get(mask, nx, ny, 0, ix, iy), $"Slice 0 ({ix},{iy}) should be zero.");
                    Assert.AreEqual(0, Get(mask, nx, ny, 2, ix, iy), $"Slice 2 ({ix},{iy}) should be zero.");
                }

            // Slice 1 interior should be set
            Assert.AreEqual(1, Get(mask, nx, ny, 1, 0, 0));
            Assert.AreEqual(1, Get(mask, nx, ny, 1, 3, 3));
        }

        // -----------------------------------------------------------------------
        // Degenerate: fewer than 3 vertices — should not crash or set any pixel
        // -----------------------------------------------------------------------

        [TestMethod]
        public void TwoVertexPolygon_NoCrashNothingFilled()
        {
            int nx = 5, ny = 5, nz = 1;
            byte[] mask = new byte[nx * ny * nz];
            double[] pxX = { 1, 3 };
            double[] pxY = { 1, 3 };

            // FillPolygonIntoSlice has no guard for < 3 vertices itself (the guard
            // lives in RasterizeStructureMask), but the scanline intersection loop
            // with n=2 should produce no odd-count intersection pairs and fill nothing.
            DeformableRegistrationService.FillPolygonIntoSlice(pxX, pxY, nx, ny, sliceZ: 0, mask);

            Assert.AreEqual(0, CountSet(mask));
        }

        // -----------------------------------------------------------------------
        // Clipping: polygon extends beyond image bounds — buffer must not overflow
        // -----------------------------------------------------------------------

        [TestMethod]
        public void PolygonExtendingBeyondBounds_ClipsToImageEdge()
        {
            int nx = 4, ny = 4, nz = 1;
            byte[] mask = new byte[nx * ny * nz];
            // Square covering [-2,6) × [-2,6) — far outside the 4×4 image
            double[] pxX = { -2, 6, 6, -2 };
            double[] pxY = { -2, -2, 6, 6 };

            DeformableRegistrationService.FillPolygonIntoSlice(pxX, pxY, nx, ny, sliceZ: 0, mask);

            // All 16 pixels should be set (entire image is inside the polygon)
            Assert.AreEqual(16, CountSet(mask));
        }

        // -----------------------------------------------------------------------
        // Non-convex (L-shape): even-odd rule must leave the concave region unset
        //
        //   Pixel grid (6×6). L-shape defined so that the top-right 3×3 block
        //   is outside the polygon (even-odd crossing count = 2 there).
        //
        //   Vertices (clockwise):
        //     (0,0) → (6,0) → (6,3) → (3,3) → (3,6) → (0,6)
        //   The bottom-right pocket (3..5, 3..5) is outside (even winding).
        // -----------------------------------------------------------------------

        [TestMethod]
        public void LShapePolygon_ConcaveRegionIsNotFilled()
        {
            int nx = 6, ny = 6, nz = 1;
            byte[] mask = new byte[nx * ny * nz];
            // L-shape: entire grid minus top-right 3×3
            double[] pxX = { 0, 6, 6, 3, 3, 0 };
            double[] pxY = { 0, 0, 3, 3, 6, 6 };

            DeformableRegistrationService.FillPolygonIntoSlice(pxX, pxY, nx, ny, sliceZ: 0, mask);

            // Top-right block (ix 3-5, iy 3-5) should NOT be set
            for (int ix = 3; ix < 6; ix++)
                for (int iy = 3; iy < 6; iy++)
                    Assert.AreEqual(0, Get(mask, nx, ny, 0, ix, iy),
                        $"Concave region ({ix},{iy}) should be outside the L-shape.");

            // Bottom-left pixels (ix 0-2, iy 0-2) should be set
            for (int ix = 0; ix < 3; ix++)
                for (int iy = 0; iy < 3; iy++)
                    Assert.AreEqual(1, Get(mask, nx, ny, 0, ix, iy),
                        $"Interior ({ix},{iy}) should be inside the L-shape.");
        }

        // -----------------------------------------------------------------------
        // Multiple calls accumulate (mask is OR-ed, not reset)
        // -----------------------------------------------------------------------

        [TestMethod]
        public void TwoNonOverlappingPolygons_BothRegionsSet()
        {
            int nx = 10, ny = 6, nz = 1;
            byte[] mask = new byte[nx * ny * nz];

            // Left square [0,3)×[0,3)
            double[] pxX1 = { 0, 3, 3, 0 };
            double[] pxY1 = { 0, 0, 3, 3 };
            DeformableRegistrationService.FillPolygonIntoSlice(pxX1, pxY1, nx, ny, sliceZ: 0, mask);

            // Right square [6,9)×[0,3)
            double[] pxX2 = { 6, 9, 9, 6 };
            double[] pxY2 = { 0, 0, 3, 3 };
            DeformableRegistrationService.FillPolygonIntoSlice(pxX2, pxY2, nx, ny, sliceZ: 0, mask);

            // Interior of left square
            Assert.AreEqual(1, Get(mask, nx, ny, 0, 1, 1));
            // Gap between the squares (col 4)
            Assert.AreEqual(0, Get(mask, nx, ny, 0, 4, 1));
            // Interior of right square
            Assert.AreEqual(1, Get(mask, nx, ny, 0, 7, 1));
        }

        // -----------------------------------------------------------------------
        // Sub-pixel-width span: intersection pair is narrower than one pixel so no
        // pixel centre falls inside.  ixStart > ixEnd — the inner loop must not
        // execute and no pixel must be set.
        // -----------------------------------------------------------------------
        [TestMethod]
        public void SubPixelWideSpan_NoPixelFilled()
        {
            int nx = 6, ny = 6, nz = 1;
            byte[] mask = new byte[nx * ny * nz];

            // Very thin vertical sliver: x from 1.1 to 1.4 (width = 0.3 px, centre 1.5 not inside)
            // Height spans rows 0–3 so the scanline definitely fires.
            double[] pxX = { 1.1, 1.4, 1.4, 1.1 };
            double[] pxY = { 0.0, 0.0, 3.0, 3.0 };

            DeformableRegistrationService.FillPolygonIntoSlice(pxX, pxY, nx, ny, sliceZ: 0, mask);

            Assert.AreEqual(0, mask.Count(b => b == 1),
                "A sub-pixel-wide polygon should fill no pixels.");
        }
    }
}
