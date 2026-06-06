using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FellowOakDicom;
using FellowOakDicom.Imaging;
using FellowOakDicom.IO.Buffer;

namespace DoseConverter
{
    /// <summary>
    /// Writes a deformed CT image series and an RT Structure Set to DICOM using fo-dicom.
    /// New Series / SOP / Structure-Set UIDs are generated, while the patient, study and
    /// frame-of-reference relational tags are preserved so the files re-import cleanly into
    /// the originating TPS.
    /// </summary>
    internal static class DicomExportService
    {
        // CT pixel encoding: store HU as unsigned 16-bit with a fixed rescale intercept so
        // the warp background (-1000 HU) maps to 0 and negative HU values stay non-negative.
        private const int RescaleIntercept = -1000;

        /// <summary>
        /// Writes the deformed CT slices and RTSTRUCT to <paramref name="outputDirectory"/>.
        /// Must be called from a background (non-dispatcher) thread.
        /// </summary>
        public static void WriteDeformedSeries(
            DeformableRegistrationService.DirExportData export,
            List<DeformableRegistrationService.DeformedStructure> structures,
            string outputDirectory,
            IProgress<string> progress = null)
        {
            if (export == null) throw new ArgumentNullException(nameof(export));
            Directory.CreateDirectory(outputDirectory);

            int nx = (int)export.Size[0];
            int ny = (int)export.Size[1];
            int nz = (int)export.Size[2];

            // Preserved / new UIDs.
            string studyUid  = string.IsNullOrEmpty(export.StudyInstanceUid)
                ? DicomUIDGenerator.GenerateDerivedFromUUID().UID
                : export.StudyInstanceUid;
            string forUid    = string.IsNullOrEmpty(export.FrameOfReferenceUid)
                ? DicomUIDGenerator.GenerateDerivedFromUUID().UID
                : export.FrameOfReferenceUid;
            string ctSeriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

            string nowDate = DateTime.Now.ToString("yyyyMMdd");
            string nowTime = DateTime.Now.ToString("HHmmss");

            // Direction cosines (row-major 3x3) and orientation for IOP.
            double[] dir = export.Direction;
            double[] iop =
            {
                dir[0], dir[1], dir[2],   // row (X) direction
                dir[3], dir[4], dir[5]    // column (Y) direction
            };
            // Slice (Z) direction used to compute each slice position.
            double zx = dir[6], zy = dir[7], zz = dir[8];

            double sx = export.Spacing[0]; // column spacing (x)
            double sy = export.Spacing[1]; // row spacing (y)
            double sz = export.Spacing[2]; // slice spacing (z)

            string ctDir = Path.Combine(outputDirectory, "CT");
            Directory.CreateDirectory(ctDir);

            // SOP Instance UID per slice, so the RTSTRUCT can reference the correct images.
            string[] sliceSopUids = new string[nz];

            for (int z = 0; z < nz; z++)
            {
                if ((z % 10) == 0)
                    progress?.Report($"Writing CT slice {z + 1}/{nz}...");

                string sopUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
                sliceSopUids[z] = sopUid;

                // Slice origin in patient coordinates: image origin + zDir * sz * z.
                double posX = export.Origin[0] + zx * sz * z;
                double posY = export.Origin[1] + zy * sz * z;
                double posZ = export.Origin[2] + zz * sz * z;

                var ds = new DicomDataset();
                AddPatientStudyTags(ds, export, studyUid, nowDate, nowTime);

                ds.AddOrUpdate(DicomTag.Modality, "CT");
                // (0008,0008) ImageType — Type 1 mandatory for CT Image Storage.
                // DERIVED because the voxels come from a deformation, SECONDARY because
                // this is not direct detector data.
                ds.AddOrUpdate(DicomTag.ImageType, new[] { "DERIVED", "SECONDARY" });
                ds.AddOrUpdate(DicomTag.SeriesInstanceUID, ctSeriesUid);
                ds.AddOrUpdate(DicomTag.SeriesNumber, "1");
                ds.AddOrUpdate(DicomTag.SeriesDescription, "Deformed CT (DIR)");
                ds.AddOrUpdate(DicomTag.SeriesDate, nowDate);
                ds.AddOrUpdate(DicomTag.SeriesTime, nowTime);
                ds.AddOrUpdate(DicomTag.AcquisitionDate, nowDate);
                ds.AddOrUpdate(DicomTag.AcquisitionTime, nowTime);
                ds.AddOrUpdate(DicomTag.ContentDate, nowDate);
                ds.AddOrUpdate(DicomTag.ContentTime, nowTime);
                ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, forUid);
                ds.AddOrUpdate(DicomTag.PositionReferenceIndicator, string.Empty);

                ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.CTImageStorage);
                ds.AddOrUpdate(DicomTag.SOPInstanceUID, sopUid);
                ds.AddOrUpdate(DicomTag.InstanceNumber, (z + 1).ToString(CultureInfo.InvariantCulture));

                // Geometry.
                ds.AddOrUpdate(DicomTag.ImageOrientationPatient, iop.Select(FormatDs).ToArray());
                ds.AddOrUpdate(DicomTag.ImagePositionPatient,
                    new[] { FormatDs(posX), FormatDs(posY), FormatDs(posZ) });
                // PixelSpacing: row spacing then column spacing (DICOM: dy, dx).
                ds.AddOrUpdate(DicomTag.PixelSpacing, new[] { FormatDs(sy), FormatDs(sx) });
                ds.AddOrUpdate(DicomTag.SliceThickness, FormatDs(sz));
                ds.AddOrUpdate(DicomTag.SliceLocation, FormatDs(posZ));
                // PatientPosition — Type 2 for CT; Eclipse typically stores HFS.
                ds.AddOrUpdate(DicomTag.PatientPosition, "HFS");
                // KVP, XRayTubeCurrent, ExposureTime — Type 2 for CT Image Storage;
                // must be present (may be empty for secondary/derived objects).
                ds.AddOrUpdate(DicomTag.KVP, string.Empty);
                ds.AddOrUpdate(DicomTag.XRayTubeCurrent, string.Empty);
                ds.AddOrUpdate(DicomTag.ExposureTime, string.Empty);

                // Pixel module.
                ds.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);
                ds.AddOrUpdate(DicomTag.PhotometricInterpretation, "MONOCHROME2");
                ds.AddOrUpdate(DicomTag.Rows, (ushort)ny);
                ds.AddOrUpdate(DicomTag.Columns, (ushort)nx);
                ds.AddOrUpdate(DicomTag.BitsAllocated, (ushort)16);
                ds.AddOrUpdate(DicomTag.BitsStored, (ushort)16);
                ds.AddOrUpdate(DicomTag.HighBit, (ushort)15);
                ds.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0); // unsigned
                ds.AddOrUpdate(DicomTag.RescaleIntercept, FormatDs(RescaleIntercept));
                ds.AddOrUpdate(DicomTag.RescaleSlope, "1");
                ds.AddOrUpdate(DicomTag.RescaleType, "HU");
                // WindowCenter / WindowWidth — Type 3 but makes CT immediately viewable.
                ds.AddOrUpdate(DicomTag.WindowCenter, "40");
                ds.AddOrUpdate(DicomTag.WindowWidth, "400");

                // Build the 16-bit pixel buffer for this slice.
                byte[] pixelBytes = BuildSlicePixels(export.DeformedCtHu, nx, ny, z);

                var pixelData = DicomPixelData.Create(ds, true);
                pixelData.AddFrame(new MemoryByteBuffer(pixelBytes));

                var file = new DicomFile(ds);
                file.Save(Path.Combine(ctDir, $"CT_{z + 1:D4}.dcm"));
            }

            // ---- RT Structure Set ----
            string structSetUid = null;
            if (structures != null && structures.Count > 0)
            {
                progress?.Report("Writing RT structure set...");
                structSetUid = WriteStructureSet(export, structures, sliceSopUids, studyUid, forUid, ctSeriesUid,
                    nowDate, nowTime, outputDirectory);
            }

            // ---- RT Plan (minimal) + RT Dose ----
            // ESAPI cannot create a dose on a structure set that has no existing dose, so the deformed
            // dose is exported here as an RTDOSE (plus a minimal RTPLAN it references) on the SAME grid
            // as the deformed CT. Importing CT + RTSTRUCT + RTPLAN + RTDOSE yields the deformed dose on
            // the target image even when the target has no plan.
            if (export.DeformedDoseGy != null)
            {
                WriteDose(export, structSetUid, studyUid, forUid,
                    iop, zx, zy, zz, sx, sy, sz, nx, ny, nz, nowDate, nowTime, outputDirectory, progress);
            }
        }

        /// <summary>
        /// Writes a minimal RT Plan and the deformed dose as an RTDOSE (multi-frame, 16-bit unsigned)
        /// on the deformed-CT grid.  The RTDOSE references the RTPLAN (DoseSummationType PLAN); the
        /// RTPLAN references the structure set when one was written.  NOTE: the minimal RTPLAN
        /// (no beams) is the part most likely to need site-specific tweaks for a given Eclipse version
        /// to accept the import — see DIR_troubleshooting.md.
        /// </summary>
        private static void WriteDose(
            DeformableRegistrationService.DirExportData export,
            string structSetUid,
            string studyUid, string forUid,
            double[] iop, double zx, double zy, double zz,
            double sx, double sy, double sz,
            int nx, int ny, int nz,
            string nowDate, string nowTime,
            string outputDirectory,
            IProgress<string> progress)
        {
            // ---- Minimal RT Plan so the dose imports as a plan ----
            string planSeriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            string planSopUid     = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

            var pl = new DicomDataset();
            AddPatientStudyTags(pl, export, studyUid, nowDate, nowTime);
            pl.AddOrUpdate(DicomTag.Modality, "RTPLAN");
            pl.AddOrUpdate(DicomTag.SeriesInstanceUID, planSeriesUid);
            pl.AddOrUpdate(DicomTag.SeriesNumber, "3");
            pl.AddOrUpdate(DicomTag.SeriesDescription, "Deformed dose plan (DIR)");
            pl.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.RTPlanStorage);
            pl.AddOrUpdate(DicomTag.SOPInstanceUID, planSopUid);
            pl.AddOrUpdate(DicomTag.InstanceNumber, "1");
            pl.AddOrUpdate(DicomTag.FrameOfReferenceUID, forUid);
            pl.AddOrUpdate(DicomTag.RTPlanLabel, "DIR_Deformed");
            pl.AddOrUpdate(DicomTag.RTPlanName, "Deformed dose (DIR)");
            pl.AddOrUpdate(DicomTag.RTPlanDate, nowDate);
            pl.AddOrUpdate(DicomTag.RTPlanTime, nowTime);
            pl.AddOrUpdate(DicomTag.RTPlanGeometry, string.IsNullOrEmpty(structSetUid) ? "TREATMENT_DEVICE" : "PATIENT");
            if (!string.IsNullOrEmpty(structSetUid))
            {
                var rss = new DicomDataset();
                rss.AddOrUpdate(DicomTag.ReferencedSOPClassUID, DicomUID.RTStructureSetStorage);
                rss.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, structSetUid);
                pl.Add(new DicomSequence(DicomTag.ReferencedStructureSetSequence, rss));
            }
            // Patient setup (referenced by the beam).
            var setup = new DicomDataset();
            setup.AddOrUpdate(DicomTag.PatientSetupNumber, "1");
            setup.AddOrUpdate(DicomTag.PatientPosition, "HFS");
            pl.Add(new DicomSequence(DicomTag.PatientSetupSequence, setup));

            // A single STATIC dummy beam. The deformed dose is supplied externally (not computed from
            // this beam), but Eclipse requires a BeamSequence for the plan to import — a plan with zero
            // beams warns "missing Beam Sequence". Isocentre = geometric centre of the dose grid.
            double[] o = export.Origin;
            double cx = o[0] + 0.5 * (nx - 1) * sx * iop[0] + 0.5 * (ny - 1) * sy * iop[3] + 0.5 * (nz - 1) * sz * zx;
            double cy = o[1] + 0.5 * (nx - 1) * sx * iop[1] + 0.5 * (ny - 1) * sy * iop[4] + 0.5 * (nz - 1) * sz * zy;
            double cz = o[2] + 0.5 * (nx - 1) * sx * iop[2] + 0.5 * (ny - 1) * sy * iop[5] + 0.5 * (nz - 1) * sz * zz;

            // Two control points (start + end) with an open 10×10 cm aperture.
            var cp0 = new DicomDataset();
            cp0.AddOrUpdate(DicomTag.ControlPointIndex, "0");
            cp0.AddOrUpdate(DicomTag.NominalBeamEnergy, FormatDs(6));
            cp0.AddOrUpdate(DicomTag.GantryAngle, FormatDs(0));
            cp0.AddOrUpdate(DicomTag.GantryRotationDirection, "NONE");
            cp0.AddOrUpdate(DicomTag.BeamLimitingDeviceAngle, FormatDs(0));
            cp0.AddOrUpdate(DicomTag.BeamLimitingDeviceRotationDirection, "NONE");
            cp0.AddOrUpdate(DicomTag.PatientSupportAngle, FormatDs(0));
            cp0.AddOrUpdate(DicomTag.PatientSupportRotationDirection, "NONE");
            cp0.AddOrUpdate(DicomTag.TableTopEccentricAngle, FormatDs(0));
            cp0.AddOrUpdate(DicomTag.TableTopEccentricRotationDirection, "NONE");
            cp0.AddOrUpdate(DicomTag.IsocenterPosition, new[] { FormatDs(cx), FormatDs(cy), FormatDs(cz) });
            cp0.AddOrUpdate(DicomTag.SourceToSurfaceDistance, FormatDs(900));
            cp0.AddOrUpdate(DicomTag.CumulativeMetersetWeight, FormatDs(0));
            var jawX = new DicomDataset();
            jawX.AddOrUpdate(DicomTag.RTBeamLimitingDeviceType, "ASYMX");
            jawX.AddOrUpdate(DicomTag.LeafJawPositions, new[] { FormatDs(-50), FormatDs(50) });
            var jawY = new DicomDataset();
            jawY.AddOrUpdate(DicomTag.RTBeamLimitingDeviceType, "ASYMY");
            jawY.AddOrUpdate(DicomTag.LeafJawPositions, new[] { FormatDs(-50), FormatDs(50) });
            cp0.Add(new DicomSequence(DicomTag.BeamLimitingDevicePositionSequence, jawX, jawY));

            var cp1 = new DicomDataset();
            cp1.AddOrUpdate(DicomTag.ControlPointIndex, "1");
            cp1.AddOrUpdate(DicomTag.CumulativeMetersetWeight, FormatDs(1));

            var beam = new DicomDataset();
            beam.AddOrUpdate(DicomTag.BeamNumber, "1");
            beam.AddOrUpdate(DicomTag.BeamName, "DIR");
            beam.AddOrUpdate(DicomTag.BeamType, "STATIC");
            beam.AddOrUpdate(DicomTag.RadiationType, "PHOTON");
            // Default the dummy-beam machine to the source plan's machine so the plan imports against
            // a real machine; fall back to "DIR" only if the source machine was unavailable.
            beam.AddOrUpdate(DicomTag.TreatmentMachineName,
                string.IsNullOrWhiteSpace(export.SourceMachineName) ? "DIR" : export.SourceMachineName);
            beam.AddOrUpdate(DicomTag.PrimaryDosimeterUnit, "MU");
            beam.AddOrUpdate(DicomTag.SourceAxisDistance, FormatDs(1000));
            beam.AddOrUpdate(DicomTag.TreatmentDeliveryType, "TREATMENT");
            beam.AddOrUpdate(DicomTag.ReferencedPatientSetupNumber, "1");
            beam.AddOrUpdate(DicomTag.NumberOfWedges, "0");
            beam.AddOrUpdate(DicomTag.NumberOfCompensators, "0");
            beam.AddOrUpdate(DicomTag.NumberOfBoli, "0");
            beam.AddOrUpdate(DicomTag.NumberOfBlocks, "0");
            beam.AddOrUpdate(DicomTag.FinalCumulativeMetersetWeight, FormatDs(1));
            beam.AddOrUpdate(DicomTag.NumberOfControlPoints, "2");
            var bldX = new DicomDataset();
            bldX.AddOrUpdate(DicomTag.RTBeamLimitingDeviceType, "ASYMX");
            bldX.AddOrUpdate(DicomTag.NumberOfLeafJawPairs, "1");
            var bldY = new DicomDataset();
            bldY.AddOrUpdate(DicomTag.RTBeamLimitingDeviceType, "ASYMY");
            bldY.AddOrUpdate(DicomTag.NumberOfLeafJawPairs, "1");
            beam.Add(new DicomSequence(DicomTag.BeamLimitingDeviceSequence, bldX, bldY));
            beam.Add(new DicomSequence(DicomTag.ControlPointSequence, cp0, cp1));
            pl.Add(new DicomSequence(DicomTag.BeamSequence, beam));

            // One fraction group referencing the dummy beam.
            var refBeam = new DicomDataset();
            refBeam.AddOrUpdate(DicomTag.ReferencedBeamNumber, "1");
            refBeam.AddOrUpdate(DicomTag.BeamMeterset, FormatDs(1));
            var fg = new DicomDataset();
            fg.AddOrUpdate(DicomTag.FractionGroupNumber, "1");
            fg.AddOrUpdate(DicomTag.NumberOfFractionsPlanned, "1");
            fg.AddOrUpdate(DicomTag.NumberOfBeams, "1");
            fg.AddOrUpdate(DicomTag.NumberOfBrachyApplicationSetups, "0");
            fg.Add(new DicomSequence(DicomTag.ReferencedBeamSequence, refBeam));
            pl.Add(new DicomSequence(DicomTag.FractionGroupSequence, fg));
            new DicomFile(pl).Save(Path.Combine(outputDirectory, "RP_Deformed.dcm"));

            // ---- RT Dose (multi-frame, deformed-CT grid, references the plan) ----
            progress?.Report("Writing RT dose...");
            float[] dose = export.DeformedDoseGy;
            double maxGy = 0.0;
            for (int i = 0; i < dose.Length; i++) if (dose[i] > maxGy) maxGy = dose[i];
            // 16-bit unsigned storage; scale so the max maps near 60000 (headroom below 65535).
            double doseGridScaling = maxGy > 0 ? maxGy / 60000.0 : 1.0;

            string doseSeriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            string doseSopUid     = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

            var ds = new DicomDataset();
            AddPatientStudyTags(ds, export, studyUid, nowDate, nowTime);
            ds.AddOrUpdate(DicomTag.Modality, "RTDOSE");
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID, doseSeriesUid);
            ds.AddOrUpdate(DicomTag.SeriesNumber, "4");
            ds.AddOrUpdate(DicomTag.SeriesDescription, "Deformed dose (DIR)");
            ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.RTDoseStorage);
            ds.AddOrUpdate(DicomTag.SOPInstanceUID, doseSopUid);
            ds.AddOrUpdate(DicomTag.InstanceNumber, "1");
            ds.AddOrUpdate(DicomTag.FrameOfReferenceUID, forUid);

            var refPlan = new DicomDataset();
            refPlan.AddOrUpdate(DicomTag.ReferencedSOPClassUID, DicomUID.RTPlanStorage);
            refPlan.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, planSopUid);
            ds.Add(new DicomSequence(DicomTag.ReferencedRTPlanSequence, refPlan));

            // Geometry — same grid as the deformed CT.
            ds.AddOrUpdate(DicomTag.ImageOrientationPatient, iop.Select(FormatDs).ToArray());
            ds.AddOrUpdate(DicomTag.ImagePositionPatient,
                new[] { FormatDs(export.Origin[0]), FormatDs(export.Origin[1]), FormatDs(export.Origin[2]) });
            ds.AddOrUpdate(DicomTag.PixelSpacing, new[] { FormatDs(sy), FormatDs(sx) });
            ds.AddOrUpdate(DicomTag.SliceThickness, FormatDs(sz));

            ds.AddOrUpdate(DicomTag.SamplesPerPixel, (ushort)1);
            ds.AddOrUpdate(DicomTag.PhotometricInterpretation, "MONOCHROME2");
            ds.AddOrUpdate(DicomTag.NumberOfFrames, nz.ToString(CultureInfo.InvariantCulture));
            ds.AddOrUpdate(DicomTag.FrameIncrementPointer, DicomTag.GridFrameOffsetVector);
            ds.AddOrUpdate(DicomTag.Rows, (ushort)ny);
            ds.AddOrUpdate(DicomTag.Columns, (ushort)nx);
            ds.AddOrUpdate(DicomTag.BitsAllocated, (ushort)16);
            ds.AddOrUpdate(DicomTag.BitsStored, (ushort)16);
            ds.AddOrUpdate(DicomTag.HighBit, (ushort)15);
            ds.AddOrUpdate(DicomTag.PixelRepresentation, (ushort)0);
            ds.AddOrUpdate(DicomTag.DoseUnits, "GY");
            ds.AddOrUpdate(DicomTag.DoseType, "PHYSICAL");
            ds.AddOrUpdate(DicomTag.DoseSummationType, "PLAN");
            ds.AddOrUpdate(DicomTag.DoseGridScaling, FormatDs(doseGridScaling));
            // GridFrameOffsetVector: relative offset of each frame along the slice axis (0, sz, 2·sz, …).
            string[] gfov = new string[nz];
            for (int z = 0; z < nz; z++) gfov[z] = FormatDs(sz * z);
            ds.AddOrUpdate(DicomTag.GridFrameOffsetVector, gfov);

            var pixelData = DicomPixelData.Create(ds, true);
            for (int z = 0; z < nz; z++)
            {
                if ((z % 10) == 0) progress?.Report($"Writing dose frame {z + 1}/{nz}...");
                pixelData.AddFrame(new MemoryByteBuffer(BuildDoseFrame(dose, nx, ny, z, doseGridScaling)));
            }

            new DicomFile(ds).Save(Path.Combine(outputDirectory, "RD_Deformed.dcm"));
        }

        /// <summary>Packs one Z slice of dose (Gy) into a little-endian unsigned 16-bit buffer using the grid scaling.</summary>
        private static byte[] BuildDoseFrame(float[] dose, int nx, int ny, int z, double doseGridScaling)
        {
            byte[] bytes = new byte[nx * ny * 2];
            long baseIdx = (long)z * nx * ny;
            for (int i = 0; i < nx * ny; i++)
            {
                double gy = dose[baseIdx + i];
                int stored = (gy > 0 && doseGridScaling > 0) ? (int)Math.Round(gy / doseGridScaling) : 0;
                if (stored < 0) stored = 0;
                if (stored > ushort.MaxValue) stored = ushort.MaxValue;
                ushort v = (ushort)stored;
                bytes[i * 2]     = (byte)(v & 0xFF);
                bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            return bytes;
        }

        /// <summary>Packs a single Z slice of HU values into a little-endian unsigned 16-bit pixel buffer.</summary>
        private static byte[] BuildSlicePixels(float[] hu, int nx, int ny, int z)
        {
            byte[] bytes = new byte[nx * ny * 2];
            long baseIdx = (long)z * nx * ny;
            for (int i = 0; i < nx * ny; i++)
            {
                int stored = (int)Math.Round(hu[baseIdx + i]) - RescaleIntercept;
                if (stored < 0) stored = 0;
                if (stored > ushort.MaxValue) stored = ushort.MaxValue;
                ushort v = (ushort)stored;
                bytes[i * 2]     = (byte)(v & 0xFF);
                bytes[i * 2 + 1] = (byte)((v >> 8) & 0xFF);
            }
            return bytes;
        }

        private static string WriteStructureSet(
            DeformableRegistrationService.DirExportData export,
            List<DeformableRegistrationService.DeformedStructure> structures,
            string[] sliceSopUids,
            string studyUid, string forUid, string ctSeriesUid,
            string nowDate, string nowTime,
            string outputDirectory)
        {
            string structSetUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;
            string structSeriesUid = DicomUIDGenerator.GenerateDerivedFromUUID().UID;

            var ds = new DicomDataset();
            AddPatientStudyTags(ds, export, studyUid, nowDate, nowTime);

            ds.AddOrUpdate(DicomTag.Modality, "RTSTRUCT");
            ds.AddOrUpdate(DicomTag.SeriesInstanceUID, structSeriesUid);
            ds.AddOrUpdate(DicomTag.SeriesNumber, "2");
            ds.AddOrUpdate(DicomTag.SeriesDescription, "Deformed structures (DIR)");
            ds.AddOrUpdate(DicomTag.SOPClassUID, DicomUID.RTStructureSetStorage);
            ds.AddOrUpdate(DicomTag.SOPInstanceUID, structSetUid);
            ds.AddOrUpdate(DicomTag.InstanceNumber, "1");

            ds.AddOrUpdate(DicomTag.StructureSetLabel, "Deformed");
            ds.AddOrUpdate(DicomTag.StructureSetName, "Deformed structures");
            ds.AddOrUpdate(DicomTag.StructureSetDate, nowDate);
            ds.AddOrUpdate(DicomTag.StructureSetTime, nowTime);

            // ---- ReferencedFrameOfReferenceSequence ----
            var contourImageItems = sliceSopUids
                .Where(u => !string.IsNullOrEmpty(u))
                .Select(u =>
                {
                    var ci = new DicomDataset();
                    ci.AddOrUpdate(DicomTag.ReferencedSOPClassUID, DicomUID.CTImageStorage);
                    ci.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, u);
                    return ci;
                })
                .ToArray();

            var rtRefSeries = new DicomDataset();
            rtRefSeries.AddOrUpdate(DicomTag.SeriesInstanceUID, ctSeriesUid);
            rtRefSeries.Add(new DicomSequence(DicomTag.ContourImageSequence, contourImageItems));

            var rtRefStudy = new DicomDataset();
            rtRefStudy.AddOrUpdate(DicomTag.ReferencedSOPClassUID, "1.2.840.10008.3.1.2.3.1"); // Detached Study Management (retired)
            rtRefStudy.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, studyUid);
            rtRefStudy.Add(new DicomSequence(DicomTag.RTReferencedSeriesSequence, rtRefSeries));

            var refForItem = new DicomDataset();
            refForItem.AddOrUpdate(DicomTag.FrameOfReferenceUID, forUid);
            refForItem.Add(new DicomSequence(DicomTag.RTReferencedStudySequence, rtRefStudy));
            ds.Add(new DicomSequence(DicomTag.ReferencedFrameOfReferenceSequence, refForItem));

            // ---- StructureSetROISequence / ROIContourSequence / RTROIObservationsSequence ----
            var roiItems = new List<DicomDataset>();
            var contourItems = new List<DicomDataset>();
            var obsItems = new List<DicomDataset>();

            int roiNumber = 1;
            foreach (var s in structures)
            {
                // StructureSetROISequence item
                var roi = new DicomDataset();
                roi.AddOrUpdate(DicomTag.ROINumber, roiNumber.ToString(CultureInfo.InvariantCulture));
                roi.AddOrUpdate(DicomTag.ReferencedFrameOfReferenceUID, forUid);
                roi.AddOrUpdate(DicomTag.ROIName, s.Id ?? $"ROI{roiNumber}");
                roi.AddOrUpdate(DicomTag.ROIGenerationAlgorithm, "SEMIAUTOMATIC");
                roiItems.Add(roi);

                // ROIContourSequence item
                var contour = new DicomDataset();
                contour.AddOrUpdate(DicomTag.ReferencedROINumber, roiNumber.ToString(CultureInfo.InvariantCulture));
                byte[] col = s.Color ?? new byte[] { 255, 0, 0 };
                contour.AddOrUpdate(DicomTag.ROIDisplayColor, new[]
                {
                    col[0].ToString(CultureInfo.InvariantCulture),
                    col[1].ToString(CultureInfo.InvariantCulture),
                    col[2].ToString(CultureInfo.InvariantCulture)
                });

                var contourSeqItems = new List<DicomDataset>();
                foreach (var (z, polygons) in s.ContoursBySlice)
                {
                    string sopUid = (z >= 0 && z < sliceSopUids.Length) ? sliceSopUids[z] : null;
                    foreach (var poly in polygons)
                    {
                        if (poly == null || poly.Length < 9) continue; // need >= 3 points

                        var citem = new DicomDataset();
                        if (!string.IsNullOrEmpty(sopUid))
                        {
                            var ci = new DicomDataset();
                            ci.AddOrUpdate(DicomTag.ReferencedSOPClassUID, DicomUID.CTImageStorage);
                            ci.AddOrUpdate(DicomTag.ReferencedSOPInstanceUID, sopUid);
                            citem.Add(new DicomSequence(DicomTag.ContourImageSequence, ci));
                        }
                        citem.AddOrUpdate(DicomTag.ContourGeometricType, "CLOSED_PLANAR");
                        citem.AddOrUpdate(DicomTag.NumberOfContourPoints,
                            (poly.Length / 3).ToString(CultureInfo.InvariantCulture));
                        citem.AddOrUpdate(DicomTag.ContourData, poly.Select(FormatDs).ToArray());
                        contourSeqItems.Add(citem);
                    }
                }
                contour.Add(new DicomSequence(DicomTag.ContourSequence, contourSeqItems.ToArray()));
                contourItems.Add(contour);

                // RTROIObservationsSequence item
                var obs = new DicomDataset();
                obs.AddOrUpdate(DicomTag.ObservationNumber, roiNumber.ToString(CultureInfo.InvariantCulture));
                obs.AddOrUpdate(DicomTag.ReferencedROINumber, roiNumber.ToString(CultureInfo.InvariantCulture));
                obs.AddOrUpdate(DicomTag.RTROIInterpretedType, MapInterpretedType(s.DicomType));
                obs.AddOrUpdate(DicomTag.ROIInterpreter, string.Empty);
                obsItems.Add(obs);

                roiNumber++;
            }

            ds.Add(new DicomSequence(DicomTag.StructureSetROISequence, roiItems.ToArray()));
            ds.Add(new DicomSequence(DicomTag.ROIContourSequence, contourItems.ToArray()));
            ds.Add(new DicomSequence(DicomTag.RTROIObservationsSequence, obsItems.ToArray()));

            var file = new DicomFile(ds);
            file.Save(Path.Combine(outputDirectory, "RS_Deformed.dcm"));
            return structSetUid;
        }

        private static void AddPatientStudyTags(
            DicomDataset ds,
            DeformableRegistrationService.DirExportData export,
            string studyUid, string nowDate, string nowTime)
        {
            ds.AddOrUpdate(DicomTag.SpecificCharacterSet, "ISO_IR 100");

            // Patient module (all Type 2 — must be present, may be empty).
            ds.AddOrUpdate(DicomTag.PatientName, export.PatientName ?? string.Empty);
            ds.AddOrUpdate(DicomTag.PatientID, export.PatientId ?? string.Empty);
            ds.AddOrUpdate(DicomTag.PatientBirthDate, export.PatientBirthDate ?? string.Empty);
            ds.AddOrUpdate(DicomTag.PatientSex, export.PatientSex ?? string.Empty);

            // General Study module.
            ds.AddOrUpdate(DicomTag.StudyInstanceUID, studyUid);
            ds.AddOrUpdate(DicomTag.StudyDate, export.StudyDate ?? nowDate);
            ds.AddOrUpdate(DicomTag.StudyTime, export.StudyTime ?? nowTime);
            ds.AddOrUpdate(DicomTag.StudyID, export.StudyId ?? "DIR");
            ds.AddOrUpdate(DicomTag.AccessionNumber, string.Empty);
            ds.AddOrUpdate(DicomTag.ReferringPhysicianName, string.Empty);
            ds.AddOrUpdate(DicomTag.StudyDescription, string.Empty);

            // General Equipment module — Manufacturer is Type 2 mandatory for CT.
            ds.AddOrUpdate(DicomTag.Manufacturer, string.Empty);
        }

        /// <summary>Maps an ESAPI DICOM structure type to a valid RT ROI Interpreted Type.</summary>
        private static string MapInterpretedType(string dicomType)
        {
            if (string.IsNullOrWhiteSpace(dicomType)) return "ORGAN";
            string t = dicomType.Trim().ToUpperInvariant();
            switch (t)
            {
                case "EXTERNAL":
                case "PTV":
                case "CTV":
                case "GTV":
                case "ORGAN":
                case "AVOIDANCE":
                case "CONTROL":
                case "MARKER":
                case "SUPPORT":
                case "FIXATION":
                case "DOSE_REGION":
                case "TREATED_VOLUME":
                case "IRRAD_VOLUME":
                    return t;
                default:
                    return "ORGAN";
            }
        }

        private static string FormatDs(double v)
        {
            // DICOM DS (Decimal String) is limited to 16 chars; use round-trip then trim.
            string s = v.ToString("0.######", CultureInfo.InvariantCulture);
            if (s.Length > 16) s = v.ToString("G10", CultureInfo.InvariantCulture);
            return s;
        }
    }
}
