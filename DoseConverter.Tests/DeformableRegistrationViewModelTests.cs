using System.Collections.ObjectModel;
using System.Windows;
using DoseConverter.ViewModels;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DoseConverter.Tests
{
    /// <summary>
    /// Unit tests for <see cref="DeformableRegistrationViewModel"/> covering all
    /// validation paths. These tests use the default (no-arg) constructor so no
    /// ESAPI context or SimpleITK runtime is required.
    /// </summary>
    [TestClass]
    public class DeformableRegistrationViewModelTests
    {
        // -----------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------

        private static PlanSelectionViewModel MakePlan(string planId, string courseId = "C1")
            => new PlanSelectionViewModel(planId, courseId, "SS1", isSum: false);

        private static DeformableRegistrationViewModel MakeVmWithPlans(
            params PlanSelectionViewModel[] plans)
        {
            var vm = new DeformableRegistrationViewModel();
            var list = new ObservableCollection<PlanSelectionViewModel>(plans);
            vm.SetAvailablePlans(list);
            return vm;
        }

        // -----------------------------------------------------------------------
        // Default-constructor state
        // -----------------------------------------------------------------------

        [TestMethod]
        public void DefaultConstructor_SetsInitialStatusMessage()
        {
            var vm = new DeformableRegistrationViewModel();
            Assert.AreEqual("Select source and target plans to begin.", vm.StatusMessage);
        }

        [TestMethod]
        public void DefaultConstructor_RunButtonHidden()
        {
            var vm = new DeformableRegistrationViewModel();
            Assert.AreEqual(Visibility.Collapsed, vm.RunButtonVisibility);
        }

        [TestMethod]
        public void DefaultConstructor_NotWorking()
        {
            var vm = new DeformableRegistrationViewModel();
            Assert.IsFalse(vm.Working);
        }

        // -----------------------------------------------------------------------
        // SetAvailablePlans
        // -----------------------------------------------------------------------

        [TestMethod]
        public void SetAvailablePlans_PopulatesAllPlanOptions()
        {
            var p1 = MakePlan("Plan1");
            var p2 = MakePlan("Plan2");
            var vm = MakeVmWithPlans(p1, p2);

            Assert.AreEqual(2, vm.AllPlanOptions.Count);
        }

        [TestMethod]
        public void SetAvailablePlans_SetsSourceAndTargetToFirst()
        {
            var p1 = MakePlan("Plan1");
            var p2 = MakePlan("Plan2");
            var vm = MakeVmWithPlans(p1, p2);

            Assert.AreEqual("Plan1", vm.SelectedSourcePlan?.Id);
            Assert.AreEqual("Plan1", vm.SelectedTargetPlan?.Id);
        }

        [TestMethod]
        public void SetAvailablePlans_EmptyList_LeavesNullSelections()
        {
            var vm = MakeVmWithPlans(/* no plans */);
            Assert.IsNull(vm.SelectedSourcePlan);
            Assert.IsNull(vm.SelectedTargetPlan);
        }

        [TestMethod]
        public void SetAvailablePlans_CalledTwice_ReplacesOptions()
        {
            var vm = MakeVmWithPlans(MakePlan("Old1"), MakePlan("Old2"));
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel>
            {
                MakePlan("New1")
            });
            Assert.AreEqual(1, vm.AllPlanOptions.Count);
            Assert.AreEqual("New1", vm.AllPlanOptions[0].Id);
        }

        // -----------------------------------------------------------------------
        // Validation: RunButtonVisibility
        // -----------------------------------------------------------------------

        [TestMethod]
        public void Validation_SamePlanForSourceAndTarget_HidesRunButton()
        {
            var p1 = MakePlan("Plan1");
            var vm = MakeVmWithPlans(p1);

            // Both source and target default to the same first plan
            vm.DeformedPlanName = "OutputPlan";

            Assert.AreEqual(Visibility.Collapsed, vm.RunButtonVisibility);
        }

        [TestMethod]
        public void Validation_DifferentPlans_EmptyOutputName_HidesRunButton()
        {
            var p1 = MakePlan("Plan1");
            var p2 = MakePlan("Plan2");
            var vm = MakeVmWithPlans(p1, p2);
            vm.SelectedTargetPlan = p2;
            vm.DeformedPlanName = "";

            Assert.AreEqual(Visibility.Collapsed, vm.RunButtonVisibility);
        }

        [TestMethod]
        public void Validation_DifferentPlans_WhitespaceOutputName_HidesRunButton()
        {
            var p1 = MakePlan("Plan1");
            var p2 = MakePlan("Plan2");
            var vm = MakeVmWithPlans(p1, p2);
            vm.SelectedTargetPlan = p2;
            vm.DeformedPlanName = "   ";

            Assert.AreEqual(Visibility.Collapsed, vm.RunButtonVisibility);
        }

        [TestMethod]
        public void Validation_DifferentPlans_ValidOutputName_ShowsRunButton()
        {
            var p1 = MakePlan("Plan1");
            var p2 = MakePlan("Plan2");
            var vm = MakeVmWithPlans(p1, p2);
            vm.SelectedTargetPlan = p2;
            vm.DeformedPlanName = "OutputPlan";

            Assert.AreEqual(Visibility.Visible, vm.RunButtonVisibility);
        }

        [TestMethod]
        public void Validation_SamePlanIdDifferentCourse_ShowsRunButton()
        {
            var p1 = new PlanSelectionViewModel("Plan1", "C1", "SS1", false);
            var p2 = new PlanSelectionViewModel("Plan1", "C2", "SS1", false);
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel> { p1, p2 });
            vm.SelectedSourcePlan = p1;
            vm.SelectedTargetPlan = p2;
            vm.DeformedPlanName = "Output";

            Assert.AreEqual(Visibility.Visible, vm.RunButtonVisibility);
        }

        // -----------------------------------------------------------------------
        // DeformedPlanName truncation
        // -----------------------------------------------------------------------

        [TestMethod]
        public void DeformedPlanName_TruncatesAtThirteenChars()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.DeformedPlanName = "ABCDEFGHIJKLMNOP"; // 16 chars
            Assert.AreEqual(13, vm.DeformedPlanName.Length);
            Assert.AreEqual("ABCDEFGHIJKLM", vm.DeformedPlanName);
        }

        [TestMethod]
        public void DeformedPlanName_ShortNameNotTruncated()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.DeformedPlanName = "Short";
            Assert.AreEqual("Short", vm.DeformedPlanName);
        }

        [TestMethod]
        public void DeformedPlanName_ExactlyThirteenChars_NotTruncated()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.DeformedPlanName = "ABCDEFGHIJKLM"; // exactly 13
            Assert.AreEqual("ABCDEFGHIJKLM", vm.DeformedPlanName);
        }

        // -----------------------------------------------------------------------
        // Status messages under validation paths
        // -----------------------------------------------------------------------

        [TestMethod]
        public void Validation_NullSourcePlan_StatusIndicatesSelectSource()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SelectedSourcePlan = null;
            StringAssert.Contains(vm.StatusMessage.ToLower(), "source");
        }

        [TestMethod]
        public void Validation_ReadyState_StatusIndicatesReady()
        {
            var p1 = MakePlan("Plan1");
            var p2 = MakePlan("Plan2");
            var vm = MakeVmWithPlans(p1, p2);
            vm.SelectedTargetPlan = p2;
            vm.DeformedPlanName = "OutputPlan";

            StringAssert.Contains(vm.StatusMessage.ToLower(), "ready");
        }

        [TestMethod]
        public void Validation_SamePlanSourceTarget_StatusIndicatesDifferent()
        {
            var p1 = MakePlan("Plan1");
            var vm = MakeVmWithPlans(p1);
            vm.DeformedPlanName = "OutputPlan";

            StringAssert.Contains(vm.StatusMessage.ToLower(), "different");
        }
    }
}
