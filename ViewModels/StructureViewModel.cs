using DoseConverter.Events;
using Prism.Events;
using System.Windows.Input;

namespace DoseConverter.ViewModels
{
    public class StructureViewModel : ObservableObject
    {
        private Model _model;
        private IEventAggregator _ea;
        private double _alphaBetaRatio;
        public double AlphaBetaRatio
        {
            get { return _alphaBetaRatio; }
            set { _alphaBetaRatio = value; _alphaBetaRatioString = _alphaBetaRatio.ToString(); }
        }
               
        private string _alphaBetaRatioString;
        public string AlphaBetaRatioString
        {
            get
            {
                return _alphaBetaRatioString;
            }
            set
            {
                if (double.TryParse(value, out double alphaBetaRatio))
                {
                    if (alphaBetaRatio > 0)
                    {
                        AlphaBetaRatio = alphaBetaRatio;
                        _alphaBetaRatioString = value;
                    }
                    else
                    {
                        _alphaBetaRatioString = string.Empty;
                        AlphaBetaRatio = double.NaN;
                    }
                }
                else
                {
                    _alphaBetaRatioString = string.Empty;
                    _alphaBetaRatio = double.NaN;
                }
                ValidateInputs();
            }
        }

        public double MaxEQD2 { get; private set; } = double.NaN;
        private string _maxEQDString = string.Empty;
        public string MaxEQD2String
        {
            get { return _maxEQDString; }
            set
            {
                if (double.TryParse(value, out double maxEQD2))
                {
                    if (maxEQD2 > 0)
                    {
                        _maxEQDString = value;
                        MaxEQD2 = maxEQD2;
                    }
                }
                else
                {
                    _maxEQDString = string.Empty;
                    MaxEQD2 = double.NaN;
                }
                RaisePropertyChangedEvent(nameof(DisplayMaxEQD2inBEDn2));
                ValidateInputs();
            }
        }

        public bool IncludeEdges { get; set; } = false;

        private ushort _n2 = 0;
        public ushort n2
        {
            get { return _n2; }
            set
            {
                _n2 = value;
                RaisePropertyChangedEvent(nameof(DisplayMaxEQD2inBEDn2));
            }
        }

        private string _displayMaxEQD2inBEDn2 = "Design";
        public string DisplayMaxEQD2inBEDn2
        {
            get
            {
                if (!double.IsNaN(MaxEQD2) && _n2 != 0)
                {
                    double MaxEQD2inBEDn2 = _model.ConvertEQD2toPhysical(MaxEQD2, AlphaBetaRatio, _n2);
                    _displayMaxEQD2inBEDn2 = string.Format("{0:0.00} Gy", MaxEQD2inBEDn2, _n2);
                    return _displayMaxEQD2inBEDn2;
                }
                else
                {
                    return string.Empty;
                }

            }
        }

        public string StructureId { get; set; }

        public string StructureLabel { get; set; }

        private bool _include = false;
        public bool Include
        {
            get { return _include; }
            set
            {
                _include = value;
                ValidateInputs();
            }
        }

        private void ValidateInputs()
        {
            if (Include)
            {
                if (double.IsNaN(MaxEQD2))
                {
                    AddError(nameof(MaxEQD2String), "Max EQD2 value not defined");
                    AddError(nameof(MaxEQD2), "Max EQD2 value not defined");
                }
                else
                {
                    ClearErrors(nameof(MaxEQD2String));
                    ClearErrors(nameof(MaxEQD2));
                }
                if (!double.IsNaN(AlphaBetaRatio))
                {
                    ClearErrors(nameof(AlphaBetaRatio));
                    ClearErrors(nameof(AlphaBetaRatioString));
                }
                else
                {
                    AddError(nameof(AlphaBetaRatio), "Alpha-beta ratio not defined");
                    AddError(nameof(AlphaBetaRatioString), "Alpha-beta ratio not defined");
                }
            }
            else
            {
                ClearErrors(nameof(MaxEQD2String));
                ClearErrors(nameof(MaxEQD2));
            }
            _ea?.GetEvent<StructureValidationOccurred>().Publish();
        }

        public StructureViewModel() { }

        public StructureViewModel(IEventAggregator ea, Model model, string structureId, double alphaBetaRatio, string structureLabel, bool includeEdges = true, double? maxEQD2 = null, bool include = false)
        {
            _model = model;
            _ea = ea;
            _displayMaxEQD2inBEDn2 = string.Empty;
            StructureId = structureId;
            AlphaBetaRatio = alphaBetaRatio;
            StructureLabel = structureLabel;
            IncludeEdges = includeEdges;
            Include = include;
            if (maxEQD2 != null)
            {
                if (maxEQD2 > 0)
                {
                    MaxEQD2 = (double)maxEQD2;
                    MaxEQD2String = maxEQD2.ToString();
                }
            }
        }

        public ICommand ToggleIncludeCommand
        {
            get { return new DelegateCommand(ToggleInclude); }
        }

        public void ToggleInclude(object parameter = null)
        {
            Include = !Include;
            _ea.GetEvent<StructureInclusionChanged>().Publish(); 
        }
    }
}
