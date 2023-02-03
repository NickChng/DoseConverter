using ESAPIScript;
using OxyPlot.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Serialization;
using VMS.TPS.Common.Model.API;
using GongSolutions;

namespace EQD2Converter
{
    public enum DoseOutputFormat
    {
        [Description("EQD2")] EQD2,
        [Description("BED")] BED,
        [Description("EQDn")] EQDn
    }
    public enum AlphaBetaSortFormat
    {
        [Description("Ascending")] Ascending,
        [Description("Descending")] Descending
    }
    public class ViewModel : ObservableObject
    {
        private EsapiWorker _ew = null;
        private Dispatcher _ui = null;
        private string _dataPath = string.Empty;
        private Model _model;
        private EQD2ConverterConfig _scriptConfig;
        public AlphaBetaMapping SelectedMapping { get; set; } = new AlphaBetaMapping() { StructureId = "Design", AlphaBetaRatio = 3, StructureLabel = "Design" };
        public ObservableCollection<AlphaBetaMapping> AlphaBetaMappings { get; private set; } = new ObservableCollection<AlphaBetaMapping>() { new AlphaBetaMapping() { StructureId = "Design", AlphaBetaRatio = 3, StructureLabel = "Design" } };

        public bool WasStructureSetCreated = false; // set to true when adding margins to structures

        private string _convertedPlanName = "Design";
        public string ConvertedPlanName
        {
            get { return _convertedPlanName; }
            set
            {
                _convertedPlanName = value.Substring(0, Math.Min(value.Length, 13));
            }
        }

        private AlphaBetaSortFormat _selectedAlphaBetaSortFormat = AlphaBetaSortFormat.Ascending;
        public AlphaBetaSortFormat SelectedAlphaBetaSortFormat
        {
            get
            {
                return _selectedAlphaBetaSortFormat;
            }
            set
            {
                _selectedAlphaBetaSortFormat = value;
                switch (_selectedAlphaBetaSortFormat)
                {
                    case AlphaBetaSortFormat.Ascending:
                        AlphaBetaMappings = new ObservableCollection<AlphaBetaMapping>(AlphaBetaMappings.OrderByDescending(x => x.Include).ThenBy(x => x.AlphaBetaRatio).ThenBy(x => x.StructureId));
                        break;
                    case AlphaBetaSortFormat.Descending:
                        AlphaBetaMappings = new ObservableCollection<AlphaBetaMapping>(AlphaBetaMappings.OrderByDescending(x => x.Include).ThenByDescending(x => x.AlphaBetaRatio).ThenBy(x => x.StructureId));
                        break;
                }
            }
        }

        public ObservableCollection<AlphaBetaSortFormat> AlphaBetaSortFormatOptions { get; private set; } = new ObservableCollection<AlphaBetaSortFormat>() { AlphaBetaSortFormat.Ascending, AlphaBetaSortFormat.Descending };

        public bool IsEQDNSelected
        {
            get { if (_selectedOutputFormat == DoseOutputFormat.EQDn) return true; else return false; }
        }

        public int SelectedIndex { get; set; }
        public double DosePerFraction { get; set; } = 2;

        private DoseOutputFormat _selectedOutputFormat = DoseOutputFormat.EQD2;
        public DoseOutputFormat SelectedOutputFormat
        {
            get { return _selectedOutputFormat; }
            set
            {
                _selectedOutputFormat = value;
                if (_model != null)
                {
                    SetDefaultPlanName();
                }
                RaisePropertyChangedEvent(nameof(IsEQDNSelected));
            }
        }
        public ObservableCollection<DoseOutputFormat> DoseOutputFormatOptions { get; private set; } = new ObservableCollection<DoseOutputFormat>() { DoseOutputFormat.EQD2, DoseOutputFormat.BED, DoseOutputFormat.EQDn };
        public bool Working { get; set; } = true;

        public string StatusMessage { get; set; } = "Loading...";
        public SolidColorBrush StatusColor { get; set; } = new SolidColorBrush(Colors.PapayaWhip);
        public ViewModel() { }
        public ViewModel(EsapiWorker ew = null)
        {
            _ew = ew;
            _ui = Dispatcher.CurrentDispatcher;
            StatusColor = new SolidColorBrush(Colors.Transparent);
            ClearDesignParameters();
            Initialize();

        }

        private async void SetDefaultPlanName()
        {
            string fullPlanName = await _model.GetCurrentPlanName();
            ConvertedPlanName = fullPlanName.Substring(0, Math.Min(fullPlanName.Length, 9)) + "_" + SelectedOutputFormat.Display();
        }
        private void ClearDesignParameters()
        {
            AlphaBetaMappings.Clear();
            ConvertedPlanName = "";
        }

        private async void Initialize()
        {
            try
            {
                // Read Script Configuration
                _ui.Invoke(() => Working = true);
                var AssemblyLocation = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(AssemblyLocation))
                    AssemblyLocation = AppDomain.CurrentDomain.BaseDirectory;
                var AssemblyPath = Path.GetDirectoryName(AssemblyLocation);
                _dataPath = AssemblyPath;
                GetScriptConfigFromXML(); // note this will fail unless this config file is defined
                // Initialize other GUI settings
                _model = new Model(_scriptConfig, _ew);
                await _model.InitializeModel();
                // Get structures in plan
                var ui2 = Dispatcher.CurrentDispatcher;
                _ui.Invoke(() =>
                {
                    SetDefaultPlanName();
                    RaisePropertyChangedEvent(nameof(ConvertedPlanName));
                    foreach (var mapping in _model.GetAlphaBetaMappings().OrderByDescending(x => x.Include).ThenBy(x=>x.AlphaBetaRatio).ThenBy(x => x.StructureId))
                    {
                        mapping.PropertyChanged += Mapping_PropertyChanged;
                        AlphaBetaMappings.Add(mapping);
                    }
                });
                StatusMessage = "Ready...";
                _ui.Invoke(() => Working = false);

            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
                MessageBox.Show(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
            }
        }

        private void Mapping_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(AlphaBetaMapping.Include):
                    {
                        AlphaBetaMappings = new ObservableCollection<AlphaBetaMapping>(AlphaBetaMappings.OrderByDescending(x => x.Include).ThenBy(x => x.StructureId));
                        break;
                    }
                default:
                    break;
            }
        }

        public ICommand button1Command
        {
            get
            {
                return new DelegateCommand(ConvertDose);
            }
        }

        private async void ConvertDose(object param = null)
        {
            Working = true;
            StatusColor = new SolidColorBrush(Colors.Transparent);
            StatusMessage = "Converting...";
            int[,,] convdose = new int[0, 0, 0];
            bool success = true;
            try
            {
                (convdose, success, StatusMessage) = await _model.GetConvertedDose(ConvertedPlanName, AlphaBetaMappings.ToList(), SelectedOutputFormat, DosePerFraction);
                
            }
            catch (Exception f)
            {
                StatusMessage = string.Format("Conversion Error");
                success = false;
            }
            if (!success)
            {
                StatusColor = new SolidColorBrush(Colors.Tomato);
            }
            else
                StatusColor = new SolidColorBrush(Colors.PaleGreen);

            //MessageBox.Show("A new verification plan was created with a modified dose distribution.\n\n" +
            //      "Voxel value to dose scaling factor (original dose): " + _model.scaling.ToString() + "\n" +
            //      "Voxel value to dose scaling factor (evaluation dose): " + _model.scaling2.ToString() + "\n" +
            //      "Voxel value to voxel value scaling factor (evaluation dose): " + _model.scaling3.ToString(), "Message");


            //Tuple<int, int> minMaxConverted = Helpers.GetMinMaxValues(convdose, convdose.GetLength(1), convdose.GetLength(2), convdose.GetLength(0));

            //PreviewWindow previewWindow = new PreviewWindow(this.scriptcontext, convdose, this.originalArray,
            //    this.scaling, this.doseMin, this.doseMax, minMaxConverted.Item1, minMaxConverted.Item2);
            //previewWindow.ShowDialog();
            Working = false;
        }

        //private void Button_Click_2(object sender, RoutedEventArgs e)
        //{
        //    HelpWindow helpwindow = new HelpWindow();
        //    helpwindow.Owner = this;
        //    helpwindow.Show();
        //}

        public void GetScriptConfigFromXML()
        {
            try
            {
                XmlSerializer Ser = new XmlSerializer(typeof(EQD2ConverterConfig));
                var configFile = Path.Combine(_dataPath, @"EQD2ConverterConfig.xml");
                using (StreamReader config = new StreamReader(configFile))
                {
                    try
                    {
                        _scriptConfig = (EQD2ConverterConfig)Ser.Deserialize(config);
                    }
                    catch (Exception ex)
                    {
                        Helpers.SeriLog.LogFatal(string.Format("Unable to deserialize config file: {0}\r\n", configFile), ex);
                        MessageBox.Show(string.Format("Unable to read protocol file {0}\r\n\r\nDetails: {1}", configFile, ex.InnerException));

                    }
                }
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogFatal(string.Format("Unable to find/open config file"), ex);
                MessageBox.Show(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
            }
        }
    }
}
