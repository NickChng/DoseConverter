using ESAPIScript;
using OxyPlot.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using System.Xml.Serialization;

namespace EQD2Converter
{
    public class ViewModel : ObservableObject
    {
        private EsapiWorker _ew = null;
        private Dispatcher _ui = null;
        private string _dataPath = string.Empty;

        public bool working { get; private set; } = false;

        public ViewModel(EsapiWorker ew = null)
        {
            _ew = ew;
            _ui = Dispatcher.CurrentDispatcher;
            Initialize()
        }

        private async void Initialize()
        {
            try
            {
                // Read Script Configuration
                _dataPath = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                GetScriptConfigFromXML(); // note this will fail unless this config file is defined
                // Initialize other GUI settings

                string ew_currentStructureSetId = string.Empty;

                structureIds.Clear();
                await ew.AsyncRunStructureContext((pat, ss) =>
                {
                    ew_currentStructureSetId = ss.Id;
                    ui.Invoke(() =>
                    {
                        currentStructureSetId = ew_currentStructureSetId;
                    });
                    foreach (string structureId in ss.Structures.Select(x => x.Id))
                    {
                        ui.Invoke(() =>
                        {
                            structureIds.Add(structureId);
                        });
                    }
                });
                //structureIds = ew_structureIds;
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
                MessageBox.Show(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
            }
        }

        public void GetScriptConfigFromXML()
        {
            working = true;
            try
            {
                XmlSerializer Ser = new XmlSerializer(typeof(ScriptConfig));
                var configFile = Path.Combine(_dataPath, @"ScriptConfig.xml");
                using (StreamReader config = new StreamReader(configFile))
                {
                    try
                    {
                        scriptConfig = (ScriptConfig)Ser.Deserialize(config);
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
