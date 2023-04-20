using System.Windows.Input;

namespace EQD2Converter
{
    public class StructureViewModel : ObservableObject
    {
        private Model _model;
        public double AlphaBetaRatio { get; set; }

        private double? _maxEQD2 = null;
        public double? MaxEQD2
        {
            get { return _maxEQD2; }
            set
            {
                _maxEQD2 = value;
                RaisePropertyChangedEvent(nameof(DisplayMaxEQD2inBEDn2));
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
                if (MaxEQD2 != null && _n2 != 0)
                {
                    double MaxEQD2inBEDn2 = _model.ConvertEQD2toPhysical((double)MaxEQD2, AlphaBetaRatio, _n2);
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

        public bool Include { get; set; } = false;

        public StructureViewModel() { }

        public StructureViewModel(Model model, string structureId, double alphaBetaRatio, string structureLabel, double? maxEQD2 = null, bool include = false)
        {
            _model = model;
            _displayMaxEQD2inBEDn2 = string.Empty;
            StructureId = structureId;
            AlphaBetaRatio = alphaBetaRatio;
            StructureLabel = structureLabel;
            Include = include;
            MaxEQD2 = maxEQD2;
        }

        public ICommand ToggleIncludeCommand
        {
            get { return new DelegateCommand(ToggleInclude); }
        }

        public void ToggleInclude(object parameter = null)
        {
            Include = !Include;
        }
    }
}
