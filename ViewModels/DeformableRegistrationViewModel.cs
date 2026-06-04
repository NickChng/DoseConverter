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
        private Model _model;
        private Dispatcher _ui;
        private DeformableRegistrationService _dirService;
        private DoseConverterConfigRegistrationParameters _regParams;

        /// <summary>Retained payload from the most recent successful DIR run, used for DICOM export.</summary>
        private DeformableRegistrationService.DirExportData _lastExportData;

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
                LoadRigidRegistrationsForCurrentPlans();
                ValidateInputs();
            }
        }

        // Target (fixed) image is chosen from the patient's structure sets, not plans.
        public ObservableCollection<StructureSetSelectionViewModel> AllTargetOptions { get; private set; }
            = new ObservableCollection<StructureSetSelectionViewModel>
            {
                new StructureSetSelectionViewModel("DesignSS", "DesignCourse", "DesignImage")
            };

        private StructureSetSelectionViewModel _selectedTargetSS;
        public StructureSetSelectionViewModel SelectedTargetSS
        {
            get => _selectedTargetSS;
            set
            {
                _selectedTargetSS = value;
                RaisePropertyChangedEvent(nameof(SelectedTargetSS));
                SetDefaultOutputPlanName();
                LoadStructuresForSS(value);
                LoadRigidRegistrationsForCurrentPlans();
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
        // Rigid initial registration selection
        // -----------------------------------------------------------------------

        public ObservableCollection<RigidRegistrationOption> RigidRegistrationOptions { get; private set; }
            = new ObservableCollection<RigidRegistrationOption> { new RigidRegistrationOption() };

        private RigidRegistrationOption _selectedRigidRegistration;
        public RigidRegistrationOption SelectedRigidRegistration
        {
            get => _selectedRigidRegistration;
            set { _selectedRigidRegistration = value; RaisePropertyChangedEvent(nameof(SelectedRigidRegistration)); }
        }

        // -----------------------------------------------------------------------
        // Registration algorithm selection
        // -----------------------------------------------------------------------

        /// <summary>A selectable registration algorithm presented in the UI.</summary>
        public sealed class AlgorithmOption
        {
            public RegistrationAlgorithmType Value { get; }
            public string DisplayString { get; }
            public string Description { get; }
            public AlgorithmOption(RegistrationAlgorithmType value, string display, string description)
            {
                Value = value; DisplayString = display; Description = description;
            }
            public override string ToString() => DisplayString;
        }

        public ObservableCollection<AlgorithmOption> AlgorithmOptions { get; }
            = new ObservableCollection<AlgorithmOption>
            {
                new AlgorithmOption(RegistrationAlgorithmType.Demons,
                    "Diffeomorphic Demons (best without masks)",
                    "PDE/Thirion demons. Smooth, fast CT-CT registration. Masks are only approximated by zeroing intensity outside the contour, so out-of-body anatomy (e.g. bolus) can still bias the result near the body surface."),
                new AlgorithmOption(RegistrationAlgorithmType.BSpline,
                    "B-spline + metric masks (excludes out-of-body)",
                    "ImageRegistrationMethod with Mattes mutual information and TRUE metric masks. Voxels outside the selected body masks are excluded from the optimisation itself, so a bolus present on only one image cannot pull tissue. Recommended when masks are used."),
            };

        private AlgorithmOption _selectedAlgorithm;
        public AlgorithmOption SelectedAlgorithm
        {
            get => _selectedAlgorithm;
            set
            {
                _selectedAlgorithm = value;
                if (_regParams != null && value != null)
                    _regParams.RegistrationAlgorithm = value.Value;
                RaisePropertyChangedEvent(nameof(SelectedAlgorithm));
                RaisePropertyChangedEvent(nameof(SelectedAlgorithmDescription));
                RaisePropertyChangedEvent(nameof(IsBSplineSelected));
                RaisePropertyChangedEvent(nameof(IsDemonsSelected));
            }
        }

        public string SelectedAlgorithmDescription => _selectedAlgorithm?.Description ?? string.Empty;
        public bool IsBSplineSelected => _selectedAlgorithm?.Value == RegistrationAlgorithmType.BSpline;
        public bool IsDemonsSelected => _selectedAlgorithm?.Value != RegistrationAlgorithmType.BSpline;

        // -----------------------------------------------------------------------
        // Registration parameter overrides (most influential knobs)
        // -----------------------------------------------------------------------

        /// <summary>
        /// B-spline control-point spacing in mm over the body region. The grid mesh is derived from
        /// this spacing and the (auto-detected or masked) body extent, so it is FOV-independent.
        /// Smaller = finer local deformation. Replaces the old raw grid-node count in the UI.
        /// </summary>
        public string RegControlPointSpacing
        {
            get => (_regParams != null && _regParams.BSplineControlPointSpacing > 0
                        ? _regParams.BSplineControlPointSpacing : 20.0)
                   .ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
            set
            {
                if (_regParams != null
                    && double.TryParse(value, System.Globalization.NumberStyles.Float,
                                       System.Globalization.CultureInfo.InvariantCulture, out double mm)
                    && mm > 0)
                {
                    _regParams.BSplineControlPointSpacing = mm;
                }
                RaisePropertyChangedEvent(nameof(RegControlPointSpacing));
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

        /// <summary>Gaussian standard deviation(s) for displacement-field smoothing in diffeomorphic demons, e.g. "1.0".</summary>
        public string RegDemonsStdDev
        {
            get => _regParams?.DemonsStandardDeviations ?? "1.0";
            set
            {
                if (_regParams != null) _regParams.DemonsStandardDeviations = value;
                RaisePropertyChangedEvent(nameof(RegDemonsStdDev));
            }
        }

        // -----------------------------------------------------------------------
        // Smoothing slider (maps 1.0–3.0 to "Flexible"–"Smooth")
        // -----------------------------------------------------------------------

        private const double SmoothingSliderMin = 1.0;
        private const double SmoothingSliderMax = 3.0;

        /// <summary>
        /// Smoothing σ as a double, kept in sync with RegDemonsStdDev.
        /// Setting this from the slider also clears the site selection to "(custom)".
        /// </summary>
        private double _smoothingSigma = 1.5;
        public double SmoothingSigma
        {
            get => _smoothingSigma;
            set
            {
                _smoothingSigma = value;
                // Round to 2 dp for display; update the underlying config string.
                if (_regParams != null)
                    _regParams.DemonsStandardDeviations = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                RaisePropertyChangedEvent(nameof(SmoothingSigma));
                RaisePropertyChangedEvent(nameof(RegDemonsStdDev));
                RaisePropertyChangedEvent(nameof(SmoothingLabel));
                // If the new value doesn't match the selected preset, switch to Custom.
                if (_selectedSite != null && Math.Abs(_selectedSite.StdDev - value) > 0.05)
                {
                    _selectedSite = null;
                    RaisePropertyChangedEvent(nameof(SelectedSite));
                }
            }
        }

        /// <summary>"Flexible", "Smooth", or intermediate numeric label shown beside the slider.</summary>
        public string SmoothingLabel =>
            _smoothingSigma <= SmoothingSliderMin + 0.05 ? "Flexible (1.0)" :
            _smoothingSigma >= SmoothingSliderMax - 0.05 ? "Smooth (3.0)" :
            _smoothingSigma.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        // -----------------------------------------------------------------------
        // Max step length
        // -----------------------------------------------------------------------

        public double RegMaxStepLength
        {
            get => _regParams?.DemonsMaxStepLength ?? 2.0;
            set
            {
                if (_regParams != null) _regParams.DemonsMaxStepLength = value;
                RaisePropertyChangedEvent(nameof(RegMaxStepLength));
            }
        }

        // -----------------------------------------------------------------------
        // Anatomical site presets
        // -----------------------------------------------------------------------

        /// <summary>A single site preset record.</summary>
        public sealed class SitePreset
        {
            public string Name             { get; }
            public double StdDev           { get; }   // smoothing σ
            public string Iterations       { get; }   // MaxIterationsPerLevel string
            public double MaxStepLength    { get; }
            public double BSplineSpacingMm { get; }   // B-spline control-point spacing (mm); 0 = use config default

            public SitePreset(string name, double stdDev, string iters, double maxStep, double bsplineSpacingMm = 0)
            {
                Name = name; StdDev = stdDev; Iterations = iters; MaxStepLength = maxStep;
                BSplineSpacingMm = bsplineSpacingMm;
            }
            public override string ToString() => Name;
        }

        public static readonly SitePreset CustomSitePreset = new SitePreset("(custom)", 1.5, "75 50 20", 2.0);

        // Built-in fallback presets used when the XML config contains no SitePresets element.
        // BSplineSpacingMm (last arg) sets the B-spline control-point spacing for that site.
        private static readonly IReadOnlyList<SitePreset> _fallbackPresets = new List<SitePreset>
        {
            new SitePreset("Brain",                 2.5, "50 50 20",  1.0, 20),
            new SitePreset("Head & Neck",           2.0, "50 50 20",  1.5, 15),
            new SitePreset("Thorax / Lung",         1.0, "75 75 20",  3.0, 20),
            new SitePreset("Abdomen",               1.5, "75 50 20",  2.5, 20),
            new SitePreset("Pelvis / Bladder",      1.5, "75 50 20",  3.0, 20),
        };

        // The ObservableCollection presented to the combo; includes Custom at the bottom.
        // Populated at Initialize time from the XML config (or the fallback list above).
        public ObservableCollection<SitePreset> SitePresets { get; } = new ObservableCollection<SitePreset>();

        /// <summary>
        /// Rebuilds <see cref="SitePresets"/> from the loaded config, falling back to the
        /// built-in list if the XML contains no <c>SitePresets</c> element.
        /// </summary>
        private void LoadSitePresetsFromConfig(DoseConverterConfig config)
        {
            SitePresets.Clear();
            var configPresets = config?.SitePresets;
            if (configPresets != null && configPresets.Length > 0)
            {
                foreach (var cp in configPresets)
                    SitePresets.Add(new SitePreset(cp.Name, cp.StdDev, cp.Iterations, cp.MaxStepLength, cp.BSplineSpacing));
            }
            else
            {
                foreach (var p in _fallbackPresets)
                    SitePresets.Add(p);
            }
            SitePresets.Add(CustomSitePreset);
        }

        private SitePreset _selectedSite;
        public SitePreset SelectedSite
        {
            get => _selectedSite;
            set
            {
                _selectedSite = value;
                RaisePropertyChangedEvent(nameof(SelectedSite));
                if (value != null && !ReferenceEquals(value, CustomSitePreset))
                    ApplySitePreset(value);
            }
        }

        private void ApplySitePreset(SitePreset preset)
        {
            _smoothingSigma = preset.StdDev;
            if (_regParams != null)
            {
                _regParams.DemonsStandardDeviations = preset.StdDev.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
                _regParams.MaxIterationsPerLevel    = preset.Iterations;
                _regParams.DemonsMaxStepLength      = preset.MaxStepLength;
                // B-spline control-point spacing for this site (0 = leave the config default in place).
                if (preset.BSplineSpacingMm > 0)
                    _regParams.BSplineControlPointSpacing = preset.BSplineSpacingMm;
            }
            RaisePropertyChangedEvent(nameof(SmoothingSigma));
            RaisePropertyChangedEvent(nameof(RegDemonsStdDev));
            RaisePropertyChangedEvent(nameof(RegMaxIterationsPerLevel));
            RaisePropertyChangedEvent(nameof(RegMaxStepLength));
            RaisePropertyChangedEvent(nameof(RegControlPointSpacing));
            RaisePropertyChangedEvent(nameof(SmoothingLabel));
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
        private bool   _isPlanNameValid  = true;   // false while async check is pending or name is taken

        public string DeformedPlanName
        {
            get => _deformedPlanName;
            set
            {
                _deformedPlanName = value != null && value.Length > 13
                    ? value.Substring(0, 13) : (value ?? string.Empty);
                RaisePropertyChangedEvent(nameof(DeformedPlanName));
                // Mark invalid optimistically while the async check runs so the button
                // stays disabled until we know the name is free.
                _isPlanNameValid = false;
                ValidateInputs();
                _ = ValidateDeformedPlanNameAsync(_deformedPlanName);
            }
        }

        /// <summary>Orange highlight on the plan-name textbox when the name is already in use.</summary>
        public SolidColorBrush DeformedPlanNameBackground { get; private set; }
            = new SolidColorBrush(Colors.Transparent);

        /// <summary>Tooltip text shown on the textbox when the name is already taken.</summary>
        public string DeformedPlanNameError { get; private set; } = string.Empty;

        // -----------------------------------------------------------------------
        // Status / progress
        // -----------------------------------------------------------------------

        public bool Working { get; private set; } = false;
        public string StatusMessage { get; private set; } = "Select source and target plans to begin.";
        public SolidColorBrush StatusColor { get; private set; } = new SolidColorBrush(Colors.Transparent);
        public Visibility SuccessVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility ErrorVisibility { get; private set; } = Visibility.Collapsed;
        public Visibility RunButtonVisibility { get; private set; } = Visibility.Collapsed;

        /// <summary>Export-to-DICOM button is revealed only after a successful DIR run.</summary>
        public Visibility ExportButtonVisibility { get; private set; } = Visibility.Collapsed;

        public string ErrorDetails { get; private set; } = "";
        public bool IsErrorDetailsOpen { get; set; } = false;

        // -----------------------------------------------------------------------
        // DICOM export directory
        // -----------------------------------------------------------------------

        private string _exportDirectory = string.Empty;

        /// <summary>
        /// Output directory for deformed DICOM export.  The user may type or paste any path
        /// (including UNC paths) directly.  Defaults to the value from config, or the
        /// assembly directory if the config attribute is absent.
        /// </summary>
        public string ExportDirectory
        {
            get => _exportDirectory;
            set
            {
                _exportDirectory = value ?? string.Empty;
                RaisePropertyChangedEvent(nameof(ExportDirectory));
            }
        }

        // -----------------------------------------------------------------------
        // Commands
        // -----------------------------------------------------------------------

        public ICommand RunDIRCommand => new DelegateCommand(RunDIR);
        public ICommand ErrorDetailsButtonCommand => new DelegateCommand(_ => { IsErrorDetailsOpen ^= true; });
        public ICommand ExportDicomCommand => new DelegateCommand(ExportDicom);
        public ICommand BrowseExportDirectoryCommand => new DelegateCommand(BrowseExportDirectory);

        // -----------------------------------------------------------------------
        // Events
        // -----------------------------------------------------------------------

        /// <summary>
        /// Raised on the UI dispatcher once a DIR run completes successfully and review
        /// imagery is available. The parent view model uses this to reveal the DIR
        /// quality review tab and load the blended overlay.
        /// </summary>
        public event EventHandler<DeformableRegistrationService.DirReviewData> DirCompleted;

        // -----------------------------------------------------------------------
        // Construction / initialisation
        // -----------------------------------------------------------------------

        public DeformableRegistrationViewModel() { }

        public DeformableRegistrationViewModel(EsapiWorker ew, Model model)
        {
            _ew  = ew;
            _model = model;
            _ui  = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
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
            _ew    = ew;
            _model = model;
            _ui    = uiDispatcher;
            _dirService = new DeformableRegistrationService(ew, model);
            _regParams = model?.Config?.RegistrationParameters
                         ?? new DoseConverterConfigRegistrationParameters();
            _ui.Invoke(() =>
            {
                AllPlanOptions.Clear();
                RigidRegistrationOptions.Clear();
                RigidRegistrationOptions.Add(new RigidRegistrationOption());
                SelectedRigidRegistration = RigidRegistrationOptions[0];
                LoadSitePresetsFromConfig(model?.Config);
                // Sync smoothing slider from loaded config.
                _smoothingSigma = ParseDoubleOrDefault(_regParams?.DemonsStandardDeviations, 1.5);
                // Sync algorithm selection from loaded config (default Demons).
                var configAlgo = _regParams?.RegistrationAlgorithm ?? RegistrationAlgorithmType.Demons;
                _selectedAlgorithm = AlgorithmOptions.FirstOrDefault(a => a.Value == configAlgo)
                                     ?? AlgorithmOptions[0];
                RaisePropertyChangedEvent(nameof(SelectedAlgorithm));
                RaisePropertyChangedEvent(nameof(SelectedAlgorithmDescription));
                RaisePropertyChangedEvent(nameof(IsBSplineSelected));
                RaisePropertyChangedEvent(nameof(IsDemonsSelected));
                RaisePropertyChangedEvent(nameof(RegControlPointSpacing));
                RaisePropertyChangedEvent(nameof(RegMaxIterationsPerLevel));
                RaisePropertyChangedEvent(nameof(RegSamplingPercent));
                RaisePropertyChangedEvent(nameof(RegDemonsStdDev));
                RaisePropertyChangedEvent(nameof(SmoothingSigma));
                RaisePropertyChangedEvent(nameof(SmoothingLabel));
                RaisePropertyChangedEvent(nameof(RegMaxStepLength));
                // Seed the default export directory: config value takes priority;
                // fall back to the assembly directory so UNC paths work out of the box.
                var configDir = _regParams?.DicomExportDirectory;
                if (!string.IsNullOrWhiteSpace(configDir))
                {
                    ExportDirectory = configDir;
                }
                else
                {
                    try
                    {
                        var loc = System.Reflection.Assembly.GetExecutingAssembly().Location;
                        ExportDirectory = string.IsNullOrEmpty(loc)
                            ? AppDomain.CurrentDomain.BaseDirectory
                            : System.IO.Path.GetDirectoryName(loc);
                    }
                    catch { ExportDirectory = string.Empty; }
                }
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
            }

            if (_ui != null)
                _ui.Invoke(Apply);
            else
                Apply();
        }

        /// <summary>
        /// Called by the parent ViewModel once structure sets have been loaded.
        /// Populates the target (fixed) image dropdown.
        /// </summary>
        public void SetAvailableTargets(ObservableCollection<StructureSetSelectionViewModel> structureSets)
        {
            void Apply()
            {
                AllTargetOptions.Clear();
                foreach (var ss in structureSets)
                    AllTargetOptions.Add(ss);

                SelectedTargetSS = AllTargetOptions.FirstOrDefault();
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

            // Re-check the output plan name immediately before starting so that a
            // second run with an unchanged (now-taken) name is caught at click time
            // rather than after the registration completes.
            await ValidateDeformedPlanNameAsync(_deformedPlanName);
            if (!_isPlanNameValid)
            {
                // ValidateInputs will have already updated the status message and
                // hidden the Run button; just return.
                return;
            }

            Working = true;
            SuccessVisibility = Visibility.Collapsed;
            ErrorVisibility = Visibility.Collapsed;
            ExportButtonVisibility = Visibility.Collapsed;
            RaisePropertyChangedEvent(nameof(ExportButtonVisibility));
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

            (ScriptStatus status, string message, DeformableRegistrationService.DirReviewData review, DeformableRegistrationService.DirExportData export) result;
            try
            {
                result = await _dirService.PerformDIRAndWritePlan(
                    _selectedSourcePlan.CourseId,
                    _selectedSourcePlan.Id,
                    _selectedTargetSS.Id,
                    DeformedPlanName,
                    _selectedSourceStructure == NoMaskSentinel ? null : _selectedSourceStructure,
                    _selectedTargetStructure == NoMaskSentinel ? null : _selectedTargetStructure,
                    progressReporter,
                    _selectedRigidRegistration?.MatrixRowMajor,
                    _selectedAlgorithm?.Value ?? RegistrationAlgorithmType.Demons);
            }
            catch (Exception ex)
            {
                result = (ScriptStatus.Error, $"Unexpected error: {ex.Message}", null, null);
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
                    if (result.review != null)
                        DirCompleted?.Invoke(this, result.review);
                    // Retain export payload and reveal the DICOM export control.
                    _lastExportData = result.export;
                    ExportButtonVisibility = (_lastExportData != null)
                        ? Visibility.Visible : Visibility.Collapsed;
                    RaisePropertyChangedEvent(nameof(ExportButtonVisibility));
                    // Leave _isPlanNameValid as-is so the completion message is not
                    // immediately overwritten by a re-validation status.  The name will
                    // be re-checked at the start of the next RunDIR invocation.
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
        // DICOM export
        // -----------------------------------------------------------------------

        /// <summary>
        /// Opens a fallback folder-picker dialog to let the user browse to an output directory.
        /// The user can also type or paste a path (including UNC paths) directly in the text box.
        /// </summary>
        private void BrowseExportDirectory(object param = null)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "Select a folder to export the deformed CT and structure set as DICOM.";
                dialog.ShowNewFolderButton = true;
                if (!string.IsNullOrWhiteSpace(ExportDirectory)
                    && System.IO.Directory.Exists(ExportDirectory))
                    dialog.SelectedPath = ExportDirectory;

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
                    && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
                    ExportDirectory = dialog.SelectedPath;
            }
        }

        /// <summary>
        /// Exports the deformed CT series and warped structure set as DICOM to
        /// <see cref="ExportDirectory"/>, preserving relational patient/study tags.
        /// </summary>
        private async void ExportDicom(object param = null)
        {
            if (_lastExportData == null)
            {
                StatusMessage = "No deformed result available to export. Run DIR first.";
                NotifyStatusChanged();
                return;
            }

            if (Working) return;

            var outputDirectory = ExportDirectory?.Trim();
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                StatusMessage = "Please enter or browse to an output directory before exporting.";
                NotifyStatusChanged();
                return;
            }

            Working = true;
            SuccessVisibility = Visibility.Collapsed;
            ErrorVisibility = Visibility.Collapsed;
            StatusColor = new SolidColorBrush(Colors.Transparent);
            StatusMessage = "Exporting deformed DICOM...";
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
                result = await _dirService.ExportDeformedDicom(
                    _lastExportData, outputDirectory, progressReporter);
            }
            catch (Exception ex)
            {
                result = (ScriptStatus.Error, $"Unexpected error: {ex.Message}");
                Helpers.SeriLog.LogError("Unexpected DICOM export error", ex);
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
                    StatusMessage = "DICOM export failed. Click for details.";
                    ErrorDetails = result.message;
                    ErrorVisibility = Visibility.Visible;
                    SuccessVisibility = Visibility.Collapsed;
                    break;
            }
            // Keep the export button available so the user can retry / re-export.
            ExportButtonVisibility = Visibility.Visible;
            RaisePropertyChangedEvent(nameof(ExportButtonVisibility));
            NotifyStatusChanged();
        }

        // -----------------------------------------------------------------------
        // Validation
        // -----------------------------------------------------------------------

        private bool CanRun()
        {
            return _selectedSourcePlan != null
                && _selectedTargetSS != null
                && !string.IsNullOrWhiteSpace(DeformedPlanName)
                && _isPlanNameValid
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
            else if (_selectedTargetSS == null)
            {
                StatusMessage = "Select a target (fixed) image.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (string.Equals(_selectedSourcePlan.SsId, _selectedTargetSS.Id, StringComparison.OrdinalIgnoreCase))
            {
                StatusMessage = "Source plan and target image must use different structure sets.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (string.IsNullOrWhiteSpace(DeformedPlanName))
            {
                StatusMessage = "Enter a name for the output deformed dose plan.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else if (!_isPlanNameValid)
            {
                StatusMessage = "Output plan name already exists in the target course — choose a different name.";
                RunButtonVisibility = Visibility.Collapsed;
            }
            else
            {
                StatusMessage = "Ready.";
                RunButtonVisibility = Visibility.Visible;
            }
            RaisePropertyChangedEvent(nameof(StatusMessage));
            RaisePropertyChangedEvent(nameof(RunButtonVisibility));
        }

        /// <summary>
        /// Asynchronously checks whether <paramref name="name"/> is already in use within
        /// the target plan's course.  Updates <see cref="DeformedPlanNameBackground"/> and
        /// re-runs <see cref="ValidateInputs"/> on the UI thread once the result is known.
        /// </summary>
        private async Task ValidateDeformedPlanNameAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                // Empty name is already handled by ValidateInputs; keep flag false so
                // the button stays disabled.
                ApplyNameValidationResult(name, isValid: false);
                return;
            }

            // If we have no ESAPI context yet, optimistically allow the name through so
            // the UI is not stuck in an error state at design / init time.
            if (_ew == null || _selectedSourcePlan == null)
            {
                ApplyNameValidationResult(name, isValid: true);
                return;
            }

            bool nameIsFree = false;
            string targetCourseId = _selectedSourcePlan.CourseId;
            await _ew.AsyncRunPatientContext(patient =>
            {
                var course = patient.Courses.FirstOrDefault(c =>
                    string.Equals(c.Id, targetCourseId, StringComparison.OrdinalIgnoreCase));
                if (course == null)
                {
                    nameIsFree = true;
                    return;
                }
                nameIsFree = !course.PlanSetups.Any(p =>
                    string.Equals(p.Id, name, StringComparison.OrdinalIgnoreCase));
            });

            // Only apply if the field hasn't changed again while we were awaiting.
            if (string.Equals(name, _deformedPlanName, StringComparison.Ordinal))
                ApplyNameValidationResult(name, nameIsFree);
        }

        private void ApplyNameValidationResult(string name, bool isValid)
        {
            _isPlanNameValid = isValid;

            var newBrush = isValid
                ? new SolidColorBrush(Colors.Transparent)
                : new SolidColorBrush(Colors.DarkOrange);
            var newError = isValid
                ? string.Empty
                : $"A plan named \"{name}\" already exists in the target course. Choose a different name.";

            void Apply()
            {
                DeformedPlanNameBackground = newBrush;
                DeformedPlanNameError = newError;
                RaisePropertyChangedEvent(nameof(DeformedPlanNameBackground));
                RaisePropertyChangedEvent(nameof(DeformedPlanNameError));
                ValidateInputs();
            }

            if (_ui != null)
                _ui.Invoke(Apply);
            else
                Apply();
        }

        private void SetDefaultOutputPlanName()
        {
            if (_selectedSourcePlan == null) return;
            string srcShort = _selectedSourcePlan.Id.Substring(0, Math.Min(_selectedSourcePlan.Id.Length, 9));
            // Go through the public setter so the async uniqueness check is triggered.
            DeformedPlanName = srcShort + "_DIR";
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

        /// <summary>
        /// Asynchronously queries ESAPI for the structure IDs in the given structure set
        /// and populates <see cref="TargetStructureOptions"/>.
        /// target images' CT frames of reference and populates
        /// </summary>
        private async void LoadStructuresForSS(StructureSetSelectionViewModel ss)
        {
            void SetList(IEnumerable<string> ids)
            {
                TargetStructureOptions.Clear();
                TargetStructureOptions.Add(NoMaskSentinel);
                foreach (var id in ids.OrderBy(s => s))
                    TargetStructureOptions.Add(id);
                SelectedTargetStructure = NoMaskSentinel;
            }

            if (ss == null || _ew == null)
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
                    var structureSet = p.StructureSets.FirstOrDefault(s =>
                        string.Equals(s.Id, ss.Id, StringComparison.OrdinalIgnoreCase));
                    structureIds = structureSet?.Structures
                        .Where(s => !s.IsEmpty)
                        .Select(s => s.Id)
                        .ToList()
                        ?? new List<string>();
                }
                catch { structureIds = new List<string>(); }
            });

            _ui.Invoke(() => SetList(structureIds ?? Enumerable.Empty<string>()));
        }

        /// <summary>
        /// Asynchronously loads ESAPI rigid registrations that link the current source and
        /// target images' CT frames of reference and populates
        /// <see cref="RigidRegistrationOptions"/>.
        /// </summary>
        private async void LoadRigidRegistrationsForCurrentPlans()
        {
            var src = _selectedSourcePlan;
            var tgtSS = _selectedTargetSS;

            void ResetToNone()
            {
                RigidRegistrationOptions.Clear();
                RigidRegistrationOptions.Add(new RigidRegistrationOption());
                SelectedRigidRegistration = RigidRegistrationOptions[0];
            }

            if (src == null || tgtSS == null || _ew == null)
            {
                if (_ui != null) _ui.Invoke(ResetToNone); else ResetToNone();
                return;
            }

            var options = new List<RigidRegistrationOption> { new RigidRegistrationOption() };

            await _ew.AsyncRunPatientContext(p =>
            {
                try
                {
                    var sourceCourse = p.Courses.FirstOrDefault(c =>
                        string.Equals(c.Id, src.CourseId, StringComparison.OrdinalIgnoreCase));
                    var sourcePlan = sourceCourse?.PlanSetups.FirstOrDefault(pl =>
                        string.Equals(pl.Id, src.Id, StringComparison.OrdinalIgnoreCase));
                    var targetStructureSet = p.StructureSets.FirstOrDefault(s =>
                        string.Equals(s.Id, tgtSS.Id, StringComparison.OrdinalIgnoreCase));

                    string srcFOR = sourcePlan?.StructureSet?.Image?.FOR;
                    string tgtFOR = targetStructureSet?.Image?.FOR;

                    if (string.IsNullOrEmpty(srcFOR) || string.IsNullOrEmpty(tgtFOR))
                        return;

                    // Skip if both images share the same CT.
                    if (string.Equals(srcFOR, tgtFOR, StringComparison.OrdinalIgnoreCase))
                        return;

                    foreach (var reg in p.Registrations)
                    {
                        bool srcToTgt = string.Equals(reg.SourceFOR, srcFOR, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(reg.RegisteredFOR, tgtFOR, StringComparison.OrdinalIgnoreCase);
                        bool tgtToSrc = string.Equals(reg.SourceFOR, tgtFOR, StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(reg.RegisteredFOR, srcFOR, StringComparison.OrdinalIgnoreCase);

                        if (!srcToTgt && !tgtToSrc)
                            continue;

                        double[,] matrix2d = reg.TransformationMatrix;
                        if (matrix2d == null || matrix2d.Length != 16)
                            continue;

                        // Flatten row-major.
                        double[] matrix = new double[16];
                        for (int r = 0; r < 4; r++)
                            for (int c = 0; c < 4; c++)
                                matrix[r * 4 + c] = matrix2d[r, c];

                        // If stored as tgt→src, invert so we have src→tgt.
                        if (tgtToSrc)
                            matrix = InvertMatrix4x4(matrix);

                        string label = $"{reg.Id}  [{reg.Status}]";
                        options.Add(new RigidRegistrationOption(label, matrix));
                    }
                }
                catch { /* fall back to sentinel only */ }
            });

            _ui.Invoke(() =>
            {
                RigidRegistrationOptions.Clear();
                foreach (var o in options)
                    RigidRegistrationOptions.Add(o);
                SelectedRigidRegistration = RigidRegistrationOptions[0];
            });
        }

        /// <summary>Inverts a row-major 4×4 rigid homogeneous matrix using R^-1 = R^T.</summary>
        private static double[] InvertMatrix4x4(double[] m)
        {
            double[] inv = new double[16];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    inv[r * 4 + c] = m[c * 4 + r];
            for (int r = 0; r < 3; r++)
            {
                double sum = 0;
                for (int c = 0; c < 3; c++)
                    sum += inv[r * 4 + c] * m[c * 4 + 3];
                inv[r * 4 + 3] = -sum;
            }
            inv[12] = 0; inv[13] = 0; inv[14] = 0; inv[15] = 1;
            return inv;
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

        private static double ParseDoubleOrDefault(string s, double fallback)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            var first = s.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
            return double.TryParse(first, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : fallback;
        }
    }
}
