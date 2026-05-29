using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ESAPIScript;
using VMS.TPS.Common.Model.API;

namespace DoseConverter
{
    /// <summary>
    /// Describes a single source plan that contributes to a cumulative dose.
    /// </summary>
    public sealed class AccumulationSourceSpec
    {
        public string CourseId;
        public string PlanId;
        /// <summary>Structure Id on the source plan's structure set to use as moving mask (null = full image).</summary>
        public string SourceMaskStructureId;
        /// <summary>Structure Id on the target plan's structure set to use as fixed mask (null = full image).</summary>
        public string TargetMaskStructureId;
        /// <summary>Optional override of source plan's fractionation; if null, the plan's own number of fractions is used.</summary>
        public int? OverrideFractions;
    }

    /// <summary>
    /// Accumulates EQD2-converted, deformably registered dose from multiple source plans onto a single
    /// target plan's geometry and writes the result as a verification plan in the target plan's course.
    /// </summary>
    public class DoseAccumulationService
    {
        private readonly EsapiWorker _ew;
        private readonly Model _model;
        private readonly DeformableRegistrationService _dirService;

        public DoseAccumulationService(EsapiWorker ew, Model model, DeformableRegistrationService dirService)
        {
            _ew = ew;
            _model = model;
            _dirService = dirService;
        }

        /// <summary>
        /// Per-voxel EQD2 conversion (physical Gy → EQD2 Gy) for a plan delivered in <paramref name="numFractions"/>
        /// fractions, with the given α/β ratio.
        ///   EQD2 = D * (α/β + D/n) / (α/β + 2)
        /// </summary>
        public static float ConvertVoxelToEQD2(float doseGy, double alphaBeta, int numFractions)
        {
            if (numFractions <= 0 || doseGy <= 0f) return doseGy > 0f ? doseGy : 0f;
            double d = doseGy;
            double dPerFx = d / numFractions;
            double eqd2 = d * (alphaBeta + dPerFx) / (alphaBeta + 2.0);
            return (float)Math.Max(eqd2, 0.0);
        }

        /// <summary>
        /// Runs the full accumulation pipeline:
        /// for each source plan, DIR onto the target grid, convert deformed dose to EQD2, sum into accumulator;
        /// then write a single verification plan on the target course containing the accumulated EQD2 dose.
        /// </summary>
        public async Task<(ScriptStatus status, string message)> AccumulateAndWritePlan(
            IReadOnlyList<AccumulationSourceSpec> sources,
            string targetCourseId,
            string targetPlanId,
            string newPlanName,
            double alphaBeta,
            IProgress<string> progress = null)
        {
            if (sources == null || sources.Count == 0)
                return (ScriptStatus.Error, "No source plans were specified for accumulation.");
            if (string.IsNullOrWhiteSpace(newPlanName))
                return (ScriptStatus.Error, "Output plan name is required.");
            if (alphaBeta <= 0 || double.IsNaN(alphaBeta) || double.IsInfinity(alphaBeta))
                return (ScriptStatus.Error, "α/β must be a positive number.");

            float[,,] accumulator = null;
            int totalPlans = sources.Count;

            for (int i = 0; i < totalPlans; i++)
            {
                var src = sources[i];
                Report(progress, $"({i + 1}/{totalPlans}) Processing {src.CourseId}/{src.PlanId}...");

                var (status, message, result) = await _dirService.ComputeDeformedDoseGy(
                    src.CourseId, src.PlanId,
                    targetCourseId, targetPlanId,
                    src.SourceMaskStructureId,
                    src.TargetMaskStructureId,
                    progress);

                if (status != ScriptStatus.Complete || result == null)
                    return (ScriptStatus.Error, $"Failed on source {src.CourseId}/{src.PlanId}: {message}");

                int nFx = src.OverrideFractions ?? result.SourceNumberOfFractions;
                Report(progress, $"({i + 1}/{totalPlans}) Converting deformed dose to EQD2 (n = {nFx}, α/β = {alphaBeta:G})...");

                int Z = result.DeformedDoseGy.GetLength(0);
                int X = result.DeformedDoseGy.GetLength(1);
                int Y = result.DeformedDoseGy.GetLength(2);

                if (accumulator == null)
                {
                    accumulator = new float[Z, X, Y];
                }
                else if (accumulator.GetLength(0) != Z || accumulator.GetLength(1) != X || accumulator.GetLength(2) != Y)
                {
                    return (ScriptStatus.Error,
                        $"Deformed dose grid for {src.CourseId}/{src.PlanId} does not match target geometry of earlier sources.");
                }

                for (int k = 0; k < Z; k++)
                    for (int ix = 0; ix < X; ix++)
                        for (int j = 0; j < Y; j++)
                        {
                            float gy = result.DeformedDoseGy[k, ix, j];
                            if (gy <= 0f || float.IsNaN(gy)) continue;
                            accumulator[k, ix, j] += ConvertVoxelToEQD2(gy, alphaBeta, nFx);
                        }
            }

            if (accumulator == null)
                return (ScriptStatus.Error, "No deformed dose was produced.");

            // Write the accumulated EQD2 grid as a verification plan on the target course
            Report(progress, "Writing accumulated EQD2 plan to Eclipse...");
            string createdPlanId = null;
            string writeError = null;
            await _ew.AsyncRunPatientContext(p =>
            {
                try
                {
                    var targetCourse = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, targetCourseId, StringComparison.OrdinalIgnoreCase));
                    var targetPlan = targetCourse?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, targetPlanId, StringComparison.OrdinalIgnoreCase)) as ExternalPlanSetup;
                    if (targetPlan == null) { writeError = "Target plan not found when writing accumulated plan."; return; }

                    createdPlanId = _model.CreateDeformedDosePlan(
                        newPlanName, targetPlan, accumulator, targetPlan.Dose);
                }
                catch (Exception ex)
                {
                    writeError = $"Failed to write accumulated dose plan: {ex.Message}";
                    Helpers.SeriLog.LogError("Accumulation plan write error", ex);
                }
            });

            if (writeError != null)
                return (ScriptStatus.Error, writeError);

            string ok = $"Accumulated EQD2 plan '{createdPlanId}' created successfully from {totalPlans} source plan(s). " +
                        "Review in Eclipse before clinical use.";
            Helpers.SeriLog.LogInfo(ok);
            Report(progress, ok);
            return (ScriptStatus.Complete, ok);
        }

        private static void Report(IProgress<string> progress, string message)
        {
            Helpers.SeriLog.LogInfo(message);
            progress?.Report(message);
        }
    }
}
