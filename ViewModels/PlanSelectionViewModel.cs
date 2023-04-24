namespace DoseConverter
{
    public class PlanSelectionViewModel : ObservableObject
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
        public PlanSelectionViewModel()
        {

        }
        public PlanSelectionViewModel(string id, string courseId, string ssId, bool isSum)
        {
            Id = id;
            CourseId = courseId;
            SsId = ssId;
            IsSum = isSum;
        }
    }
}
