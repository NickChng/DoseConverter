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
}
