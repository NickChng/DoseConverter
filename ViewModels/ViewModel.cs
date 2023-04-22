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
using System.Net.Http.Headers;
using System.Windows.Media.Animation;
using System.Drawing;

namespace EQD2Converter
{

    public class ViewModel : ObservableObject
    {
        private EsapiWorker _ew = null;
        private Dispatcher _ui = null;
        private string _dataPath = string.Empty;
        private Model _model;
        private EQD2ConverterConfig _scriptConfig;
        private OnlineHelpDefinitions _onlineHelpDefinitions;
        public StructureViewModel SelectedMapping { get; set; } = new StructureViewModel() { StructureId = "Design", AlphaBetaRatio = 3, StructureLabel = "Design" };
        public ObservableCollection<StructureViewModel> StructureDefinitions { get; private set; } = new ObservableCollection<StructureViewModel>() { new StructureViewModel() { StructureId = "Design", AlphaBetaRatio = 3, StructureLabel = "Design" } };

        public bool WasStructureSetCreated = false; // set to true when adding margins to structures

        public string n2html
        {
            get
            {
                return @"<i>n2</i> fx";
            }
        }
        private string _convertedPlanName = "Design";
        public string ConvertedPlanName
        {
            get { return _convertedPlanName; }
            set
            {
                _convertedPlanName = value.Substring(0, Math.Min(value.Length, 13));
                _isPlanNameValid = false;
                _conversionComplete = false;
                ValidatePlanName(_convertedPlanName);
            }
        }
        private bool _isPlanNameValid = true;

        public SolidColorBrush ConvertedPlanNameBackgroundColor { get; set; } = new SolidColorBrush(Colors.Transparent);
        private async void ValidatePlanName(string proposedPlanName)
        {
            if (_model != null)
            {
                _isPlanNameValid = await _model.ValidatePlanName(proposedPlanName);
                if (!_isPlanNameValid)
                {
                    DisplayScriptError("This plan already exists, please choose a different plan Id.");
                    ConvertedPlanNameBackgroundColor = new SolidColorBrush(Colors.DarkOrange);
                }
                else
                {
                    DisplayScriptReady();
                    ConvertedPlanNameBackgroundColor = new SolidColorBrush(Colors.Transparent);
                }
                RaisePropertyChangedEvent(nameof(ConvertedPlanNameBackgroundColor));
                RaisePropertyChangedEvent(nameof(StartButtonVisibility));
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
                        StructureDefinitions = new ObservableCollection<StructureViewModel>(StructureDefinitions.OrderByDescending(x => x.Include).ThenBy(x => x.AlphaBetaRatio).ThenBy(x => x.StructureId));
                        break;
                    case AlphaBetaSortFormat.Descending:
                        StructureDefinitions = new ObservableCollection<StructureViewModel>(StructureDefinitions.OrderByDescending(x => x.Include).ThenByDescending(x => x.AlphaBetaRatio).ThenBy(x => x.StructureId));
                        break;
                }
            }
        }

        public ObservableCollection<AlphaBetaSortFormat> AlphaBetaSortFormatOptions { get; private set; } = new ObservableCollection<AlphaBetaSortFormat>() { AlphaBetaSortFormat.Ascending, AlphaBetaSortFormat.Descending };

        public ObservableCollection<PlanSelectionViewModel> PlanInputOptions { get; private set; } = new ObservableCollection<PlanSelectionViewModel> { new PlanSelectionViewModel("designPlan", "designCourse", "designSS", false) };

        private PlanSelectionViewModel _selectedInputOption;

        public bool NoFatalErrorOccurred
        {
            get
            {
                return !_fatalError;
            }
        }
        public PlanSelectionViewModel SelectedInputOption
        {
            get { return _selectedInputOption; }
            set
            {
                _selectedInputOption = value;
                SetDefaultPlanName();
                if (_selectedInputOption.IsSum)
                {
                    UpdateStructureData(_selectedInputOption.SsId);
                    DoseOutputFormatOptions.Clear();
                    DoseOutputFormatOptions.Add(DoseFormat.Base);
                    SelectedOutputFormat = DoseFormat.Base;
                }
                else
                {
                    DoseOutputFormatOptions.Clear();
                    foreach (var format in _doseOutputFormatOptionDefaults)
                    {
                        DoseOutputFormatOptions.Add(format);
                    }
                    SelectedOutputFormat = DoseFormat.None;
                }
                RaisePropertyChangedEvent(nameof(SelectedOutputFormat));
                RaisePropertyChangedEvent(nameof(StartButtonVisibility));
            }
        }
        public bool DesignTime { get; private set; } = true;

        public string ConversionInputWarning { get; private set; } = "Design";
        public bool isDoseSelectionInfoOpen { get; set; } = false;

        public bool isConvertToInfoOpen { get; set; } = false;


        public DescriptionViewModel DoseDescriptionViewModel { get; private set; } = new DescriptionViewModel("Default", "Design");
        public DescriptionViewModel ConvertToDescriptionViewModel { get; private set; } = new DescriptionViewModel("Default", "Design");

        public DescriptionViewModel MaxDoseInfoDescriptionViewModel { get; private set; } = new DescriptionViewModel("Default", "Design");
        public DescriptionViewModel IncludeEdgesInfoDescriptionViewModel { get; private set; } = new DescriptionViewModel("Default", "Design");

        public Visibility ShowN2Fractions
        {
            get
            {
                switch (_selectedOutputFormat)
                {
                    case DoseFormat.iBEDn2:
                        return Visibility.Visible;
                    case DoseFormat.Base:
                        return Visibility.Visible;
                    default:
                        if (DesignTime)
                            return Visibility.Visible;
                        else
                            return Visibility.Collapsed;
                }
            }
        }

        private bool _conversionComplete = false;
        public Visibility StartButtonVisibility
        {
            get
            {
                if (_selectedInputOption != null
                    && _isPlanNameValid
                    && _selectedOutputFormat != DoseFormat.None
                    && !string.IsNullOrEmpty(ConvertedPlanName)
                    && !_conversionComplete
                    && isConvParameterValid()
                    && !_fatalError)
                    return Visibility.Visible;
                else
                    return Visibility.Collapsed;
            }
        }
        public Visibility ShowMaxEQD2
        {
            get
            {
                if (_selectedOutputFormat == DoseFormat.Base || DesignTime)
                {
                    return Visibility.Visible;
                }
                else return Visibility.Collapsed;
            }
        }
        public int SelectedIndex { get; set; }

        private double? _convParameter = null;
        private string _convParameterString;
        public string convParameterString
        {
            get
            {
                return _convParameterString;
            }
            set
            {
                _conversionComplete = false;
                if (value == null || value == string.Empty)
                {
                    _convParameter = null;
                    _convParameterString = string.Empty;
                    RaisePropertyChangedEvent(nameof(convParameterString));
                }
                else
                {
                    switch (_selectedOutputFormat)
                    {
                        case DoseFormat.iBEDn2:
                            if (int.TryParse(value, out int intVal))
                            {
                                _convParameter = intVal;
                                _convParameterString = intVal.ToString();
                                SetDefaultPlanName();
                            }
                            else
                            {
                                _convParameter = null;
                                _convParameterString = string.Empty;
                            }
                            break;
                        case DoseFormat.EQDd:
                            if (double.TryParse(value, out double doubleVal))
                            {
                                _convParameter = doubleVal;
                                _convParameterString = doubleVal.ToString();
                                SetDefaultPlanName();
                            }
                            else
                            {
                                _convParameter = null;
                                _convParameterString = string.Empty;
                            }
                            break;
                        case DoseFormat.Base:
                            if (int.TryParse(value, out intVal))
                            {
                                _convParameter = intVal;
                                _convParameterString = intVal.ToString();
                                SetDefaultPlanName();
                                foreach (var abm in StructureDefinitions)
                                {
                                    abm.n2 = ushort.Parse(value);
                                }
                                RaisePropertyChangedEvent(nameof(DisplayMaxGy));
                            }
                            else
                            {
                                _convParameter = null;
                                _convParameterString = string.Empty;
                                foreach (var abm in StructureDefinitions)
                                {
                                    abm.n2 = 0;
                                }
                                RaisePropertyChangedEvent(nameof(DisplayMaxGy));
                            }
                            break;
                    }
                }
                RaisePropertyChangedEvent(nameof(StartButtonVisibility));
                RaisePropertyChangedEvent(nameof(convParameterTextBoxStatusColor));
            }
        }

        public SolidColorBrush convParameterTextBoxStatusColor
        {
            get
            {
                if (isConvParameterValid())
                    return new SolidColorBrush(Colors.White);
                else
                    return new SolidColorBrush(Colors.DarkOrange);
            }
        }
        private bool isConvParameterValid()
        {
            switch (_selectedOutputFormat)
            {
                case DoseFormat.iBEDn2:
                    {
                        if (_convParameter == null)
                            return false;
                        else
                        {
                            if (_convParameter % 1 == 0) // int check
                                return true;
                            else return false;
                        }
                    }
                case DoseFormat.Base:
                    if (_convParameter == null)
                        return false;
                    else
                    {
                        if (_convParameter % 1 == 0) // int check
                            return true;
                        else return false;
                    }
                default:
                    {
                        return true;
                    }
            }

        }
        public string DisplayMaxGy
        {
            get
            {
                if (_convParameter != null)
                    return string.Format("Eqv. in {0}#", _convParameter);
                else
                    return string.Empty;
            }
        }
        private DoseFormat _selectedOutputFormat = DoseFormat.None;
        public DoseFormat SelectedOutputFormat
        {
            get { return _selectedOutputFormat; }
            set
            {
                _selectedOutputFormat = value;
                _conversionComplete = false;
                convParameterString = string.Empty;
                if (_model != null)
                {
                    SetDefaultPlanName();
                }
                switch (_selectedOutputFormat)
                {
                    case DoseFormat.iBEDn2:
                        ConversionInputWarning = "Ensure source distribution has units of physical dose. Plan fractionation will be used to determined BEDn2.";
                        break;
                    case DoseFormat.EQD2:
                        ConversionInputWarning = "Ensure source distribution has units of physical dose. Plan fractionation will be used to determined EQD2.";
                        break;
                    case DoseFormat.Base:
                        ConversionInputWarning = "Ensure source distribution has units of EQD2. Input plan fractionation and total dose is ignored.";
                        break;
                }
                RaisePropertyChangedEvent(nameof(convParameterTextBoxStatusColor));
                RaisePropertyChangedEvent(nameof(ShowN2Fractions));
                RaisePropertyChangedEvent(nameof(ShowMaxEQD2));
                RaisePropertyChangedEvent(nameof(StartButtonVisibility));
            }
        }

        private static List<DoseFormat> _doseOutputFormatOptionDefaults = new List<DoseFormat>() { DoseFormat.EQD2, DoseFormat.iBEDn2, DoseFormat.BED, DoseFormat.Base };
        private bool _fatalError = false;

        public ObservableCollection<DoseFormat> DoseOutputFormatOptions { get; private set; } = new ObservableCollection<DoseFormat>(_doseOutputFormatOptionDefaults);
        public bool Working { get; set; } = true;

        public string StatusMessage { get; set; } = "Loading...";
        public string StatusDetails { get; set; } = "";
        public SolidColorBrush StatusColor { get; set; } = new SolidColorBrush(Colors.PapayaWhip);
        public Visibility SuccessVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility ErrorVisibility { get; private set; } = Visibility.Collapsed;

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
            switch (SelectedOutputFormat)
            {
                case DoseFormat.EQD2:
                    ConvertedPlanName = fullPlanName.Substring(0, Math.Min(fullPlanName.Length, 9)) + "_" + SelectedOutputFormat.Display();
                    break;
                case DoseFormat.iBEDn2:
                    ConvertedPlanName = string.Format("{0}_{1}fx", fullPlanName.Substring(0, Math.Min(fullPlanName.Length, 9)), _convParameter);
                    break;
                case DoseFormat.Base:
                    ConvertedPlanName = string.Format("{0}_{1}fxB", fullPlanName.Substring(0, Math.Min(fullPlanName.Length, 9)), _convParameter);
                    break;
                case DoseFormat.BED:
                    ConvertedPlanName = fullPlanName.Substring(0, Math.Min(fullPlanName.Length, 9)) + "_" + SelectedOutputFormat.Display();
                    break;
                default:
                    ConvertedPlanName = fullPlanName.Substring(0, Math.Min(fullPlanName.Length, 9)) + "_" + "eval";
                    break;
            }
        }
        private void ClearDesignParameters()
        {
            DesignTime = false;
            ConversionInputWarning = "";
            StructureDefinitions.Clear();
            ConvertedPlanName = "";
            PlanInputOptions.Clear();
        }

        public async void Initialize()
        {
            try
            {
                // Read Script Configuration
                Working = true;
                var AssemblyLocation = Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(AssemblyLocation))
                    AssemblyLocation = AppDomain.CurrentDomain.BaseDirectory;
                var AssemblyPath = Path.GetDirectoryName(AssemblyLocation);
                _dataPath = AssemblyPath;
                Helpers.SeriLog.LogInfo("Resolved script path...");
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
                DisplayScriptError("Error resolving script path, please contact your Eclipse administrator.", ex.Message, true);
                return;
            }
            try
            {
                GetScriptConfigFromXML(); // note this will fail unless this config file is defined
                                          // Initialize other GUI settings
                Helpers.SeriLog.LogInfo("Loaded script configuration data...");
            }
            catch (Exception ex)
            {
                DisplayScriptError("Error loading script configuration file, please contact your Eclipse administrator.", ex.Message, true);
                return;
            }
            try
            {
                InitializeOnlineHelp(); // note this will fail unless this config file is defined
                                        // Initialize other GUI settings
                Helpers.SeriLog.LogInfo("Initialized online help...");
            }
            catch (Exception ex)
            {
                DisplayScriptError("Error loading online help file, please contact your Eclipse administrator.", ex.Message);
                return;
            }
            try
            {
                _model = new Model(_scriptConfig, _ew);
                await _model.InitializeModel();
                Helpers.SeriLog.LogInfo("Initialized ESAPI model...");
            }
            catch (Exception ex)
            {
                DisplayScriptError("Error initializing ESAPI, please contact your Eclipse administrator.", ex.Message, true);
                return;
            }
            // Get structures in plan
            try
            {
                UpdateStructureData();
                Helpers.SeriLog.LogInfo("Structure configuration and settings updated...");
            }
            catch (Exception ex)
            {
                DisplayScriptError("Error loading structures from plan, please contact your Eclipse administrator.", ex.Message, true);
                return;
            }
            // Get plans in context
            try
            {
                var plansInContext = await _model.GetPlans();
                _ui.Invoke(() =>
                {
                    foreach (var p in plansInContext)
                        PlanInputOptions.Add(new PlanSelectionViewModel(p.Item2, p.Item1, p.Item3, p.Item4));
                    SelectedInputOption = PlanInputOptions.FirstOrDefault();
                    SetDefaultPlanName(); // This needs to be in the invoke due to threading
                });
                Helpers.SeriLog.LogInfo("Loaded available source distributions...");
            }
            catch (Exception ex)
            {
                DisplayScriptError("Error loading plans, please contact your Eclipse administrator.");
                Helpers.SeriLog.LogError("Error details", ex);
                return;
            }
            if (!_fatalError)
            {
                DisplayScriptReady();
                Working = false;
                Helpers.SeriLog.LogInfo("Initialization complete!");
            }
        }

        private void DisplayScriptComplete(string message = "Conversion complete!")
        {
            StatusMessage = message;
            StatusDetails = "No errors or warnings.";
            StatusColor = new SolidColorBrush(Colors.Transparent);
            _conversionComplete = true;
            Working = false;
            SuccessVisibility = Visibility.Visible;
            ErrorVisibility = Visibility.Collapsed;
            RaisePropertyChangedEvent(nameof(StartButtonVisibility));
        }
        private void DisplayScriptError(string message, string details = "", bool fatalError = false)
        {
            StatusMessage = message;
            StatusDetails = details;
            _fatalError = fatalError;
            Working = false;
            _conversionComplete = false;
            ErrorVisibility = Visibility.Visible;
            SuccessVisibility = Visibility.Collapsed;
            RaisePropertyChangedEvent(nameof(StartButtonVisibility));
        }
        private void DisplayScriptReady()
        {
            StatusMessage = "Ready to convert...";
            Working = false;
            _conversionComplete = false;
            SuccessVisibility = Visibility.Collapsed;
            ErrorVisibility = Visibility.Collapsed;
        }
        private void InitializeOnlineHelp()
        {
            try
            {
                XmlSerializer Ser = new XmlSerializer(typeof(OnlineHelpDefinitions));
                var helpFile = Path.Combine(_dataPath, @"OnlineHelp\OnlineHelp.xml");
                using (StreamReader help = new StreamReader(helpFile))
                {
                    try
                    {
                        _onlineHelpDefinitions = (OnlineHelpDefinitions)Ser.Deserialize(help);
                    }
                    catch (Exception ex)
                    {
                        Helpers.SeriLog.LogFatal(string.Format("Unable to deserialize online help file: {0}\r\n", helpFile), ex);
                        MessageBox.Show(string.Format("Unable to read online help file {0}\r\n\r\nDetails: {1}", helpFile, ex.InnerException));

                    }
                }

                DoseDescriptionViewModel = new DescriptionViewModel(_onlineHelpDefinitions.Definitions.FirstOrDefault(x => string.Equals(x.DefinitionId, "Source dose", StringComparison.OrdinalIgnoreCase)));
                ConvertToDescriptionViewModel = new DescriptionViewModel(_onlineHelpDefinitions.Definitions.FirstOrDefault(x => string.Equals(x.DefinitionId, "Convert to", StringComparison.OrdinalIgnoreCase)));
                MaxDoseInfoDescriptionViewModel = new DescriptionViewModel(_onlineHelpDefinitions.Definitions.FirstOrDefault(x => string.Equals(x.DefinitionId, "Max Equivalent Dose", StringComparison.OrdinalIgnoreCase)));
                IncludeEdgesInfoDescriptionViewModel = new DescriptionViewModel(_onlineHelpDefinitions.Definitions.FirstOrDefault(x => string.Equals(x.DefinitionId, "Include structure edges", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                string errorMesssage = "Unable to find/open online help file";
                Helpers.SeriLog.LogFatal(errorMesssage, ex);
                throw new Exception(errorMesssage);
            }
        }

        private async void UpdateStructureData(string ssId = null)
        {
            try
            {
                List<StructureViewModel> unsortedMappings = new List<StructureViewModel>();
                if (ssId != null)
                    unsortedMappings = await _model.GetStructureDefinitions(ssId);
                else
                    unsortedMappings = await _model.GetStructureDefinitions();
                _ui.Invoke(() =>
                {
                    StructureDefinitions.Clear();
                    foreach (var mapping in unsortedMappings.OrderByDescending(x => x.Include).ThenBy(x => x.AlphaBetaRatio).ThenBy(x => x.StructureId))
                    {
                        //mapping.PropertyChanged += Mapping_PropertyChanged;
                        StructureDefinitions.Add(mapping);
                    }
                });
            }
            catch (Exception ex)
            {
                string errorMessage = "Error updating structure mappings in UpdateMapping()";
                Helpers.SeriLog.LogError(errorMessage, ex);
                throw new Exception(errorMessage);
            }
        }
        private void Mapping_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(StructureViewModel.Include):
                    {
                        StructureDefinitions = new ObservableCollection<StructureViewModel>(StructureDefinitions.OrderByDescending(x => x.Include).ThenBy(x => x.StructureId));
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
            SuccessVisibility = Visibility.Collapsed;
            ErrorVisibility = Visibility.Collapsed;
            StatusColor = new SolidColorBrush(Colors.Transparent);
            StatusMessage = "Converting...";
            int[,,] convdose = new int[0, 0, 0];
            bool success = true;
            string handledExceptionMessage = "";
            string unhandledExceptionMessage = "No further details.";
            try
            {
                (convdose, success, handledExceptionMessage) = await _model.GetConvertedDose(SelectedInputOption.CourseId, SelectedInputOption.Id, SelectedInputOption.IsSum, ConvertedPlanName, StructureDefinitions.ToList(), SelectedOutputFormat, _convParameter);

            }
            catch (Exception ex)
            {
                success = false;
                unhandledExceptionMessage = ex.Message;
            }
            if (success)
            {
                DisplayScriptComplete();
            }
            else
            {
                DisplayScriptError(handledExceptionMessage, unhandledExceptionMessage);
            }
        }

        public ICommand ConvertToInfoButtonCommand
        {
            get
            {
                return new DelegateCommand(ToggleConvertToInfo);
            }
        }

        private void ToggleConvertToInfo(object param = null)
        {
            isConvertToInfoOpen ^= true;
        }

        public ICommand MaxDoseInfoButtonCommand
        {
            get
            {
                return new DelegateCommand(ToggleMaxDoseInfo);
            }
        }

        private void ToggleMaxDoseInfo(object param = null)
        {
            isMaxDoseInfoOpen ^= true;
        }

        public bool isMaxDoseInfoOpen { get; set; }

        public ICommand IncludeEdgesInfoButtonCommand
        {
            get
            {
                return new DelegateCommand(ToggleIncludeEdgesInfo);
            }
        }

        private void ToggleIncludeEdgesInfo(object param = null)
        {
            isIncludeEdgesInfoOpen ^= true;
        }

        public bool isIncludeEdgesInfoOpen { get; set; }



        public ICommand DoseSelectionInfoButtonCommand
        {
            get
            {
                return new DelegateCommand(ToggleDoseSelectionInfo);
            }
        }

        private void ToggleDoseSelectionInfo(object param = null)
        {
            isDoseSelectionInfoOpen ^= true;
        }

        public ICommand button_ToggleAlphaBetaOrderCommand
        {
            get
            {
                return new DelegateCommand(ToggleAlphaBetaOrder);
            }
        }

        private void ToggleAlphaBetaOrder(object param = null)
        {
            if (SelectedAlphaBetaSortFormat == AlphaBetaSortFormat.Ascending)
                SelectedAlphaBetaSortFormat = AlphaBetaSortFormat.Descending;
            else
                SelectedAlphaBetaSortFormat = AlphaBetaSortFormat.Ascending;
        }

        public void GetScriptConfigFromXML()
        {
            try
            {
                XmlSerializer Ser = new XmlSerializer(typeof(EQD2ConverterConfig));
                var configFile = Path.Combine(_dataPath, @"Configuration\EQD2ConverterConfig.xml");
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
                string errorMessage = "Unable to find/open config file";
                Helpers.SeriLog.LogFatal(errorMessage, ex);
                throw new Exception(errorMessage);
            }
        }
    }
}
