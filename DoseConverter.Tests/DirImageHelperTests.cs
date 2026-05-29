using System;
using DoseConverter;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using VMS.TPS.Common.Model.Types;

namespace DoseConverter.Tests
{
    /// <summary>
    /// Unit tests for the pure-math static helpers in
    /// <see cref="DeformableRegistrationService"/> that do not require
    /// an ESAPI context or SimpleITK runtime.
    /// </summary>
    [TestClass]
    public class DirImageHelperTests
    {
        // -----------------------------------------------------------------------
        // BufferToArray3D
        // -----------------------------------------------------------------------

        [TestMethod]
        public void BufferToArray3D_SingleVoxel_RoundTrips()
        {
            float[] buf = { 42.0f };
            uint[] size = { 1, 1, 1 };

            float[,,] arr = DeformableRegistrationService.BufferToArray3D(buf, size);

            Assert.AreEqual(42.0f, arr[0, 0, 0]);
        }

        [TestMethod]
        public void BufferToArray3D_KnownValues_MapsCorrectly()
        {
            // 2×2×2 volume, flat layout: x + y*nx + z*nx*ny
            // Expected: arr[z, x, y]
            //   Index 0: z=0,x=0,y=0  → value 10
            //   Index 1: z=0,x=1,y=0  → value 11
            //   Index 2: z=0,x=0,y=1  → value 12
            //   Index 3: z=0,x=1,y=1  → value 13
            //   Index 4: z=1,x=0,y=0  → value 14
            //   ...
            float[] buf = { 10, 11, 12, 13, 14, 15, 16, 17 };
            uint[] size = { 2, 2, 2 };

            float[,,] arr = DeformableRegistrationService.BufferToArray3D(buf, size);

            Assert.AreEqual(10f, arr[0, 0, 0], "z=0,x=0,y=0");
            Assert.AreEqual(11f, arr[0, 1, 0], "z=0,x=1,y=0");
            Assert.AreEqual(12f, arr[0, 0, 1], "z=0,x=0,y=1");
            Assert.AreEqual(13f, arr[0, 1, 1], "z=0,x=1,y=1");
            Assert.AreEqual(14f, arr[1, 0, 0], "z=1,x=0,y=0");
            Assert.AreEqual(15f, arr[1, 1, 0], "z=1,x=1,y=0");
            Assert.AreEqual(16f, arr[1, 0, 1], "z=1,x=0,y=1");
            Assert.AreEqual(17f, arr[1, 1, 1], "z=1,x=1,y=1");
        }

        [TestMethod]
        public void BufferToArray3D_OutputDimensions_MatchSizeVector()
        {
            float[] buf = new float[3 * 4 * 5];
            uint[] size = { 3, 4, 5 };

            float[,,] arr = DeformableRegistrationService.BufferToArray3D(buf, size);

            Assert.AreEqual(5, arr.GetLength(0), "Z dimension");
            Assert.AreEqual(3, arr.GetLength(1), "X dimension");
            Assert.AreEqual(4, arr.GetLength(2), "Y dimension");
        }

        [TestMethod]
        public void BufferToArray3D_AllZeros_ReturnsZeroArray()
        {
            float[] buf = new float[2 * 3 * 4]; // all 0.0
            uint[] size = { 2, 3, 4 };

            float[,,] arr = DeformableRegistrationService.BufferToArray3D(buf, size);

            foreach (float v in arr)
                Assert.AreEqual(0f, v);
        }

        [TestMethod]
        public void BufferToArray3D_NegativeValues_PreservedCorrectly()
        {
            float[] buf = { -1.5f, 0f, 1.5f, -100f };
            uint[] size = { 2, 2, 1 };

            float[,,] arr = DeformableRegistrationService.BufferToArray3D(buf, size);

            Assert.AreEqual(-1.5f, arr[0, 0, 0]);
            Assert.AreEqual(0f,    arr[0, 1, 0]);
            Assert.AreEqual(1.5f,  arr[0, 0, 1]);
            Assert.AreEqual(-100f, arr[0, 1, 1]);
        }

        // -----------------------------------------------------------------------
        // BuildDirectionCosines
        // -----------------------------------------------------------------------

        [TestMethod]
        public void BuildDirectionCosines_IdentityAxes_ReturnsIdentityMatrix()
        {
            var x = new VVector(1, 0, 0);
            var y = new VVector(0, 1, 0);
            var z = new VVector(0, 0, 1);

            double[] dir = DeformableRegistrationService.BuildDirectionCosines(x, y, z);

            // Row-major [Xx Xy Xz  Yx Yy Yz  Zx Zy Zz]
            double[] expected = { 1, 0, 0,  0, 1, 0,  0, 0, 1 };
            CollectionAssert.AreEqual(expected, dir);
        }

        [TestMethod]
        public void BuildDirectionCosines_ReturnsNineElements()
        {
            var x = new VVector(1, 0, 0);
            var y = new VVector(0, 1, 0);
            var z = new VVector(0, 0, 1);

            double[] dir = DeformableRegistrationService.BuildDirectionCosines(x, y, z);

            Assert.AreEqual(9, dir.Length);
        }

        [TestMethod]
        public void BuildDirectionCosines_ArbitraryAxes_ElementsMapCorrectly()
        {
            // X along (0,1,0), Y along (-1,0,0), Z along (0,0,1)
            var x = new VVector(0, 1, 0);
            var y = new VVector(-1, 0, 0);
            var z = new VVector(0, 0, 1);

            double[] dir = DeformableRegistrationService.BuildDirectionCosines(x, y, z);

            // [Xx Xy Xz  Yx Yy Yz  Zx Zy Zz]
            Assert.AreEqual( 0, dir[0], "Xx");
            Assert.AreEqual( 1, dir[1], "Xy");
            Assert.AreEqual( 0, dir[2], "Xz");
            Assert.AreEqual(-1, dir[3], "Yx");
            Assert.AreEqual( 0, dir[4], "Yy");
            Assert.AreEqual( 0, dir[5], "Yz");
            Assert.AreEqual( 0, dir[6], "Zx");
            Assert.AreEqual( 0, dir[7], "Zy");
            Assert.AreEqual( 1, dir[8], "Zz");
        }

        [TestMethod]
        public void BuildDirectionCosines_XRow_MatchesXDirection()
        {
            var x = new VVector(0.5, 0.5, 0.707);
            var y = new VVector(0, 1, 0);
            var z = new VVector(0, 0, 1);

            double[] dir = DeformableRegistrationService.BuildDirectionCosines(x, y, z);

            Assert.AreEqual(x.x, dir[0], 1e-12);
            Assert.AreEqual(x.y, dir[1], 1e-12);
            Assert.AreEqual(x.z, dir[2], 1e-12);
        }

        [TestMethod]
        public void BuildDirectionCosines_YRow_MatchesYDirection()
        {
            var x = new VVector(1, 0, 0);
            var y = new VVector(0.3, 0.9, 0.1);
            var z = new VVector(0, 0, 1);

            double[] dir = DeformableRegistrationService.BuildDirectionCosines(x, y, z);

            Assert.AreEqual(y.x, dir[3], 1e-12);
            Assert.AreEqual(y.y, dir[4], 1e-12);
            Assert.AreEqual(y.z, dir[5], 1e-12);
        }

        [TestMethod]
        public void BuildDirectionCosines_ZRow_MatchesZDirection()
        {
            var x = new VVector(1, 0, 0);
            var y = new VVector(0, 1, 0);
            var z = new VVector(0.1, 0.2, 0.97);

            double[] dir = DeformableRegistrationService.BuildDirectionCosines(x, y, z);

            Assert.AreEqual(z.x, dir[6], 1e-12);
            Assert.AreEqual(z.y, dir[7], 1e-12);
            Assert.AreEqual(z.z, dir[8], 1e-12);
        }
    }
}
