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

        // Bounds on the B-spline mesh size derived from control-point spacing, applied per axis.
        // Floor keeps a usable grid on tiny body extents; ceiling caps the parameter count so a
        // very fine spacing cannot explode the L-BFGS-B problem (e.g. 5 mm over a long FOV).
        private const uint MinBSplineMeshCells = 3;
        private const uint MaxBSplineMeshCells = 40;

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
        /// Lightweight payload used by the post-DIR quality review UI. Holds the warped
        /// (deformed) moving CT and the fixed (target) CT, both sampled on the fixed CT
        /// voxel grid, as flat float buffers in [x + y*nx + z*nx*ny] layout.
        /// </summary>
        public sealed class DirReviewData
        {
            public float[] WarpedMovingCt;  // DIR result resampled onto the fixed grid
            public float[] FixedCt;         // target CT on the same grid
            public uint[]  Size;            // [W, H, D] of the fixed grid
            public double[] Spacing;        // [sx, sy, sz] mm

            // --- Overlay data (may be null if computation failed) ---

            /// <summary>
            /// Jacobian determinant of the deformation field, one float per voxel on the
            /// fixed CT grid.  Values &lt; 1 indicate compression, &gt; 1 expansion.
            /// </summary>
            public float[] JacobianDet;

            /// <summary>
            /// Displacement field on the fixed CT grid, interleaved [dx, dy, dz] per voxel
            /// in [x + y*nx + z*nx*ny] order — i.e. length = 3 * nx * ny * nz.  Units: mm.
            /// </summary>
            public float[] DisplacementField;

            /// <summary>
            /// Deformed dose (Gy) resampled onto the fixed CT grid.  Same Size/Spacing as
            /// the CT fields above.  Null if dose data was not available.
            /// </summary>
            public float[] DeformedDoseGy;

            /// <summary>Maximum dose value in <see cref="DeformedDoseGy"/> (Gy), used to initialise the W/L slider.</summary>
            public float DeformedDoseMaxGy;
        }

        /// <summary>
        /// Payload retained after a successful DIR run so that the deformed CT image and the
        /// deformed structures can be exported to DICOM on demand.  The deformed CT is the
        /// warped moving image sampled on the fixed (target) CT grid; structures are warped
        /// later by reconstructing the displacement field stored here.
        /// All relational DICOM tags are captured from the target image so the exported series
        /// re-imports cleanly into the TPS (same patient / study / frame of reference).
        /// </summary>
        public sealed class DirExportData
        {
            // --- Deformed CT (warped moving image on the fixed grid), HU values ---
            public float[] DeformedCtHu;       // [x + y*nx + z*nx*ny] layout, HU
            public uint[]  Size;               // [W, H, D] of the fixed grid
            public double[] Spacing;           // [sx, sy, sz] mm
            public double[] Origin;            // [ox, oy, oz] mm (patient coords)
            public double[] Direction;         // row-major 3x3 direction cosines

            /// <summary>
            /// Displacement field on the fixed CT grid, interleaved [dx, dy, dz] per voxel in
            /// [x + y*nx + z*nx*ny] order (length = 3 * nx * ny * nz).  Units: mm.  Maps a fixed
            /// (output) voxel back to its location in the moving image (SimpleITK transform sense).
            /// </summary>
            public float[] DisplacementField;

            // --- Source plan identifiers (the structures to warp come from here) ---
            public string SourceCourseId;
            public string SourcePlanId;

            // --- Relational DICOM tags captured from the TARGET image (preserved on export) ---
            public string PatientId;
            public string PatientName;
            public string PatientBirthDate;     // DICOM DA (yyyyMMdd) or empty
            public string PatientSex;           // M / F / O or empty
            public string StudyInstanceUid;     // preserved so the series stays in the same study
            public string FrameOfReferenceUid;  // preserved so spatial relation is retained
            public string StudyDate;            // DICOM DA (yyyyMMdd) from target study
            public string StudyTime;            // DICOM TM (HHmmss) from target study
            public string StudyId;              // Study ID string from target study
        }

        /// <summary>
        /// Full DIR pipeline: extract CTs → register → resample dose → write Eclipse plan.
        /// Must be called from a non-dispatcher thread (Task.Run is fine).
        /// </summary>
        /// <param name="sourceCourseId">Course containing the source (moving) plan.</param>
        /// <param name="sourcePlanId">Plan whose dose will be deformed.</param>
        /// <param name="targetSsId">Structure set ID whose CT image defines the fixed (target) space.</param>
        /// <param name="newPlanName">Eclipse Id for the output verification plan (max 13 chars).</param>
        /// <param name="sourceMaskStructureId">Structure Id in the source plan's structure set to use as moving mask (null = full image).</param>
        /// <param name="targetMaskStructureId">Structure Id in the target structure set to use as fixed mask (null = full image).</param>
        /// <param name="progress">Optional progress reporter for status strings.</param>
        /// <param name="rigidMatrixRowMajor">Optional row-major 4×4 ESAPI rigid registration matrix (source→target, mm) to use as the
        /// initial composite transform before B-spline fitting. Null = B-spline default initialisation.</param>
        public async Task<(ScriptStatus status, string message, DirReviewData review, DirExportData export)> PerformDIRAndWritePlan(
            string sourceCourseId,
            string sourcePlanId,
            string targetSsId,
            string newPlanName,
            string sourceMaskStructureId = null,
            string targetMaskStructureId = null,
            IProgress<string> progress = null,
            double[] rigidMatrixRowMajor = null,
            RegistrationAlgorithmType algorithm = RegistrationAlgorithmType.Demons)
        {
            // ---- 1. Extract image and dose data inside the Eclipse dispatcher --------
            float[] movingCTBuffer = null, fixedCTBuffer = null, sourceDoseBuffer = null;
            uint[] movingCTSize = null, fixedCTSize = null, sourceDoseSize = null;
            double[] movingCTSpacing = null, fixedCTSpacing = null, sourceDoseSpacing = null;
            double[] movingCTOrigin = null, fixedCTOrigin = null, sourceDoseOrigin = null;
            double[] movingCTDirection = null, fixedCTDirection = null, sourceDoseDirection = null;
            double sourceDoseScaling = 1.0;

            // Source plan fractionation (used for the output verification plan prescription).
            int sourceFractions = 0;
            VMS.TPS.Common.Model.Types.DoseValue sourceDosePerFraction = default;

            // Mask buffers (null = full-image registration)
            byte[] movingMaskBuffer = null, fixedMaskBuffer = null;

            // Relational DICOM tags captured from the target image (preserved on DICOM export).
            string patientId = null, patientName = null, patientBirthDate = null, patientSex = null;
            string studyInstanceUid = null, frameOfReferenceUid = null;
            string studyDate = null, studyTime = null, studyId = null;

            // Target dose geometry (defines the output grid)
            uint[] targetDoseSize = null;
            double[] targetDoseSpacing = null, targetDoseOrigin = null, targetDoseDirection = null;

            string extractionError = null;

            Report(progress, "Extracting image data from Eclipse...");

            // Context plan for the plan-write step: first active plan in the patient that
            // references the target SS and has dose.  Resolved during data extraction.
            ExternalPlanSetup resolvedContextPlan = null;

            await _ew.AsyncRunPatientContext(p =>
            {
                try
                {
                    var sourceCourse = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, sourceCourseId, StringComparison.OrdinalIgnoreCase));
                    var sourcePlan = sourceCourse?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, sourcePlanId, StringComparison.OrdinalIgnoreCase)) as ExternalPlanSetup;

                    var targetSS = p.StructureSets.FirstOrDefault(s =>
                        string.Equals(s.Id, targetSsId, StringComparison.OrdinalIgnoreCase));

                    if (sourcePlan == null) { extractionError = $"Source plan '{sourceCourseId}/{sourcePlanId}' not found."; return; }
                    if (sourcePlan.Dose == null) { extractionError = "Source plan has no dose."; return; }
                    if (targetSS == null) { extractionError = $"Target structure set '{targetSsId}' not found."; return; }

                    var movingImage = sourcePlan.StructureSet?.Image;
                    var fixedImage  = targetSS.Image;
                    if (movingImage == null) { extractionError = "Source plan has no CT image."; return; }
                    if (fixedImage  == null) { extractionError = $"Target structure set '{targetSsId}' has no CT image."; return; }

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
                            targetSS, targetMaskStructureId, fixedImage);

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

                    // Capture source fractionation for the output verification plan.
                    sourceFractions = (int)(sourcePlan.NumberOfFractions ?? 1);
                    sourceDosePerFraction = sourcePlan.DosePerFraction;

                    // Find a context plan for writing the output deformed-dose plan:
                    // first active ExternalPlanSetup that references the target SS and has dose.
                    var excludedStatuses = new[]
                    {
                        VMS.TPS.Common.Model.Types.PlanSetupApprovalStatus.Completed,
                        VMS.TPS.Common.Model.Types.PlanSetupApprovalStatus.CompletedEarly,
                        VMS.TPS.Common.Model.Types.PlanSetupApprovalStatus.TreatmentApproved,
                        VMS.TPS.Common.Model.Types.PlanSetupApprovalStatus.Retired,
                    };
                    foreach (var course in p.Courses)
                    {
                        foreach (var plan in course.PlanSetups.OfType<ExternalPlanSetup>()
                            .Where(pl => pl.StructureSet != null
                                && string.Equals(pl.StructureSet.Id, targetSsId, StringComparison.OrdinalIgnoreCase)
                                && pl.Dose != null
                                && !excludedStatuses.Contains(pl.ApprovalStatus)))
                        {
                            resolvedContextPlan = plan;
                            break;
                        }
                        if (resolvedContextPlan != null) break;
                    }

                    // Target dose geometry: use context plan's dose grid if found,
                    // otherwise fall back to the fixed CT grid.
                    if (resolvedContextPlan != null)
                    {
                        (targetDoseSize, targetDoseSpacing, targetDoseOrigin, targetDoseDirection) =
                            GetDoseGeometry(resolvedContextPlan.Dose);
                    }
                    else
                    {
                        targetDoseSize      = new uint[]   { fixedCTSize[0], fixedCTSize[1], fixedCTSize[2] };
                        targetDoseSpacing   = (double[])fixedCTSpacing.Clone();
                        targetDoseOrigin    = (double[])fixedCTOrigin.Clone();
                        targetDoseDirection = (double[])fixedCTDirection.Clone();
                    }

                    // Capture relational DICOM tags from the target image so a later DICOM
                    // export keeps the deformed series in the same patient / study / FoR.
                    try
                    {
                        patientId        = p.Id;
                        patientName      = BuildDicomPatientName(p);
                        patientBirthDate = p.DateOfBirth.HasValue
                            ? p.DateOfBirth.Value.ToString("yyyyMMdd")
                            : string.Empty;
                        patientSex          = MapPatientSex(p.Sex);
                        studyInstanceUid    = fixedImage.Series?.Study?.UID ?? string.Empty;
                        frameOfReferenceUid = fixedImage.FOR ?? string.Empty;
                        studyDate           = fixedImage.Series?.Study?.CreationDateTime.HasValue == true
                            ? fixedImage.Series.Study.CreationDateTime.Value.ToString("yyyyMMdd")
                            : string.Empty;
                        studyTime           = fixedImage.Series?.Study?.CreationDateTime.HasValue == true
                            ? fixedImage.Series.Study.CreationDateTime.Value.ToString("HHmmss")
                            : string.Empty;
                        studyId             = fixedImage.Series?.Study?.Id ?? string.Empty;
                    }
                    catch (Exception exTags)
                    {
                        Helpers.SeriLog.LogError("Failed to capture relational DICOM tags", exTags);
                    }
                }
                catch (Exception ex)
                {
                    extractionError = $"Data extraction failed: {ex.Message}";
                    Helpers.SeriLog.LogError("DIR extraction error", ex);
                }
            });

            if (extractionError != null)
                return (ScriptStatus.Error, extractionError, null, null);

            // ---- 2. Run SimpleITK registration and resampling on background thread ---
            Report(progress, "Running deformable image registration (this may take several minutes)...");
            float[] deformedDoseBuffer = null;
            string sitkError = null;
            DirReviewData reviewData = null;
            DirExportData exportData = null;
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

                        SitkTransform finalTransform;
                        if (algorithm == RegistrationAlgorithmType.BSpline)
                        {
                            Report(progress, "Starting B-spline registration (metric masks)...");
                            finalTransform = RunBSplineMaskedRegistration(
                                fixedCT, movingCT, progress, fixedMaskImg, movingMaskImg, rigidMatrixRowMajor);
                        }
                        else
                        {
                            Report(progress, "Starting diffeomorphic demons registration...");
                            finalTransform = RunDiffeomorphicDemonsRegistration(
                                fixedCT, movingCT, progress, fixedMaskImg, movingMaskImg, rigidMatrixRowMajor);
                        }
                        try
                        {
                            using (var warpedMovingCt = SimpleITK.Resample(
                                movingCT,
                                fixedCT,
                                finalTransform,
                                InterpolatorEnum.sitkLinear,
                                -1000.0,
                                movingCT.GetPixelID()))
                            {
                                reviewData = new DirReviewData
                                {
                                    WarpedMovingCt = ImageToBuffer(warpedMovingCt),
                                    FixedCt        = ImageToBuffer(fixedCT),
                                    Size           = new uint[]
                                    {
                                        fixedCT.GetWidth(),
                                        fixedCT.GetHeight(),
                                        fixedCT.GetDepth()
                                    },
                                    Spacing = new double[]
                                    {
                                        fixedCTSpacing[0], fixedCTSpacing[1], fixedCTSpacing[2]
                                    }
                                };

                                // Retain the deformed CT + fixed-grid geometry for an optional
                                // DICOM export. The warped moving CT (HU) is the deformed image.
                                exportData = new DirExportData
                                {
                                    DeformedCtHu = reviewData.WarpedMovingCt,
                                    Size      = new uint[]   { fixedCT.GetWidth(), fixedCT.GetHeight(), fixedCT.GetDepth() },
                                    Spacing   = new double[] { fixedCTSpacing[0], fixedCTSpacing[1], fixedCTSpacing[2] },
                                    Origin    = new double[] { fixedCTOrigin[0], fixedCTOrigin[1], fixedCTOrigin[2] },
                                    Direction = (double[])fixedCTDirection.Clone(),
                                    SourceCourseId      = sourceCourseId,
                                    SourcePlanId        = sourcePlanId,
                                    PatientId           = patientId,
                                    PatientName         = patientName,
                                    PatientBirthDate    = patientBirthDate,
                                    PatientSex          = patientSex,
                                    StudyInstanceUid    = studyInstanceUid,
                                    FrameOfReferenceUid = frameOfReferenceUid,
                                    StudyDate           = studyDate,
                                    StudyTime           = studyTime,
                                    StudyId             = studyId
                                };
                            }

                            // ---- Jacobian determinant ----
                            Report(progress, "Computing Jacobian determinant...");
                            try
                            {
                                // Convert the composite/displacement transform to a full-resolution
                                // displacement field so SimpleITK can compute its Jacobian.
                                var t2df = new TransformToDisplacementFieldFilter();
                                t2df.SetReferenceImage(fixedCT);
                                using (var fullField = t2df.Execute(finalTransform))
                                {
                                    // Jacobian determinant map (one scalar per voxel)
                                    using (var jacImg = SimpleITK.DisplacementFieldJacobianDeterminant(fullField))
                                    {
                                        reviewData.JacobianDet = ImageToBuffer(jacImg);
                                    }

                                    // Raw displacement field (vector image, 3 components per voxel)
                                    // Store as interleaved float[]: [dx0,dy0,dz0, dx1,dy1,dz1, ...]
                                    reviewData.DisplacementField = ExtractVectorImageBuffer(fullField);

                                    // Retain the same field for structure warping during DICOM export.
                                    if (exportData != null)
                                        exportData.DisplacementField = reviewData.DisplacementField;
                                }
                            }
                            catch (Exception exJac)
                            {
                                Helpers.SeriLog.LogError("Failed to compute Jacobian/displacement field for review", exJac);
                                // Non-fatal; reviewData.JacobianDet / DisplacementField stay null.
                            }

                            // ---- Deformed dose on the fixed-CT grid ----
                            Report(progress, "Resampling deformed dose onto CT grid for review...");
                            try
                            {
                                using (var resampledDoseOnCt = SimpleITK.Resample(
                                    sourceDoseImg,
                                    fixedCT,
                                    finalTransform,
                                    InterpolatorEnum.sitkLinear,
                                    0.0,
                                    sourceDoseImg.GetPixelID()))
                                {
                                    reviewData.DeformedDoseGy = ImageToBuffer(resampledDoseOnCt);
                                    // Find max for slider initialisation
                                    float doseMax = 0f;
                                    foreach (float v in reviewData.DeformedDoseGy)
                                        if (v > doseMax) doseMax = v;
                                    reviewData.DeformedDoseMaxGy = doseMax > 0 ? doseMax : 1f;
                                }
                            }
                            catch (Exception exDose)
                            {
                                Helpers.SeriLog.LogError("Failed to resample dose onto CT grid for review", exDose);
                            }
                        }
                        catch (Exception exReview)
                        {
                            // Review data is non-essential; log and continue with dose deformation.
                            Helpers.SeriLog.LogError("Failed to build DIR review images", exReview);
                            reviewData = null;
                        }

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
                return (ScriptStatus.Error, sitkError, null, null);
            }

            // ---- 3. Reconstruct float[Z,X,Y] from buffer and write Eclipse plan -----
            Report(progress, "Writing deformed dose plan to Eclipse...");
            float[,,] deformedDoseGy = BufferToArray3D(deformedDoseBuffer, targetDoseSize);

            string planWriteError = null;
            string createdPlanId = null;

            if (resolvedContextPlan != null)
            {
                // We have a plan with dose on the target SS — write the deformed result into
                // the same course/SS as a verification plan.
                string ctxCourseId = resolvedContextPlan.Course.Id;
                string ctxPlanId   = resolvedContextPlan.Id;
                await _ew.AsyncRunPatientContext(p =>
                {
                    try
                    {
                        var ctxCourse = p.Courses.FirstOrDefault(c =>
                            string.Equals(c.Id, ctxCourseId, StringComparison.OrdinalIgnoreCase));
                        var ctxPlan = ctxCourse?.PlanSetups.FirstOrDefault(pl =>
                            string.Equals(pl.Id, ctxPlanId, StringComparison.OrdinalIgnoreCase)) as ExternalPlanSetup;

                        if (ctxPlan == null) { planWriteError = "Context plan not found when writing result."; return; }

                        createdPlanId = _model.CreateDeformedDosePlan(
                            newPlanName, ctxPlan, deformedDoseGy, ctxPlan.Dose,
                            sourceFractions, sourceDosePerFraction);
                    }
                    catch (Exception ex)
                    {
                        planWriteError = $"Failed to write deformed dose plan: {ex.Message}";
                        Helpers.SeriLog.LogError("DIR plan write error", ex);
                    }
                });

                if (planWriteError != null)
                    return (ScriptStatus.Error, planWriteError, reviewData, exportData);
            }
            else
            {
                Helpers.SeriLog.LogInfo("No active plan with dose found for target SS — skipping Eclipse plan write. " +
                    "Use DICOM export to retrieve the deformed image and structures.");
            }

            string successMsg = $"Deformable registration complete - please go to DIR Quality Review.";
            Helpers.SeriLog.LogInfo(successMsg);
            Report(progress, successMsg);
            return (ScriptStatus.Complete, successMsg, reviewData, exportData);
        }

        // -----------------------------------------------------------------------
        // DICOM export of the deformed image + structures
        // -----------------------------------------------------------------------

        /// <summary>A single deformed structure ready for RTSTRUCT export.</summary>
        public sealed class DeformedStructure
        {
            public string Id;
            public string DicomType;          // e.g. "ORGAN", "PTV", "EXTERNAL", "CONTROL"
            public byte[] Color = { 255, 0, 0 };  // RGB
            /// <summary>
            /// One entry per CT slice that has contours.  Each entry: (z-slice-index, list of
            /// polygons, each polygon a flat list of patient-space points [x0,y0,z0, x1,y1,z1, …]).
            /// </summary>
            public List<(int z, List<double[]> polygons)> ContoursBySlice = new List<(int, List<double[]>)>();
        }

        /// <summary>
        /// Exports the deformed CT image and the deformed source structures to DICOM in
        /// <paramref name="outputDirectory"/>.  Rasterizes each source structure on the moving CT
        /// grid (ESAPI context), warps it onto the fixed grid via the stored displacement field
        /// (background thread), traces contours, and writes a CT series + RTSTRUCT that preserve
        /// the patient / study / frame-of-reference relational tags while assigning new
        /// Series / SOP / RTSTRUCT UIDs.
        /// </summary>
        public async Task<(ScriptStatus status, string message)> ExportDeformedDicom(
            DirExportData export,
            string outputDirectory,
            IProgress<string> progress = null)
        {
            if (export == null)
                return (ScriptStatus.Error, "No deformed data available to export. Run a DIR first.");
            if (export.DeformedCtHu == null || export.Size == null)
                return (ScriptStatus.Error, "Deformed CT image is unavailable for export.");
            if (string.IsNullOrWhiteSpace(outputDirectory))
                return (ScriptStatus.Error, "No output directory was selected.");

            // ---- 1. Rasterize source structures on the moving CT grid (ESAPI context) ----
            Report(progress, "Extracting source structures for warping...");

            var movingMasks = new List<(string id, string dicomType, byte[] color, byte[] mask)>();
            uint[] mSize = null;
            double[] mSpacing = null, mOrigin = null, mDir = null;
            string extractError = null;

            string srcCourseId = export.SourceCourseId;
            string srcPlanId   = export.SourcePlanId;

            await _ew.AsyncRunPatientContext(p =>
            {
                try
                {
                    var course = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, srcCourseId, StringComparison.OrdinalIgnoreCase));
                    var plan = course?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, srcPlanId, StringComparison.OrdinalIgnoreCase));
                    var ss  = plan?.StructureSet;
                    var img = ss?.Image;
                    if (ss == null || img == null) { extractError = "Source structure set or image not found."; return; }

                    mSize    = new uint[]   { (uint)img.XSize, (uint)img.YSize, (uint)img.ZSize };
                    mSpacing = new double[] { img.XRes, img.YRes, img.ZRes };
                    mOrigin  = new double[] { img.Origin.x, img.Origin.y, img.Origin.z };
                    mDir     = BuildDirectionCosines(img.XDirection, img.YDirection, img.ZDirection);

                    foreach (var s in ss.Structures)
                    {
                        if (s.IsEmpty) continue;
                        // Skip non-contourable structure types (e.g. markers handled separately).
                        byte[] mask = RasterizeStructureMask(ss, s.Id, img);
                        if (mask == null) continue;

                        byte[] color = { 255, 0, 0 };
                        try { color = new[] { s.Color.R, s.Color.G, s.Color.B }; } catch { }

                        string dtype = "ORGAN";
                        try { if (!string.IsNullOrEmpty(s.DicomType)) dtype = s.DicomType; } catch { }

                        movingMasks.Add((s.Id, dtype, color, mask));
                    }
                }
                catch (Exception ex)
                {
                    extractError = $"Failed to extract source structures: {ex.Message}";
                    Helpers.SeriLog.LogError("DICOM export structure extraction error", ex);
                }
            });

            if (extractError != null)
                return (ScriptStatus.Error, extractError);

            // ---- 2. Warp masks + trace contours on a background thread ----
            var deformedStructures = new List<DeformedStructure>();
            string warpError = null;
            try
            {
                await Task.Run(() =>
                {
                    int fnx = (int)export.Size[0], fny = (int)export.Size[1], fnz = (int)export.Size[2];

                    foreach (var ms in movingMasks)
                    {
                        Report(progress, $"Warping structure '{ms.id}'...");

                        byte[] warped;
                        if (export.DisplacementField != null)
                        {
                            warped = WarpMaskToFixedGrid(
                                ms.mask, mSize, mSpacing, mOrigin, mDir,
                                export.DisplacementField,
                                export.Size, export.Spacing, export.Origin, export.Direction);
                        }
                        else
                        {
                            // No field available (rare): skip warping, structure cannot be exported.
                            continue;
                        }

                        var ds = new DeformedStructure
                        {
                            Id = ms.id,
                            DicomType = ms.dicomType,
                            Color = ms.color
                        };

                        for (int z = 0; z < fnz; z++)
                        {
                            var slicePolys = TraceSliceContours(warped, fnx, fny, z);
                            if (slicePolys.Count == 0) continue;

                            var worldPolys = new List<double[]>();
                            foreach (var poly in slicePolys)
                            {
                                // Smooth the jagged pixel-boundary polygon and reduce its
                                // point count before converting to patient coordinates.
                                // SmoothAndSubsamplePolygon already applies the +0.5
                                // pixel-centre offset so PixelToWorld receives fractional coords.
                                var smoothed = SmoothAndSubsamplePolygon(poly);
                                if (smoothed.Count < 3) continue;

                                var pts = new List<double>(smoothed.Count * 3);
                                foreach (var sp in smoothed)
                                {
                                    double[] w = PixelToWorld(
                                        sp[0], sp[1], z,
                                        export.Origin, export.Spacing, export.Direction);
                                    pts.Add(w[0]); pts.Add(w[1]); pts.Add(w[2]);
                                }
                                worldPolys.Add(pts.ToArray());
                            }
                            if (worldPolys.Count > 0)
                                ds.ContoursBySlice.Add((z, worldPolys));
                        }

                        if (ds.ContoursBySlice.Count > 0)
                            deformedStructures.Add(ds);
                    }
                });
            }
            catch (Exception ex)
            {
                warpError = $"Failed to warp structures: {ex.Message}";
                Helpers.SeriLog.LogError("DICOM export warp error", ex);
            }

            if (warpError != null)
                return (ScriptStatus.Error, warpError);

            // ---- 3. Write DICOM CT series + RTSTRUCT ----
            Report(progress, "Writing DICOM files...");
            try
            {
                await Task.Run(() =>
                    DicomExportService.WriteDeformedSeries(export, deformedStructures, outputDirectory, progress));
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError("DICOM export write error", ex);
                return (ScriptStatus.Error, $"Failed to write DICOM files: {ex.Message}");
            }

            string msg = $"Exported deformed CT ({export.Size[2]} slices) and {deformedStructures.Count} structure(s) to: {outputDirectory}";
            Helpers.SeriLog.LogInfo(msg);
            Report(progress, msg);
            return (ScriptStatus.Complete, msg);
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
                IProgress<string> progress = null,
                double[] rigidMatrixRowMajor = null)
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

                        Report(progress, $"[{sourceCourseId}/{sourcePlanId}] Starting diffeomorphic demons registration...");
                        SitkTransform finalTransform = RunDiffeomorphicDemonsRegistration(fixedCT, movingCT, progress, fixedMaskImg, movingMaskImg, rigidMatrixRowMajor);

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

        /// <summary>
        /// Runs a multi-resolution diffeomorphic demons registration (CT-to-CT, unimodal).
        /// When <paramref name="rigidMatrixRowMajor"/> is supplied, the moving image is
        /// pre-warped onto the fixed grid via the rigid transform before demons iterations
        /// begin; the returned transform composes the rigid alignment with the demons
        /// displacement field so that the caller can use it directly to resample the dose.
        /// </summary>
        private SitkTransform RunDiffeomorphicDemonsRegistration(
            SitkImage fixedCT, SitkImage movingCT,
            IProgress<string> progress,
            SitkImage fixedMask = null, SitkImage movingMask = null,
            double[] rigidMatrixRowMajor = null)
        {
            var rp = _model.Config?.RegistrationParameters ?? new DoseConverterConfigRegistrationParameters();

            // ---- Diagnostic: confirm masks are present and non-empty -------------
            // Demons has NO native mask support, so the ONLY way masks constrain the
            // registration is by zeroing image intensity outside the contour.  If a
            // mask buffer is silently empty (e.g. a structure id mismatch), the run
            // will proceed completely unconstrained and the failure is only visible
            // after an expensive visual review.  Logging the in-body voxel counts here
            // lets the user confirm from the log file that masking is actually active.
            LogMaskCoverage("fixed (target) body mask", fixedMask);
            LogMaskCoverage("moving (source) body mask", movingMask);
            if (fixedMask == null && movingMask == null)
                Helpers.SeriLog.LogInfo(
                    "DIR WARNING: no body masks supplied - registration is fully UNCONSTRAINED. " +
                    "Bolus or other out-of-body anatomy CAN influence the deformation.");

            var shrinkFactors  = ParseUIntList(rp.ShrinkFactorsPerLevel, new uint[]   { 4, 2, 1 });
            var smoothSigmas   = ParseDoubleList(rp.SmoothingSigmasPerLevel, new double[] { 2.0, 1.0, 0.0 });
            int numLevels      = shrinkFactors.Length;

            uint defaultMaxIter = ParseUInt(rp.MaxIterations, 50);
            uint[] perLevelIter = ParseUIntList(rp.MaxIterationsPerLevel, new uint[0]);
            uint[] maxIterPerLevel = new uint[numLevels];
            for (int i = 0; i < numLevels; i++)
                maxIterPerLevel[i] = (i < perLevelIter.Length) ? perLevelIter[i] : defaultMaxIter;

            // Demons displacement-field smoothing standard deviation(s).
            double[] stdDevs = ParseDoubleList(rp.DemonsStandardDeviations, new double[] { 1.0 });

            // Maximum demons update step length (mm).
            double maxStepLength = rp.DemonsMaxStepLength > 0 ? rp.DemonsMaxStepLength : 2.0;

            // ---- Optional rigid pre-alignment ----------------------------------
            // Build an AffineTransform from the supplied 4×4 ESAPI matrix so we can
            // pre-warp the moving CT onto the fixed coordinate system before running demons.
            // SimpleITK transforms map fixed→moving, so we invert the src→tgt ESAPI matrix.
            AffineTransform rigidAffine = null;
            SitkImage preAlignedMoving = movingCT;
            bool disposedPreAligned = false;

            // The moving body mask must be transformed into the fixed image's physical
            // coordinate space by the same rigid transform used for the moving CT.
            // If it is left in the original moving space, the mask boundary ends up
            // displaced from the actual rigidly-aligned skin surface in the fixed frame,
            // producing a sharp artefact ring floating 3-5 cm from the patient surface.
            SitkImage preAlignedMovingMask = movingMask;
            bool disposePreAlignedMovingMask = false;

            if (rigidMatrixRowMajor != null && rigidMatrixRowMajor.Length == 16)
            {
                Report(progress, "Pre-aligning moving CT with rigid registration...");
                double[] inv = InvertRigidMatrix4x4(rigidMatrixRowMajor);
                rigidAffine = new AffineTransform(3);
                rigidAffine.SetMatrix(new VectorDouble(new double[]
                {
                    inv[0], inv[1], inv[2],
                    inv[4], inv[5], inv[6],
                    inv[8], inv[9], inv[10]
                }));
                rigidAffine.SetTranslation(new VectorDouble(new double[] { inv[3], inv[7], inv[11] }));

                preAlignedMoving = SimpleITK.Resample(
                    movingCT, fixedCT, rigidAffine,
                    InterpolatorEnum.sitkLinear, 0.0, movingCT.GetPixelID());
                disposedPreAligned = true;

                // Warp the moving body mask into the fixed coordinate space using the
                // same rigid transform so its boundary coincides with the pre-aligned anatomy.
                if (movingMask != null)
                {
                    preAlignedMovingMask = SimpleITK.Resample(
                        movingMask, fixedCT, rigidAffine,
                        InterpolatorEnum.sitkNearestNeighbor, 0.0, movingMask.GetPixelID());
                    disposePreAlignedMovingMask = true;
                }
            }

            // ---- Intensity normalisation (demons works best on matched ranges) --
            // 1. Histogram-match moving → fixed so intensity distributions align.
            // 2. Rescale both to [0, 255] for comparable gradient magnitudes.
            SitkImage normFixed  = null;
            SitkImage normMoving = null;
            SitkImage histMatchedMoving = null;
            try
            {
                Report(progress, "Histogram matching moving CT to fixed CT...");
                var histMatcher = new HistogramMatchingImageFilter();
                histMatcher.SetNumberOfHistogramLevels(1024);
                histMatcher.SetNumberOfMatchPoints(7);
                histMatcher.ThresholdAtMeanIntensityOn();
                // Execute(sourceImage, referenceImage)
                histMatchedMoving = histMatcher.Execute(preAlignedMoving, fixedCT);

                var rescale = new RescaleIntensityImageFilter();
                rescale.SetOutputMinimum(0);
                rescale.SetOutputMaximum(255);
                normFixed  = rescale.Execute(fixedCT);

                // Resample the (histogram-matched) moving image onto the fixed image grid
                // ONCE before the pyramid.  This ensures both images share exactly the same
                // origin, spacing, direction, and voxel count, so independent ShrinkImageFilter
                // calls will always produce identical sizes at every level — avoiding the
                // size-mismatch error that occurs when two CTs have different Z extents.
                SitkImage movingOnFixedGrid;
                if (histMatchedMoving.GetSize().SequenceEqual(fixedCT.GetSize()))
                {
                    movingOnFixedGrid = histMatchedMoving;
                    histMatchedMoving = null; // ownership transferred; don't double-dispose
                }
                else
                {
                    var preRsmp = new ResampleImageFilter();
                    preRsmp.SetReferenceImage(fixedCT);
                    preRsmp.SetInterpolator(InterpolatorEnum.sitkLinear);
                    preRsmp.SetDefaultPixelValue(0);
                    movingOnFixedGrid = preRsmp.Execute(histMatchedMoving);
                    histMatchedMoving.Dispose();
                    histMatchedMoving = null;
                }
                SitkImage rawNormMoving = rescale.Execute(movingOnFixedGrid);
                movingOnFixedGrid.Dispose();
                normMoving = rawNormMoving;

                // ---- Apply body masks to the normalised images ------------------
                // Pre-registration masking uses the INTERSECTION (AND) of both body masks.
                //
                // Why intersection, not union:
                //   Demons minimises per-voxel intensity differences.  If a structure
                //   exists only in one image — e.g. a bolus present only on the fixed
                //   (target) CT — using the union means the fixed image retains the
                //   bolus intensities while the moving image has zero there.  This
                //   non-zero vs zero difference creates a large gradient force that
                //   drives displacement vectors toward the bolus, pulling source tissue
                //   into an unrealistic position.  The Gaussian field-smoothing that
                //   demons applies at every iteration then propagates ("bleeds") this
                //   artefact across the surrounding field, producing the characteristic
                //   outward-stretching artefact outside the body contour even after the
                //   post-registration displacement field is zeroed.
                //
                //   With intersection masking, any voxel outside EITHER body is zeroed
                //   in BOTH images (fixed = 0, moving = 0), so the intensity difference
                //   is already zero and demons produces no update force there.  The
                //   bolus region (inside fixed body, outside moving body) therefore
                //   receives zero displacement, which is the clinically correct result.
                //
                // Post-registration field masking (below) keeps the UNION so that
                // displacement vectors outside both bodies are zeroed in the final field.
                if (fixedMask != null || preAlignedMovingMask != null)
                {
                    SitkImage preMask = null;
                    if (fixedMask != null && preAlignedMovingMask != null)
                    {
                        // Resample the (already rigidly-aligned) moving mask onto the fixed
                        // grid, then AND with the fixed mask to get the intersection of both
                        // bodies.  Intersection ensures that regions present only in one image
                        // (e.g. a bolus on the target CT) appear as 0/0 in both images so
                        // demons sees zero gradient there and produces no displacement force.
                        var rsmp = new ResampleImageFilter();
                        rsmp.SetReferenceImage(fixedCT);
                        rsmp.SetInterpolator(InterpolatorEnum.sitkNearestNeighbor);
                        rsmp.SetDefaultPixelValue(0);
                        using (var movOnFixed = rsmp.Execute(preAlignedMovingMask))
                            preMask = SimpleITK.And(
                                SimpleITK.Cast(fixedMask,         PixelIDValueEnum.sitkUInt8),
                                SimpleITK.Cast(movOnFixed,        PixelIDValueEnum.sitkUInt8));
                    }
                    else if (fixedMask != null)
                    {
                        preMask = SimpleITK.Cast(fixedMask, PixelIDValueEnum.sitkUInt8);
                    }
                    else
                    {
                        var rsmp = new ResampleImageFilter();
                        rsmp.SetReferenceImage(fixedCT);
                        rsmp.SetInterpolator(InterpolatorEnum.sitkNearestNeighbor);
                        rsmp.SetDefaultPixelValue(0);
                        using (var movOnFixed = rsmp.Execute(preAlignedMovingMask))
                            preMask = SimpleITK.Cast(movOnFixed, PixelIDValueEnum.sitkUInt8);
                    }

                    using (preMask)
                    {
                        SitkImage maskedFixed  = SimpleITK.Mask(normFixed,  preMask);
                        SitkImage maskedMoving = SimpleITK.Mask(normMoving, preMask);
                        normFixed.Dispose();
                        normMoving.Dispose();
                        normFixed  = maskedFixed;
                        normMoving = maskedMoving;
                    }
                }

                // ---- Multi-resolution pyramid -----------------------------------
                // Run demons at each pyramid level; upsample the resulting displacement
                // field to initialise the next (finer) level.
                // Both normFixed and normMoving are now on the same grid, so independent
                // shrinking is safe and produces identical voxel counts at every level.
                SitkImage currentDispField = null;

                for (int lvl = 0; lvl < numLevels; lvl++)
                {
                    uint sf     = shrinkFactors[lvl];
                    double sigma = smoothSigmas.Length > lvl ? smoothSigmas[lvl] : 0.0;
                    uint iters  = maxIterPerLevel[lvl];
                    double stdDev = stdDevs.Length > lvl ? stdDevs[lvl] : stdDevs[stdDevs.Length - 1];

                    Report(progress, $"Demons level {lvl + 1}/{numLevels} (shrink×{sf}, σ={sigma:F1} mm, {iters} iters, field σ={stdDev:F1})...");

                    // Smooth + downsample both CTs independently (safe because they share
                    // the same grid after the pre-loop resample above).
                    SitkImage shrunkFixed, shrunkMoving;
                    if (sf > 1)
                    {
                        var smoother = new SmoothingRecursiveGaussianImageFilter();
                        smoother.SetSigma(sigma);
                        using (var smoothedFixed  = smoother.Execute(normFixed))
                        using (var smoothedMoving = smoother.Execute(normMoving))
                        {
                            var shrinker  = new ShrinkImageFilter();
                            var shrinkVec = new VectorUInt32(new uint[] { sf, sf, sf });
                            shrinker.SetShrinkFactors(shrinkVec);
                            shrunkFixed  = shrinker.Execute(smoothedFixed);
                            shrunkMoving = shrinker.Execute(smoothedMoving);
                        }
                    }
                    else
                    {
                        shrunkFixed  = normFixed;
                        shrunkMoving = normMoving;
                    }

                    // Upsample previous displacement field to current resolution.
                    SitkImage initField = null;
                    if (currentDispField != null)
                    {
                        var upsampleFilter = new ResampleImageFilter();
                        upsampleFilter.SetReferenceImage(shrunkFixed);
                        upsampleFilter.SetInterpolator(InterpolatorEnum.sitkLinear);
                        upsampleFilter.SetOutputPixelType(currentDispField.GetPixelID());
                        initField = upsampleFilter.Execute(currentDispField);
                        currentDispField.Dispose();
                    }

                    var demons = new DiffeomorphicDemonsRegistrationFilter();
                    demons.SetNumberOfIterations(iters);
                    demons.SetStandardDeviations(stdDev);
                    demons.SetMaximumUpdateStepLength(maxStepLength);
                    demons.SetMaximumRMSError(1e-4);   // stop early when RMS drops below this
                    demons.SmoothDisplacementFieldOn();
                    demons.SmoothUpdateFieldOn();

                    // Attach a per-iteration callback so the user sees forward motion.
                    // Note: GetRMSChange() always returns 0.0 from within the command
                    // callback in SimpleITK's .NET binding (the filter's internal metric
                    // state is not exposed during execution). Report iteration count only
                    // and let the post-Execute summary carry the final RMS.
                    uint iterCount = 0;
                    var iterCmd = new ActionCommand(() =>
                    {
                        iterCount++;
                        if (iterCount % 5 == 0)
                            Report(progress, $"  Level {lvl + 1}/{numLevels} — iteration {iterCount}/{iters}...");
                    });
                    demons.AddCommand(EventEnum.sitkIterationEvent, iterCmd);

                    SitkImage newField = initField != null
                        ? demons.Execute(shrunkFixed, shrunkMoving, initField)
                        : demons.Execute(shrunkFixed, shrunkMoving);

                    // GetRMSChange() IS valid after Execute() returns.
                    double finalRms = demons.GetRMSChange();
                    initField?.Dispose();
                    if (sf > 1) { shrunkFixed.Dispose(); shrunkMoving.Dispose(); }
                    string rmsText = finalRms > 0.0 ? $", final RMS: {finalRms:F6}" : string.Empty;
                    string convergenceNote = iterCount < (uint)iters ? " (converged early)" : string.Empty;
                    Report(progress, $"  Level {lvl + 1} complete — {iterCount}/{iters} iterations{rmsText}{convergenceNote}.");
                    currentDispField = newField;
                }

                // ---- Upsample final field to full fixed-CT resolution -----------
                SitkImage fullResField;
                if (currentDispField != null)
                {
                    var upsampleFinal = new ResampleImageFilter();
                    upsampleFinal.SetReferenceImage(fixedCT);
                    upsampleFinal.SetInterpolator(InterpolatorEnum.sitkLinear);
                    upsampleFinal.SetOutputPixelType(currentDispField.GetPixelID());
                    fullResField = upsampleFinal.Execute(currentDispField);
                    currentDispField.Dispose();
                }
                else
                {
                    // Degenerate path — return identity displacement field.
                    var t2d = new TransformToDisplacementFieldFilter();
                    t2d.SetReferenceImage(fixedCT);
                    var identity = new AffineTransform(3);
                    fullResField = t2d.Execute(identity);
                }

                // ---- Compute rigid-only displacement field (used for blending below) ----
                // We need this BEFORE the flatten step consumes rigidAffine.
                // When rigid pre-alignment is in use, voxels outside the body should be
                // transformed by the rigid component only, not left at zero displacement.
                // Zero displacement means "sample movingCT at the same physical position as
                // the fixed voxel", which — because the patient bodies are offset by the
                // rigid registration — lands on patient anatomy at the wrong location.
                // This manifests as (a) a ghost of the source image floating at its
                // pre-rigid position outside the body contour, and (b) an apparent
                // "tissue pulled over the bolus" where the bolus is outside the body.
                // Both artefacts are caused by the zero-displacement approximation, not
                // by a real demons registration error.
                SitkImage rigidOnlyField = null;
                if (rigidAffine != null)
                {
                    var rigidT2df = new TransformToDisplacementFieldFilter();
                    rigidT2df.SetReferenceImage(fixedCT);
                    rigidOnlyField = rigidT2df.Execute(rigidAffine);
                }

                // ---- Flatten rigid + demons into one displacement field -----------
                // The demons field was estimated with preAlignedMoving (which is already
                // in fixed-image space after the rigid resample).  Therefore the demons
                // field D maps fixed-space → pre-aligned-moving-space (which IS fixed-space).
                // To reach the original moving CT space we must then apply the rigid:
                //   finalPoint = rigidAffine( x + D(x) ) = rigidAffine( dispTransform(x) )
                // SimpleITK's CompositeTransform applies the LAST-added transform FIRST (LIFO),
                // so to evaluate rigidAffine(dispTransform(x)) we add the rigid first (outer,
                // applied last) and the demons field second (inner, applied first).
                // (The earlier "insertion order" comment here was wrong; the inverted order
                // produced a folded composite — see DIR_troubleshooting.md.)
                if (rigidAffine != null)
                {
                    Report(progress, "Composing rigid + deformable fields...");
                    var tempDisp = new DisplacementFieldTransform(fullResField);
                    var tempComposite = new CompositeTransform(3);
                    // demons residual is inner (applied first); rigid is outer (applied last).
                    tempComposite.AddTransform(rigidAffine);
                    tempComposite.AddTransform(tempDisp);

                    var flattenFilter = new TransformToDisplacementFieldFilter();
                    flattenFilter.SetReferenceImage(fixedCT);
                    SitkImage flatField = flattenFilter.Execute(tempComposite);
                    fullResField.Dispose();
                    fullResField = flatField;
                    // rigidAffine is now baked in; don't use CompositeTransform below.
                    rigidAffine = null;
                }

                // ---- Blend: rigid-only outside body, full field inside body ------
                // Simply zeroing the displacement field outside the body is incorrect when
                // a rigid pre-alignment is present: zero displacement means "sample movingCT
                // at the same world coordinates as the fixed voxel", which misses the rigid
                // offset and produces ghosting and false tissue pull near asymmetric anatomy
                // (e.g. a bolus present only on one image).
                // The correct behaviour outside the body is to use the rigid-only displacement
                // so that the resampler lands on the correct anatomical position in the
                // original moving CT even where demons was not constrained to run.
                // Inside the body: use the full flat field (rigid + demons).
                // When no rigid transform was used, rigidOnlyField is null and the behaviour
                // reduces to the simpler zero-outside-body case.
                if (fixedMask != null || preAlignedMovingMask != null)
                {
                    // Build the union of both body masks on the fixed-CT grid.
                    SitkImage combinedMask = null;
                    if (fixedMask != null && preAlignedMovingMask != null)
                    {
                        var rsmpMov = new ResampleImageFilter();
                        rsmpMov.SetReferenceImage(fixedCT);
                        rsmpMov.SetInterpolator(InterpolatorEnum.sitkNearestNeighbor);
                        rsmpMov.SetDefaultPixelValue(0);
                        using (var movOnFixed = rsmpMov.Execute(preAlignedMovingMask))
                        {
                            combinedMask = SimpleITK.Or(
                                SimpleITK.Cast(fixedMask,  PixelIDValueEnum.sitkUInt8),
                                SimpleITK.Cast(movOnFixed, PixelIDValueEnum.sitkUInt8));
                        }
                    }
                    else if (fixedMask != null)
                    {
                        combinedMask = SimpleITK.Cast(fixedMask, PixelIDValueEnum.sitkUInt8);
                    }
                    else
                    {
                        var rsmpMov = new ResampleImageFilter();
                        rsmpMov.SetReferenceImage(fixedCT);
                        rsmpMov.SetInterpolator(InterpolatorEnum.sitkNearestNeighbor);
                        rsmpMov.SetDefaultPixelValue(0);
                        using (var movOnFixed = rsmpMov.Execute(preAlignedMovingMask))
                            combinedMask = SimpleITK.Cast(movOnFixed, PixelIDValueEnum.sitkUInt8);
                    }

                    using (combinedMask)
                    {
                        if (rigidOnlyField != null)
                        {
                            // Compute: result = rigidOnly + Mask(fullField - rigidOnly, body)
                            // Inside body: rigidOnly + (fullField - rigidOnly) = fullField
                            // Outside body: rigidOnly + 0 = rigidOnly  (correct, no ghost)
                            using (var diff = SimpleITK.Subtract(fullResField, rigidOnlyField))
                            using (var maskedDiff = SimpleITK.Mask(diff, combinedMask))
                            {
                                SitkImage blended = SimpleITK.Add(rigidOnlyField, maskedDiff);
                                fullResField.Dispose();
                                fullResField = blended;
                            }
                        }
                        else
                        {
                            // No rigid: outside body should be identity (zero displacement).
                            SitkImage maskedField = SimpleITK.Mask(fullResField, combinedMask);
                            fullResField.Dispose();
                            fullResField = maskedField;
                        }
                    }
                }

                rigidOnlyField?.Dispose();

                var dispTransform = new DisplacementFieldTransform(fullResField);
                fullResField.Dispose();

                if (rigidAffine != null)
                {
                    // No mask was set, so the flatten-and-blend block above was skipped.
                    // rigidAffine(dispTransform(x)): SimpleITK applies the LAST-added first, so
                    // add rigid first (outer) and the demons field second (inner).
                    var composite = new CompositeTransform(3);
                    composite.AddTransform(rigidAffine);
                    composite.AddTransform(dispTransform);
                    Helpers.SeriLog.LogInfo("Diffeomorphic demons registration complete (rigid + deformable).");
                    LogTransformDiagnostics("Demons + rigid (final)", composite, fixedCT, fixedMask, progress);
                    return composite;
                }

                // Either no rigid pre-alignment, or it was already baked into the
                // displacement field by the flatten step above.
                Helpers.SeriLog.LogInfo("Diffeomorphic demons registration complete.");
                LogTransformDiagnostics("Demons (final)", dispTransform, fixedCT, fixedMask, progress);
                return dispTransform;
            }
            finally
            {
                normFixed?.Dispose();
                normMoving?.Dispose();
                histMatchedMoving?.Dispose();
                if (disposedPreAligned && !ReferenceEquals(preAlignedMoving, movingCT))
                    preAlignedMoving?.Dispose();
                if (disposePreAlignedMovingMask && !ReferenceEquals(preAlignedMovingMask, movingMask))
                    preAlignedMovingMask?.Dispose();
            }
        }

        // -----------------------------------------------------------------------
        // B-spline registration with TRUE metric masks (ImageRegistrationMethod)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Registers <paramref name="movingCT"/> to <paramref name="fixedCT"/> using a
        /// multi-resolution B-spline transform optimised by <c>ImageRegistrationMethod</c>
        /// with normalised cross-correlation (unimodal CT-to-CT).  Unlike diffeomorphic demons, this method supports
        /// TRUE metric masks: when fixed/moving body masks are supplied they are passed to
        /// <c>SetMetricFixedMask</c>/<c>SetMetricMovingMask</c> so voxels OUTSIDE the body
        /// (e.g. a bolus present only on one image) are excluded from the similarity metric
        /// and its gradient entirely — no artificial intensity edge is created, so out-of-body
        /// anatomy cannot drive the deformation.
        ///
        /// An optional rigid pre-alignment (<paramref name="rigidMatrixRowMajor"/>) is applied
        /// to the moving CT and moving mask first; the returned transform composes the rigid
        /// alignment with the estimated B-spline so it can be used directly to resample the dose.
        /// </summary>
        private SitkTransform RunBSplineMaskedRegistration(
            SitkImage fixedCT, SitkImage movingCT,
            IProgress<string> progress,
            SitkImage fixedMask = null, SitkImage movingMask = null,
            double[] rigidMatrixRowMajor = null)
        {
            var rp = _model.Config?.RegistrationParameters ?? new DoseConverterConfigRegistrationParameters();

            LogMaskCoverage("fixed (target) body mask", fixedMask);
            LogMaskCoverage("moving (source) body mask", movingMask);
            if (fixedMask == null && movingMask == null)
                Helpers.SeriLog.LogInfo(
                    "B-spline DIR: no masks supplied - registration uses the whole image. " +
                    "For bolus exclusion, select source and target body masks.");

            uint[] gridNodes      = ParseUIntList(rp.BSplineGridNodes, new uint[] { 12, 12, 8 });
            uint[] shrinkFactors  = ParseUIntList(rp.ShrinkFactorsPerLevel, new uint[] { 4, 2, 1 });
            double[] smoothSigmas = ParseDoubleList(rp.SmoothingSigmasPerLevel, new double[] { 2.0, 1.0, 0.0 });
            uint[] iterPerLevel   = ParseUIntList(rp.MaxIterationsPerLevel, new uint[] { 50, 30, 20 });
            double samplingPct    = rp.MetricSamplingPercentage > 0 ? rp.MetricSamplingPercentage : 1.0;
            // With metric masks the in-body sample count is already small; sample densely so the
            // high-DOF B-spline gradient is not starved.  REGULAR sampling (set below) keeps this
            // deterministic, which L-BFGS-B's line search requires.
            if (fixedMask != null || movingMask != null)
                samplingPct = Math.Max(samplingPct, 0.5);
            double gradTol        = rp.GradientConvergenceTolerance > 0 ? rp.GradientConvergenceTolerance : 1e-5;
            int    maxCorrections = (int)ParseUInt(rp.MaxCorrections, 5);
            int    maxFuncEval    = (int)ParseUInt(rp.MaxFunctionEvaluations, 1000);
            // CostFunctionConvergenceFactor: 0 means disabled (rely on iteration count + gradient tolerance).
            // With stochastic sampling a nonzero factor can cause premature early exit on noise-level metric changes.
            double costConvFactor = rp.CostFunctionConvergenceFactor;

            // ---- Optional rigid pre-alignment (same convention as the demons path) ----
            AffineTransform rigidAffine = null;
            SitkImage preAlignedMoving = movingCT;
            bool disposedPreAligned = false;
            SitkImage preAlignedMovingMask = movingMask;
            bool disposePreAlignedMovingMask = false;

            try
            {
                if (rigidMatrixRowMajor != null && rigidMatrixRowMajor.Length == 16)
                {
                    Report(progress, "Pre-aligning moving CT with rigid registration...");
                    double[] inv = InvertRigidMatrix4x4(rigidMatrixRowMajor);
                    rigidAffine = new AffineTransform(3);
                    rigidAffine.SetMatrix(new VectorDouble(new double[]
                    {
                        inv[0], inv[1], inv[2],
                        inv[4], inv[5], inv[6],
                        inv[8], inv[9], inv[10]
                    }));
                    rigidAffine.SetTranslation(new VectorDouble(new double[] { inv[3], inv[7], inv[11] }));

                    preAlignedMoving = SimpleITK.Resample(
                        movingCT, fixedCT, rigidAffine,
                        InterpolatorEnum.sitkLinear, 0.0, movingCT.GetPixelID());
                    disposedPreAligned = true;

                    if (movingMask != null)
                    {
                        preAlignedMovingMask = SimpleITK.Resample(
                            movingMask, fixedCT, rigidAffine,
                            InterpolatorEnum.sitkNearestNeighbor, 0.0, movingMask.GetPixelID());
                        disposePreAlignedMovingMask = true;
                    }
                }

                // ---- Initialise the B-spline grid over the BODY region with physical spacing ----
                // A node COUNT spread over the whole (mostly-air) CT FOV places almost all control
                // points outside the patient, leaving anatomy undersampled — the dominant reason a
                // coarse-looking grid under-deforms.  Instead derive the mesh from a target control-
                // point SPACING (mm) over the body bounding box, so density is anatomically meaningful
                // and independent of FOV / patient size.  The body region is the supplied fixed mask
                // when present, else auto-detected from the CT so the grid is well placed even with
                // no mask selected.  Outside its grid domain a B-spline is identity, so confining the
                // grid to the body also avoids deforming out-of-body anatomy (bolus / immobilisation).
                Report(progress, "Initialising B-spline transform...");

                double cpSpacingMm  = rp.BSplineControlPointSpacing > 0 ? rp.BSplineControlPointSpacing : 20.0;
                double gridMarginMm = rp.MaskMarginMm > 0 ? rp.MaskMarginMm : 20.0;

                // Resolve a body-region grid reference image (cropped to body bbox + margin).
                SitkImage gridRef = null;
                SitkImage autoBodyMask = null;
                try
                {
                    SitkImage bodyForBounds = fixedMask;
                    if (bodyForBounds == null)
                    {
                        autoBodyMask = BuildAutoBodyMask(fixedCT);
                        bodyForBounds = autoBodyMask;
                    }
                    if (bodyForBounds != null)
                    {
                        var (cropImg, cropMask) = CropImageToMaskBounds(fixedCT, bodyForBounds, gridMarginMm);
                        cropMask?.Dispose();
                        gridRef = cropImg;
                    }
                }
                catch (Exception exBody)
                {
                    Helpers.SeriLog.LogError(
                        "B-spline body-region detection failed; falling back to full-FOV node-count grid.", exBody);
                }

                uint[] resolvedMesh;
                string gridDomainDesc;
                BSplineTransform bspline;
                if (gridRef != null)
                {
                    // Mesh size = body extent / target spacing, clamped per axis.
                    var gSize    = gridRef.GetSize();
                    var gSpacing = gridRef.GetSpacing();
                    resolvedMesh = new uint[3];
                    for (int a = 0; a < 3; a++)
                    {
                        double extentMm = gSize[a] * gSpacing[a];
                        long cells = (long)Math.Round(extentMm / cpSpacingMm);
                        if (cells < MinBSplineMeshCells) cells = MinBSplineMeshCells;
                        if (cells > MaxBSplineMeshCells) cells = MaxBSplineMeshCells;
                        resolvedMesh[a] = (uint)cells;
                    }
                    bspline = SimpleITK.BSplineTransformInitializer(gridRef, new VectorUInt32(resolvedMesh), 3);
                    gridDomainDesc =
                        $"body-region {gSize[0] * gSpacing[0]:F0}x{gSize[1] * gSpacing[1]:F0}x{gSize[2] * gSpacing[2]:F0} mm " +
                        $"@ {cpSpacingMm:F0} mm spacing ({(fixedMask != null ? "mask" : "auto-detected")})";
                    gridRef.Dispose();
                }
                else
                {
                    // Fallback: legacy fixed node count over the full fixed-image FOV.
                    resolvedMesh = new uint[]
                    {
                        gridNodes.Length > 0 ? gridNodes[0] : 5u,
                        gridNodes.Length > 1 ? gridNodes[1] : 5u,
                        gridNodes.Length > 2 ? gridNodes[2] : 5u
                    };
                    bspline = SimpleITK.BSplineTransformInitializer(fixedCT, new VectorUInt32(resolvedMesh), 3);
                    gridDomainDesc = "full-FOV node-count fallback (body detection unavailable)";
                }
                autoBodyMask?.Dispose();

                int numLevels = shrinkFactors.Length;

                // NOTE: a SINGLE fixed B-spline grid is used across all pyramid levels.
                // Per-level grid refinement (SetInitialTransformAsBSpline with scaleFactors) is
                // incompatible with the L-BFGS-B optimizer: when the grid refines at a new level
                // the parameter count changes (e.g. 1536 -> 6591) but the optimizer's scales array
                // is not resized, producing
                //   "Size of scales (N) must equal number of local parameters (M)".
                // A single grid keeps the parameter count constant and, being coarser, also has
                // fewer under-constrained control points at the mask boundary (more stable).
                // To capture finer deformation, DECREASE BSplineControlPointSpacing in the config
                // (or pick a site preset with a smaller spacing).
                // (Per-level refinement would require switching the optimizer to LBFGS2.)

                // ---- Diagnostics: log every resolved parameter so test runs are actionable ----
                Helpers.SeriLog.LogInfo(
                    "B-spline DIR parameters: " +
                    $"mesh={string.Join("x", resolvedMesh)} cells [{gridDomainDesc}], " +
                    $"shrink=[{string.Join(",", shrinkFactors)}], " +
                    $"smoothSigmas=[{string.Join(",", smoothSigmas)}], " +
                    $"itersPerLevel=[{string.Join(",", iterPerLevel)}], " +
                    $"sampling={samplingPct:F3}, gradTol={gradTol:E2}, " +
                    $"maxCorrections={maxCorrections}, maxFuncEval={maxFuncEval}, " +
                    $"costConvFactor={costConvFactor:E2}, rigidStart={rigidAffine != null}, " +
                    $"fixedMask={fixedMask != null}, movingMask={preAlignedMovingMask != null}");

                // ---- Configure the registration method ----
                var reg = new ImageRegistrationMethod();
                // Correlation (normalised cross-correlation) is the right metric for unimodal
                // CT-to-CT: far less noisy than Mattes mutual information (which targets multi-
                // modal data), giving L-BFGS-B a smoother, better-conditioned gradient — important
                // for a high-DOF B-spline driven through small masked regions.
                reg.SetMetricAsCorrelation();
                // REGULAR sampling gives L-BFGS-B the SAME voxel set on every function evaluation,
                // so its line search sees a deterministic metric (RANDOM would break it).
                reg.SetMetricSamplingStrategy(ImageRegistrationMethod.MetricSamplingStrategyType.REGULAR);
                reg.SetMetricSamplingPercentage(samplingPct);
                reg.SetInterpolator(InterpolatorEnum.sitkLinear);

                // TRUE metric masks: voxels == 0 are excluded from the metric entirely.
                if (fixedMask != null)
                    reg.SetMetricFixedMask(SimpleITK.Cast(fixedMask, PixelIDValueEnum.sitkUInt8));
                if (preAlignedMovingMask != null)
                    reg.SetMetricMovingMask(SimpleITK.Cast(preAlignedMovingMask, PixelIDValueEnum.sitkUInt8));

                // L-BFGS-B handles the large B-spline parameter set well via its approximate
                // Hessian.  numberOfIterations is applied PER pyramid level by SimpleITK, so a
                // single configuration suffices — we use the largest per-level cap.
                // (The previous code reconfigured the optimizer from inside the multi-resolution
                // callback during Execute(); that is not a supported pattern — at best ignored,
                // at worst it perturbs optimizer state — and has been removed.)
                uint iterCap = 0;
                foreach (var it in iterPerLevel) iterCap = Math.Max(iterCap, it);
                if (iterCap == 0) iterCap = ParseUInt(rp.MaxIterations, 50);
                // L-BFGS-B uses ONE iteration cap for every pyramid level (it cannot vary the cap
                // per level — that would need LBFGS2).  The config's per-level list collapses to
                // its maximum, applied uniformly.  This is the denominator the progress display shows.
                Helpers.SeriLog.LogInfo(
                    $"B-spline optimizer: L-BFGS-B, uniform per-level iteration cap = {iterCap} " +
                    $"(from MaxIterationsPerLevel=[{string.Join(",", iterPerLevel)}], using max).");
                reg.SetOptimizerAsLBFGSB(
                    gradientConvergenceTolerance: gradTol,
                    numberOfIterations: iterCap,
                    maximumNumberOfCorrections: (uint)maxCorrections,
                    maximumNumberOfFunctionEvaluations: (uint)maxFuncEval,
                    costFunctionConvergenceFactor: costConvFactor);

                // Multi-resolution pyramid.
                reg.SetShrinkFactorsPerLevel(new VectorUInt32(shrinkFactors));
                reg.SetSmoothingSigmasPerLevel(new VectorDouble(smoothSigmas));
                reg.SmoothingSigmasAreSpecifiedInPhysicalUnitsOn();

                // Single fixed B-spline grid across all pyramid levels (per-level refinement is
                // not used with L-BFGS-B — that would need LBFGS2; see DIR_troubleshooting.md).
                reg.SetInitialTransform(bspline, inPlace: true);

                // Calibrate optimizer step scaling to physical shift.  Without this, L-BFGS-B
                // takes poorly-scaled, timid steps on the B-spline parameters and converges to a
                // shallow minimum — under-deforming, so it cannot capture large changes such as
                // weight loss / body-contour change.  Must be called AFTER SetInitialTransform
                // (it needs the transform's parameter layout).  Scales are valid across all
                // levels because the grid (parameter count) is fixed.
                reg.SetOptimizerScalesFromPhysicalShift();

                // Progress-only level tracking.  IMPORTANT: do NOT call any reg.Set*() here — the
                // registration is mid-Execute and reconfiguring it is unsafe.
                // sitkMultiResolutionIterationEvent fires once at the START of each level (before
                // that level's first iteration).  Start at -1 and increment on each fire so the
                // first level reads 1/N.  (Starting at 0 and pre-incrementing showed it as 2/N.)
                int levelIdx = -1;
                var lvlCmd = new ActionCommand(() =>
                {
                    levelIdx = Math.Min(levelIdx + 1, numLevels - 1);
                    Report(progress, $"  B-spline entering level {levelIdx + 1}/{numLevels} (iteration cap {iterCap})...");
                });
                reg.AddCommand(EventEnum.sitkMultiResolutionIterationEvent, lvlCmd);

                var iterCmd = new ActionCommand(() =>
                {
                    int displayLevel = Math.Max(levelIdx + 1, 1);
                    // GetOptimizerIteration() resets to 0 at the start of each pyramid level, so it
                    // counts 0..iterCap WITHIN the current level (not cumulatively across levels).
                    Report(progress,
                        $"  B-spline level {displayLevel}/{numLevels} — metric {reg.GetMetricValue():F4} (iter {reg.GetOptimizerIteration()}/{iterCap})...");
                });
                reg.AddCommand(EventEnum.sitkIterationEvent, iterCmd);

                Report(progress, "Running B-spline registration (this may take several minutes)...");
                // Correlation does not require histogram normalisation; use raw CTs.
                SitkTransform optimizedTransform = reg.Execute(fixedCT, preAlignedMoving);

                Helpers.SeriLog.LogInfo(
                    $"B-spline registration complete. Final metric: {reg.GetMetricValue():F6}. " +
                    $"Stop condition: {reg.GetOptimizerStopConditionDescription()}");

                // ---- Diagnostics: quantify the deformation so 'garbage' is measurable ----
                // Logs max in-body displacement and Jacobian range.  A very large displacement or
                // a Jacobian <= 0 (folding) is the signature of the masked-boundary instability.
                LogTransformDiagnostics("B-spline (deformable only)", optimizedTransform, fixedCT, fixedMask, progress);

                // ---- Compose rigid (if any) with the B-spline ----
                // The B-spline was estimated on preAlignedMoving (already in fixed space after the
                // rigid resample), so it maps fixed -> pre-aligned-moving.  To reach ORIGINAL
                // moving space we need finalPoint = rigidAffine( bspline(x) ): apply the B-spline
                // FIRST, then the rigid.
                //
                // SimpleITK's CompositeTransform applies the LAST-added transform FIRST (LIFO).
                // So to evaluate rigidAffine(bspline(x)) we must add the rigid first (outer,
                // applied last) and the B-spline second (inner, applied first).
                //
                // (Verified empirically: the previous order — bspline added first, rigid second —
                // evaluated bspline(rigidAffine(x)), which pushes points outside the B-spline grid
                // domain and produced a folded field: Jacobian min -1.3 with 62k folded voxels,
                // despite a clean deformable-only field. See DIR_troubleshooting.md.)
                SitkTransform deformableThenRigid;
                if (rigidAffine != null)
                {
                    var composite = new CompositeTransform(3);
                    composite.AddTransform(rigidAffine);        // outer (applied last)
                    composite.AddTransform(optimizedTransform); // inner (applied first)
                    deformableThenRigid = composite;
                }
                else
                {
                    deformableThenRigid = optimizedTransform;
                }

                // Report the raw transform first (whole-grid numbers reveal the out-of-body
                // extrapolation magnitude — the part the in-body number hides).
                LogTransformDiagnostics("B-spline + rigid (pre-blend)", deformableThenRigid, fixedCT, fixedMask, progress);

                // ---- Constrain the deformation to inside the target body --------------------
                // Metric masks stop out-of-body anatomy (e.g. a bolus excluded from the target
                // body contour) from DRIVING the optimisation, but the B-spline field is still
                // defined everywhere and EXTRAPOLATES outside the body, where nothing constrains
                // it.  Resampling with that extrapolated field is what drags the bolus region.
                // So — exactly as the demons path does — keep the full transform inside the target
                // body and fall back to rigid-only (identity when there is no rigid) outside it.
                // This guarantees nothing outside the target body contour is deformed.
                if (fixedMask != null)
                {
                    Report(progress, "Constraining B-spline deformation to inside the target body...");
                    var t2df = new TransformToDisplacementFieldFilter();
                    t2df.SetReferenceImage(fixedCT);
                    SitkImage fullField = t2df.Execute(deformableThenRigid);

                    SitkImage rigidOnlyField = null;
                    if (rigidAffine != null)
                    {
                        var rt2df = new TransformToDisplacementFieldFilter();
                        rt2df.SetReferenceImage(fixedCT);
                        rigidOnlyField = rt2df.Execute(rigidAffine);
                    }

                    using (var bodyU8 = SimpleITK.Cast(fixedMask, PixelIDValueEnum.sitkUInt8))
                    {
                        if (rigidOnlyField != null)
                        {
                            // inside body: rigidOnly + (full - rigidOnly) = full
                            // outside body: rigidOnly + 0 = rigidOnly  (no deformable warp)
                            using (var diff = SimpleITK.Subtract(fullField, rigidOnlyField))
                            using (var maskedDiff = SimpleITK.Mask(diff, bodyU8))
                            {
                                SitkImage blended = SimpleITK.Add(rigidOnlyField, maskedDiff);
                                fullField.Dispose();
                                fullField = blended;
                            }
                        }
                        else
                        {
                            // no rigid: outside body -> identity (zero displacement)
                            SitkImage masked = SimpleITK.Mask(fullField, bodyU8);
                            fullField.Dispose();
                            fullField = masked;
                        }
                    }
                    rigidOnlyField?.Dispose();

                    var blendedTransform = new DisplacementFieldTransform(fullField);
                    fullField.Dispose();
                    LogTransformDiagnostics("B-spline + rigid (final, body-constrained)", blendedTransform, fixedCT, fixedMask, progress);
                    return blendedTransform;
                }

                LogTransformDiagnostics("B-spline + rigid (final)", deformableThenRigid, fixedCT, fixedMask, progress);
                return deformableThenRigid;
            }
            finally
            {
                if (disposedPreAligned && !ReferenceEquals(preAlignedMoving, movingCT))
                    preAlignedMoving?.Dispose();
                if (disposePreAlignedMovingMask && !ReferenceEquals(preAlignedMovingMask, movingMask))
                    preAlignedMovingMask?.Dispose();
            }
        }

        /// <summary>
        /// Logs quantitative diagnostics for a registration transform over the reference grid:
        /// maximum / mean displacement magnitude (restricted to the body mask when supplied) and
        /// the Jacobian-determinant range plus a folded-voxel count (Jacobian &lt;= 0 over the
        /// whole grid).  Large displacements or folding are the measurable signature of a B-spline
        /// that has diverged — e.g. the masked-boundary instability.  All failures are non-fatal.
        /// </summary>
        private static void LogTransformDiagnostics(
            string label, SitkTransform transform, SitkImage referenceGrid,
            SitkImage bodyMask, IProgress<string> progress)
        {
            try
            {
                var t2df = new TransformToDisplacementFieldFilter();
                t2df.SetReferenceImage(referenceGrid);
                using (var field = t2df.Execute(transform))
                using (var magnitude = SimpleITK.VectorMagnitude(field))
                {
                    // Always report WHOLE-GRID displacement — this exposes the out-of-body
                    // extrapolation that the in-body number hides (the bolus-dragging culprit).
                    var wholeStats = new StatisticsImageFilter();
                    wholeStats.Execute(magnitude);
                    string dispText = $"maxDisp(whole-grid)={wholeStats.GetMaximum():F1} mm, " +
                                      $"meanDisp(whole-grid)={wholeStats.GetMean():F1} mm";

                    // When a body mask is available, also report the in-body max so anatomy and
                    // out-of-body extrapolation can be compared directly.
                    if (bodyMask != null)
                    {
                        using (var masked = SimpleITK.Mask(magnitude, SimpleITK.Cast(bodyMask, PixelIDValueEnum.sitkUInt8)))
                        {
                            var inStats = new StatisticsImageFilter();
                            inStats.Execute(masked);
                            dispText += $"; maxDisp(in-body)={inStats.GetMaximum():F1} mm";
                        }
                    }

                    string jacText;
                    try
                    {
                        using (var jac = SimpleITK.DisplacementFieldJacobianDeterminant(field))
                        {
                            var jStats = new StatisticsImageFilter();
                            jStats.Execute(jac);
                            // Count folded voxels (Jacobian <= 0) — a direct divergence indicator.
                            using (var folded = SimpleITK.BinaryThreshold(jac, -1.0e9, 0.0, 1, 0))
                            {
                                var fStats = new StatisticsImageFilter();
                                fStats.Execute(folded);
                                long foldVoxels = (long)Math.Round(fStats.GetSum());
                                jacText = $"jacDet[min={jStats.GetMinimum():F3}, max={jStats.GetMaximum():F3}], " +
                                          $"foldedVoxels(jac<=0, whole-grid)={foldVoxels}";
                            }
                        }
                    }
                    catch (Exception exJac)
                    {
                        jacText = $"jacDet=<unavailable: {exJac.Message}>";
                    }

                    string msg = $"DIR diagnostics [{label}]: {dispText}, {jacText}";
                    Helpers.SeriLog.LogInfo(msg);
                    progress?.Report(msg);
                }
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError($"DIR diagnostics [{label}] failed", ex);
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

        /// <summary>
        /// Auto-detects the patient body region on a CT when no body mask is supplied, returning a
        /// binary (UInt8) mask on the input grid.  Thresholds out air (HU &lt; ~-350) and keeps the
        /// largest connected component, which drops disconnected couch rails / immobilisation noise.
        /// Used only to BOUND the B-spline grid (so control points concentrate on anatomy rather than
        /// the mostly-air FOV); precise contouring is not required, so a simple threshold suffices.
        /// </summary>
        private static SitkImage BuildAutoBodyMask(SitkImage fixedCT)
        {
            // -350 HU sits well below soft tissue/fat and above air, cleanly separating the patient
            // (and contacting immobilisation) from the surrounding air.
            using (var bin = SimpleITK.BinaryThreshold(fixedCT, -350.0, 1.0e6, 1, 0))
            using (var cc = SimpleITK.ConnectedComponent(bin))
            using (var relabel = SimpleITK.RelabelComponent(cc, 0, true)) // label 1 = largest component
                return SimpleITK.BinaryThreshold(relabel, 1.0, 1.0, 1, 0);
        }

        // -----------------------------------------------------------------------
        // Config parsing helpers
        // -----------------------------------------------------------------------

        /// <summary>
        /// Inverts a row-major 4×4 rigid homogeneous matrix using R^-1 = R^T.
        /// (For a pure rigid body transform the 3×3 rotation block is orthonormal.)
        /// </summary>
        private static double[] InvertRigidMatrix4x4(double[] m)
        {
            double[] inv = new double[16];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    inv[r * 4 + c] = m[c * 4 + r];
            for (int r = 0; r < 3; r++)
            {
                double sum = 0;
                for (int c = 0; c < 3; c++)
                    sum += inv[r * 4 + c] * m[c * 4 + 3];
                inv[r * 4 + 3] = -sum;
            }
            inv[12] = 0; inv[13] = 0; inv[14] = 0; inv[15] = 1;
            return inv;
        }

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

            // Determine the linear HU calibration (HU = raw * slope + intercept) with two calls
            // rather than calling VoxelToDisplayValue on every voxel (50M+ calls would hang the
            // ESAPI dispatcher thread for minutes on a typical CT volume).
            double huIntercept = img.VoxelToDisplayValue(0);
            double huSlope     = img.VoxelToDisplayValue(1) - huIntercept;
            if (huSlope == 0) huSlope = 1.0; // guard against degenerate calibration

            for (int z = 0; z < nz; z++)
            {
                img.GetVoxels(z, sliceBuf);
                int baseIdx = z * nx * ny;
                for (int x = 0; x < nx; x++)
                    for (int y = 0; y < ny; y++)
                        buffer[baseIdx + y * nx + x] = (float)(sliceBuf[x, y] * huSlope + huIntercept);
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
        /// Logs the in-body voxel count and fractional coverage of a mask so the user
        /// can confirm from the log file that masking is actually active and non-empty
        /// before committing to an expensive visual review of the deformed result.
        /// </summary>
        private static void LogMaskCoverage(string label, SitkImage mask)
        {
            if (mask == null)
            {
                Helpers.SeriLog.LogInfo($"DIR mask: {label} = NONE (not supplied).");
                return;
            }
            try
            {
                var stats = new StatisticsImageFilter();
                using (var u8 = SimpleITK.Cast(mask, PixelIDValueEnum.sitkUInt8))
                {
                    stats.Execute(u8);
                    double sum = stats.GetSum();          // number of voxels == 1
                    var size = u8.GetSize();
                    double total = 1.0;
                    for (int i = 0; i < size.Count; i++) total *= size[i];
                    double pct = total > 0 ? 100.0 * sum / total : 0.0;
                    if (sum <= 0)
                        Helpers.SeriLog.LogInfo(
                            $"DIR mask: {label} is EMPTY (0 voxels). Masking will have NO effect - " +
                            "check the structure id and that it has contours on this image.");
                    else
                        Helpers.SeriLog.LogInfo(
                            $"DIR mask: {label} = {sum:F0} voxels in-body ({pct:F1}% of volume).");
                }
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError($"Failed to compute mask coverage for {label}", ex);
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

        // -----------------------------------------------------------------------
        // Structure warping + contour tracing (DICOM export support)
        // -----------------------------------------------------------------------

        /// <summary>
        /// Warps a binary mask defined on the moving CT grid onto the fixed (target) CT grid
        /// using the displacement field produced by the DIR.  For each fixed voxel the field
        /// gives the corresponding moving-space physical point (moving = fixed + displacement);
        /// the moving mask is then sampled with nearest-neighbour interpolation.  Returns a
        /// flat byte[] in [x + y*nx + z*nx*ny] layout on the fixed grid (1 inside, 0 outside).
        /// </summary>
        internal static byte[] WarpMaskToFixedGrid(
            byte[] movingMask, uint[] mSize, double[] mSpacing, double[] mOrigin, double[] mDir,
            float[] disp,       uint[] fSize, double[] fSpacing, double[] fOrigin, double[] fDir)
        {
            int fnx = (int)fSize[0], fny = (int)fSize[1], fnz = (int)fSize[2];
            int mnx = (int)mSize[0], mny = (int)mSize[1], mnz = (int)mSize[2];

            byte[] outMask = new byte[(long)fnx * fny * fnz];

            for (int z = 0; z < fnz; z++)
            {
                for (int y = 0; y < fny; y++)
                {
                    for (int x = 0; x < fnx; x++)
                    {
                        long vi = (long)z * fnx * fny + (long)y * fnx + x;

                        // Fixed-grid voxel → physical point (patient mm).
                        double wx = fOrigin[0] + fDir[0] * fSpacing[0] * x + fDir[1] * fSpacing[1] * y + fDir[2] * fSpacing[2] * z;
                        double wy = fOrigin[1] + fDir[3] * fSpacing[0] * x + fDir[4] * fSpacing[1] * y + fDir[5] * fSpacing[2] * z;
                        double wz = fOrigin[2] + fDir[6] * fSpacing[0] * x + fDir[7] * fSpacing[1] * y + fDir[8] * fSpacing[2] * z;

                        // Apply displacement (interleaved dx,dy,dz mm) → moving physical point.
                        long di = vi * 3;
                        double mxw = wx + disp[di];
                        double myw = wy + disp[di + 1];
                        double mzw = wz + disp[di + 2];

                        // Moving physical point → fractional moving voxel index.
                        double rx = mxw - mOrigin[0], ry = myw - mOrigin[1], rz = mzw - mOrigin[2];
                        double mix = (mDir[0] * rx + mDir[3] * ry + mDir[6] * rz) / mSpacing[0];
                        double miy = (mDir[1] * rx + mDir[4] * ry + mDir[7] * rz) / mSpacing[1];
                        double miz = (mDir[2] * rx + mDir[5] * ry + mDir[8] * rz) / mSpacing[2];

                        // Trilinear interpolation on the binary moving mask.
                        // Threshold at 0.5: a fixed voxel is inside if the majority of the
                        // 8 surrounding source voxels are inside.  This prevents slice dropout
                        // caused by sub-voxel displacement offsets along Z.
                        int x0 = (int)Math.Floor(mix), y0 = (int)Math.Floor(miy), z0 = (int)Math.Floor(miz);
                        int x1 = x0 + 1, y1 = y0 + 1, z1 = z0 + 1;
                        double tx = mix - x0, ty = miy - y0, tz = miz - z0;

                        double c000 = (x0>=0&&x0<mnx&&y0>=0&&y0<mny&&z0>=0&&z0<mnz) ? movingMask[(long)z0*mnx*mny+y0*mnx+x0] : 0.0;
                        double c100 = (x1>=0&&x1<mnx&&y0>=0&&y0<mny&&z0>=0&&z0<mnz) ? movingMask[(long)z0*mnx*mny+y0*mnx+x1] : 0.0;
                        double c010 = (x0>=0&&x0<mnx&&y1>=0&&y1<mny&&z0>=0&&z0<mnz) ? movingMask[(long)z0*mnx*mny+y1*mnx+x0] : 0.0;
                        double c110 = (x1>=0&&x1<mnx&&y1>=0&&y1<mny&&z0>=0&&z0<mnz) ? movingMask[(long)z0*mnx*mny+y1*mnx+x1] : 0.0;
                        double c001 = (x0>=0&&x0<mnx&&y0>=0&&y0<mny&&z1>=0&&z1<mnz) ? movingMask[(long)z1*mnx*mny+y0*mnx+x0] : 0.0;
                        double c101 = (x1>=0&&x1<mnx&&y0>=0&&y0<mny&&z1>=0&&z1<mnz) ? movingMask[(long)z1*mnx*mny+y0*mnx+x1] : 0.0;
                        double c011 = (x0>=0&&x0<mnx&&y1>=0&&y1<mny&&z1>=0&&z1<mnz) ? movingMask[(long)z1*mnx*mny+y1*mnx+x0] : 0.0;
                        double c111 = (x1>=0&&x1<mnx&&y1>=0&&y1<mny&&z1>=0&&z1<mnz) ? movingMask[(long)z1*mnx*mny+y1*mnx+x1] : 0.0;

                        double val = c000*(1-tx)*(1-ty)*(1-tz) + c100*tx*(1-ty)*(1-tz)
                                   + c010*(1-tx)*ty*(1-tz)     + c110*tx*ty*(1-tz)
                                   + c001*(1-tx)*(1-ty)*tz     + c101*tx*(1-ty)*tz
                                   + c011*(1-tx)*ty*tz         + c111*tx*ty*tz;

                        if (val >= 0.5)
                            outMask[vi] = 1;
                    }
                }
            }

            // Per-slice 2D morphological close (3×3 dilation then erosion).
            // Fills single-voxel gaps that persist after trilinear warping and
            // removes isolated specks, further reducing dropout and speckle noise.
            byte[] dilated = new byte[fnx * fny];
            for (int z = 0; z < fnz; z++)
            {
                long sliceBase = (long)z * fnx * fny;

                // Dilation: any background pixel adjacent (8-connected) to foreground becomes foreground.
                for (int y = 0; y < fny; y++)
                {
                    for (int x = 0; x < fnx; x++)
                    {
                        bool found = false;
                        for (int dy = -1; dy <= 1 && !found; dy++)
                            for (int dx = -1; dx <= 1 && !found; dx++)
                            {
                                int nx2 = x + dx, ny2 = y + dy;
                                if (nx2 >= 0 && nx2 < fnx && ny2 >= 0 && ny2 < fny
                                    && outMask[sliceBase + ny2 * fnx + nx2] != 0)
                                    found = true;
                            }
                        dilated[y * fnx + x] = found ? (byte)1 : (byte)0;
                    }
                }

                // Erosion of the dilated slice written back to outMask.
                for (int y = 0; y < fny; y++)
                {
                    for (int x = 0; x < fnx; x++)
                    {
                        bool allFg = true;
                        for (int dy = -1; dy <= 1 && allFg; dy++)
                            for (int dx = -1; dx <= 1 && allFg; dx++)
                            {
                                int nx2 = x + dx, ny2 = y + dy;
                                if (nx2 < 0 || nx2 >= fnx || ny2 < 0 || ny2 >= fny
                                    || dilated[ny2 * fnx + nx2] == 0)
                                    allFg = false;
                            }
                        outMask[sliceBase + y * fnx + x] = allFg ? (byte)1 : (byte)0;
                    }
                }
            }

            return outMask;
        }

        // 8-neighbourhood offsets, clockwise starting at North (used by Moore tracing).
        private static readonly int[] _mooreDx = { 0, 1, 1, 1, 0, -1, -1, -1 };
        private static readonly int[] _mooreDy = { -1, -1, 0, 1, 1, 1, 0, -1 };

        /// <summary>
        /// Extracts ordered boundary polygons (in fractional pixel coordinates) for every
        /// connected foreground component in a single slice of a binary mask.  Uses 4-connected
        /// flood fill to isolate components and Moore-neighbour tracing for each outer boundary.
        /// Returns a list of polygons, each a list of [ix, iy] integer pixel coordinates.
        /// </summary>
        internal static List<List<int[]>> TraceSliceContours(byte[] mask, int nx, int ny, int z)
        {
            var results = new List<List<int[]>>();
            long baseIdx = (long)z * nx * ny;

            bool[,] fg = new bool[nx, ny];
            for (int y = 0; y < ny; y++)
                for (int x = 0; x < nx; x++)
                    fg[x, y] = mask[baseIdx + y * nx + x] != 0;

            bool[,] visited = new bool[nx, ny];
            for (int y = 0; y < ny; y++)
            {
                for (int x = 0; x < nx; x++)
                {
                    if (!fg[x, y] || visited[x, y]) continue;

                    // Mark the whole component so it is only traced once. The first pixel
                    // encountered (this one) is the topmost-leftmost of the component, so its
                    // west neighbour is background — an ideal Moore-tracing start.
                    FloodFillComponent(fg, visited, nx, ny, x, y);

                    var poly = MooreNeighbourTrace(fg, nx, ny, x, y);
                    if (poly.Count >= 3)
                        results.Add(poly);
                }
            }
            return results;
        }

        /// <summary>4-connected flood fill that marks every pixel of a component as visited.</summary>
        private static void FloodFillComponent(bool[,] fg, bool[,] visited, int nx, int ny, int sx, int sy)
        {
            var stack = new Stack<int[]>();
            stack.Push(new[] { sx, sy });
            visited[sx, sy] = true;
            while (stack.Count > 0)
            {
                var p = stack.Pop();
                int px = p[0], py = p[1];
                // 4-neighbours
                if (px + 1 < nx && fg[px + 1, py] && !visited[px + 1, py]) { visited[px + 1, py] = true; stack.Push(new[] { px + 1, py }); }
                if (px - 1 >= 0 && fg[px - 1, py] && !visited[px - 1, py]) { visited[px - 1, py] = true; stack.Push(new[] { px - 1, py }); }
                if (py + 1 < ny && fg[px, py + 1] && !visited[px, py + 1]) { visited[px, py + 1] = true; stack.Push(new[] { px, py + 1 }); }
                if (py - 1 >= 0 && fg[px, py - 1] && !visited[px, py - 1]) { visited[px, py - 1] = true; stack.Push(new[] { px, py - 1 }); }
            }
        }

        /// <summary>
        /// Moore-neighbour boundary tracing (clockwise) of the component containing the start
        /// pixel, which must have a background/out-of-bounds west neighbour.  Returns ordered
        /// boundary pixel coordinates as [ix, iy].
        /// </summary>
        private static List<int[]> MooreNeighbourTrace(bool[,] fg, int nx, int ny, int sx, int sy)
        {
            var trace = new List<int[]> { new[] { sx, sy } };

            int bx = sx, by = sy;       // current boundary pixel
            int px = sx - 1, py = sy;   // backtrack pixel (west, background)

            int guard = 0, maxGuard = 8 * nx * ny + 100;
            while (guard++ < maxGuard)
            {
                int startIdx = NeighbourIndex(bx, by, px, py);
                int foundIdx = -1, prevBgX = px, prevBgY = py;

                for (int k = 1; k <= 8; k++)
                {
                    int idx = (startIdx + k) % 8;
                    int cx = bx + _mooreDx[idx];
                    int cy = by + _mooreDy[idx];
                    if (cx >= 0 && cx < nx && cy >= 0 && cy < ny && fg[cx, cy])
                    {
                        foundIdx = idx;
                        int pidx = (startIdx + k - 1) % 8;
                        prevBgX = bx + _mooreDx[pidx];
                        prevBgY = by + _mooreDy[pidx];
                        break;
                    }
                }

                if (foundIdx < 0) break; // isolated single pixel

                int ncx = bx + _mooreDx[foundIdx];
                int ncy = by + _mooreDy[foundIdx];

                if (ncx == sx && ncy == sy) break; // returned to start → closed

                trace.Add(new[] { ncx, ncy });
                bx = ncx; by = ncy; px = prevBgX; py = prevBgY;
            }
            return trace;
        }

        /// <summary>Returns the Moore-neighbourhood index (0–7) of pixel (px,py) relative to (bx,by), or 6 (west) if not adjacent.</summary>
        private static int NeighbourIndex(int bx, int by, int px, int py)
        {
            for (int i = 0; i < 8; i++)
                if (bx + _mooreDx[i] == px && by + _mooreDy[i] == py)
                    return i;
            return 6; // default to west
        }

        /// <summary>
        /// Applies a cyclic Gaussian smooth to a boundary polygon then subsamples it to
        /// reduce the point count to a clinically appropriate level.  Returns fractional
        /// pixel-centre coordinates (including the +0.5 centre offset) ready for
        /// <see cref="PixelToWorld"/>.
        /// </summary>
        /// <param name="pixelPoly">Pixel-integer boundary points from Moore tracing.</param>
        /// <param name="stride">Keep every <paramref name="stride"/>-th smoothed point (default 2).</param>
        /// <param name="sigma">Gaussian sigma in pixels (default 1.0).</param>
        private static List<double[]> SmoothAndSubsamplePolygon(
            List<int[]> pixelPoly, int stride = 2, double sigma = 1.0)
        {
            int n = pixelPoly.Count;
            if (n < 3)
                return pixelPoly.Select(p => new double[] { p[0] + 0.5, p[1] + 0.5 }).ToList();

            // Build normalised Gaussian kernel (half-width = ceil(3σ)).
            int halfW = (int)Math.Ceiling(3.0 * sigma);
            int kLen  = 2 * halfW + 1;
            double[] kernel = new double[kLen];
            double ksum = 0;
            for (int i = 0; i < kLen; i++)
            {
                double d = i - halfW;
                kernel[i] = Math.Exp(-d * d / (2.0 * sigma * sigma));
                ksum += kernel[i];
            }
            for (int i = 0; i < kLen; i++) kernel[i] /= ksum;

            // Cyclic Gaussian smooth over the closed polygon.
            var smoothed = new double[n][];
            for (int i = 0; i < n; i++)
            {
                double sx = 0, sy = 0;
                for (int k = 0; k < kLen; k++)
                {
                    int j = ((i - halfW + k) % n + n) % n;
                    sx += (pixelPoly[j][0] + 0.5) * kernel[k];
                    sy += (pixelPoly[j][1] + 0.5) * kernel[k];
                }
                smoothed[i] = new double[] { sx, sy };
            }

            // Subsample: keep every stride-th point.
            var result = new List<double[]>(n / stride + 1);
            for (int i = 0; i < n; i += stride)
                result.Add(smoothed[i]);
            return result;
        }

        /// <summary>
        /// Converts a fixed-grid pixel coordinate (ix, iy, slice z) into a patient-space point
        /// (mm), using the fixed CT geometry.  Returns [x, y, z].
        /// </summary>
        internal static double[] PixelToWorld(
            double ix, double iy, int z,
            double[] origin, double[] spacing, double[] direction)
        {
            double wx = origin[0] + direction[0] * spacing[0] * ix + direction[1] * spacing[1] * iy + direction[2] * spacing[2] * z;
            double wy = origin[1] + direction[3] * spacing[0] * ix + direction[4] * spacing[1] * iy + direction[5] * spacing[2] * z;
            double wz = origin[2] + direction[6] * spacing[0] * ix + direction[7] * spacing[1] * iy + direction[8] * spacing[2] * z;
            return new[] { wx, wy, wz };
        }

        internal static double[] BuildDirectionCosines(VVector xDir, VVector yDir, VVector zDir)
        {
            // [Xx Xy Xz  Yx Yy Yz  Zx Zy Zz]
            return new double[]
            {
                xDir.x, xDir.y, xDir.z,
                yDir.x, yDir.y, yDir.z,
                zDir.x, zDir.y, zDir.z
            };
        }

        /// <summary>
        /// Builds a DICOM PN-formatted patient name ("Family^Given^Middle") from an ESAPI
        /// patient.  Falls back to the patient Id when no name components are available.
        /// </summary>
        private static string BuildDicomPatientName(Patient p)
        {
            try
            {
                string family = p.LastName ?? string.Empty;
                string given  = p.FirstName ?? string.Empty;
                string middle = p.MiddleName ?? string.Empty;
                string pn = $"{family}^{given}^{middle}".TrimEnd('^');
                return string.IsNullOrWhiteSpace(pn) ? (p.Id ?? string.Empty) : pn;
            }
            catch
            {
                return p?.Id ?? string.Empty;
            }
        }

        /// <summary>Maps an ESAPI patient sex string to a DICOM sex code (M / F / O).</summary>
        private static string MapPatientSex(string sex)
        {
            if (string.IsNullOrWhiteSpace(sex)) return string.Empty;
            switch (sex.Trim().ToUpperInvariant())
            {
                case "MALE":   case "M": return "M";
                case "FEMALE": case "F": return "F";
                default:                 return "O";
            }
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
        /// Extracts a SimpleITK vector image (e.g. a displacement field) into an interleaved
        /// float array: [v0_x, v0_y, v0_z, v1_x, v1_y, v1_z, …] in voxel-order.
        /// Assumes the vector image has 3 components per voxel (3-D displacement field).
        /// </summary>
        private static float[] ExtractVectorImageBuffer(SitkImage vectorImg)
        {
            uint nx = vectorImg.GetWidth();
            uint ny = vectorImg.GetHeight();
            uint nz = vectorImg.GetDepth();
            uint nComp = vectorImg.GetNumberOfComponentsPerPixel();
            long totalFloats = (long)(nx * ny * nz * nComp);

            // SimpleITK stores vector images as a flat, component-interleaved buffer.
            // GetBufferAsFloat() works directly for sitkVectorFloat32.
            SitkImage floatImg = vectorImg;
            bool disposeCast = false;
            if (vectorImg.GetPixelID() != PixelIDValueEnum.sitkVectorFloat32)
            {
                floatImg = SimpleITK.Cast(vectorImg, PixelIDValueEnum.sitkVectorFloat32);
                disposeCast = true;
            }

            float[] buffer = new float[totalFloats];
            try
            {
                IntPtr ptr = floatImg.GetBufferAsFloat();
                Marshal.Copy(ptr, buffer, 0, (int)totalFloats);
            }
            finally
            {
                if (disposeCast) floatImg.Dispose();
            }
            return buffer;
        }


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
