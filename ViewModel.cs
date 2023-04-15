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

namespace EQD2Converter
{
    public class DescriptionViewModel
    {
        public string Id { get; set; }
        public string Description { get; set; } = "Default Description";

        public DescriptionViewModel(string id, string description)
        {
            Id = id;
            Description = description;
        }
        public DescriptionViewModel(OnlineHelpDefinitionsDefinition definition)
        {
            if (definition != null)
            {
                Id = definition.DefinitionId;
                Description = definition.Text;
            }
            else
            {
                Description = "No online help";
            }

        }

    }
    public class PlanSelectionView : ObservableObject
    {
        public string Id { get; set; }
        public string CourseId { get; set; }
        public string SsId { get; set; }
        public bool IsSum { get; set; }
        public string DisplayString
        {
            get
            {
                if (IsSum)
                    return string.Format(@"{0}/{1} [sum]", CourseId, Id);
                else
                    return string.Format(@"{0}/{1}", CourseId, Id);
            }
        }
        public PlanSelectionView()
        {

        }
        public PlanSelectionView(string id, string courseId, string ssId, bool isSum)
        {
            Id = id;
            CourseId = courseId;
            SsId = ssId;
            IsSum = isSum;
        }
    }
    public enum DoseFormat
    {
        [Description("None")] None,
        [Description("EQD2")] EQD2,
        [Description("BED")] BED,
        [Description("EQDd")] EQDd,
        [Description("BEDn2")] BEDn2,
        [Description("BASE")] Base,
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
        private OnlineHelpDefinitions _onlineHelpDefinitions;
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

        public ObservableCollection<PlanSelectionView> PlanInputOptions { get; private set; } = new ObservableCollection<PlanSelectionView> { new PlanSelectionView("designPlan", "designCourse", "designSS", false) };

        private PlanSelectionView _selectedInputOption;

        public PlanSelectionView SelectedInputOption
        {
            get { return _selectedInputOption; }
            set
            {
                _selectedInputOption = value;
                SetDefaultPlanName();
                if (_selectedInputOption.IsSum)
                {
                    UpdateMapping(_selectedInputOption.SsId);
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
            }
        }
        public bool DesignTime { get; private set; } = true;

        public string ConversionInputWarning { get; private set; } = "Design";
        public bool isDoseSelectionInfoOpen { get; set; } = false;

        public bool isConvertToInfoOpen { get; set; } = false;


        public DescriptionViewModel DoseDescriptionViewModel { get; private set; } = new DescriptionViewModel("Default", "Design");
        public DescriptionViewModel ConvertToDescriptionViewModel { get; private set; } = new DescriptionViewModel("Default", "Design");

        public DescriptionViewModel MaxDoseInfoDescriptionViewModel { get; private set; } = new DescriptionViewModel("Default", "Design");
        public Visibility ShowN2Fractions
        {
            get
            {
                switch (_selectedOutputFormat)
                {
                    case DoseFormat.BEDn2:
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
                if (_selectedInputOption != null && _selectedOutputFormat != DoseFormat.None && !string.IsNullOrEmpty(ConvertedPlanName) && !_conversionComplete && isConvParameterValid())
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
                        case DoseFormat.BEDn2:
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
                                foreach (var abm in AlphaBetaMappings)
                                {
                                    abm.n2 = ushort.Parse(value);
                                }
                                RaisePropertyChangedEvent(nameof(DisplayMaxGy));
                            }
                            else
                            {
                                _convParameter = null;
                                _convParameterString = string.Empty;
                                foreach (var abm in AlphaBetaMappings)
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
                case DoseFormat.BEDn2:
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
                    case DoseFormat.BEDn2:
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

        private static List<DoseFormat> _doseOutputFormatOptionDefaults = new List<DoseFormat>() { DoseFormat.EQD2, DoseFormat.BEDn2, DoseFormat.BED, DoseFormat.Base };
        public ObservableCollection<DoseFormat> DoseOutputFormatOptions { get; private set; } = new ObservableCollection<DoseFormat>(_doseOutputFormatOptionDefaults);
        public bool Working { get; set; } = true;

        public string StatusMessage { get; set; } = "Loading...";
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
                case DoseFormat.BEDn2:
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
            AlphaBetaMappings.Clear();
            ConvertedPlanName = "";
            PlanInputOptions.Clear();
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
                InitializeOnlineHelp();
                _model = new Model(_scriptConfig, _ew);
                await _model.InitializeModel();
                // Get structures in plan
                var ui2 = Dispatcher.CurrentDispatcher;
                UpdateMapping();
                // Get plans in context
                var plansInContext = await _model.GetPlans();
                foreach (var p in plansInContext)
                {
                    _ui.Invoke(() =>
                    {
                        PlanInputOptions.Add(new PlanSelectionView(p.Item2, p.Item1, p.Item3, p.Item4));
                    });
                }
                _ui.Invoke(() =>
                {
                    SelectedInputOption = PlanInputOptions.FirstOrDefault();
                });

                StatusMessage = "Ready...";
                SetDefaultPlanName();
                _ui.Invoke(() => Working = false);

            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogError(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
                MessageBox.Show(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
            }
        }

        private void InitializeOnlineHelp()
        {
            try
            {
                XmlSerializer Ser = new XmlSerializer(typeof(OnlineHelpDefinitions));
                var helpFile = Path.Combine(_dataPath, @"OnlineHelp.xml");
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
                MaxDoseInfoDescriptionViewModel = new DescriptionViewModel(_onlineHelpDefinitions.Definitions.FirstOrDefault(x=> string.Equals(x.DefinitionId, "Max Equivalent Dose", StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex)
            {
                Helpers.SeriLog.LogFatal(string.Format("Unable to find/open online help file"), ex);
                MessageBox.Show(string.Format("{0}\r\n{1}\r\n{2}", ex.Message, ex.InnerException, ex.StackTrace));
            }
        }

        private async void UpdateMapping(string ssId = null)
        {
            List<AlphaBetaMapping> unsortedMappings = new List<AlphaBetaMapping>();
            if (ssId != null)
                unsortedMappings = await _model.GetAlphaBetaMappings(ssId);
            else
                unsortedMappings = await _model.GetAlphaBetaMappings();
            _ui.Invoke(() =>
            {
                AlphaBetaMappings.Clear();
                foreach (var mapping in unsortedMappings.OrderByDescending(x => x.Include).ThenBy(x => x.AlphaBetaRatio).ThenBy(x => x.StructureId))
                {
                    //mapping.PropertyChanged += Mapping_PropertyChanged;
                    AlphaBetaMappings.Add(mapping);
                }
            });
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
            SuccessVisibility = Visibility.Collapsed;
            ErrorVisibility = Visibility.Collapsed;
            StatusColor = new SolidColorBrush(Colors.Transparent);
            StatusMessage = "Converting...";
            int[,,] convdose = new int[0, 0, 0];
            bool success = true;
            try
            {
                (convdose, success, StatusMessage) = await _model.GetConvertedDose(SelectedInputOption.CourseId, SelectedInputOption.Id, SelectedInputOption.IsSum, ConvertedPlanName, AlphaBetaMappings.ToList(), SelectedOutputFormat, _convParameter);

            }
            catch (Exception f)
            {
                StatusMessage = string.Format("Conversion Error");
                success = false;
            }
            if (!success)
            {
                StatusColor = new SolidColorBrush(Colors.Tomato);
                SuccessVisibility = Visibility.Collapsed;
                ErrorVisibility = Visibility.Visible;
            }
            else
            {
                StatusColor = new SolidColorBrush(Colors.Transparent);
                _conversionComplete = true;
                SuccessVisibility = Visibility.Visible;
                ErrorVisibility = Visibility.Collapsed;
            }
            Working = false;
            RaisePropertyChangedEvent(nameof(StartButtonVisibility));
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
