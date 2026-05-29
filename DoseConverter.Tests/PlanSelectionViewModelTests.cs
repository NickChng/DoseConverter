using DoseConverter.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoseConverter.Tests
{
    /// <summary>
    /// Unit tests for <see cref="PlanSelectionViewModel"/> covering display
    /// string formatting and property assignment.
    /// </summary>
    [TestClass]
    public class PlanSelectionViewModelTests
    {
        // -----------------------------------------------------------------------
        // DisplayString formatting
        // -----------------------------------------------------------------------

        [TestMethod]
        public void DisplayString_RegularPlan_FormatsAsCourseSlashId()
        {
            var vm = new PlanSelectionViewModel("Plan1", "C1", "SS1", isSum: false);
            Assert.AreEqual("C1/Plan1", vm.DisplayString);
        }

        [TestMethod]
        public void DisplayString_PlanSum_AppendsSumSuffix()
        {
            var vm = new PlanSelectionViewModel("PlanSum", "C1", "SS1", isSum: true);
            Assert.AreEqual("C1/PlanSum [sum]", vm.DisplayString);
        }

        [TestMethod]
        public void DisplayString_LongCourseId_IncludesFullId()
        {
            var vm = new PlanSelectionViewModel("P", "LongCourseName", "SS", isSum: false);
            Assert.AreEqual("LongCourseName/P", vm.DisplayString);
        }

        // -----------------------------------------------------------------------
        // Property round-trips
        // -----------------------------------------------------------------------

        [TestMethod]
        public void Id_CanBeSetAndRead()
        {
            var vm = new PlanSelectionViewModel();
            vm.Id = "MyPlan";
            Assert.AreEqual("MyPlan", vm.Id);
        }

        [TestMethod]
        public void CourseId_CanBeSetAndRead()
        {
            var vm = new PlanSelectionViewModel();
            vm.CourseId = "Course1";
            Assert.AreEqual("Course1", vm.CourseId);
        }

        [TestMethod]
        public void SsId_CanBeSetAndRead()
        {
            var vm = new PlanSelectionViewModel();
            vm.SsId = "SS_A";
            Assert.AreEqual("SS_A", vm.SsId);
        }

        [TestMethod]
        public void IsSum_DefaultConstructor_IsFalse()
        {
            var vm = new PlanSelectionViewModel();
            Assert.IsFalse(vm.IsSum);
        }

        [TestMethod]
        public void FullConstructor_SetsAllProperties()
        {
            var vm = new PlanSelectionViewModel("P1", "C1", "SS1", true);
            Assert.AreEqual("P1", vm.Id);
            Assert.AreEqual("C1", vm.CourseId);
            Assert.AreEqual("SS1", vm.SsId);
            Assert.IsTrue(vm.IsSum);
        }

        // -----------------------------------------------------------------------
        // Edge cases
        // -----------------------------------------------------------------------

        [TestMethod]
        public void DisplayString_EmptyPlanId_ShowsSlashOnly()
        {
            var vm = new PlanSelectionViewModel("", "C1", "SS1", false);
            Assert.AreEqual("C1/", vm.DisplayString);
        }

        [TestMethod]
        public void DisplayString_EmptyCourseId_ShowsSlashAndPlanId()
        {
            var vm = new PlanSelectionViewModel("P1", "", "SS1", false);
            Assert.AreEqual("/P1", vm.DisplayString);
        }

        [TestMethod]
        public void DisplayString_SumFlagFalse_NoSuffix()
        {
            var vm = new PlanSelectionViewModel("P1", "C1", "SS1", isSum: false);
            StringAssert.DoesNotMatch(vm.DisplayString,
                new System.Text.RegularExpressions.Regex(@"\[sum\]"));
        }
    }
}
