using System.Windows.Input;

namespace EQD2Converter
{
    public class AlphaBetaMapping :ObservableObject
    {
        public double AlphaBetaRatio { get; set; }
        public double? MaxEQD2 { get; set; } = null;
        public string StructureId { get; set; }

        public string StructureLabel { get; set; }

        public bool Include { get; set; } = false;

        public AlphaBetaMapping() { }
        
        public AlphaBetaMapping(string structureId, double alphaBetaRatio, string structureLabel, double? maxEQD2 = null, bool include=false)
        {
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
