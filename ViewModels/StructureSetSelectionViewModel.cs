namespace DoseConverter.ViewModels
{
    /// <summary>
    /// Represents a single patient structure set as a selectable item for the
    /// Target (fixed) image picker on the DIR tab.
    /// </summary>
    public class StructureSetSelectionViewModel : ObservableObject
    {
        /// <summary>Structure set ID.</summary>
        public string Id { get; set; }

        /// <summary>Course that owns a plan referencing this SS (may be empty).</summary>
        public string CourseId { get; set; }

        /// <summary>CT image ID associated with this structure set.</summary>
        public string ImageId { get; set; }

        public string DisplayString
        {
            get
            {
                if (string.IsNullOrEmpty(CourseId))
                    return $"{Id}  [{ImageId}]";
                return $"{CourseId}/{Id}  [{ImageId}]";
            }
        }

        public StructureSetSelectionViewModel() { }

        public StructureSetSelectionViewModel(string id, string courseId, string imageId)
        {
            Id      = id;
            CourseId = courseId;
            ImageId  = imageId;
        }
    }
}
