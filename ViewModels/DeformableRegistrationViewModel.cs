using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ESAPIScript;
using VMS.TPS.Common.Model.API;

namespace DoseConverter.ViewModels
{
    public class DeformableRegistrationViewModel : ObservableObject
    {
        private EsapiWorker _ew;
        private Dispatcher _ui;
        private DeformableRegistrationService _dirService;
        private DoseConverterConfigRegistrationParameters _regParams;

        // -----------------------------------------------------------------------
        // Plan selection
        // -----------------------------------------------------------------------

        public ObservableCollection<PlanSelectionViewModel> AllPlanOptions { get; private set; }
            = new ObservableCollection<PlanSelectionViewModel>
            {
                new PlanSelectionViewModel("Design", "DesignCourse", "DesignSS", false)
            };

        private PlanSelectionViewModel _selectedSourcePlan;
        public PlanSelectionViewModel SelectedSourcePlan
        {
            get => _selectedSourcePlan;
            set
            {
                _selectedSourcePlan = value;
                RaisePropertyChangedEvent(nameof(SelectedSourcePlan));
                SetDefaultOutputPlanName();
                LoadStructuresForPlan(value, isSource: true);
                ValidateInputs();
            }
        }

        private PlanSelectionViewModel _selectedTargetPlan;
        public PlanSelectionViewModel SelectedTargetPlan
        {
            get => _selectedTargetPlan;
            set
            {
                _selectedTargetPlan = value;
                RaisePropertyChangedEvent(nameof(SelectedTargetPlan));
                SetDefaultOutputPlanName();
                LoadStructuresForPlan(value, isSource: false);
                ValidateInputs();
            }
        }

        // -----------------------------------------------------------------------
        // Structure mask selection
        // -----------------------------------------------------------------------

        public const string NoMaskSentinel = "(none — full image)";

        public ObservableCollection<string> SourceStructureOptions { get; private set; }
            = new ObservableCollection<string> { NoMaskSentinel };

        public ObservableCollection<string> TargetStructureOptions { get; private set; }
            = new ObservableCollection<string> { NoMaskSentinel };

        private string _selectedSourceStructure = NoMaskSentinel;
        public string SelectedSourceStructure
        {
            get => _selectedSourceStructure;
            set { _selectedSourceStructure = value; RaisePropertyChangedEvent(nameof(SelectedSourceStructure)); }
        }

        private string _selectedTargetStructure = NoMaskSentinel;
        public string SelectedTargetStructure
        {
            get => _selectedTargetStructure;
            set { _selectedTargetStructure = value; RaisePropertyChangedEvent(nameof(SelectedTargetStructure)); }
        }

        // -----------------------------------------------------------------------
        // Registration parameter overrides (most influential knobs)
        // -----------------------------------------------------------------------

        /// <summary>B-spline grid nodes along each axis, e.g. "5 5 5".</summary>
        public string RegGridNodes
        {
            get => _regParams?.BSplineGridNodes ?? "5 5 5";
            set
            {
                if (_regParams != null) _regParams.BSplineGridNodes = value;
                RaisePropertyChangedEvent(nameof(RegGridNodes));
            }
        }

        /// <summary>Maximum L-BFGS-B iterations per resolution level, e.g. "100 50 20".</summary>
        public string RegMaxIterationsPerLevel
        {
            get => _regParams?.MaxIterationsPerLevel ?? "100 50 20";
            set
            {
                if (_regParams != null) _regParams.MaxIterationsPerLevel = value;
                RaisePropertyChangedEvent(nameof(RegMaxIterationsPerLevel));
            }
        }

        /// <summary>Metric sampling as a percentage (1–100); stored internally as a fraction.</summary>
        public string RegSamplingPercent
        {
            get => _regParams != null
                ? ((int)Math.Round(_regParams.MetricSamplingPercentage * 100)).ToString()
                : "10";
            set
            {
                if (_regParams != null && int.TryParse(value?.Trim(), out int pct) && pct > 0 && pct <= 100)
                    _regParams.MetricSamplingPercentage = pct / 100.0;
                RaisePropertyChangedEvent(nameof(RegSamplingPercent));
            }
        }

        // -----------------------------------------------------------------------
        // Output plan name
        // -----------------------------------------------------------------------

        private string _deformedPlanName = "";
        public string DeformedPlanName
        {
            get => _deformedPlanName;
            set
            {
                _deformedPlanName = value.Length > 13 ? value.Substring(0, 13) : value;
                RaisePropertyChangedEvent(nameof(DeformedPlanName));
                ValidateInputs();
            }
        }

        // -----------------------------------------------------------------------
        // Status / progress
        // -----------------------------------------------------------------------

        public bool Working { get; private set; } = false;
        public string StatusMessage { get; private set; } = "Select source and target plans to begin.";
        public SolidColorBrush StatusColor { get; private set; } = new SolidColorBrush(Colors.Transparent);
        public Visibility SuccessVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility ErrorVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility RunButtonVisibility { get; private set; } = Visibility.Collapsed;

        public string ErrorDetails { get; private set; } = "";
        public bool IsErrorDetailsOpen { get; set; } = false;

        // -----------------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------------

        public ICommand RunDIRCommand => new DelegateCommand(RunDIR);
        public ICommand ErrorDetailsButtonCommand => new DelegateCommand(_ => { IsErrorDetailsOpen ^= true; });

        // -----------------------------------------------------------------------
        // Construction / initialisation
        // -----------------------------------------------------------------------

        public DeformableRegistrationViewModel() { }

        public DeformableRegistrationViewModel(EsapiWorker ew, Model model)
        {
            _ew = ew;
            _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _dirService = new DeformableRegistrationService(ew, model);
            AllPlanOptions.Clear();
            _regParams = model?.Config?.RegistrationParameters
                         ?? new DoseConverterConfigRegistrationParameters();
        }

        /// <summary>
        /// Initialises the view model in-place (without replacing the instance) so that
        /// existing WPF bindings remain valid.
        /// </summary>
        public void Initialize(EsapiWorker ew, Model model, Dispatcher uiDispatcher)
        {
            _ew = ew;
            _ui = uiDispatcher;
            _dirService = new DeformableRegistrationService(ew, model);
            _regParams = model?.Config?.RegistrationParameters
                         ?? new DoseConverterConfigRegistrationParameters();
            _ui.Invoke(() =>
            {
                AllPlanOptions.Clear();
                RaisePropertyChangedEvent(nameof(RegGridNodes));
                RaisePropertyChangedEvent(nameof(RegMaxIterationsPerLevel));
                RaisePropertyChangedEvent(nameof(RegSamplingPercent));
            });
        }

        /// <summary>
        /// Called by the parent ViewModel once plans have been loaded into PlanInputOptions.
        /// Mirrors the same list so the DIR tab always shows the same options.
        /// </summary>
        public void SetAvailablePlans(ObservableCollection<PlanSelectionViewModel> plans)
        {
            void Apply()
            {
                AllPlanOptions.Clear();
                foreach (var p in plans)
                    AllPlanOptions.Add(p);

                SelectedSourcePlan = AllPlanOptions.FirstOrDefault();
                SelectedTargetPlan = AllPlanOptions.FirstOrDefault();
            }

            if (_ui != null)
                _ui.Invoke(Apply);
            else
                Apply();
        }

        // -----------------------------------------------------------------------
        // Core execution
        // -----------------------------------------------------------------------

        private async void RunDIR(object param = null)
        {
            if (!CanRun()) return;

            Working = true;
            SuccessVisibility = Visibility.Collapsed;
            ErrorVisibility = Visibility.Collapsed;
            StatusColor = new SolidColorBrush(Colors.Transparent);
            StatusMessage = "Starting deformable registration...";
            NotifyStatusChanged();

            var progressReporter = new Progress<string>(msg =>
            {
                _ui.Invoke(() =>
                {
                    StatusMessage = msg;
                    RaisePropertyChangedEvent(nameof(StatusMessage));
                });
            });

            (ScriptStatus status, string message) result;
            try
            {
                result = await _dirService.PerformDIRAndWritePlan(
                    _selectedSourcePlan.CourseId,
                    _selectedSourcePlan.Id,
                    _selectedTargetPlan.CourseId,
                    _selectedTargetPlan.Id,
                    DeformedPlanName,
                    _selectedSourceStructure == NoMaskSentinel ? null : _selectedSourceStructure,
                    _selectedTargetStructure == NoMaskSentinel ? null : _selectedTargetStructure,
                    progressReporter);
            }
            catch (Exception ex)
            {
                result = (ScriptStatus.Error, $"Unexpected error: {ex.Message}");
                Helpers.SeriLog.LogError("Unexpected DIR error", ex);
            }

            Working = false;
            switch (result.status)
            {
                case ScriptStatus.Complete:
                    StatusMessage = result.message;
                    StatusColor = new SolidColorBrush(Colors.Transparent);
                    SuccessVisibility = Visibility.Visible;
                    ErrorVisibility = Visibility.Collapsed;
                    break;
                case ScriptStatus.Error:
                    StatusMessage = "Deformable registration failed. Click for details.";
                    ErrorDetails = result.message;
                    ErrorVisibility = Visibility.Visible;
                    SuccessVisibility = Visibility.Collapsed;
                    break;
            }
            NotifyStatusChanged();
            ValidateInputs();
        }

        // -----------------------------------------------------------------------
        // Validation
        // -----------------------------------------------------------------------

        private bool CanRun()
        {
            return _selectedSourcePlan != null
                && _selectedTargetPlan != null
                && !string.IsNullOrWhiteSpace(DeformedPlanName)
                && !Working;
        }

        private void ValidateInputs()
        {
            if (Working)
            {
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (_selectedSourcePlan == null)
            {
                StatusMessage = "Select a source (moving) plan.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (_selectedTargetPlan == null)
            {
                StatusMessage = "Select a target (fixed) plan.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (_selectedSourcePlan == _selectedTargetPlan ||
                     (string.Equals(_selectedSourcePlan.Id, _selectedTargetPlan.Id, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(_selectedSourcePlan.CourseId, _selectedTargetPlan.CourseId, StringComparison.OrdinalIgnoreCase)))
            {
                StatusMessage = "Source and target plans must be different.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (string.IsNullOrWhiteSpace(DeformedPlanName))
            {
                StatusMessage = "Enter a name for the output deformed dose plan.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else
            {
                StatusMessage = "Ready to run deformable registration.";
                RunButtonVisibility = Visibility.Visible;
            }
            RaisePropertyChangedEvent(nameof(StatusMessage));
            RaisePropertyChangedEvent(nameof(RunButtonVisibility));
        }

        private void SetDefaultOutputPlanName()
        {
            if (_selectedSourcePlan == null) return;
            string srcShort = _selectedSourcePlan.Id.Substring(0, Math.Min(_selectedSourcePlan.Id.Length, 9));
            _deformedPlanName = srcShort + "_DIR";
            RaisePropertyChangedEvent(nameof(DeformedPlanName));
        }

        /// <summary>
        /// Asynchronously queries ESAPI for the structure IDs available on the given plan's
        /// structure set and populates either <see cref="SourceStructureOptions"/> or
        /// <see cref="TargetStructureOptions"/>.  Falls back to an empty list (sentinel only)
        /// when the plan has no structure set or no ESAPI context is available.
        /// </summary>
        private async void LoadStructuresForPlan(PlanSelectionViewModel plan, bool isSource)
        {
            var targetCollection = isSource ? SourceStructureOptions : TargetStructureOptions;
            var resetSelection   = isSource
                ? (Action)(() => SelectedSourceStructure = NoMaskSentinel)
                : (Action)(() => SelectedTargetStructure = NoMaskSentinel);

            void SetList(IEnumerable<string> ids)
            {
                targetCollection.Clear();
                targetCollection.Add(NoMaskSentinel);
                foreach (var id in ids.OrderBy(s => s))
                    targetCollection.Add(id);
                resetSelection();
            }

            if (plan == null || _ew == null)
            {
                if (_ui != null) _ui.Invoke(() => SetList(Enumerable.Empty<string>()));
                else             SetList(Enumerable.Empty<string>());
                return;
            }

            List<string> structureIds = null;
            await _ew.AsyncRunPatientContext(p =>
            {
                try
                {
                    var course = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, plan.CourseId, StringComparison.OrdinalIgnoreCase));
                    var planSetup = course?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, plan.Id, StringComparison.OrdinalIgnoreCase));
                    structureIds = planSetup?.StructureSet?.Structures
                        .Where(s => !s.IsEmpty)
                        .Select(s => s.Id)
                        .ToList()
                        ?? new List<string>();
                }
                catch { structureIds = new List<string>(); }
            });

            _ui.Invoke(() => SetList(structureIds ?? Enumerable.Empty<string>()));
        }

        private void NotifyStatusChanged()
        {
            RaisePropertyChangedEvent(nameof(Working));
            RaisePropertyChangedEvent(nameof(StatusMessage));
            RaisePropertyChangedEvent(nameof(StatusColor));
            RaisePropertyChangedEvent(nameof(SuccessVisibility));
            RaisePropertyChangedEvent(nameof(ErrorVisibility));
            RaisePropertyChangedEvent(nameof(RunButtonVisibility));
        }
    }
}
