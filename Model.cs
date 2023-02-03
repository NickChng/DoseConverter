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

        public async Task<(int[,,], bool, string)> GetConvertedDose(string newPlanName, List<AlphaBetaMapping> mappings, DoseOutputFormat format, double? DosePerFraction = null)
        {
            ExternalPlanSetup newPlan = (ExternalPlanSetup)null;
            int[,,] outputDose = null;
            string errorMessage = "";
            bool success = true;
            await _ew.AsyncRunPlanContext((p, pl) =>
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
            });
            if (!success)
                return (null, success, errorMessage);

            await _ew.AsyncRunPlanContext((p, pl) =>
            {
                try
                {
                    outputDose = ConvertDose(newPlan, pl, mappings, format, DosePerFraction, false);
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
                    if (Code != null)
                        StructureList.Add(new Tuple<string, string>(structure.Id, StructureCodeLookup[Code.Code]));
                    else
                        StructureList.Add(new Tuple<string, string>(structure.Id, ""));
                }
            });
            foreach (var structureRef in StructureList)
            {
                var matchingStructure = _config.Structures.FirstOrDefault(x => x.Aliases.Select(y => y.StructureId).Any(z => string.Equals(z.Replace("_", ""), structureRef.Item1, StringComparison.OrdinalIgnoreCase))
                || string.Equals(x.StructureLabel, structureRef.Item2, StringComparison.InvariantCultureIgnoreCase));
                if (matchingStructure != null)
                {
                    AlphaBetaMappings.Add(new AlphaBetaMapping(structureRef.Item1, matchingStructure.AlphaBetaRatio, structureRef.Item2, true));
                }
                else
                    AlphaBetaMappings.Add(new AlphaBetaMapping(structureRef.Item1, DefaultAlphaBeta, structureRef.Item2, false));
            }


            return true;
        }

        public List<AlphaBetaMapping> GetAlphaBetaMappings()
        {
            return AlphaBetaMappings.ToList();
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

        public double GetMaxDoseVal(Dose dose, ExternalPlanSetup plan)
        {
            DoseValue maxDose = dose.DoseMax3D;
            double maxDoseVal = maxDose.Dose;

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

            if (maxDose.Unit == DoseValue.DoseUnit.cGy)
            {
                maxDoseVal = maxDoseVal / 100.0;
            }
            return maxDoseVal;
        }

        private int[,,] ConvertDose(ExternalPlanSetup newPlan, PlanSetup thisPlan, List<AlphaBetaMapping> mappings, DoseOutputFormat format, double? dosePerFraction = null, bool preview = false)
        {
            ExternalPlanSetup epl = (ExternalPlanSetup)thisPlan;
            Dose dose = epl.Dose;

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

            int[,,] doseMatrix = GetDoseVoxelsFromDose(dose);
            originalArray = GetDoseVoxelsFromDose(dose); // a copy

            double maxDoseVal = GetMaxDoseVal(dose, epl);

            Tuple<int, int> minMaxDose = Helpers.GetMinMaxValues(doseMatrix, Xsize, Ysize, Zsize);

            scaling = maxDoseVal / minMaxDose.Item2;

            doseMin = minMaxDose.Item1 * scaling;
            doseMax = minMaxDose.Item2 * scaling;

            //Dictionary<Structure, double> structureDict = new Dictionary<Structure, double>() { };

            //foreach (var row in this.DataGridStructuresList)
            //{
            //    if (row.AlphaBeta != null && row.AlphaBeta != "" && ConvertTextToDouble(row.AlphaBeta) != Double.NaN)
            //    {
            //        Structure structure = this.scriptcontext.StructureSet.Structures.First(id => id.Id == row.Structure);
            //        double alphabeta = ConvertTextToDouble(row.AlphaBeta);

            //        structureDict.Add(structure, alphabeta);
            //    }
            //}

            //IOrderedEnumerable<KeyValuePair<Structure, double>> sortedDict;

            //if (this.ComboBox.SelectedValue.ToString() == "Descending")
            //{
            //    sortedDict = from entry in structureDict orderby entry.Value descending select entry;
            //}
            //else
            //{
            //    sortedDict = from entry in structureDict orderby entry.Value ascending select entry;
            //}

            // If forced edge conversion is on, add margin to structure on a seperate structureset
            //if ((bool)this.ForceConversionCheckBox.IsChecked)
            //{
            //    if (!this.WasStructureSetCreated)
            //    {
            //        this.AuxStructureSet = this.scriptcontext.Image.CreateNewStructureSet();
            //        this.WasStructureSetCreated = true;
            //    }
            //}

            foreach (var str in mappings.Where(x => x.Include).Reverse())
            {
                Structure structure = epl.StructureSet.Structures.FirstOrDefault(x => string.Equals(x.Id, str.StructureId, StringComparison.InvariantCultureIgnoreCase));
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

                switch (format)
                {
                    case DoseOutputFormat.EQD2:
                        OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                     Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateEQD2);
                        break;
                    case DoseOutputFormat.BED:
                        OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                     Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateBED);
                        break;
                    case DoseOutputFormat.EQDn:
                        OverridePixels(structure, alphabeta, (short)epl.NumberOfFractions, originalArray, doseMatrix, scaling, Xsize, Ysize, Zsize,
                     Xres, Yres, Zres, Xdir, Ydir, Zdir, doseOrigin, CalculateEQDn, dosePerFraction);
                        break;
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

            if (!preview)
            {
                CreatePlanAndAddDose(Xsize, Ysize, Zsize, doseMatrix, maxDoseVal, newPlan, epl);
                return new int[0, 0, 0];
            }
            else
            {
                return doseMatrix;
            }
        }

        public void CreatePlanAndAddDose(int Xsize, int Ysize, int Zsize, int[,,] doseMatrix, double doseMaxOriginal, ExternalPlanSetup newPlan, ExternalPlanSetup thisPlan)
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

            EvaluationDose evalDose = newPlan.CopyEvaluationDose(thisPlan.Dose);

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
            double Xres, double Yres, double Zres, VVector Xdir, VVector Ydir, VVector Zdir, VVector doseOrigin, calculateFunction functionCalculate, double? nOut = null)
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
                            if (nOut != null)
                                doseMatrixOut[k, imin + p, j] = functionCalculate(dose, alphabeta, scaling, numFractions, nOut);
                            else
                                doseMatrixOut[k, imin + p, j] = functionCalculate(dose, alphabeta, scaling, numFractions);
                            // this.existingIndexes.Add(newIndices); // allow overwriting as I'm controlling the order from the list
                        }
                    }
                }
            }
        }


        public int CalculateEQD2(int dose, double alphabeta, double scaling, short numberOfFractions, double? dFraction = null)
        {
            return Convert.ToInt32((dose * (alphabeta + dose * scaling / numberOfFractions) / (alphabeta + 2.0)));
        }
        public int CalculateEQDn(int dose, double alphabeta, double scaling, short numberOfFractions, double? dFraction = null)
        {
            return Convert.ToInt32((dose * (alphabeta + dose * scaling / numberOfFractions) / (alphabeta + dFraction)));
        }

        public int CalculateBED(int dose, double alphabeta, double scaling, short numberOfFractions, double? dFraction = null)
        {
            return Convert.ToInt32(dose * (1 + dose * scaling / (numberOfFractions * alphabeta)));
        }

        public int MultiplyByAlphaBeta(int dose, double alphabeta, double scaling)
        {
            return Convert.ToInt32(dose * alphabeta);
        }

    }
}
