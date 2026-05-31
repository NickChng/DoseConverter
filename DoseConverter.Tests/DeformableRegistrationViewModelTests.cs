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

        /// <summary>Source plan uses SS id "SS1".</summary>
        private static PlanSelectionViewModel MakePlan(string planId, string ssId = "SS1", string courseId = "C1")
            => new PlanSelectionViewModel(planId, courseId, ssId, isSum: false);

        /// <summary>Target structure set with a distinct SS id by default.</summary>
        private static StructureSetSelectionViewModel MakeSS(string ssId, string courseId = "C1", string imageId = "IMG1")
            => new StructureSetSelectionViewModel(ssId, courseId, imageId);

        /// <summary>
        /// Creates a vm with source plans populated and a distinct target SS
        /// (ssId "SS2") so validation does not trip the same-SS guard.
        /// </summary>
        private static DeformableRegistrationViewModel MakeReadyVm(
            PlanSelectionViewModel sourcePlan,
            StructureSetSelectionViewModel targetSS = null)
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel> { sourcePlan });
            var ss = targetSS ?? MakeSS("SS2");
            vm.SetAvailableTargets(new ObservableCollection<StructureSetSelectionViewModel> { ss });
            return vm;
        }

        // -----------------------------------------------------------------------
        // Default-constructor state
        // -----------------------------------------------------------------------

        [TestMethod]
        public void DefaultConstructor_SetsInitialStatusMessage()
        {
            var vm = new DeformableRegistrationViewModel();
            StringAssert.Contains(vm.StatusMessage.ToLower(), "source");
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
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel>
            {
                MakePlan("Plan1"), MakePlan("Plan2")
            });
            Assert.AreEqual(2, vm.AllPlanOptions.Count);
        }

        [TestMethod]
        public void SetAvailablePlans_SetsSourceToFirst()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel>
            {
                MakePlan("Plan1"), MakePlan("Plan2")
            });
            Assert.AreEqual("Plan1", vm.SelectedSourcePlan?.Id);
        }

        [TestMethod]
        public void SetAvailablePlans_EmptyList_LeavesNullSourceSelection()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel>());
            Assert.IsNull(vm.SelectedSourcePlan);
        }

        [TestMethod]
        public void SetAvailablePlans_CalledTwice_ReplacesOptions()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel>
            {
                MakePlan("Old1"), MakePlan("Old2")
            });
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel>
            {
                MakePlan("New1")
            });
            Assert.AreEqual(1, vm.AllPlanOptions.Count);
            Assert.AreEqual("New1", vm.AllPlanOptions[0].Id);
        }

        // -----------------------------------------------------------------------
        // SetAvailableTargets
        // -----------------------------------------------------------------------

        [TestMethod]
        public void SetAvailableTargets_PopulatesAllTargetOptions()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailableTargets(new ObservableCollection<StructureSetSelectionViewModel>
            {
                MakeSS("SS1"), MakeSS("SS2")
            });
            Assert.AreEqual(2, vm.AllTargetOptions.Count);
        }

        [TestMethod]
        public void SetAvailableTargets_SetsTargetToFirst()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailableTargets(new ObservableCollection<StructureSetSelectionViewModel>
            {
                MakeSS("SS1"), MakeSS("SS2")
            });
            Assert.AreEqual("SS1", vm.SelectedTargetSS?.Id);
        }

        [TestMethod]
        public void SetAvailableTargets_EmptyList_LeavesNullTargetSelection()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailableTargets(new ObservableCollection<StructureSetSelectionViewModel>());
            Assert.IsNull(vm.SelectedTargetSS);
        }

        // -----------------------------------------------------------------------
        // Validation: RunButtonVisibility
        // -----------------------------------------------------------------------

        [TestMethod]
        public void Validation_NoTargetSS_HidesRunButton()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel> { MakePlan("Plan1") });
            // No target SS set
            vm.DeformedPlanName = "OutputPlan";
            Assert.AreEqual(Visibility.Collapsed, vm.RunButtonVisibility);
        }

        [TestMethod]
        public void Validation_SameSSForSourceAndTarget_HidesRunButton()
        {
            // Source plan uses SS1, target SS is also SS1 → conflict
            var vm = MakeReadyVm(MakePlan("Plan1", ssId: "SS1"), MakeSS("SS1"));
            vm.DeformedPlanName = "OutputPlan";
            Assert.AreEqual(Visibility.Collapsed, vm.RunButtonVisibility);
        }

        [TestMethod]
        public void Validation_DifferentSS_EmptyOutputName_HidesRunButton()
        {
            var vm = MakeReadyVm(MakePlan("Plan1", ssId: "SS1"), MakeSS("SS2"));
            vm.DeformedPlanName = "";
            Assert.AreEqual(Visibility.Collapsed, vm.RunButtonVisibility);
        }

        [TestMethod]
        public void Validation_DifferentSS_WhitespaceOutputName_HidesRunButton()
        {
            var vm = MakeReadyVm(MakePlan("Plan1", ssId: "SS1"), MakeSS("SS2"));
            vm.DeformedPlanName = "   ";
            Assert.AreEqual(Visibility.Collapsed, vm.RunButtonVisibility);
        }

        [TestMethod]
        public void Validation_DifferentSS_ValidOutputName_ShowsRunButton()
        {
            var vm = MakeReadyVm(MakePlan("Plan1", ssId: "SS1"), MakeSS("SS2"));
            vm.DeformedPlanName = "OutputPlan";
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
        public void Validation_NullTargetSS_StatusIndicatesSelectTarget()
        {
            var vm = new DeformableRegistrationViewModel();
            vm.SetAvailablePlans(new ObservableCollection<PlanSelectionViewModel> { MakePlan("Plan1") });
            // No target SS — SelectedTargetSS remains null
            StringAssert.Contains(vm.StatusMessage.ToLower(), "target");
        }

        [TestMethod]
        public void Validation_ReadyState_StatusIndicatesReady()
        {
            var vm = MakeReadyVm(MakePlan("Plan1", ssId: "SS1"), MakeSS("SS2"));
            vm.DeformedPlanName = "OutputPlan";
            StringAssert.Contains(vm.StatusMessage.ToLower(), "ready");
        }

        [TestMethod]
        public void Validation_SameSSSourceTarget_StatusIndicatesDifferent()
        {
            var vm = MakeReadyVm(MakePlan("Plan1", ssId: "SS1"), MakeSS("SS1"));
            vm.DeformedPlanName = "OutputPlan";
            StringAssert.Contains(vm.StatusMessage.ToLower(), "different");
        }
    }
}
