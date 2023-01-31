namespace EQD2Converter
{
    public class AlphaBetaMapping :ObservableObject
    {
        public double AlphaBetaRatio { get; set; }
        public string StructureId { get; set; }

        public string StructureLabel { get; set; }

        public AlphaBetaMapping() { }
        
        public AlphaBetaMapping(string structureId, double alphaBetaRatio, string structureLabel)
        {
            StructureId = structureId;
            AlphaBetaRatio = alphaBetaRatio;
            StructureLabel = structureLabel;
        }
    }
}
