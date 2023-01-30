using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using VMS.TPS.Common.Model.API;
using VMS.TPS.Common.Model.Types;

namespace EQD2Converter
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        ScriptContext scriptcontext;

        public List<DataGridStructures> DataGridStructuresList = new List<DataGridStructures>() { };
        public ListCollectionView DataGridStructuresCollection { get; set; }

      

        public bool WasStructureSetCreated = false; // set to true when adding margins to structures
        public StructureSet AuxStructureSet; // auxiliary structureset

        public MainWindow(ScriptContext scriptcontext)
        {
            this.scriptcontext = scriptcontext;
            this.numberOfFractions = (int)scriptcontext.ExternalPlanSetup.NumberOfFractions;
            this.AuxStructureSet = (StructureSet)null;

            InitializeComponent();

            this.ComboBox.ItemsSource = new List<string> { "Ascending", "Descending" };
            this.ComboBox.SelectedIndex = 0;

            this.ComboBox2.ItemsSource = new List<string> { "EQD2", "BED" , "Multiply by a/b"};
            this.ComboBox2.SelectedIndex = 0;

            PopulateDataGrid();

            DetermineMargin();

        }

        public class DataGridStructures
        {
            public string Structure { get; set; }
            public string AlphaBeta { get; set; }
        }


        public void PopulateDataGrid()
        {
            List<DataGridStructures> datagrid = new List<DataGridStructures>() { };

            foreach (var structure in scriptcontext.StructureSet.Structures.OrderBy(u => u.Id).ToList())
            {
                if (!structure.IsEmpty & structure.DicomType != "SUPPORT" & structure.DicomType != "MARKER" & structure.DicomType != "BOLUS")
                {
                    DataGridStructures item = new DataGridStructures()
                    {
                        Structure = structure.Id,
                        AlphaBeta = "",
                    };
                    datagrid.Add(item);
                }
            }

            this.DataGridStructuresList = datagrid;
            ListCollectionView collectionView = new ListCollectionView(this.DataGridStructuresList);
            collectionView.GroupDescriptions.Add(new PropertyGroupDescription("Structures"));
            this.DataGridStructuresCollection = collectionView;
            this.DataGrid1.ItemsSource = this.DataGridStructuresCollection;
        }


        private double ConvertTextToDouble(string text)
        {
            if (Double.TryParse(text, out double result))
            {
                return result;
            }
            else
            {
                return Double.NaN;
            }
        }

        private void OnTextChanged(object sender, TextChangedEventArgs e)
        {
            TextBox txt = sender as TextBox;
            int ind = txt.CaretIndex;
            txt.Text = txt.Text.Replace(",", ".");
            txt.CaretIndex = ind;
        }

        private void DataGrid_SourceUpdated(object sender, DataTransferEventArgs e)
        {

        }

    

        private void Button_Click_1(object sender, RoutedEventArgs e)
        {
            int[,,] convdose = new int[0, 0, 0];
            var waitWindow = new WaitingWindow();
            // Show waiting window temporarily
            this.Cursor = Cursors.Wait;
            waitWindow.Show();
            try
            {
                convdose = ConvertDose((ExternalPlanSetup)null, true);
            }
            catch (Exception f)
            {
                waitWindow.Close();
                this.Cursor = null;
                MessageBox.Show(f.Message + "\n" + f.StackTrace, "Error");
                return;
            }
            waitWindow.Close();
            this.Cursor = null;

            Tuple<int, int> minMaxConverted = GetMinMaxValues(convdose, convdose.GetLength(1), convdose.GetLength(2), convdose.GetLength(0));
            PreviewWindow previewWindow = new PreviewWindow(this.scriptcontext, convdose, this.originalArray,
                this.scaling, this.doseMin, this.doseMax, minMaxConverted.Item1, minMaxConverted.Item2);
            previewWindow.ShowDialog();
        }

        private void Button_Click_2(object sender, RoutedEventArgs e)
        {
            HelpWindow helpwindow = new HelpWindow();
            helpwindow.Owner = this;
            helpwindow.Show();
        }

        public void DetermineMargin()
        {
            //determine margin for structures from dose voxel size
            double dx = this.scriptcontext.ExternalPlanSetup.Dose.XRes;
            double dy = this.scriptcontext.ExternalPlanSetup.Dose.YRes;
            double dz = this.scriptcontext.ExternalPlanSetup.Dose.ZRes;
            this.ForceConversionMargin.Text = new List<double>() { dx, dy, dz }.Max().ToString();
        }

        private void ForceConversionCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if ((bool)this.ForceConversionCheckBox.IsChecked)
            {
                this.ForcedConversionLabel.Text = "Warning. An auxiliary structure set will be created. The plan and the original structure set" +
                    " will be left untouched. After conversion you must manually delete the new structure set/image.";
                this.ForcedConversionLabel.Foreground = Brushes.Red;
                this.ForceConversionMarginStackPanel.Visibility = Visibility.Visible;
            }
            else
            {
                this.ForcedConversionLabel.Text = "";
                this.ForceConversionMarginStackPanel.Visibility = Visibility.Hidden;
            }
        }

        private void ForceConversionMargin_TextChanged(object sender, TextChangedEventArgs e)
        {
            TextBox txt = sender as TextBox;
            int ind = txt.CaretIndex;
            txt.Text = txt.Text.Replace(",", ".");
            txt.CaretIndex = ind;
        }
    }
}