using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using static EQD2Converter.MainWindow;
using System.Windows.Input;
using System.Windows;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;
using ESAPIScript;
using System.Windows.Controls;
using System.Windows.Media;
using System.Reflection;
using System.Runtime.InteropServices;

namespace EQD2Converter
{
    public class Model
    {
        private EQD2ConverterConfig _config;
        private List<AlphaBetaMapping> AlphaBetaMappings { get; set; } = new List<AlphaBetaMapping>();
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

        public async Task<(int[,,], bool, string)> GetConvertedDose(string courseId, string planId, bool isSum, string newPlanName, List<AlphaBetaMapping> mappings, DoseFormat format, double? convParameter = null)
        {
            ExternalPlanSetup newPlan = (ExternalPlanSetup)null;
            int[,,] outputDose = null;
            string errorMessage = "";
            bool success = true;

            await _ew.AsyncRunPlanContext((p, pl) =>
            {
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
                                newPlan = pl.Course.AddExternalPlanSetupAsVerificationPlan(pl.StructureSet, (ExternalPlanSetup)pl);
                                newPlan.Id = newPlanName;
                            }
                            catch
                            {
                                errorMessage = "Error creating plan";
                                success = false;
                            }
                            outputDose = ConvertDose(format, newPlan, (ExternalPlanSetup)pl, sum, mappings, convParameter, false);
                        }
                        else
                        {
                            errorMessage = "Selected plan sum has no dose.";
                            success = false;
                        }

                    }
                    else
                    {
                        var plan = p.Courses.FirstOrDefault(x => string.Equals(courseId, x.Id, StringComparison.OrdinalIgnoreCase)).PlanSetups.FirstOrDefault(x => string.Equals(x.Id, planId, StringComparison.OrdinalIgnoreCase));
                        if (plan.Dose != null)
                        {
                            try
                            {
                                newPlan = pl.Course.AddExternalPlanSetupAsVerificationPlan(pl.StructureSet, (ExternalPlanSetup)plan);
                                newPlan.Id = newPlanName;
                            }
                            catch (Exception ex)
                            {
                                errorMessage = "Error creating plan";
                                success = false;
                            }
                            outputDose = ConvertDose(format, newPlan, (ExternalPlanSetup)pl, plan, mappings, convParameter, false);

                        }
                        else
                        {
                            errorMessage = "Selected plan sum has no dose.";
                            success = false;
                        }
                    }
                }
                catch (Exception f)
                {
                    //waitWindow.Close();
                    //this.Cursor = null;
                    success = false;
                    MessageBox.Show(f.Message + "\n" + f.StackTrace, "Error");
                }
            });
            if (!success)
            {
                return (null, success, errorMessage);
            }
            else
                return (outputDose, success, "Complete!");
        }

        public Model(EQD2ConverterConfig config, EsapiWorker ew)
        {
            _config = config;
            _ew = ew;
            DefaultAlphaBeta = _config.Defaults.AlphaBetaRatio;
        }

        public async Task<List<Tuple<string, string, string, bool>>> GetPlans()
        {
            var AllPlans = new List<Tuple<string, string, string, bool>>();
            await _ew.AsyncRunPlanContext((p, pl) =>
            {
                foreach (var c in p.Courses)
                {
                    // Can't map between structure sets without registration info so no point allowing other plans, might as well launch from them


                    AllPlans.Add(new Tuple<string, string, string, bool>(pl.Course.Id, pl.Id, pl.StructureSet.Id, false));

                    foreach (var ps in c.PlanSums)
                    {
                        if (ps.StructureSet == pl.StructureSet)
                            AllPlans.Add(new Tuple<string, string, string, bool>(c.Id, ps.Id, ps.StructureSet.Id, true));
                    }
                }
            });
            return AllPlans;
        }

        public async Task<bool> InitializeModel()
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
                    AlphaBetaMappings.Add(new AlphaBetaMapping(structureRef.Item1, matchingStructure.AlphaBetaRatio, structureRef.Item2, matchingStructure.MaxEQD2, true));
                }
                else
                    AlphaBetaMappings.Add(new AlphaBetaMapping(structureRef.Item1, DefaultAlphaBeta, structureRef.Item2, null, false));
            }


            return true;
        }

        public async Task<List<AlphaBetaMapping>> GetAlphaBetaMappings(string ssId = null)
        {
            if (ssId == null)
                return AlphaBetaMappings.ToList();
            else
            {
                AlphaBetaMappings.Clear();
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
                            AlphaBetaMappings.Add(new AlphaBetaMapping(structure.Id, matchingStructure.AlphaBetaRatio, structureLabel, matchingStructure.MaxEQD2, true));
                        else
                            AlphaBetaMappings.Add(new AlphaBetaMapping(structure.Id, DefaultAlphaBeta, "", 0, false));
                    }
                });
            }
            return AlphaBetaMappings;
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

        public async void Start(string newPlanName)
        {






            //// Add a waiting window here
            //this.Cursor = Cursors.Wait;
            //var waitWindow = new WaitingWindow();
            //waitWindow.Show();



            //waitWindow.Close();
            //this.Cursor = null;



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

        private int[,,] ConvertDose(DoseFormat format, ExternalPlanSetup newPlan, ExternalPlanSetup targetPlan, PlanningItem source, List<AlphaBetaMapping> mappings, double? convParameter = null, bool preview = false)
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

            foreach (var str in mappings.Where(x => x.Include).Reverse())
            {
                Structure structure = ss.Structures.FirstOrDefault(x => string.Equals(x.Id, str.StructureId, StringComparison.InvariantCultureIgnoreCase));
                double alphabeta = str.AlphaBetaRatio;

                // transfer structure to auxiliary structure set and add margin:
                //if ((bool)this.ForceConversionCheckBox.IsChecked)
                //{
                //    Structure newStructure = this.AuxStructureSet.AddStructure(structure.DicomType, structure.Id);
                //    double margin = ConvertTextToDouble(this.ForceConversionMargin.Text);
                //    var segmVolMargin = structure.SegmentVolume.Margin(margin);

                //    newStructure.SegmentVolume = segmVolMargin;
                //    structure = newStructure;
                //}

                if (structure.IsEmpty)
                {
                    // MessageBox.Show("Cannot apply margin to " + structure.Id + ". Structure will be skipped.", "Error");
                    continue;
                }

                //if (this.ComboBox2.SelectedValue.ToString() == "EQD2")
                //{
                if (epl != null)
                {
                    switch (format)
                    {
                        case DoseFormat.EQD2:
                            OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateEQD2);
                            break;
                        case DoseFormat.BED:
                            OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateBED);
                            break;
                        case DoseFormat.EQDd:
                            OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateEQDd, convParameter);
                            break;
                        case DoseFormat.BEDn2:
                            OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateEQDn, convParameter);
                            break;
                        case DoseFormat.Base:
                            OverridePixels(structure, alphabeta, 0, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                        Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculatePhysicalDose, convParameter, str.MaxEQD2);
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
                            OverridePixels(structure, alphabeta, 0, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                       Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculatePhysicalDose, convParameter, str.MaxEQD2);
                            break;

                    }
                    CreatePlanAndAddDose(Xsize, Ysize, Zsize, doseMatrix, maxDoseVal, newPlan, targetPlan, (PlanSum)source);
                }

                //}
                //else if (this.ComboBox2.SelectedValue.ToString() == "BED")
                //{
                //    OverridePixels(structure, alphabeta, doseMatrix, scaling, Xsize, Ysize, Zsize,
                //         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateBED);
                //}
                //else
                //{
                //    OverridePixels(structure, alphabeta, doseMatrix, scaling, Xsize, Ysize, Zsize,
                //         Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, MultiplyByAlphaBeta);
                //}

                //if ((bool)this.ForceConversionCheckBox.IsChecked)
                //{
                //    if (this.AuxStructureSet.CanRemoveStructure(structure))
                //    {
                //        this.AuxStructureSet.RemoveStructure(structure);
                //    }
                //}
            }

            existingIndexes = new HashSet<Tuple<int, int, int>>() { }; // reset!




            return doseMatrix;

            //if (!preview)
            //{
            //    CreatePlanAndAddDose(Xsize, Ysize, Zsize, doseMatrix, maxDoseVal, newPlan, epl);
            //    return new int[0, 0, 0];
            //}
            //else
            //{
            //    return doseMatrix;
            //}
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


        private void ForceConversionCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            //if ((bool)this.ForceConversionCheckBox.IsChecked)
            //{
            //    this.ForcedConversionLabel.Text = "Warning. An auxiliary structure set will be created. The plan and the original structure set" +
            //        " will be left untouched. After conversion you must manually delete the new structure set/image.";
            //    this.ForcedConversionLabel.Foreground = Brushes.Red;
            //    this.ForceConversionMarginStackPanel.Visibility = Visibility.Visible;
            //}
            //else
            //{
            //    this.ForcedConversionLabel.Text = "";
            //    this.ForceConversionMarginStackPanel.Visibility = Visibility.Hidden;
            //}
        }

        private void ForceConversionMargin_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBox txt = sender as TextBox;
            int ind = txt.CaretIndex;
            txt.Text = txt.Text.Replace(",", ".");
            txt.CaretIndex = ind;
        }

        public void DetermineMargin()
        {
            ////determine margin for structures from dose voxel size
            //double dx = this.scriptcontext.ExternalPlanSetup.Dose.XRes;
            //double dy = this.scriptcontext.ExternalPlanSetup.Dose.YRes;
            //double dz = this.scriptcontext.ExternalPlanSetup.Dose.ZRes;
            //this.ForceConversionMargin.Text = new List<double>() { dx, dy, dz }.Max().ToString();
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
                            // this.existingIndexes.Add(newIndices); // allow overwriting as I'm controlling the order from the list
                        }
                    }

                }
            }
        }


        public int CalculateEQD2(int dose, double alphabeta, double scaling, short numberOfFractions, double? convParam1 = null)
        {
            return Convert.ToInt32(dose * (alphabeta + dose * scaling / numberOfFractions) / (alphabeta + 2.0));
        }

        public int CalculatePhysicalDose(int EQD2, double alphabeta, double scaling, short numberOfFractions, double? n2 = null)
        {
            return Convert.ToInt32((double)n2 * alphabeta / 2 / scaling * (Math.Sqrt(1 + 4 * scaling / (double)n2 / alphabeta * EQD2 * (1 + 2 / alphabeta)) - 1));
        }

        public int CalculateEQDd(int dose, double alphabeta, double scaling, short numberOfFractions, double? dFraction = null)
        {
            return Convert.ToInt32((dose * (alphabeta + dose * scaling / numberOfFractions) / (alphabeta + dFraction)));
        }

        public int CalculateBED(int dose, double alphabeta, double scaling, short numberOfFractions, double? convParam1 = null)
        {
                return Convert.ToInt32(dose * (1 + dose * scaling / (numberOfFractions * alphabeta)));
        }

        public int CalculateEQDn(int dose, double alphabeta, double scaling, short numberOfFractions, double? n2 = null)
        {
            return Convert.ToInt32((double)n2 * alphabeta / 2 / scaling * (Math.Sqrt(1 + 4 * scaling / (double)n2 / alphabeta * (dose * (1 + dose * scaling / (numberOfFractions * alphabeta)))) - 1));
        }


        public int MultiplyByAlphaBeta(int dose, double alphabeta, double scaling)
        {
            return Convert.ToInt32(dose * alphabeta);
        }

    }
}
