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
using System.Windows.Documents;
using HTMLConverter;
using System.Windows.Controls;

namespace EQD2Converter
{

    public static class HtmlTextBoxProperties
    {
        public static string GetHtmlText(TextBlock wb)
        {
            return wb.GetValue(HtmlTextProperty) as string;
        }
        public static void SetHtmlText(TextBlock wb, string html)
        {
            wb.SetValue(HtmlTextProperty, html);
        }
        public static readonly DependencyProperty HtmlTextProperty =
            DependencyProperty.RegisterAttached("HtmlText", typeof(string), typeof(HtmlTextBoxProperties), new UIPropertyMetadata("", OnHtmlTextChanged));

        private static void OnHtmlTextChanged(
            DependencyObject depObj, DependencyPropertyChangedEventArgs e)
        {
            // Go ahead and return out if we set the property
            //on something other than a textblock, or set a value that is not a string.
            var txtBox = depObj as TextBlock;
            if (txtBox == null)
                return;
            if (!(e.NewValue is string))
                return;
            var html = e.NewValue as string;
            string xaml;
            InlineCollection xamLines;
            try
            {
                xaml = HtmlToXamlConverter.ConvertHtmlToXaml(html,false);
                xamLines = ((Paragraph)((Section)System.Windows.Markup.XamlReader.Parse(xaml)).Blocks.FirstBlock).Inlines;
            }
            catch
            { 
                // There was a problem parsing the html, return out. 
                return;
            }
            // Create a copy of the Inlines and add them to the TextBlock.
            Inline[] newLines = new Inline[xamLines.Count];
            xamLines.CopyTo(newLines, 0);
            txtBox.Inlines.Clear();
            foreach (var l in newLines)
            {
                txtBox.Inlines.Add(l);
            }
        }
    }
    public class DescriptionViewModel
    {
        public string Id { get; set; }
        public string Description { get; set; } = "Default Description";

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
                    DoseOutputFormatOptions.Add(DoseFormat.EQD2);
                    DoseOutputFormatOptions.Add(DoseFormat.Base);
                    SelectedOutputFormat = DoseFormat.EQD2;
                }
                else
                {
                    DoseOutputFormatOptions.Clear();
                    foreach (var format in _doseOutputFormatOptionDefaults)
                    {
                        DoseOutputFormatOptions.Add(format);
                    }
                    SelectedOutputFormat = DoseFormat.EQD2;
                }

            }
        }
        public bool DesignTime { get; private set; } = true;

        public bool isDoseSelectionInfoOpen { get; set; } = false;

        public bool isConvertToInfoOpen { get; set; } = false;

        public DescriptionViewModel DoseDescriptionViewModel { get; private set; } = new DescriptionViewModel() { Id = "Select source dose distribution", Description = "Select the source dose distribution to convert from. If there are multiple relevant prior courses, convert each to EQD and create a sum in Eclipse before generating a BASE distribution" };
        public DescriptionViewModel ConvertToDescriptionViewModel { get; private set; } = new DescriptionViewModel() { Id = "Convert to", Description = "Select the output distribution:<br><br><b>EQD2</b> and <b>BED</b> are as traditionally defined, for an source physical dose distribution with a given fractionation.<br><br><b>BEDn2</b> is the BED-equivalent physical dose output in the # of fractions specified by the user, for an source physical dose distribution with a given fractionation.<br><br>The <b>BASE</b> option outputs a dose distribution that, when used as a base dose for optimizing a new plan with the # of fractions specified, will result in the EQD2 constraint for each included structure (defined below) being exceeded only if the <u>optimization</u> exceeds that EQD2 in the fractionation of the new plan. The input for BASE <u>must be a BED distribution</u>. The fractionation of the source distribution is ignored.<br><br>Note that the output dose will be zero in any region not covered by an included contour." };

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

        private double _convParameter = 20;
        public double convParameter
        {
            get { return _convParameter; }
            set
            {
                switch (_selectedOutputFormat)
                {
                    case DoseFormat.BEDn2:
                        _convParameter = Convert.ToUInt32(value);
                        SetDefaultPlanName();
                        break;
                    case DoseFormat.EQDd:
                        _convParameter = value;
                        break;
                    case DoseFormat.Base:
                        _convParameter = Convert.ToUInt32(value);
                        break;
                }
            }
        }

        private DoseFormat _selectedOutputFormat = DoseFormat.EQD2;
        public DoseFormat SelectedOutputFormat
        {
            get { return _selectedOutputFormat; }
            set
            {
                _selectedOutputFormat = value;
                if (_model != null)
                {
                    SetDefaultPlanName();
                }
                RaisePropertyChangedEvent(nameof(ShowN2Fractions));
            }
        }

        private static List<DoseFormat> _doseOutputFormatOptionDefaults = new List<DoseFormat>() { DoseFormat.EQD2, DoseFormat.BEDn2, DoseFormat.BED, DoseFormat.Base };
        public ObservableCollection<DoseFormat> DoseOutputFormatOptions { get; private set; } = new ObservableCollection<DoseFormat>(_doseOutputFormatOptionDefaults);
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
            switch (SelectedOutputFormat)
            {
                case DoseFormat.EQD2:
                    ConvertedPlanName = fullPlanName.Substring(0, Math.Min(fullPlanName.Length, 9)) + "_" + SelectedOutputFormat.Display();
                    break;
                case DoseFormat.BEDn2:
                    ConvertedPlanName = string.Format("{0}_{1}fx", fullPlanName.Substring(0, Math.Min(fullPlanName.Length, 9)), convParameter);
                    break;
                case DoseFormat.Base:
                    ConvertedPlanName = string.Format("{0}_{1}fxB", fullPlanName.Substring(0, Math.Min(fullPlanName.Length, 9)), convParameter);
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
            StatusColor = new SolidColorBrush(Colors.Transparent);
            StatusMessage = "Converting...";
            int[,,] convdose = new int[0, 0, 0];
            bool success = true;
            try
            {
                (convdose, success, StatusMessage) = await _model.GetConvertedDose(SelectedInputOption.CourseId, SelectedInputOption.Id, SelectedInputOption.IsSum, ConvertedPlanName, AlphaBetaMappings.ToList(), SelectedOutputFormat, convParameter);

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

            //Tuple<int, int> minMaxConverted = Helpers.GetMinMaxValues(convdose, convdose.GetLength(1), convdose.GetLength(2), convdose.GetLength(0));

            //PreviewWindow previewWindow = new PreviewWindow(this.scriptcontext, convdose, this.originalArray,
            //    this.scaling, this.doseMin, this.doseMax, minMaxConverted.Item1, minMaxConverted.Item2);
            //previewWindow.ShowDialog();
            Working = false;
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
