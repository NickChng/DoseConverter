using ESAPIScript;
using OxyPlot.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using System.Xml.Serialization;
using VMS.TPS.Common.Model.API;

namespace EQD2Converter
{

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
        //public StructureSet AuxStructureSet; // auxiliary structureset

        public bool working { get; private set; } = false;

        public ViewModel() { }
        public ViewModel(EsapiWorker ew = null)
        {
            _ew = ew;
            _ui = Dispatcher.CurrentDispatcher;
            Initialize();
        }

        private async void Initialize()
        {
            try
            {
                // Read Script Configuration
                
                _dataPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                GetScriptConfigFromXML(); // note this will fail unless this config file is defined
                // Initialize other GUI settings
                _model = new Model(_scriptConfig, _ew);
                await _model.InitializeModel();
                // Get structures in plan
                AlphaBetaMappings.Clear(); // Clear default design settings;
                foreach (var s in _model.AlphaBetaMapping)
                {
                    AlphaBetaMappings.Add(new AlphaBetaMapping(s.Key, s.Value, await _model.GetStructureLabel(s.Key)));
                }
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
                MessageBox.Show(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
            }
        }

        public ICommand button1Command
        {
            get
            {
                return new DelegateCommand(ConvertDose);
            }
        }

        private async void ConvertDose(object param=null)
        {
            int[,,] convdose = new int[0, 0, 0];
            //var waitWindow = new WaitingWindow();
            //// Show waiting window temporarily
            //this.Cursor = Cursors.Wait;
            //waitWindow.Show();
            try
            {
                convdose = await _model.GetConvertedDose("TestPlan");
            }
            catch (Exception f)
            {
                //waitWindow.Close();
                //this.Cursor = null;
                MessageBox.Show(f.Message + "\n" + f.StackTrace, "Error");
                return;
            }
            //waitWindow.Close();
            //this.Cursor = null;
            MessageBox.Show("A new verification plan was created with a modified dose distribution.\n\n" +
                  "Voxel value to dose scaling factor (original dose): " + _model.scaling.ToString() + "\n" +
                  "Voxel value to dose scaling factor (evaluation dose): " + _model.scaling2.ToString() + "\n" +
                  "Voxel value to voxel value scaling factor (evaluation dose): " + _model.scaling3.ToString(), "Message");


            Tuple<int, int> minMaxConverted = Helpers.GetMinMaxValues(convdose, convdose.GetLength(1), convdose.GetLength(2), convdose.GetLength(0));
            //PreviewWindow previewWindow = new PreviewWindow(this.scriptcontext, convdose, this.originalArray,
            //    this.scaling, this.doseMin, this.doseMax, minMaxConverted.Item1, minMaxConverted.Item2);
            //previewWindow.ShowDialog();
        }

        //private void Button_Click_2(object sender, RoutedEventArgs e)
        //{
        //    HelpWindow helpwindow = new HelpWindow();
        //    helpwindow.Owner = this;
        //    helpwindow.Show();
        //}

        public void GetScriptConfigFromXML()
        {
            working = true;
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
            working = false;
        }
    }
}
