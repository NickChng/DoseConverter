using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using static DoseConverter.MainWindow;
using System.Windows.Input;
using System.Windows;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPIScript;
using System.Windows.Controls;
using System.Windows.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using Serilog.Core;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;

namespace DoseConverter
{
    public class Model
    {
        private DoseConverterConfig _config;
        private List<StructureViewModel> StructureDefinitions { get; set; } = new List<StructureViewModel>();
        public double DefaultAlphaBeta { get; private set; }

        public int[,,] originalArray { get; private set; }

        private EsapiWorker _ew;

        public async Task<string> GetCurrentPlanName()
        {
            string output = "";
            await _ew.AsyncRunPlanContext((p, pl) =>
            {
                output = pl.Id;
            });
            return output;
        }

        public double scaling;
        public double scaling2;
        public double scaling3;

        public HashSet<Tuple<int, int, int>> existingIndexes = new HashSet<Tuple<int, int, int>>() { }; // reset!

        private Dictionary<string, string> StructureCodeLookup = new Dictionary<string, string>();

        public double doseMax;
        public double doseMin;

        public async Task<(int[,,], ScriptStatus, string, string)> GetConvertedDose(string courseId, string planId, bool isSum, string newPlanName, List<StructureViewModel> mappings, DoseFormat format, double? convParameter = null)
        {
            ExternalPlanSetup newPlan = (ExternalPlanSetup)null;
            int[,,] outputDose = null;
            string returnMessage = "";
            ScriptStatus status = ScriptStatus.Incomplete;
            string exceptionMessage = "";
            bool overrideStructureSet = false;

            await _ew.AsyncRunPlanContext((p, pl) =>
            {
                try
                {
                    if (!pl.StructureSet.CanAddStructure("Control", _config.Defaults.TempEdgeStructureName))
                    {
                        returnMessage = "Error: Edge conversion is not possible because structure set is approved/completed.";
                        overrideStructureSet = true;
                    }
                }
                catch (Exception ex)
                {
                    status = ScriptStatus.Error;
                    returnMessage = "Unexpected error - mouse over for details";
                    exceptionMessage = ex.Message;
                }
                try
                {
                    if (isSum)
                    {
                        var c = p.Courses.FirstOrDefault(x => string.Equals(courseId, x.Id, StringComparison.OrdinalIgnoreCase));
                        var sum = c.PlanSums.FirstOrDefault(x => string.Equals(x.Id, planId, StringComparison.OrdinalIgnoreCase));

                        // can't sum over plans because dose matrices have different sizes, and unknown whether interpolating will result in same result as internal Eclipse sum.
                        if (sum.Dose != null)
                        {
                            try
                            {
                                Helpers.SeriLog.LogInfo(string.Format("Creating new verification plan [{0}]", newPlanName));
                                newPlan = pl.Course.AddExternalPlanSetupAsVerificationPlan(pl.StructureSet, (ExternalPlanSetup)pl);
                                newPlan.Id = newPlanName;
                            }
                            catch (Exception ex)
                            {
                                returnMessage = "Error creating plan. Check that course is active.";
                                Helpers.SeriLog.LogInfo(string.Format("Creating new verification plan [{0}]", newPlanName));
                                exceptionMessage = ex.Message;
                                status = ScriptStatus.Error;
                                return;
                            }
                            try
                            {
                                outputDose = ConvertDose(format, newPlan, (ExternalPlanSetup)pl, sum, mappings, convParameter, overrideStructureSet);
                            }
                            catch (Exception ex)
                            {
                                returnMessage = "Error converting plan sum dose...";
                                exceptionMessage = ex.Message;
                                Helpers.SeriLog.LogError(returnMessage, ex);
                                status = ScriptStatus.Error;
                                return;
                            }
                        }
                        else
                        {
                            returnMessage = "Selected plan sum has no dose.";
                            status = ScriptStatus.Error;
                            return;
                        }

                    }
                    else
                    {
                        var plan = p.Courses.FirstOrDefault(x => string.Equals(courseId, x.Id, StringComparison.OrdinalIgnoreCase)).PlanSetups.FirstOrDefault(x => string.Equals(x.Id, planId, StringComparison.OrdinalIgnoreCase));
                        if (plan.Dose != null)
                        {
                            try
                            {
                                Helpers.SeriLog.LogInfo(string.Format("Creating new verification plan [{0}]", newPlanName));
                                newPlan = pl.Course.AddExternalPlanSetupAsVerificationPlan(pl.StructureSet, (ExternalPlanSetup)plan);
                                newPlan.Id = newPlanName;
                            }
                            catch (Exception ex)
                            {
                                returnMessage = "Error creating plan. Check that course is active.";
                                Helpers.SeriLog.LogError(returnMessage, ex);
                                exceptionMessage = ex.Message;
                                status = ScriptStatus.Error;
                                return;
                            }
                            try
                            {
                                outputDose = ConvertDose(format, newPlan, (ExternalPlanSetup)pl, plan, mappings, convParameter, overrideStructureSet);
                            }
                            catch (Exception ex)
                            {
                                Helpers.SeriLog.LogError(returnMessage, ex);
                                exceptionMessage = ex.Message;
                                if (pl.Course.CanRemovePlanSetup(newPlan))
                                {
                                    returnMessage = "Error converting dose, plan removed.";
                                    Helpers.SeriLog.LogInfo("Removed converted plan...");
                                    pl.Course.RemovePlanSetup(newPlan);
                                }
                                else
                                {
                                    returnMessage = "Error converting dose, please delete plan!";
                                    Helpers.SeriLog.LogInfo("Cannot remove converted plan...");
                                }
                                status = ScriptStatus.Error;
                                return;
                            }
                        }
                        else
                        {
                            returnMessage = "Selected plan has no dose.";
                            status = ScriptStatus.Error;
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    status = ScriptStatus.Error;
                    returnMessage = "Unexpected error - mouse over for details";
                    exceptionMessage = ex.Message;
                }
            });
            if (status == ScriptStatus.Error)
            {
                return (null, status, returnMessage, exceptionMessage);
            }
            else
            {
                if (overrideStructureSet)
                {
                    returnMessage = "Complete! - Structure set was protected so creation of TEMP_DoseConv structure set was necessary. Please delete if necessary.";
                    status = ScriptStatus.Warning;
                }
                else
                {
                    returnMessage = "Complete!";
                    status = ScriptStatus.Complete;
                }
                return (outputDose, status, returnMessage, exceptionMessage);
            }
        }

        public Model(DoseConverterConfig config, EsapiWorker ew)
        {
            _config = config;
            _ew = ew;
            DefaultAlphaBeta = _config.Defaults.AlphaBetaRatio;
        }

        public async Task<List<Tuple<string, string, string, bool>>> GetPlans(bool includeSums = true)
        {
            try
            {
                var AllPlans = new List<Tuple<string, string, string, bool>>();
                await _ew.AsyncRunPlanContext((p, pl) =>
                {
                    AllPlans.Add(new Tuple<string, string, string, bool>(pl.Course.Id, pl.Id, pl.StructureSet.Id, false));
                    foreach (var otherPlan in pl.Course.PlanSetups)
                    {
                        // Can't map between structure sets without registration info so no point allowing other plans, might as well launch from them
                        if (otherPlan.StructureSet.Id == pl.StructureSet.Id && otherPlan != pl)
                            AllPlans.Add(new Tuple<string, string, string, bool>(otherPlan.Course.Id, otherPlan.Id, otherPlan.StructureSet.Id, false));
                    }
                    if (includeSums)
                        foreach (var sum in pl.Course.PlanSums)
                        {
                            if (sum.StructureSet.Id == pl.StructureSet.Id)
                                AllPlans.Add(new Tuple<string, string, string, bool>(pl.Course.Id, sum.Id, sum.StructureSet.Id, true));
                        }
                });
                return AllPlans;
            }
            catch (Exception ex)
            {
                string errorMessage = "Error enumerating plans in GetPlans().";
                Helpers.SeriLog.LogError(errorMessage, ex);
                throw new Exception(errorMessage);
            }
        }

        public async Task<bool> ValidatePlanName(string proposedName)
        {
            bool planWithNameExists = false;
            await _ew.AsyncRunPlanContext((p, pl) =>
            {
                planWithNameExists = !pl.Course.PlanSetups.Any(x => string.Equals(x.Id, proposedName, StringComparison.OrdinalIgnoreCase));
            });
            return planWithNameExists;
        }

        private async Task<Tuple<bool, string>> ValidatePlan()
        {
            bool success = true;
            string errorMessage = string.Empty;
            await _ew.AsyncRunPlanContext((p, pl) =>
            {
                if (pl.TreatmentPercentage != 1)
                {
                    success = false;
                    errorMessage = "Treatment percentage must be 100%.";
                }
                if (double.IsNaN(pl.DosePerFraction.Dose))
                {
                    success = false;
                    errorMessage = "Dose per fraction must be defined.";
                }
                if (double.IsNaN(pl.TotalDose.Dose))
                {
                    success = false;
                    errorMessage = "Plan's total dose must be defined.";
                }
            });
            return new Tuple<bool, string>(success, errorMessage);
        }

        public async Task<bool> InitializeModel()
        {
            try
            {
                string resourceName = Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(s => s.EndsWith("StructureCodes.csv"));
                using (var labelData = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)))
                {
                    while (!labelData.EndOfStream)
                    {
                        string[] line = labelData.ReadLine().Split(',');
                        StructureCodeLookup.Add(line[1].Trim(), line[2].Trim());
                    }
                }
                // Validate selected plan:
                (bool success, string errorMessage) = await ValidatePlan();
                if (!success)
                {
                    string exceptionMessage = string.Format("Error with selected plan: {0}.\r\nPlease correct the error and relaunch the script.", errorMessage);
                    throw new Exception(exceptionMessage);
                }
                var StructureList = new List<Tuple<string, string>>();
                await _ew.AsyncRunStructureContext((pat, ss) =>
                {
                    pat.BeginModifications();
                    foreach (var structure in ss.Structures.Where(x => !x.IsEmpty))
                    {
                        var Code = structure.StructureCodeInfos.FirstOrDefault();
                        bool codeMatched = false;
                        if (Code != null)
                            if (!string.IsNullOrEmpty(Code.Code))
                                if (StructureCodeLookup.ContainsKey(Code.Code))
                                {
                                    StructureList.Add(new Tuple<string, string>(structure.Id, StructureCodeLookup[Code.Code]));
                                    codeMatched = true;
                                }
                        if (!codeMatched)
                            StructureList.Add(new Tuple<string, string>(structure.Id, ""));
                    }
                });
                foreach (var structureRef in StructureList)
                {
                    var matchingStructure = _config.Structures.FirstOrDefault(x => x.Aliases.Select(y => y.StructureId).Any(z => string.Equals(z.Replace("_", ""), structureRef.Item1.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                    || string.Equals(x.StructureLabel, structureRef.Item2, StringComparison.InvariantCultureIgnoreCase) && !string.IsNullOrEmpty(structureRef.Item2));
                    if (matchingStructure != null)
                    {
                        StructureDefinitions.Add(new StructureViewModel(this, structureRef.Item1, matchingStructure.AlphaBetaRatio, structureRef.Item2, matchingStructure.ForceEdgeConversion, matchingStructure.MaxEQD2, true));
                    }
                    else
                        StructureDefinitions.Add(new StructureViewModel(this, structureRef.Item1, DefaultAlphaBeta, structureRef.Item2, false, null, false));
                }
                return true;
            }
            catch (Exception ex)
            {
                string errorMessage = "Error during initialization.";
                Helpers.SeriLog.LogFatal(errorMessage, ex);
                throw new Exception(errorMessage);
            }
        }

        public async Task<List<StructureViewModel>> GetStructureDefinitions(string ssId = null)
        {
            if (ssId == null)
                return StructureDefinitions.ToList();
            else
            {
                StructureDefinitions.Clear();
                await _ew.AsyncRunStructureContext((pat, ss) =>
                {
                    var ssOverride = pat.StructureSets.FirstOrDefault(s => s.Id == ssId);
                    foreach (var structure in ssOverride.Structures.Where(x => !x.IsEmpty))
                    {
                        var Code = structure.StructureCodeInfos.FirstOrDefault();
                        string structureLabel = string.Empty;
                        if (Code != null)
                            if (!string.IsNullOrEmpty(Code.Code))
                                if (StructureCodeLookup.ContainsKey(Code.Code))
                                    structureLabel = StructureCodeLookup[Code.Code];
                        var matchingStructure = _config.Structures.FirstOrDefault(x => x.Aliases.Select(y => y.StructureId).Any(z => string.Equals(z.Replace("_", ""), structure.Id.Replace("_", ""), StringComparison.OrdinalIgnoreCase))
                           || string.Equals(x.StructureLabel, structureLabel, StringComparison.InvariantCultureIgnoreCase) && !string.IsNullOrEmpty(structureLabel));
                        if (matchingStructure != null)
                            StructureDefinitions.Add(new StructureViewModel(this, structure.Id, matchingStructure.AlphaBetaRatio, structureLabel, matchingStructure.ForceEdgeConversion, matchingStructure.MaxEQD2, true));
                        else
                            StructureDefinitions.Add(new StructureViewModel(this, structure.Id, DefaultAlphaBeta, "", false, null, false));
                    }
                });
            }
            return StructureDefinitions;
        }

        public async Task<string> GetStructureLabel(string StructureId)
        {
            string Label = "Unset";
            await _ew.AsyncRunPlanContext((p, pl) =>
            {
                var matchingStructure = pl.StructureSet.Structures.FirstOrDefault(x => string.Equals(x.Id, StructureId, StringComparison.InvariantCultureIgnoreCase));
                if (matchingStructure != null)
                {
                    var CodeInfo = matchingStructure.StructureCodeInfos.FirstOrDefault();
                    if (CodeInfo != null)
                    {
                        Label = StructureCodeLookup[CodeInfo.Code];
                    }
                }
            });
            return Label;
        }

        public int[,,] GetDoseVoxelsFromDose(Dose dose)
        {
            int Xsize = dose.XSize;
            int Ysize = dose.YSize;
            int Zsize = dose.ZSize;

            int[,,] doseMatrix = new int[Zsize, Xsize, Ysize];

            // Get whole dose matrix from context
            for (int k = 0; k < Zsize; k++)
            {
                int[,] plane = new int[Xsize, Ysize];
                dose.GetVoxels(k, plane);

                for (int i = 0; i < Xsize; i++)
                {
                    for (int j = 0; j < Ysize; j++)
                    {
                        doseMatrix[k, i, j] = plane[i, j];
                    }
                }
            }
            return doseMatrix;
        }

        public double GetMaxDoseVal(Dose dose, PlanningItem source)
        {
            DoseValue maxDose = dose.DoseMax3D;
            double maxDoseVal = maxDose.Dose;

            PlanSetup plan = source as PlanSetup;
            if (plan != null)
            {
                if (maxDose.IsRelativeDoseValue)
                {
                    if (plan.TotalDose.Unit == DoseValue.DoseUnit.cGy)
                    {
                        maxDoseVal = maxDoseVal * plan.TotalDose.Dose / 10000.0;
                    }
                    else
                    {
                        maxDoseVal = maxDoseVal * plan.TotalDose.Dose / 100.0;
                    }
                }
            }
            if (maxDose.Unit == DoseValue.DoseUnit.cGy)
            {
                maxDoseVal = maxDoseVal / 100.0;
            }
            return maxDoseVal;
        }

        private int[,,] ConvertDose(DoseFormat format, ExternalPlanSetup newPlan, ExternalPlanSetup targetPlan, PlanningItem source, List<StructureViewModel> mappings, double? convParameter = null, bool overrideStructureSet = false)
        {
            Dose dose = source.Dose;

            int Xsize = dose.XSize;
            int Ysize = dose.YSize;
            int Zsize = dose.ZSize;

            double Xres = dose.XRes;
            double Yres = dose.YRes;
            double Zres = dose.ZRes;

            VVector Xdir = dose.XDirection;
            VVector Ydir = dose.YDirection;
            VVector Zdir = dose.ZDirection;

            VVector doseOrigin = dose.Origin;

            //int[,,] doseMatrix = GetDoseVoxelsFromDose(dose);
            originalArray = GetDoseVoxelsFromDose(dose); // a copy
            int[,,] doseMatrix = new int[originalArray.GetLength(0), originalArray.GetLength(1), originalArray.GetLength(2)];

            double maxDoseVal = GetMaxDoseVal(dose, source);

            Tuple<int, int> minMaxDose = Helpers.GetMinMaxValues(originalArray, Xsize, Ysize, Zsize);

            scaling = maxDoseVal / minMaxDose.Item2;

            doseMin = minMaxDose.Item1 * scaling;
            doseMax = minMaxDose.Item2 * scaling;

            var plan = source as PlanSetup;

            StructureSet ss;
            ExternalPlanSetup epl = null;
            if (plan != null)
            {
                epl = (ExternalPlanSetup)plan;
                ss = epl.StructureSet;
            }
            else
                ss = source.StructureSet;

            foreach (var strVM in mappings.Where(x => x.Include).Reverse())
            {
                Structure structure = ss.Structures.FirstOrDefault(x => string.Equals(x.Id, strVM.StructureId, StringComparison.InvariantCultureIgnoreCase));
                double alphabeta = strVM.AlphaBetaRatio;

                if (structure.IsEmpty)
                {
                    Helpers.SeriLog.LogWarning(string.Format("Structure {0} is empty, skipping conversion", structure.Id));
                    continue;
                }

                if (strVM.IncludeEdges)
                {
                    try
                    {
                        if (overrideStructureSet)
                        {
                            var ssOverride = plan.Course.Patient.StructureSets.FirstOrDefault(x => string.Equals(x.Id, _config.Defaults.TempStructureSetName, StringComparison.InvariantCultureIgnoreCase));
                            if (ssOverride == null)
                            {
                                ssOverride = ss.Copy();
                                ssOverride.Id = _config.Defaults.TempStructureSetName;
                                ss = ssOverride;
                                Helpers.SeriLog.LogWarning("Input structure set is protected, generating override structure set.");
                            }
                            else
                            {
                                ss = ssOverride;
                                Helpers.SeriLog.LogWarning($"Input structure set is protected, using existing override structure set ({ssOverride.Id})");
                                if (!ss.CanAddStructure("CONTROL", _config.Defaults.TempEdgeStructureName))
                                {
                                    throw new Exception("TEMP_DoseConv structure set must be accessible / not in a complete course.");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string errorMessage = "Coule not create or access TEMP_DoseConv structure set.";
                        Helpers.SeriLog.LogError(errorMessage, ex); 
                    }
                    try
                    {
                        var edgeStructure = ss.Structures.FirstOrDefault(x => string.Equals(x.Id, _config.Defaults.TempEdgeStructureName, StringComparison.InvariantCultureIgnoreCase));
                        if (edgeStructure == null)
                            edgeStructure = ss.AddStructure("CONTROL", _config.Defaults.TempEdgeStructureName);
                        var margin = DetermineMargin(source);
                        edgeStructure.SegmentVolume = structure.AsymmetricMargin(new AxisAlignedMargins(StructureMarginGeometry.Outer, margin.Item1, margin.Item2, margin.Item3, margin.Item1, margin.Item2, margin.Item3));
                        Helpers.SeriLog.LogInfo(string.Format("Added inclusion margins to structure {0} of X={1}, Y={2}, Z={3}.", structure.Id, margin.Item1, margin.Item2, margin.Item3));
                        structure = edgeStructure;
                    }
                    catch (Exception ex)
                    {
                        string errorMessage = "Error creating edge volume. Check that configuration file defines TempEdgeStructureName and that structure set isn't locked";
                        Helpers.SeriLog.LogError(errorMessage, ex);
                        throw new Exception(errorMessage);
                    }
                }
                else
                {
                    structure = ss.Structures.FirstOrDefault(x => string.Equals(x.Id, strVM.StructureId, StringComparison.InvariantCultureIgnoreCase));
                }

                if (epl != null)
                {
                    switch (format)
                    {
                        case DoseFormat.EQD2:
                            Helpers.SeriLog.LogInfo("Converting voxels to EQD2...");
                            OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateEQD2);
                            break;
                        case DoseFormat.BED:
                            Helpers.SeriLog.LogInfo("Converting voxels to BED...");
                            OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateBED);
                            break;
                        case DoseFormat.EQDd:
                            Helpers.SeriLog.LogInfo("Converting voxels to EQDd...");
                            OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateEQDd, convParameter);
                            break;
                        case DoseFormat.EQDn:
                            Helpers.SeriLog.LogInfo("Converting voxels to EQDn#...");
                            OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateEQDn, convParameter);
                            break;
                        case DoseFormat.Base:
                            Helpers.SeriLog.LogInfo("Converting voxels to BASE...");
                            OverridePixels(structure, alphabeta, 0, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                        Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculatePhysicalDoseFromEQD2, convParameter, strVM.MaxEQD2);
                            break;
                    }
                    CreatePlanAndAddDose(Xsize, Ysize, Zsize, doseMatrix, maxDoseVal, newPlan, epl);
                }
                else
                {
                    // plansum, assume dose is in EQD2 already
                    switch (format)
                    {
                        case DoseFormat.Base:
                            Helpers.SeriLog.LogInfo("Converting voxels to BASE...");
                            OverridePixels(structure, alphabeta, 0, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                       Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculatePhysicalDoseFromEQD2, convParameter, strVM.MaxEQD2);
                            break;

                    }
                    CreatePlanAndAddDose(Xsize, Ysize, Zsize, doseMatrix, maxDoseVal, newPlan, targetPlan, (PlanSum)source);
                }
            }
            try
            {
                var edgeStructure = ss.Structures.FirstOrDefault(x => string.Equals(x.Id, _config.Defaults.TempEdgeStructureName, StringComparison.InvariantCultureIgnoreCase));
                if (edgeStructure != null)
                {
                    Helpers.SeriLog.LogInfo("Cleaning up edge conversion structure...");
                    ss.RemoveStructure(edgeStructure);
                }
            }
            catch (Exception ex)
            {
                string errorMessage = "Error cleaning up temporary edge conversion structure. This may need to be done manually.";
                Helpers.SeriLog.LogError(errorMessage, ex);
                throw new Exception(errorMessage);
            }
            return doseMatrix;

        }

        public void CreatePlanAndAddDose(int Xsize, int Ysize, int Zsize, int[,,] doseMatrix, double doseMaxOriginal, ExternalPlanSetup newPlan, ExternalPlanSetup thisPlan, PlanSum sum = null)
        {
            int fractions = (int)thisPlan.NumberOfFractions;
            DoseValue dosePerFraction = thisPlan.DosePerFraction;
            double treatPercentage = thisPlan.TreatmentPercentage;

            newPlan.SetPrescription(fractions, dosePerFraction, treatPercentage);

            double normalization = thisPlan.PlanNormalizationValue;
            if (!Double.IsNaN(normalization))
            {
                newPlan.PlanNormalizationValue = normalization;
            }
            else
            {
                newPlan.PlanNormalizationValue = 100;
            }

            EvaluationDose evalDose;
            if (sum != null)
                evalDose = newPlan.CopyEvaluationDose(sum.Dose);
            else
                evalDose = newPlan.CopyEvaluationDose(thisPlan.Dose);

            double maxDoseVal = GetMaxDoseVal(evalDose, newPlan);

            Tuple<int, int> minMaxDoseInt = Helpers.GetMinMaxValues(GetDoseVoxelsFromDose(evalDose), Xsize, Ysize, Zsize);
            int maxInt = minMaxDoseInt.Item2;

            scaling2 = maxDoseVal / maxInt;


            // scaling2 is used when a plan is imported into eclipse. In this case, the voxel values are correctly set,
            // however the internal scaling factor differes from the original one after Evaluation dose is copied. Don't know why exactly.
            // I solved this by introducing an additional scaling factor that in normal cases should equal 1.
            // This factor is used to renormalize the voxels so that if no conversion is done, the result is equal to the original.
            scaling3 = doseMaxOriginal / maxDoseVal;

            if (maxInt * scaling3 > 2147483647.0)
            {
                throw new InvalidDataException("Maximum integer value exceeded. Conversion failed.");
            }
            else
            {
                for (int k = 0; k < Zsize; k++)
                {
                    int[,] plane = new int[Xsize, Ysize];
                    for (int i = 0; i < Xsize; i++)
                    {
                        for (int j = 0; j < Ysize; j++)
                        {
                            plane[i, j] = (int)(doseMatrix[k, i, j] * scaling3);
                        }
                    }
                    evalDose.SetVoxels(k, plane);
                }
            }
        }




        public int GetIndexFromCoordinate(double coord, double origin, double direction, double res)
        {
            return Convert.ToInt32((coord - origin) / (direction * res));
        }

        private void ForceConversionMargin_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBox txt = sender as TextBox;
            int ind = txt.CaretIndex;
            txt.Text = txt.Text.Replace(",", ".");
            txt.CaretIndex = ind;
        }

        public Tuple<double, double, double> DetermineMargin(PlanningItem source)
        {
            ////determine margin for structures from dose voxel size
            double dx = source.Dose.XRes;
            double dy = source.Dose.YRes;
            double dz = source.Dose.ZRes;
            return new Tuple<double, double, double>(dx, dy, dz);
        }

        public delegate int calculateFunction(int dose, double alphabeta, double scaling, short numberOfFractions, double? nOut = null);

        public void OverridePixels(Structure structure, double alphabeta, short numFractions, int[,,] doseMatrixOrig, int[,,] doseMatrixOut, double scaling, int Xsize, int Ysize, int Zsize,
            double Xres, double Yres, double Zres, VVector Xdir, VVector Ydir, VVector Zdir, VVector doseOrigin, calculateFunction functionCalculate, double? nOut = null, double? maxEQD2 = null)
        {
            // The following is valid only for HFS, HFP, FFS, FFP orientations that do not mix x,y,z
            double sx = Xdir.x + Ydir.x + Zdir.x;
            double sy = Xdir.y + Ydir.y + Zdir.y;
            double sz = Xdir.z + Ydir.z + Zdir.z;

            var bounds = structure.MeshGeometry.Bounds;

            double x0 = bounds.X;
            double x1 = x0 + bounds.SizeX;
            double y0 = bounds.Y;
            double y1 = y0 + bounds.SizeY;
            double z0 = bounds.Z;
            double z1 = z0 + bounds.SizeZ;

            int imin = GetIndexFromCoordinate(x0, doseOrigin.x, sx, Xres);
            int imax = GetIndexFromCoordinate(x1, doseOrigin.x, sx, Xres);

            int jmin = GetIndexFromCoordinate(y0, doseOrigin.y, sy, Yres);
            int jmax = GetIndexFromCoordinate(y1, doseOrigin.y, sy, Yres);

            int kmin = GetIndexFromCoordinate(z0, doseOrigin.z, sz, Zres);
            int kmax = GetIndexFromCoordinate(z1, doseOrigin.z, sz, Zres);

            if (imin > imax)
            {
                int t = imin;
                imin = imax;
                imax = t;
            }
            if (jmin > jmax)
            {
                int t = jmin;
                jmin = jmax;
                jmax = t;
            }
            if (kmin > kmax)
            {
                int t = kmin;
                kmin = kmax;
                kmax = t;
            }

            imax += 2;
            imin -= 2;
            jmax += 2;
            jmin -= 2;
            kmax += 2;
            kmin -= 2;

            if (imin < 0)
            {
                imin = 0;
            }
            if (imax > Xsize - 1)
            {
                imax = Xsize - 1;
            }

            if (jmin < 0)
            {
                jmin = 0;
            }
            if (jmax > Ysize - 1)
            {
                jmax = Ysize - 1;
            }

            if (kmin < 0)
            {
                kmin = 0;
            }
            if (kmax > Zsize - 1)
            {
                kmax = Zsize - 1;
            }

            int nx = imax - imin + 1;
            int ny = jmax - jmin + 1;
            int nz = kmax - kmin + 1;

            for (int k = kmin; k <= kmax; k++)
            {
                for (int j = jmin; j <= jmax; j++)
                {
                    double y = doseOrigin.y + j * Yres * sy;
                    double z = doseOrigin.z + k * Zres * sz;

                    double xstart = doseOrigin.x + imin * Xres * sx;
                    double xstop = doseOrigin.x + imax * Xres * sx;

                    var profilePoints = structure.GetSegmentProfile(new VVector(xstart, y, z), new VVector(xstop, y, z), new BitArray(nx)).Select(profilePoint => profilePoint.Value).ToArray();

                    for (int p = 0; p < profilePoints.Length; p++)
                    {
                        Tuple<int, int, int> newIndices = Tuple.Create(k, imin + p, j);

                        if (profilePoints[p] && this.existingIndexes.Contains(newIndices) == false)
                        {
                            int dose = doseMatrixOrig[k, imin + p, j];
                            if (nOut != null)  // either converting to a new fractionation or creating a base plan
                            {
                                if (maxEQD2 != null) // creating a base plan
                                {
                                    int maxEQD2_scaled = Convert.ToInt32(Math.Abs((double)maxEQD2 / scaling));
                                    if (maxEQD2_scaled > dose)
                                        doseMatrixOut[k, imin + p, j] = Math.Max(functionCalculate(maxEQD2_scaled, alphabeta, scaling, numFractions, nOut)
                                         - functionCalculate(maxEQD2_scaled - dose, alphabeta, scaling, numFractions, nOut), 0);
                                    else
                                        doseMatrixOut[k, imin + p, j] = functionCalculate(maxEQD2_scaled, alphabeta, scaling, numFractions, nOut);

                                }
                                else
                                    doseMatrixOut[k, imin + p, j] = functionCalculate(dose, alphabeta, scaling, numFractions, nOut);

                            }
                            else
                                doseMatrixOut[k, imin + p, j] = functionCalculate(dose, alphabeta, scaling, numFractions);
                        }
                    }

                }
            }
        }


        public int CalculateEQD2(int dose, double alphabeta, double scaling, short numberOfFractions, double? convParam1 = null)
        {
            try
            {
                return Convert.ToInt32(dose * (alphabeta + dose * scaling / numberOfFractions) / (alphabeta + 2.0));
            }
            catch (Exception ex)
            {
                string errorMessage = string.Format("Exception in CalculateEQD2 [dose = {0}, alphabeta = {1}, scaling = {2}, n1 = {3}]", dose, alphabeta, scaling, numberOfFractions);
                throw new Exception(errorMessage, ex);
            }
        }

        public int CalculatePhysicalDoseFromEQD2(int EQD2, double alphabeta, double scaling, short numberOfFractions, double? n2 = null)
        {
            try
            {
                return Convert.ToInt32((double)n2 * alphabeta / 2 / scaling * (Math.Sqrt(1 + 4 * scaling / (double)n2 / alphabeta * EQD2 * (1 + 2 / alphabeta)) - 1));
            }
            catch (Exception ex)
            {
                string errorMessage = string.Format("Exception in CalculatePhysicalDose [EQD2 = {0}, alphabeta = {1}, scaling = {2}, n2 = {3}]", EQD2, alphabeta, scaling, n2);
                throw new Exception(errorMessage, ex);
            }
        }

        public int CalculateEQDd(int dose, double alphabeta, double scaling, short numberOfFractions, double? dFraction = null)
        {
            try
            {
                return Convert.ToInt32((dose * (alphabeta + dose * scaling / numberOfFractions) / (alphabeta + dFraction)));
            }
            catch (Exception ex)
            {
                string errorMessage = string.Format("Exception in CalculateEQDd [dose = {0}, alphabeta = {1}, scaling = {2}, n1 = {3}, dosePerFraction = {4}]", dose, alphabeta, scaling, numberOfFractions, dFraction);
                throw new Exception(errorMessage, ex);
            }

        }

        public int CalculateBED(int dose, double alphabeta, double scaling, short numberOfFractions, double? convParam1 = null)
        {
            try
            {
                return Convert.ToInt32(dose * (1 + dose * scaling / (numberOfFractions * alphabeta)));
            }
            catch (Exception ex)
            {
                string errorMessage = string.Format("Exception in CalculateBED [dose = {0}, alphabeta = {1}, scaling = {2}, n1 = {3}]", dose, alphabeta, scaling, numberOfFractions);
                throw new Exception(errorMessage, ex);
            }
        }

        public int CalculateEQDn(int dose, double alphabeta, double scaling, short numberOfFractions, double? n2 = null)
        {
            try
            {
                return Convert.ToInt32((double)n2 * alphabeta / 2 / scaling * (Math.Sqrt(1 + 4 * scaling / (double)n2 / alphabeta * (dose * (1 + dose * scaling / (numberOfFractions * alphabeta)))) - 1));
            }
            catch (Exception ex)
            {
                string errorMessage = string.Format("Exception in CalculateEQDn [dose = {0}, alphabeta = {1}, scaling = {2}, n1 = {3}, n2 = {4}]", dose, alphabeta, scaling, numberOfFractions, n2);
                throw new Exception(errorMessage, ex);
            }
        }

        public double ConvertEQD2toPhysical(double EQD2, double alphabeta, ushort n2)
        {
            try
            {
                return (double)n2 * alphabeta / 2 * (Math.Sqrt(1 + 4 / (double)n2 / alphabeta * EQD2 * (1 + 2 / alphabeta)) - 1);
            }
            catch (Exception ex)
            {
                string errorMessage = string.Format("Exception in ConvertEQD2toPhysical [EQD2 = {0}, alphabeta = {1}, n2 = {2}]", EQD2, alphabeta, n2);
                throw new Exception(errorMessage, ex);
            }
        }
    }
}
