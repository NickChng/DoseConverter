using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoseConverter.Tests
{
    /// <summary>
    /// Unit tests for <see cref="DoseAccumulationService.ConvertVoxelToEQD2"/> and
    /// related accumulation-service arithmetic that requires no ESAPI context.
    /// </summary>
    [TestClass]
    public class DoseAccumulationServiceTests
    {
        // ------------------------------------------------------------------
        // ConvertVoxelToEQD2 – basic math
        // ------------------------------------------------------------------

        /// <summary>
        /// EQD2 = D * (ab + d/n) / (ab + 2)
        /// Example: 60 Gy in 30 fx (2 Gy/fx), ab = 10 → dose per fx = 2, EQD2 = 60*(10+2)/(10+2) = 60 Gy
        /// Standard fractionation at 2 Gy/fx should return the physical dose unchanged.
        /// </summary>
        [TestMethod]
        public void ConvertVoxelToEQD2_StandardFractionation_ReturnsPhysicalDose()
        {
            float doseGy = 60f;
            int numFractions = 30; // 2 Gy/fx
            double alphaBeta = 10.0;

            float result = DoseAccumulationService.ConvertVoxelToEQD2(doseGy, alphaBeta, numFractions);

            Assert.AreEqual(60f, result, delta: 0.01f,
                "At exactly 2 Gy/fx the EQD2 must equal the physical dose (ab=10).");
        }

        /// <summary>
        /// Hypofractionation: 30 Gy in 5 fx (6 Gy/fx), ab = 10
        /// EQD2 = 30 * (10 + 6) / (10 + 2) = 30 * 16/12 = 40 Gy
        /// </summary>
        [TestMethod]
        public void ConvertVoxelToEQD2_HypoFractionation_GreaterThanPhysicalDose()
        {
            float doseGy = 30f;
            int numFractions = 5;
            double alphaBeta = 10.0;
            float expected = 30f * (10f + 6f) / (10f + 2f); // 40 Gy

            float result = DoseAccumulationService.ConvertVoxelToEQD2(doseGy, alphaBeta, numFractions);

            Assert.AreEqual(expected, result, delta: 0.01f);
        }

        /// <summary>
        /// Hyperfractionation: 60 Gy in 40 fx (1.5 Gy/fx), ab = 10
        /// EQD2 = 60 * (10 + 1.5) / (10 + 2) = 60 * 11.5/12 ≈ 57.5 Gy
        /// </summary>
        [TestMethod]
        public void ConvertVoxelToEQD2_HyperFractionation_LessThanPhysicalDose()
        {
            float doseGy = 60f;
            int numFractions = 40;
            double alphaBeta = 10.0;
            double expected = 60.0 * (10.0 + 1.5) / (10.0 + 2.0); // ≈ 57.5

            float result = DoseAccumulationService.ConvertVoxelToEQD2(doseGy, alphaBeta, numFractions);

            Assert.AreEqual((float)expected, result, delta: 0.01f);
        }

        /// <summary>
        /// Low ab (late-responding tissue, ab = 3): hypo-fx penalty is larger.
        /// 30 Gy in 5 fx (6 Gy/fx), ab = 3
        /// EQD2 = 30 * (3 + 6) / (3 + 2) = 30 * 9/5 = 54 Gy
        /// </summary>
        [TestMethod]
        public void ConvertVoxelToEQD2_LowAlphaBeta_LargerHypofxPenalty()
        {
            float doseGy = 30f;
            int numFractions = 5;
            double alphaBeta = 3.0;
            float expected = 30f * (3f + 6f) / (3f + 2f); // 54 Gy

            float result = DoseAccumulationService.ConvertVoxelToEQD2(doseGy, alphaBeta, numFractions);

            Assert.AreEqual(expected, result, delta: 0.01f);
        }

        /// <summary>Zero dose in returns zero out.</summary>
        [TestMethod]
        public void ConvertVoxelToEQD2_ZeroDose_ReturnsZero()
        {
            float result = DoseAccumulationService.ConvertVoxelToEQD2(0f, 10.0, 30);
            Assert.AreEqual(0f, result);
        }

        /// <summary>Negative dose is clamped to zero.</summary>
        [TestMethod]
        public void ConvertVoxelToEQD2_NegativeDose_ReturnsZero()
        {
            float result = DoseAccumulationService.ConvertVoxelToEQD2(-5f, 10.0, 30);
            Assert.AreEqual(0f, result);
        }

        /// <summary>Zero fractions falls through without throwing; dose is returned unmodified.</summary>
        [TestMethod]
        public void ConvertVoxelToEQD2_ZeroFractions_ReturnsDoseUnchanged()
        {
            // Edge case: guard against divide-by-zero; returns physical dose as-is.
            float result = DoseAccumulationService.ConvertVoxelToEQD2(10f, 10.0, 0);
            Assert.AreEqual(10f, result);
        }

        // ------------------------------------------------------------------
        // Verify that summing two identical standard-fractionation plans
        // produces exactly double the physical dose in EQD2 space.
        // ------------------------------------------------------------------

        [TestMethod]
        public void AccumulateTwoIdenticalStandardFxPlans_SumsCorrectly()
        {
            float dosePerPlan = 30f; // 2 Gy/fx in 15 fx
            int fractions = 15;
            double ab = 10.0;

            float eqd2A = DoseAccumulationService.ConvertVoxelToEQD2(dosePerPlan, ab, fractions);
            float eqd2B = DoseAccumulationService.ConvertVoxelToEQD2(dosePerPlan, ab, fractions);
            float sum = eqd2A + eqd2B;

            // Each 30 Gy / 15 fx = 2 Gy/fx → EQD2 = 30 Gy; two plans → 60 Gy total.
            Assert.AreEqual(60f, sum, delta: 0.02f,
                "Sum of two 30-Gy standard-fx plans should be 60 Gy EQD2.");
        }
    }
}
