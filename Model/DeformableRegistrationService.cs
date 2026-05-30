using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using ESAPIScript;
using SitkImage = itk.simple.Image;
using SitkTransform = itk.simple.Transform;
using itk.simple;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace DoseConverter
{
    /// <summary>
    /// Performs CT-to-CT deformable image registration using SimpleITK and warps
    /// the source plan's dose onto the target plan's dose grid.
    /// All SimpleITK processing runs on a background thread; ESAPI access is
    /// marshalled back to the Eclipse dispatcher via EsapiWorker.
    /// </summary>
    public class DeformableRegistrationService
    {
        private readonly EsapiWorker _ew;
        private readonly Model _model;

        public DeformableRegistrationService(EsapiWorker ew, Model model)
        {
            _ew = ew;
            _model = model;
        }

        // -----------------------------------------------------------------------
        // Public entry points
        // -----------------------------------------------------------------------

        /// <summary>
        /// Result of a deformable dose computation: deformed dose grid in Gy on the
        /// target plan's dose geometry, plus the fractionation of the source plan so
        /// that downstream code (e.g. EQD2 accumulation) can use it.
        /// </summary>
        public sealed class DeformedDoseResult
        {
            public float[,,] DeformedDoseGy;   // [Z, X, Y] on target dose grid
            public uint[]    TargetSize;       // [W, H, D]
            public int       SourceNumberOfFractions;
            public double    SourceDosePerFractionGy;
            public double    SourceMaxDoseGy;
        }

        /// <summary>
        /// Full DIR pipeline: extract CTs → register → resample dose → write Eclipse plan.
        /// Must be called from a non-dispatcher thread (Task.Run is fine).
        /// </summary>
        /// <param name="sourceCourseId">Course containing the source (moving) plan.</param>
        /// <param name="sourcePlanId">Plan whose dose will be deformed.</param>
        /// <param name="targetCourseId">Course containing the target (fixed) plan.</param>
        /// <param name="targetPlanId">Plan whose CT geometry defines the output space.</param>
        /// <param name="newPlanName">Eclipse Id for the output verification plan (max 13 chars).</param>
        /// <param name="sourceMaskStructureId">Structure Id in the source plan's structure set to use as moving mask (null = full image).</param>
        /// <param name="targetMaskStructureId">Structure Id in the target plan's structure set to use as fixed mask (null = full image).</param>
        /// <param name="progress">Optional progress reporter for status strings.</param>
        public async Task<(ScriptStatus status, string message)> PerformDIRAndWritePlan(
            string sourceCourseId,
            string sourcePlanId,
            string targetCourseId,
            string targetPlanId,
            string newPlanName,
            string sourceMaskStructureId = null,
            string targetMaskStructureId = null,
            IProgress<string> progress = null)
        {
            // ---- 1. Extract image and dose data inside the Eclipse dispatcher --------
            float[] movingCTBuffer = null, fixedCTBuffer = null, sourceDoseBuffer = null;
            uint[] movingCTSize = null, fixedCTSize = null, sourceDoseSize = null;
            double[] movingCTSpacing = null, fixedCTSpacing = null, sourceDoseSpacing = null;
            double[] movingCTOrigin = null, fixedCTOrigin = null, sourceDoseOrigin = null;
            double[] movingCTDirection = null, fixedCTDirection = null, sourceDoseDirection = null;
            double sourceDoseScaling = 1.0;

            // Mask buffers (null = full-image registration)
            byte[] movingMaskBuffer = null, fixedMaskBuffer = null;

            // Target dose geometry (defines the output grid)
            uint[] targetDoseSize = null;
            double[] targetDoseSpacing = null, targetDoseOrigin = null, targetDoseDirection = null;

            string extractionError = null;

            Report(progress, "Extracting image data from Eclipse...");
            await _ew.AsyncRunPatientContext(p =>
            {
                try
                {
                    var sourceCourse = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, sourceCourseId, StringComparison.OrdinalIgnoreCase));
                    var sourcePlan = sourceCourse?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, sourcePlanId, StringComparison.OrdinalIgnoreCase)) as ExternalPlanSetup;

                    var targetCourse = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, targetCourseId, StringComparison.OrdinalIgnoreCase));
                    var targetPlan = targetCourse?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, targetPlanId, StringComparison.OrdinalIgnoreCase)) as ExternalPlanSetup;

                    if (sourcePlan == null) { extractionError = $"Source plan '{sourceCourseId}/{sourcePlanId}' not found."; return; }
                    if (targetPlan == null) { extractionError = $"Target plan '{targetCourseId}/{targetPlanId}' not found."; return; }
                    if (sourcePlan.Dose == null) { extractionError = "Source plan has no dose."; return; }
                    if (targetPlan.Dose == null) { extractionError = "Target plan has no dose."; return; }

                    var movingImage = sourcePlan.StructureSet?.Image;
                    var fixedImage = targetPlan.StructureSet?.Image;
                    if (movingImage == null) { extractionError = "Source plan has no CT image."; return; }
                    if (fixedImage == null) { extractionError = "Target plan has no CT image."; return; }

                    // Extract CTs
                    (movingCTBuffer, movingCTSize, movingCTSpacing, movingCTOrigin, movingCTDirection) =
                        ExtractCTBuffers(movingImage);
                    (fixedCTBuffer, fixedCTSize, fixedCTSpacing, fixedCTOrigin, fixedCTDirection) =
                        ExtractCTBuffers(fixedImage);

                    // Rasterize optional structure masks onto the CT voxel grids
                    if (!string.IsNullOrEmpty(sourceMaskStructureId))
                        movingMaskBuffer = RasterizeStructureMask(
                            sourcePlan.StructureSet, sourceMaskStructureId, movingImage);
                    if (!string.IsNullOrEmpty(targetMaskStructureId))
                        fixedMaskBuffer = RasterizeStructureMask(
                            targetPlan.StructureSet, targetMaskStructureId, fixedImage);

                    // Extract source dose
                    var srcDose = sourcePlan.Dose;
                    double maxDoseGy = _model.GetMaxDoseVal(srcDose, sourcePlan);
                    int[,,] rawDose = _model.GetDoseVoxelsFromDose(srcDose);
                    var minMax = Helpers.GetMinMaxValues(rawDose,
                        srcDose.XSize, srcDose.YSize, srcDose.ZSize);
                    sourceDoseScaling = maxDoseGy / minMax.Item2;
                    float[,,] sourceDoseGy = _model.GetDoseVoxelsAsFloat(srcDose, sourceDoseScaling);
                    (sourceDoseBuffer, sourceDoseSize, sourceDoseSpacing, sourceDoseOrigin, sourceDoseDirection) =
                        ExtractDoseBuffers(srcDose, sourceDoseGy);

                    // Target dose geometry (output reference grid)
                    var tgtDose = targetPlan.Dose;
                    (targetDoseSize, targetDoseSpacing, targetDoseOrigin, targetDoseDirection) =
                        GetDoseGeometry(tgtDose);
                }
                catch (Exception ex)
                {
                    extractionError = $"Data extraction failed: {ex.Message}";
                    Helpers.SeriLog.LogError("DIR extraction error", ex);
                }
            });

            if (extractionError != null)
                return (ScriptStatus.Error, extractionError);

            // ---- 2. Run SimpleITK registration and resampling on background thread ---
            Report(progress, "Running deformable image registration (this may take several minutes)...");
            float[] deformedDoseBuffer = null;
            string sitkError = null;
            try
            {
                await Task.Run(() =>
                {
                    using (var fixedCT = BufferToImage(fixedCTBuffer, fixedCTSize, fixedCTSpacing, fixedCTOrigin, fixedCTDirection, PixelIDValueEnum.sitkFloat32))
                    using (var movingCT = BufferToImage(movingCTBuffer, movingCTSize, movingCTSpacing, movingCTOrigin, movingCTDirection, PixelIDValueEnum.sitkFloat32))
                    using (var sourceDoseImg = BufferToImage(sourceDoseBuffer, sourceDoseSize, sourceDoseSpacing, sourceDoseOrigin, sourceDoseDirection, PixelIDValueEnum.sitkFloat32))
                    {
                        // Build optional mask images
                        SitkImage fixedMaskImg  = fixedMaskBuffer  != null
                            ? BuildMaskImage(fixedMaskBuffer,  fixedCTSize,  fixedCTSpacing,  fixedCTOrigin,  fixedCTDirection)
                            : null;
                        SitkImage movingMaskImg = movingMaskBuffer != null
                            ? BuildMaskImage(movingMaskBuffer, movingCTSize, movingCTSpacing, movingCTOrigin, movingCTDirection)
                            : null;

                        Report(progress, "Initialising B-Spline transform...");
                        SitkTransform finalTransform = RunBSplineRegistration(
                            fixedCT, movingCT, progress, fixedMaskImg, movingMaskImg);

                        Report(progress, "Resampling source dose onto target grid...");
                        // Build a reference image with target dose geometry
                        using (var targetDoseRef = BuildReferenceImage(targetDoseSize, targetDoseSpacing, targetDoseOrigin, targetDoseDirection))
                        using (var resampledDose = SimpleITK.Resample(
                            sourceDoseImg,
                            targetDoseRef,
                            finalTransform,
                            InterpolatorEnum.sitkLinear,
                            0.0,
                            sourceDoseImg.GetPixelID()))
                        {
                            deformedDoseBuffer = ImageToBuffer(resampledDose);
                            // Update size to match the resampled image (= target dose size)
                            targetDoseSize = new uint[]
                            {
                                resampledDose.GetWidth(),
                                resampledDose.GetHeight(),
                                resampledDose.GetDepth()
                            };
                        }
                        finalTransform.Dispose();
                        fixedMaskImg?.Dispose();
                        movingMaskImg?.Dispose();
                    }
                });
            }
            catch (Exception ex)
            {
                sitkError = $"SimpleITK registration/resampling failed: {ex.Message}";
                Helpers.SeriLog.LogError("SimpleITK error", ex);
                return (ScriptStatus.Error, sitkError);
            }

            // ---- 3. Reconstruct float[Z,X,Y] from buffer and write Eclipse plan -----
            Report(progress, "Writing deformed dose plan to Eclipse...");
            float[,,] deformedDoseGy = BufferToArray3D(deformedDoseBuffer, targetDoseSize);

            string planWriteError = null;
            string createdPlanId = null;
            await _ew.AsyncRunPatientContext(p =>
            {
                try
                {
                    var targetCourse = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, targetCourseId, StringComparison.OrdinalIgnoreCase));
                    var targetPlan = targetCourse?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, targetPlanId, StringComparison.OrdinalIgnoreCase)) as ExternalPlanSetup;

                    if (targetPlan == null) { planWriteError = "Target plan not found when writing result."; return; }

                    createdPlanId = _model.CreateDeformedDosePlan(
                        newPlanName, targetPlan, deformedDoseGy, targetPlan.Dose);
                }
                catch (Exception ex)
                {
                    planWriteError = $"Failed to write deformed dose plan: {ex.Message}";
                    Helpers.SeriLog.LogError("DIR plan write error", ex);
                }
            });

            if (planWriteError != null)
                return (ScriptStatus.Error, planWriteError);

            string successMsg = $"Deformed dose plan '{createdPlanId}' created successfully. Review in Eclipse before EQD2 conversion.";
            Helpers.SeriLog.LogInfo(successMsg);
            Report(progress, successMsg);
            return (ScriptStatus.Complete, successMsg);
        }

        /// <summary>
        /// Runs DIR for a single source → target pair and returns the deformed dose grid
        /// resampled onto the target plan's dose geometry, without writing any plan.
        /// Used by the dose-accumulation workflow which sums multiple deformed doses
        /// before writing a single output plan.
        /// </summary>
        public async Task<(ScriptStatus status, string message, DeformedDoseResult result)>
            ComputeDeformedDoseGy(
                string sourceCourseId,
                string sourcePlanId,
                string targetCourseId,
                string targetPlanId,
                string sourceMaskStructureId = null,
                string targetMaskStructureId = null,
                IProgress<string> progress = null)
        {
            // ---- 1. Extract image and dose data inside the Eclipse dispatcher --------
            float[] movingCTBuffer = null, fixedCTBuffer = null, sourceDoseBuffer = null;
            uint[] movingCTSize = null, fixedCTSize = null, sourceDoseSize = null;
            double[] movingCTSpacing = null, fixedCTSpacing = null, sourceDoseSpacing = null;
            double[] movingCTOrigin = null, fixedCTOrigin = null, sourceDoseOrigin = null;
            double[] movingCTDirection = null, fixedCTDirection = null, sourceDoseDirection = null;

            byte[] movingMaskBuffer = null, fixedMaskBuffer = null;

            uint[] targetDoseSize = null;
            double[] targetDoseSpacing = null, targetDoseOrigin = null, targetDoseDirection = null;

            int sourceFractions = 1;
            double sourceDosePerFractionGy = 0.0;
            double sourceMaxDoseGy = 0.0;

            string extractionError = null;

            Report(progress, $"[{sourceCourseId}/{sourcePlanId}] Extracting image data...");
            await _ew.AsyncRunPatientContext(p =>
            {
                try
                {
                    var sourceCourse = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, sourceCourseId, StringComparison.OrdinalIgnoreCase));
                    var sourcePlan = sourceCourse?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, sourcePlanId, StringComparison.OrdinalIgnoreCase)) as ExternalPlanSetup;

                    var targetCourse = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, targetCourseId, StringComparison.OrdinalIgnoreCase));
                    var targetPlan = targetCourse?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, targetPlanId, StringComparison.OrdinalIgnoreCase)) as ExternalPlanSetup;

                    if (sourcePlan == null) { extractionError = $"Source plan '{sourceCourseId}/{sourcePlanId}' not found."; return; }
                    if (targetPlan == null) { extractionError = $"Target plan '{targetCourseId}/{targetPlanId}' not found."; return; }
                    if (sourcePlan.Dose == null) { extractionError = $"Source plan '{sourceCourseId}/{sourcePlanId}' has no dose."; return; }
                    if (targetPlan.Dose == null) { extractionError = "Target plan has no dose."; return; }

                    var movingImage = sourcePlan.StructureSet?.Image;
                    var fixedImage  = targetPlan.StructureSet?.Image;
                    if (movingImage == null) { extractionError = $"Source plan '{sourceCourseId}/{sourcePlanId}' has no CT image."; return; }
                    if (fixedImage  == null) { extractionError = "Target plan has no CT image."; return; }

                    (movingCTBuffer, movingCTSize, movingCTSpacing, movingCTOrigin, movingCTDirection) = ExtractCTBuffers(movingImage);
                    (fixedCTBuffer,  fixedCTSize,  fixedCTSpacing,  fixedCTOrigin,  fixedCTDirection)  = ExtractCTBuffers(fixedImage);

                    if (!string.IsNullOrEmpty(sourceMaskStructureId))
                        movingMaskBuffer = RasterizeStructureMask(sourcePlan.StructureSet, sourceMaskStructureId, movingImage);
                    if (!string.IsNullOrEmpty(targetMaskStructureId))
                        fixedMaskBuffer  = RasterizeStructureMask(targetPlan.StructureSet, targetMaskStructureId, fixedImage);

                    sourceFractions = (int)sourcePlan.NumberOfFractions;
                    sourceDosePerFractionGy = sourcePlan.DosePerFraction != null
                        ? sourcePlan.DosePerFraction.Dose * (sourcePlan.DosePerFraction.Unit == VMS.TPS.Common.Model.Types.DoseValue.DoseUnit.cGy ? 0.01 : 1.0)
                        : 0.0;

                    var srcDose = sourcePlan.Dose;
                    double maxDoseGy = _model.GetMaxDoseVal(srcDose, sourcePlan);
                    sourceMaxDoseGy = maxDoseGy;
                    int[,,] rawDose = _model.GetDoseVoxelsFromDose(srcDose);
                    var minMax = Helpers.GetMinMaxValues(rawDose, srcDose.XSize, srcDose.YSize, srcDose.ZSize);
                    if (minMax.Item2 <= 0) { extractionError = $"Source plan '{sourceCourseId}/{sourcePlanId}' dose grid is empty."; return; }
                    double sourceDoseScaling = maxDoseGy / minMax.Item2;
                    float[,,] sourceDoseGy = _model.GetDoseVoxelsAsFloat(srcDose, sourceDoseScaling);
                    (sourceDoseBuffer, sourceDoseSize, sourceDoseSpacing, sourceDoseOrigin, sourceDoseDirection) =
                        ExtractDoseBuffers(srcDose, sourceDoseGy);

                    var tgtDose = targetPlan.Dose;
                    (targetDoseSize, targetDoseSpacing, targetDoseOrigin, targetDoseDirection) = GetDoseGeometry(tgtDose);
                }
                catch (Exception ex)
                {
                    extractionError = $"Data extraction failed: {ex.Message}";
                    Helpers.SeriLog.LogError("DIR extraction error", ex);
                }
            });

            if (extractionError != null)
                return (ScriptStatus.Error, extractionError, null);

            // ---- 2. Run SimpleITK on background thread --------------------------------
            float[] deformedDoseBuffer = null;
            try
            {
                await Task.Run(() =>
                {
                    using (var fixedCT = BufferToImage(fixedCTBuffer, fixedCTSize, fixedCTSpacing, fixedCTOrigin, fixedCTDirection, PixelIDValueEnum.sitkFloat32))
                    using (var movingCT = BufferToImage(movingCTBuffer, movingCTSize, movingCTSpacing, movingCTOrigin, movingCTDirection, PixelIDValueEnum.sitkFloat32))
                    using (var sourceDoseImg = BufferToImage(sourceDoseBuffer, sourceDoseSize, sourceDoseSpacing, sourceDoseOrigin, sourceDoseDirection, PixelIDValueEnum.sitkFloat32))
                    {
                        SitkImage fixedMaskImg  = fixedMaskBuffer  != null ? BuildMaskImage(fixedMaskBuffer,  fixedCTSize,  fixedCTSpacing,  fixedCTOrigin,  fixedCTDirection)  : null;
                        SitkImage movingMaskImg = movingMaskBuffer != null ? BuildMaskImage(movingMaskBuffer, movingCTSize, movingCTSpacing, movingCTOrigin, movingCTDirection) : null;

                        Report(progress, $"[{sourceCourseId}/{sourcePlanId}] Running B-spline registration...");
                        SitkTransform finalTransform = RunBSplineRegistration(fixedCT, movingCT, progress, fixedMaskImg, movingMaskImg);

                        Report(progress, $"[{sourceCourseId}/{sourcePlanId}] Resampling source dose...");
                        using (var targetDoseRef = BuildReferenceImage(targetDoseSize, targetDoseSpacing, targetDoseOrigin, targetDoseDirection))
                        using (var resampledDose = SimpleITK.Resample(sourceDoseImg, targetDoseRef, finalTransform, InterpolatorEnum.sitkLinear, 0.0, sourceDoseImg.GetPixelID()))
                        {
                            deformedDoseBuffer = ImageToBuffer(resampledDose);
                            targetDoseSize = new uint[] { resampledDose.GetWidth(), resampledDose.GetHeight(), resampledDose.GetDepth() };
                        }
                        finalTransform.Dispose();
                        fixedMaskImg?.Dispose();
                        movingMaskImg?.Dispose();
                    }
                });
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError("SimpleITK error", ex);
                return (ScriptStatus.Error, $"SimpleITK registration/resampling failed: {ex.Message}", null);
            }

            var result = new DeformedDoseResult
            {
                DeformedDoseGy = BufferToArray3D(deformedDoseBuffer, targetDoseSize),
                TargetSize = targetDoseSize,
                SourceNumberOfFractions = sourceFractions,
                SourceDosePerFractionGy = sourceDosePerFractionGy,
                SourceMaxDoseGy = sourceMaxDoseGy
            };
            return (ScriptStatus.Complete, "Deformed dose computed.", result);
        }

        // -----------------------------------------------------------------------
        // SimpleITK registration
        // -----------------------------------------------------------------------

        private SitkTransform RunBSplineRegistration(
            SitkImage fixedCT, SitkImage movingCT,
            IProgress<string> progress,
            SitkImage fixedMask = null, SitkImage movingMask = null)
        {
            // Read registration parameters from config (fall back to defaults if element absent)
            var rp = _model.Config?.RegistrationParameters ?? new DoseConverterConfigRegistrationParameters();

            // Parse space-separated grid nodes (e.g. "5 5 5")
            var gridNodes = ParseUIntList(rp.BSplineGridNodes, new uint[] { 5, 5, 5 });
            uint bsplineOrder = ParseUInt(rp.BSplineOrder, 3);

            // Parse space-separated multi-resolution parameters
            var shrinkFactors = ParseUIntList(rp.ShrinkFactorsPerLevel, new uint[] { 4, 2, 1 });
            var smoothingSigmas = ParseDoubleList(rp.SmoothingSigmasPerLevel, new double[] { 2.0, 1.0, 0.0 });
            int numLevels = shrinkFactors.Length;

            // Per-level iteration caps: MaxIterationsPerLevel takes precedence; falls back to MaxIterations for
            // any level that is not specified.
            uint defaultMaxIter = ParseUInt(rp.MaxIterations, 100);
            uint[] perLevelIter = ParseUIntList(rp.MaxIterationsPerLevel, new uint[0]);
            // Build a full per-level array of length numLevels
            uint[] maxIterPerLevel = new uint[numLevels];
            for (int i = 0; i < numLevels; i++)
                maxIterPerLevel[i] = (i < perLevelIter.Length) ? perLevelIter[i] : defaultMaxIter;

            // When a fixed mask is available, crop both the fixed CT and the fixed mask to the
            // mask's bounding box (+ margin) before initialising the B-spline transform and
            // running Execute.  This limits control-point dimensionality to the ROI only.
            // The returned transform is physically georeferenced, so it is valid over the whole
            // image space and can be applied directly to the full-resolution dose grid.
            double maskMarginMm = rp.MaskMarginMm;
            SitkImage regFixed     = fixedCT;   // may be replaced by cropped version
            SitkImage regFixedMask = fixedMask;  // may be replaced by cropped version
            bool croppedFixed = false;
            if (fixedMask != null)
            {
                Report(progress, "Cropping fixed CT to mask bounding box...");
                var (croppedCT, croppedMask) = CropImageToMaskBounds(fixedCT, fixedMask, maskMarginMm);
                if (croppedCT != null)
                {
                    regFixed     = croppedCT;
                    regFixedMask = croppedMask;
                    croppedFixed = true;
                    Report(progress,
                        $"  Cropped fixed CT: {regFixed.GetWidth()}×{regFixed.GetHeight()}×{regFixed.GetDepth()} " +
                        $"(was {fixedCT.GetWidth()}×{fixedCT.GetHeight()}×{fixedCT.GetDepth()})");
                }
            }

            try
            {
            // Multi-resolution B-Spline registration (unimodal CT-CT)
            var registration = new ImageRegistrationMethod();

            // Metric: mean squares (optimal for unimodal)
            registration.SetMetricAsMeanSquares();
            registration.SetMetricSamplingStrategy(ImageRegistrationMethod.MetricSamplingStrategyType.RANDOM);
            registration.SetMetricSamplingPercentage(rp.MetricSamplingPercentage);

            // Interpolator
            registration.SetInterpolator(InterpolatorEnum.sitkLinear);

            // Optional structure masks — constrain metric to the contoured regions
            if (regFixedMask != null)  registration.SetMetricFixedMask(regFixedMask);
            if (movingMask   != null)  registration.SetMetricMovingMask(movingMask);

            // Initial transform — seeded on the (possibly cropped) fixed image so control
            // points are distributed over the ROI only.
            var bsplineTransform = SimpleITK.BSplineTransformInitializer(
                regFixed,
                new VectorUInt32(gridNodes),
                bsplineOrder);
            registration.SetInitialTransform(bsplineTransform, inPlace: true);

            // Optimizer: L-BFGS-B — seeded with the first-level iteration count; updated per level below.
            registration.SetOptimizerAsLBFGSB(
                gradientConvergenceTolerance: rp.GradientConvergenceTolerance,
                numberOfIterations: maxIterPerLevel[0],
                maximumNumberOfCorrections: (uint)ParseUInt(rp.MaxCorrections, 5),
                maximumNumberOfFunctionEvaluations: (uint)ParseUInt(rp.MaxFunctionEvaluations, 1000),
                costFunctionConvergenceFactor: rp.CostFunctionConvergenceFactor);

            // Multi-resolution pyramid
            registration.SetShrinkFactorsPerLevel(new VectorUInt32(shrinkFactors));
            registration.SetSmoothingSigmasPerLevel(new VectorDouble(smoothingSigmas));
            registration.SmoothingSigmasAreSpecifiedInPhysicalUnitsOn();

            int level = 0;
            var levelCmd = new ActionCommand(() =>
            {
                level++;
                // Re-configure the optimizer with the iteration cap for this specific level.
                // sitkMultiResolutionIterationEvent fires before the level starts, so level is 1-based here.
                int idx = Math.Min(level - 1, numLevels - 1);
                registration.SetOptimizerAsLBFGSB(
                    gradientConvergenceTolerance: rp.GradientConvergenceTolerance,
                    numberOfIterations: maxIterPerLevel[idx],
                    maximumNumberOfCorrections: (uint)ParseUInt(rp.MaxCorrections, 5),
                    maximumNumberOfFunctionEvaluations: (uint)ParseUInt(rp.MaxFunctionEvaluations, 1000),
                    costFunctionConvergenceFactor: rp.CostFunctionConvergenceFactor);
                Report(progress, $"Registration: starting level {level}/{numLevels} (max {maxIterPerLevel[idx]} iterations)...");
            });
            var iterCmd = new ActionCommand(() =>
            {
                if ((int)registration.GetOptimizerIteration() % 10 == 0)
                {
                    Report(progress, $"  Level {level} | Iteration {registration.GetOptimizerIteration()} | Metric {registration.GetMetricValue():F4}");
                }
            });
            registration.AddCommand(EventEnum.sitkMultiResolutionIterationEvent, levelCmd);
            registration.AddCommand(EventEnum.sitkIterationEvent, iterCmd);

            // Execute against the (possibly cropped) fixed image; moving stays full-resolution.
            SitkTransform result = registration.Execute(regFixed, movingCT);
            Helpers.SeriLog.LogInfo($"Registration complete. Stop condition: {registration.GetOptimizerStopConditionDescription()}");
            return result;
            } // end try
            finally
            {
                // Dispose cropped images when they differ from the originals passed in.
                if (croppedFixed)
                {
                    regFixed?.Dispose();
                    if (!ReferenceEquals(regFixedMask, fixedMask))
                        regFixedMask?.Dispose();
                }
            }
        }

        // -----------------------------------------------------------------------
        // Mask bounding-box crop
        // -----------------------------------------------------------------------

        /// <summary>
        /// Crops <paramref name="image"/> (and <paramref name="mask"/>) to the axis-aligned
        /// bounding box of the non-zero voxels in <paramref name="mask"/>, expanded by
        /// <paramref name="marginMm"/> in each direction.  Both cropped images share the
        /// same physical coordinate system as the originals, so any transform estimated on
        /// them is directly applicable to the full-resolution images.
        ///
        /// Returns (null, null) if the mask is empty or the computed region is degenerate.
        /// </summary>
        internal static (SitkImage croppedImage, SitkImage croppedMask)
            CropImageToMaskBounds(SitkImage image, SitkImage mask, double marginMm)
        {
            if (image == null || mask == null) return (null, null);

            // Scan the mask buffer to find min/max non-zero voxel indices.
            uint nx = mask.GetWidth();
            uint ny = mask.GetHeight();
            uint nz = mask.GetDepth();

            int minX = int.MaxValue, maxX = int.MinValue;
            int minY = int.MaxValue, maxY = int.MinValue;
            int minZ = int.MaxValue, maxZ = int.MinValue;

            IntPtr ptr = mask.GetBufferAsUInt8();
            long total = (long)(nx * ny * nz);
            byte[] buf = new byte[total];
            System.Runtime.InteropServices.Marshal.Copy(ptr, buf, 0, (int)total);

            for (int z = 0; z < (int)nz; z++)
            {
                for (int y = 0; y < (int)ny; y++)
                {
                    for (int x = 0; x < (int)nx; x++)
                    {
                        if (buf[(long)z * nx * ny + (long)y * nx + x] != 0)
                        {
                            if (x < minX) minX = x; if (x > maxX) maxX = x;
                            if (y < minY) minY = y; if (y > maxY) maxY = y;
                            if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
                        }
                    }
                }
            }

            if (minX == int.MaxValue) return (null, null); // empty mask

            // Convert margin from mm to voxels (use per-axis spacing).
            var sp = mask.GetSpacing();
            int mX = (int)Math.Ceiling(marginMm / sp[0]);
            int mY = (int)Math.Ceiling(marginMm / sp[1]);
            int mZ = (int)Math.Ceiling(marginMm / sp[2]);

            // Clamp to image bounds.
            int startX = Math.Max(0, minX - mX);
            int startY = Math.Max(0, minY - mY);
            int startZ = Math.Max(0, minZ - mZ);
            int endX   = Math.Min((int)nx - 1, maxX + mX);
            int endY   = Math.Min((int)ny - 1, maxY + mY);
            int endZ   = Math.Min((int)nz - 1, maxZ + mZ);

            uint sizeX = (uint)(endX - startX + 1);
            uint sizeY = (uint)(endY - startY + 1);
            uint sizeZ = (uint)(endZ - startZ + 1);

            if (sizeX < 2 || sizeY < 2 || sizeZ < 2) return (null, null);

            var roiFilter = new RegionOfInterestImageFilter();
            roiFilter.SetIndex(new VectorInt32(new int[] { startX, startY, startZ }));
            roiFilter.SetSize(new VectorUInt32(new uint[] { sizeX, sizeY, sizeZ }));

            SitkImage croppedImage = roiFilter.Execute(image);
            SitkImage croppedMask  = roiFilter.Execute(mask);
            return (croppedImage, croppedMask);
        }

        // -----------------------------------------------------------------------
        // Config parsing helpers
        // -----------------------------------------------------------------------

        private static uint[] ParseUIntList(string value, uint[] fallback)
        {
            try
            {
                return value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => uint.Parse(s.Trim()))
                            .ToArray();
            }
            catch { return fallback; }
        }

        private static double[] ParseDoubleList(string value, double[] fallback)
        {
            try
            {
                return value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => double.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture))
                            .ToArray();
            }
            catch { return fallback; }
        }

        private static uint ParseUInt(string value, uint fallback)
        {
            return uint.TryParse(value?.Trim(), out uint result) ? result : fallback;
        }

        // -----------------------------------------------------------------------
        // Buffer / image conversion helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Extracts a CT Image (int16 HU values) from an ESAPI Image into float CPU buffers.
        /// Returns (buffer, [W,H,D], spacing_mm, origin_mm, rowMajorDirectionCosines).
        /// </summary>
        private static (float[] buffer, uint[] size, double[] spacing, double[] origin, double[] direction)
            ExtractCTBuffers(VMS.TPS.Common.Model.API.Image img)
        {
            int nx = img.XSize;
            int ny = img.YSize;
            int nz = img.ZSize;

            float[] buffer = new float[nx * ny * nz];
            int[,] sliceBuf = new int[nx, ny];

            for (int z = 0; z < nz; z++)
            {
                img.GetVoxels(z, sliceBuf);
                int baseIdx = z * nx * ny;
                for (int x = 0; x < nx; x++)
                    for (int y = 0; y < ny; y++)
                        buffer[baseIdx + y * nx + x] = (float)sliceBuf[x, y];
            }

            uint[] size = { (uint)nx, (uint)ny, (uint)nz };
            double[] spacing = { img.XRes, img.YRes, img.ZRes };
            double[] origin = { img.Origin.x, img.Origin.y, img.Origin.z };
            double[] direction = BuildDirectionCosines(img.XDirection, img.YDirection, img.ZDirection);
            return (buffer, size, spacing, origin, direction);
        }

        private static (float[] buffer, uint[] size, double[] spacing, double[] origin, double[] direction)
            ExtractDoseBuffers(Dose dose, float[,,] doseGy)
        {
            int nx = dose.XSize;
            int ny = dose.YSize;
            int nz = dose.ZSize;

            float[] buffer = new float[nx * ny * nz];
            for (int z = 0; z < nz; z++)
            {
                int baseIdx = z * nx * ny;
                for (int x = 0; x < nx; x++)
                    for (int y = 0; y < ny; y++)
                        buffer[baseIdx + y * nx + x] = doseGy[z, x, y];
            }

            uint[] size = { (uint)nx, (uint)ny, (uint)nz };
            double[] spacing = { dose.XRes, dose.YRes, dose.ZRes };
            double[] origin = { dose.Origin.x, dose.Origin.y, dose.Origin.z };
            double[] direction = BuildDirectionCosines(dose.XDirection, dose.YDirection, dose.ZDirection);
            return (buffer, size, spacing, origin, direction);
        }

        private static (uint[] size, double[] spacing, double[] origin, double[] direction)
            GetDoseGeometry(Dose dose)
        {
            uint[] size = { (uint)dose.XSize, (uint)dose.YSize, (uint)dose.ZSize };
            double[] spacing = { dose.XRes, dose.YRes, dose.ZRes };
            double[] origin = { dose.Origin.x, dose.Origin.y, dose.Origin.z };
            double[] direction = BuildDirectionCosines(dose.XDirection, dose.YDirection, dose.ZDirection);
            return (size, spacing, origin, direction);
        }

        // -----------------------------------------------------------------------
        // Structure mask helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Rasterizes a named structure from a structure set onto a binary voxel grid that
        /// matches the given CT image geometry.  Returns a flat byte[] buffer in the same
        /// x + y*nx + z*nx*ny layout used by <see cref="BufferToImage"/>, with 1 inside
        /// the structure and 0 outside.  Returns null when the structure is not found or
        /// has no contours.
        /// </summary>
        /// <summary>
        /// Rasterizes a named structure from a structure set onto a binary voxel grid that
        /// matches the given CT image geometry.  Returns a flat byte[] buffer in the same
        /// x + y*nx + z*nx*ny layout used by <see cref="BufferToImage"/>, with 1 inside
        /// the structure and 0 outside.  Returns null when the structure is not found or
        /// has no contours.
        /// </summary>
        internal static byte[] RasterizeStructureMask(
            StructureSet ss,
            string structureId,
            VMS.TPS.Common.Model.API.Image img)
        {
            if (ss == null || string.IsNullOrEmpty(structureId)) return null;

            var structure = ss.Structures.FirstOrDefault(s =>
                string.Equals(s.Id, structureId, StringComparison.OrdinalIgnoreCase));
            if (structure == null || structure.IsEmpty) return null;

            int nx = img.XSize;
            int ny = img.YSize;
            int nz = img.ZSize;

            byte[] mask = new byte[nx * ny * nz];

            for (int z = 0; z < nz; z++)
            {
                VVector[][] polygons = structure.GetContoursOnImagePlane(z);
                if (polygons == null || polygons.Length == 0) continue;

                foreach (var poly in polygons)
                {
                    if (poly == null || poly.Length < 3) continue;

                    var (pxX, pxY) = ProjectPolygonToPixelSpace(
                        poly, img.Origin, img.XDirection, img.YDirection, img.XRes, img.YRes);
                    FillPolygonIntoSlice(pxX, pxY, nx, ny, z, mask);
                }
            }
            return mask;
        }

        /// <summary>
        /// Projects a world-space polygon onto the 2-D pixel coordinate system of a CT slice.
        /// Pixel index ix = dot(P − origin, xDir) / dx; iy analogously for yDir/dy.
        /// </summary>
        /// <param name="polygon">Polygon vertices in DICOM patient coordinates (mm).</param>
        /// <param name="origin">Image origin in patient coordinates.</param>
        /// <param name="xDir">Unit vector along the image X axis (column direction).</param>
        /// <param name="yDir">Unit vector along the image Y axis (row direction).</param>
        /// <param name="dx">Pixel spacing along X (mm/voxel).</param>
        /// <param name="dy">Pixel spacing along Y (mm/voxel).</param>
        /// <returns>Parallel arrays of fractional pixel column (pxX) and row (pxY) indices.</returns>
        internal static (double[] pxX, double[] pxY) ProjectPolygonToPixelSpace(
            VVector[] polygon,
            VVector origin,
            VVector xDir,
            VVector yDir,
            double dx,
            double dy)
        {
            double[] pxX = new double[polygon.Length];
            double[] pxY = new double[polygon.Length];
            for (int i = 0; i < polygon.Length; i++)
            {
                var rel = polygon[i] - origin;
                pxX[i] = (rel.x * xDir.x + rel.y * xDir.y + rel.z * xDir.z) / dx;
                pxY[i] = (rel.x * yDir.x + rel.y * yDir.y + rel.z * yDir.z) / dy;
            }
            return (pxX, pxY);
        }

        /// <summary>
        /// Scanline even-odd fill: sets mask[sliceZ, iy, ix] = 1 for all pixels inside the
        /// polygon defined by fractional pixel-space coordinates (pxX, pxY).
        /// The mask buffer is indexed as  ix + iy*nx + sliceZ*nx*ny.
        /// </summary>
        internal static void FillPolygonIntoSlice(
            double[] pxX,
            double[] pxY,
            int nx,
            int ny,
            int sliceZ,
            byte[] mask)
        {
            int minIy = Math.Max(0, (int)Math.Floor(pxY.Min()));
            int maxIy = Math.Min(ny - 1, (int)Math.Ceiling(pxY.Max()));

            var xIntersects = new System.Collections.Generic.List<double>();
            int n = pxX.Length;

            for (int iy = minIy; iy <= maxIy; iy++)
            {
                double yScan = iy + 0.5;   // sample at pixel centre
                xIntersects.Clear();

                for (int i = 0, j = n - 1; i < n; j = i++)
                {
                    double y0 = pxY[j], y1 = pxY[i];
                    double x0 = pxX[j], x1 = pxX[i];
                    if ((y0 <= yScan && y1 > yScan) || (y1 <= yScan && y0 > yScan))
                    {
                        double xInt = x0 + (yScan - y0) * (x1 - x0) / (y1 - y0);
                        xIntersects.Add(xInt);
                    }
                }
                xIntersects.Sort();

                for (int k = 0; k + 1 < xIntersects.Count; k += 2)
                {
                    // Fill pixels whose centres (ix + 0.5) lie strictly inside the span.
                    // ixStart: first ix where ix+0.5 > xLeft  ⟹ ix > xLeft-0.5  ⟹ floor(xLeft+0.5)
                    // ixEnd:   last  ix where ix+0.5 < xRight ⟹ ix < xRight-0.5 ⟹ ceil(xRight-0.5)-1
                    int ixStart = Math.Max(0,      (int)Math.Floor(xIntersects[k] + 0.5));
                    int ixEnd   = Math.Min(nx - 1, (int)Math.Ceiling(xIntersects[k + 1] - 0.5) - 1);
                    int baseIdx = sliceZ * nx * ny + iy * nx;
                    for (int ix = ixStart; ix <= ixEnd; ix++)
                        mask[baseIdx + ix] = 1;
                }
            }
        }

        /// <summary>
        /// Wraps a raw byte mask buffer (1 = inside, 0 = outside) produced by
        /// <see cref="RasterizeStructureMask"/> into a SimpleITK sitkUInt8 image
        /// with the same geometry as the corresponding CT.
        /// </summary>
        private static SitkImage BuildMaskImage(
            byte[] maskBuffer,
            uint[] size,
            double[] spacing,
            double[] origin,
            double[] direction)
        {
            var handle = GCHandle.Alloc(maskBuffer, GCHandleType.Pinned);
            try
            {
                return SimpleITK.ImportAsUInt8(
                    handle.AddrOfPinnedObject(),
                    new VectorUInt32(size),
                    new VectorDouble(spacing),
                    new VectorDouble(origin),
                    new VectorDouble(direction),
                    1);
            }
            finally
            {
                handle.Free();
            }
        }

        internal static double[] BuildDirectionCosines(VVector xDir, VVector yDir, VVector zDir)
        {
            // SimpleITK direction matrix is a flattened 3x3 in row-major order:
            // [Xx Xy Xz  Yx Yy Yz  Zx Zy Zz]
            return new double[]
            {
                xDir.x, xDir.y, xDir.z,
                yDir.x, yDir.y, yDir.z,
                zDir.x, zDir.y, zDir.z
            };
        }

        private static SitkImage BufferToImage(
            float[] buffer, uint[] size,
            double[] spacing, double[] origin, double[] direction,
            PixelIDValueEnum pixelType)
        {
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try
            {
                var importImg = SimpleITK.ImportAsFloat(
                    handle.AddrOfPinnedObject(),
                    new VectorUInt32(size),
                    new VectorDouble(spacing),
                    new VectorDouble(origin),
                    new VectorDouble(direction),
                    1);
                return importImg;
            }
            finally
            {
                handle.Free();
            }
        }

        private static SitkImage BuildReferenceImage(uint[] size, double[] spacing, double[] origin, double[] direction)
        {
            var img = new SitkImage(new VectorUInt32(size), PixelIDValueEnum.sitkFloat32);
            img.SetSpacing(new VectorDouble(spacing));
            img.SetOrigin(new VectorDouble(origin));
            img.SetDirection(new VectorDouble(direction));
            return img;
        }

        private static float[] ImageToBuffer(SitkImage img)
        {
            uint nx = img.GetWidth();
            uint ny = img.GetHeight();
            uint nz = img.GetDepth();
            long count = (long)(nx * ny * nz);
            float[] buffer = new float[count];

            // Cast to float if not already
            SitkImage floatImg = img;
            bool disposeCast = false;
            if (img.GetPixelID() != PixelIDValueEnum.sitkFloat32)
            {
                floatImg = SimpleITK.Cast(img, PixelIDValueEnum.sitkFloat32);
                disposeCast = true;
            }

            try
            {
                IntPtr ptr = floatImg.GetBufferAsFloat();
                Marshal.Copy(ptr, buffer, 0, (int)count);
            }
            finally
            {
                if (disposeCast) floatImg.Dispose();
            }
            return buffer;
        }

        /// <summary>
        /// Converts a flat buffer [x + y*nx + z*nx*ny] back to float[Z, X, Y].
        /// </summary>
        internal static float[,,] BufferToArray3D(float[] buffer, uint[] size)
        {
            uint nx = size[0];
            uint ny = size[1];
            uint nz = size[2];
            float[,,] arr = new float[nz, nx, ny];
            for (uint z = 0; z < nz; z++)
            {
                uint baseIdx = z * nx * ny;
                for (uint x = 0; x < nx; x++)
                    for (uint y = 0; y < ny; y++)
                        arr[z, x, y] = buffer[baseIdx + y * nx + x];
            }
            return arr;
        }

        private static void Report(IProgress<string> progress, string message)
        {
            Helpers.SeriLog.LogInfo(message);
            progress?.Report(message);
        }
    }

    /// <summary>Wraps an Action as a SimpleITK Command (required by the SWIG binding).</summary>
    internal sealed class ActionCommand : itk.simple.Command
    {
        private readonly Action _action;
        public ActionCommand(Action action) { _action = action; }
        public override void Execute() => _action();
    }
}
