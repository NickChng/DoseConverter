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
    /// <summary>
    /// Row representing one source plan in the dose-accumulation list.
    /// </summary>
    public class AccumulationSourceRow : ObservableObject
    {
        public ObservableCollection<PlanSelectionViewModel> AvailablePlans { get; }

        public AccumulationSourceRow(ObservableCollection<PlanSelectionViewModel> available)
        {
            AvailablePlans = available;
        }

        private PlanSelectionViewModel _plan;
        public PlanSelectionViewModel Plan
        {
            get => _plan;
            set { _plan = value; RaisePropertyChangedEvent(nameof(Plan)); }
        }

        private string _fractionsOverride = "";
        /// <summary>Optional integer override of the plan's NumberOfFractions. Blank = use plan value.</summary>
        public string FractionsOverride
        {
            get => _fractionsOverride;
            set { _fractionsOverride = value; RaisePropertyChangedEvent(nameof(FractionsOverride)); }
        }
    }

    /// <summary>
    /// ViewModel for the Dose Accumulation tab. Users pick N source plans, a target plan,
    /// an α/β, and an output plan name; the service runs DIR per source, converts each
    /// deformed dose to EQD2, and writes a single summed verification plan.
    /// </summary>
    public class DoseAccumulationViewModel : ObservableObject
    {
        private EsapiWorker _ew;
        private Dispatcher _ui;
        private DoseAccumulationService _accumulationService;

        public ObservableCollection<PlanSelectionViewModel> AllPlanOptions { get; private set; }
            = new ObservableCollection<PlanSelectionViewModel>();

        public ObservableCollection<AccumulationSourceRow> SourceRows { get; private set; }
            = new ObservableCollection<AccumulationSourceRow>();

        private PlanSelectionViewModel _selectedTargetPlan;
        public PlanSelectionViewModel SelectedTargetPlan
        {
            get => _selectedTargetPlan;
            set
            {
                _selectedTargetPlan = value;
                RaisePropertyChangedEvent(nameof(SelectedTargetPlan));
                SetDefaultOutputPlanName();
                ValidateInputs();
            }
        }

        private string _alphaBeta = "3";
        public string AlphaBeta
        {
            get => _alphaBeta;
            set { _alphaBeta = value; RaisePropertyChangedEvent(nameof(AlphaBeta)); ValidateInputs(); }
        }

        private string _outputPlanName = "";
        public string OutputPlanName
        {
            get => _outputPlanName;
            set
            {
                string safe = value ?? string.Empty;
                _outputPlanName = safe.Length > 13 ? safe.Substring(0, 13) : safe;
                RaisePropertyChangedEvent(nameof(OutputPlanName));
                ValidateInputs();
            }
        }

        public bool Working { get; private set; } = false;
        public string StatusMessage { get; private set; } = "Add source plans, choose a target, then accumulate.";
        public Visibility SuccessVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility ErrorVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility RunButtonVisibility { get; private set; } = Visibility.Collapsed;
        public string ErrorDetails { get; private set; } = "";
        public bool IsErrorDetailsOpen { get; set; } = false;

        public ICommand AddSourceCommand => new DelegateCommand(_ => AddSource());
        public ICommand RemoveSourceCommand => new DelegateCommand(row => RemoveSource(row as AccumulationSourceRow));
        public ICommand RunAccumulationCommand => new DelegateCommand(_ => RunAccumulation());
        public ICommand ErrorDetailsButtonCommand => new DelegateCommand(_ => { IsErrorDetailsOpen ^= true; RaisePropertyChangedEvent(nameof(IsErrorDetailsOpen)); });

        public DoseAccumulationViewModel() { }

        public DoseAccumulationViewModel(EsapiWorker ew, Model model)
            : this(ew, model, new DeformableRegistrationService(ew, model)) { }

        public DoseAccumulationViewModel(EsapiWorker ew, Model model, DeformableRegistrationService dirService)
        {
            _ew = ew;
            _ui = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
            _accumulationService = new DoseAccumulationService(ew, model, dirService);
        }

        /// <summary>
        /// Initialises the view model in-place (without replacing the instance) so that
        /// existing WPF bindings remain valid.
        /// </summary>
        public void Initialize(EsapiWorker ew, Model model, Dispatcher uiDispatcher)
        {
            _ew = ew;
            _ui = uiDispatcher;
            _accumulationService = new DoseAccumulationService(ew, model, new DeformableRegistrationService(ew, model));
        }

        public void SetAvailablePlans(ObservableCollection<PlanSelectionViewModel> plans)
        {
            void Apply()
            {
                AllPlanOptions.Clear();
                foreach (var p in plans) AllPlanOptions.Add(p);
                SelectedTargetPlan = AllPlanOptions.FirstOrDefault();
                if (SourceRows.Count == 0) AddSource();
                ValidateInputs();
            }
            if (_ui != null) _ui.Invoke(Apply);
            else Apply();
        }

        private void AddSource()
        {
            SourceRows.Add(new AccumulationSourceRow(AllPlanOptions)
            {
                Plan = AllPlanOptions.FirstOrDefault()
            });
            ValidateInputs();
        }

        private void RemoveSource(AccumulationSourceRow row)
        {
            if (row != null) SourceRows.Remove(row);
            ValidateInputs();
        }

        private void SetDefaultOutputPlanName()
        {
            if (_selectedTargetPlan == null) return;
            string shortId = _selectedTargetPlan.Id.Substring(0, Math.Min(_selectedTargetPlan.Id.Length, 9));
            _outputPlanName = shortId + "_ACC";
            if (_outputPlanName.Length > 13) _outputPlanName = _outputPlanName.Substring(0, 13);
            RaisePropertyChangedEvent(nameof(OutputPlanName));
        }

        private void ValidateInputs()
        {
            if (Working) { RunButtonVisibility = Visibility.Collapsed; NotifyStatus(); return; }

            if (SourceRows.Count == 0 || SourceRows.Any(r => r.Plan == null))
            {
                StatusMessage = "Add at least one source plan.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (_selectedTargetPlan == null)
            {
                StatusMessage = "Select a target plan.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (string.IsNullOrWhiteSpace(_outputPlanName))
            {
                StatusMessage = "Enter an output plan name.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (!double.TryParse(_alphaBeta, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ab) || ab <= 0)
            {
                StatusMessage = "α/β must be a positive number.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else
            {
                StatusMessage = $"Ready to accumulate {SourceRows.Count} source plan(s) onto {_selectedTargetPlan.DisplayString}.";
                RunButtonVisibility = Visibility.Visible;
            }
            NotifyStatus();
        }

        private async void RunAccumulation()
        {
            if (RunButtonVisibility != Visibility.Visible) return;

            Working = true;
            SuccessVisibility = Visibility.Collapsed;
            ErrorVisibility = Visibility.Collapsed;
            StatusMessage = "Starting dose accumulation...";
            NotifyStatus();

            var progress = new Progress<string>(msg =>
            {
                _ui.Invoke(() =>
                {
                    StatusMessage = msg;
                    RaisePropertyChangedEvent(nameof(StatusMessage));
                });
            });

            double alphaBeta = double.Parse(_alphaBeta, System.Globalization.CultureInfo.InvariantCulture);

            var specs = SourceRows
                .Where(r => r.Plan != null)
                .Select(r =>
                {
                    int? fxOverride = null;
                    if (int.TryParse(r.FractionsOverride?.Trim(), out int fx) && fx > 0) fxOverride = fx;
                    return new AccumulationSourceSpec
                    {
                        CourseId = r.Plan.CourseId,
                        PlanId = r.Plan.Id,
                        OverrideFractions = fxOverride
                    };
                })
                .ToList();

            (ScriptStatus status, string message) result;
            try
            {
                result = await _accumulationService.AccumulateAndWritePlan(
                    specs, _selectedTargetPlan.CourseId, _selectedTargetPlan.Id,
                    _outputPlanName, alphaBeta, progress);
            }
            catch (Exception ex)
            {
                result = (ScriptStatus.Error, $"Unexpected error: {ex.Message}");
                Helpers.SeriLog.LogError("Unexpected accumulation error", ex);
            }

            Working = false;
            if (result.status == ScriptStatus.Complete)
            {
                StatusMessage = result.message;
                SuccessVisibility = Visibility.Visible;
                ErrorVisibility = Visibility.Collapsed;
            }
            else
            {
                StatusMessage = "Dose accumulation failed. Click for details.";
                ErrorDetails = result.message;
                ErrorVisibility = Visibility.Visible;
                SuccessVisibility = Visibility.Collapsed;
            }
            NotifyStatus();
            ValidateInputs();
        }

        private void NotifyStatus()
        {
            RaisePropertyChangedEvent(nameof(Working));
            RaisePropertyChangedEvent(nameof(StatusMessage));
            RaisePropertyChangedEvent(nameof(SuccessVisibility));
            RaisePropertyChangedEvent(nameof(ErrorVisibility));
            RaisePropertyChangedEvent(nameof(RunButtonVisibility));
            RaisePropertyChangedEvent(nameof(ErrorDetails));
        }
    }
}
