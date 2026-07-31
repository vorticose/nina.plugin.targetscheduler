using LinqKit;
using NINA.Astrometry;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Plugin.TargetScheduler.Controls.Util;
using NINA.Plugin.TargetScheduler.Database;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning;
using NINA.Plugin.TargetScheduler.Planning.Entities;
using NINA.Plugin.TargetScheduler.Planning.Explain;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using NINA.Plugin.TargetScheduler.Util;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NINA.WPF.Base.ViewModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using RelayCommand = CommunityToolkit.Mvvm.Input.RelayCommand;

namespace NINA.Plugin.TargetScheduler.Controls.PlanPreview {

    public class PlanPreviewerViewVM : BaseVM {
        private SchedulerDatabaseInteraction database;

        public PlanPreviewerViewVM(IProfileService profileService) : base(profileService) {
            database = new SchedulerDatabaseInteraction();
            InstructionList = new ObservableCollection<TreeViewItem>();

            profileService.ProfileChanged += ProfileService_ProfileChanged;
            profileService.Profiles.CollectionChanged += ProfileService_ProfileChanged;

            InitializeCriteria();

            SetNowCommand = new RelayCommand(SetPreviewTimeNow);
            PlanPreviewCommand = new RelayCommand(RunPlanPreview);
            PlanPreviewResultsCommand = new RelayCommand(RunPlanPreviewResults);
            PlanInsightCommand = new RelayCommand(RunPlanInsight);
            ExportInsightCommand = new RelayCommand(RunExportInsight);
            MoonAvoidanceCommand = new RelayCommand(RunMoonAvoidance);
        }

        private void ProfileService_ProfileChanged(object sender, EventArgs e) {
            InstructionList.Clear();
            SelectedProfileId = profileService.ActiveProfile.Id.ToString();
            ProfileChoices = GetProfileChoices();
        }

        private void InitializeCriteria() {
            PlanDate = DateTime.Now.Date;
            SelectedProfileId = profileService.ActiveProfile.Id.ToString();
            ProfileChoices = GetProfileChoices();

            MoonAvoidanceGroups = new AsyncObservableCollection<MoonAvoidanceTargetGroup>();

            ShowPlanPreview = true;
            ShowPlanPreviewResults = false;
            ShowMoonAvoidance = false;
            TableLoading = false;
        }

        private bool tableLoading;

        public bool TableLoading {
            get => tableLoading;
            set {
                tableLoading = value;
                RaisePropertyChanged(nameof(TableLoading));
            }
        }

        private DateTime planDate = DateTime.MinValue;

        public DateTime PlanDate {
            get => planDate;
            set {
                planDate = value;
                SchedulerPlans = null;
                RaisePropertyChanged(nameof(PlanDate));
            }
        }

        private int planHours = 13;

        public int PlanHours {
            get => planHours;
            set {
                planHours = value;
                SchedulerPlans = null;
                RaisePropertyChanged(nameof(PlanHours));
            }
        }

        private int planMinutes = 0;

        public int PlanMinutes {
            get => planMinutes;
            set {
                planMinutes = value;
                SchedulerPlans = null;
                RaisePropertyChanged(nameof(PlanMinutes));
            }
        }

        private int planSeconds = 0;

        public int PlanSeconds {
            get => planSeconds;
            set {
                planSeconds = value;
                SchedulerPlans = null;
                RaisePropertyChanged(nameof(PlanSeconds));
            }
        }

        private AsyncObservableCollection<KeyValuePair<string, string>> profileChoices;

        public AsyncObservableCollection<KeyValuePair<string, string>> ProfileChoices {
            get {
                return profileChoices;
            }
            set {
                profileChoices = value;
                SchedulerPlans = null;
                RaisePropertyChanged(nameof(ProfileChoices));
            }
        }

        private string selectedProfileId;

        public string SelectedProfileId {
            get => selectedProfileId;
            set {
                selectedProfileId = value;
                SchedulerPlans = null;
                RaisePropertyChanged(nameof(SelectedProfileId));
            }
        }

        private ObservableCollection<TreeViewItem> instructionList;

        public ObservableCollection<TreeViewItem> InstructionList {
            get => instructionList;
            set {
                instructionList = value;
                RaisePropertyChanged(nameof(InstructionList));
            }
        }

        private List<SchedulerPlan> SchedulerPlans { get; set; }

        private bool showPlanPreview;

        public bool ShowPlanPreview {
            get => showPlanPreview;
            set {
                showPlanPreview = value;
                RaisePropertyChanged(nameof(ShowPlanPreview));
            }
        }

        private bool showPlanPreviewResults;

        public bool ShowPlanPreviewResults {
            get => showPlanPreviewResults;
            set {
                showPlanPreviewResults = value;
                RaisePropertyChanged(nameof(ShowPlanPreviewResults));
            }
        }

        private bool showMoonAvoidance;

        public bool ShowMoonAvoidance {
            get => showMoonAvoidance;
            set {
                showMoonAvoidance = value;
                RaisePropertyChanged(nameof(ShowMoonAvoidance));
            }
        }

        public ICommand SetNowCommand { get; private set; }
        public ICommand PlanPreviewCommand { get; private set; }
        public ICommand PlanPreviewResultsCommand { get; private set; }
        public ICommand PlanInsightCommand { get; private set; }
        public ICommand ExportInsightCommand { get; private set; }

        // **CUSTOM FORK** Planning insight: timeline + scoring transparency for the current preview.
        private bool showTimeline;

        public bool ShowTimeline {
            get => showTimeline;
            set {
                showTimeline = value;
                RaisePropertyChanged(nameof(ShowTimeline));
            }
        }

        private PlanExplanation explanation;

        public PlanExplanation Explanation {
            get => explanation;
            set {
                explanation = value;
                RaisePropertyChanged(nameof(Explanation));
            }
        }
        public ICommand MoonAvoidanceCommand { get; private set; }

        private void LoadSchedulerPlans(DateTime atDateTime, IProfileService profileService) {
            /* While the caching here works and detects changes to the preview parameters (like date/time), it's not picking
             * up changes to the database.  For now just disable the caching ... doesn't take long to run anyway.

            if (SchedulerPlans != null) {
                return;
            }*/

            try {
                TSLogger.Debug($"running plan preview for {Utils.FormatDateTimeFull(atDateTime)}, profileId={SelectedProfileId}");

                SchedulerPlanLoader loader = new SchedulerPlanLoader(GetProfile(SelectedProfileId));
                List<IProject> projects = MarkForPreview(loader.LoadActiveProjects(database.GetContext()));
                ProfilePreference profilePreference = loader.GetProfilePreferences(database.GetContext());

                ObservableCollection<TreeViewItem> list = new ObservableCollection<TreeViewItem>();
                string profileName = ProfileChoices.First(p => p.Key == selectedProfileId).Value;

                if (projects == null) {
                    TSLogger.Debug($"no active projects for preview at {atDateTime}, profileId={SelectedProfileId}");
                    InstructionList = list;

                    MyMessageBox.Show($"No active projects/targets were returned by the planner for {Utils.FormatDateTimeFull(atDateTime)} and{Environment.NewLine}profile '{profileName}' - or no active targets were found with active exposure plans.", "Oops");
                    SchedulerPlans = null;
                    return;
                }

                List<SchedulerPlan> schedulerPlans = new PreviewPlanner().GetPlanPreview(atDateTime, profileService, profilePreference, projects);
                if (schedulerPlans.Count == 0) {
                    TSLogger.Debug($"no imagable projects for preview at {atDateTime}, profileId={SelectedProfileId}");
                    InstructionList = list;

                    MyMessageBox.Show($"No imagable projects/targets were returned by the planner for {Utils.FormatDateTimeFull(atDateTime)} and{Environment.NewLine}profile '{profileName}'.", "Oops");
                    SchedulerPlans = null;
                    return;
                }

                SchedulerPlans = schedulerPlans;
                return;
            } catch (Exception ex) {
                TSLogger.Error($"failed to run plan preview: {ex.Message} {ex.StackTrace}");
                MyMessageBox.Show($"Exception running plan preview - see the TS log for details.", "Oops");
                SchedulerPlans = null;
                return;
            }
        }

        private List<IProject> MarkForPreview(List<IProject> projects) {
            if (Common.IsEmpty(projects)) return projects;

            projects.ForEach(p => {
                p.Targets.ForEach(t => { t.IsPreview = true; });
            });

            return projects;
        }

        private void SetPreviewTimeNow() {
            DateTime now = DateTime.Now;
            PlanDate = now.Date;
            PlanHours = now.Hour;
            PlanMinutes = now.Minute;
            PlanSeconds = now.Second;

            RaisePropertyChanged(nameof(PlanDate));
            RaisePropertyChanged(nameof(PlanHours));
            RaisePropertyChanged(nameof(PlanMinutes));
            RaisePropertyChanged(nameof(PlanSeconds));
        }

        private static Dispatcher _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        private void RunPlanPreview() {
            _ = ExecutePlanPreview();
        }

        private async Task<bool> ExecutePlanPreview() {
            return await Task.Run(() => {
                // Slight delay allows the UI thread to update the spinner property before the dispatcher
                // thread starts ... which seems to block the UI updates.
                TableLoading = true;
                ShowPlanPreviewResults = false;
                ShowPlanPreview = false;
                ShowMoonAvoidance = false;
                ShowTimeline = false;
                Thread.Sleep(50);

                if (PlanDate == DateTime.MinValue || SelectedProfileId == null) {
                    TableLoading = false;
                    return true;
                }

                DateTime atDateTime = PlanDate.Date.AddHours(PlanHours).AddMinutes(PlanMinutes).AddSeconds(PlanSeconds);
                LoadSchedulerPlans(atDateTime, profileService);

                if (SchedulerPlans == null || SchedulerPlans.Count == 0) {
                    TableLoading = false;
                    return true;
                }

                _dispatcher.Invoke(DispatcherPriority.Normal, new Action(() => {
                    ObservableCollection<TreeViewItem> list = new ObservableCollection<TreeViewItem>();

                    try {
                        int lastTargetId = -1;
                        string lastFilterName = null;
                        TreeViewItem planItem = null;
                        TreeViewItem lastItem = null;
                        SchedulerPlan lastTargetPlan = null;
                        ProfilePreference profilePreference = GetProfilePreference(SelectedProfileId);

                        foreach (SchedulerPlan plan in SchedulerPlans) {
                            if (plan.IsWait) {
                                AddLastItemEndTime(lastItem, plan.StartTime);
                                planItem = new TreeViewItem();
                                planItem.Header = $"Wait until {Utils.FormatDateTimeFull(plan.WaitForNextTargetTime)}";
                                list.Add(planItem);
                                lastTargetId = -1;
                                lastItem = planItem;
                                continue;
                            }

                            if (plan.PlanTarget.DatabaseId != lastTargetId) {
                                AddLastItemEndTime(lastItem, plan.StartTime);
                                lastTargetId = plan.PlanTarget.DatabaseId;
                                planItem = new TreeViewItem();
                                planItem.Header = GetTargetLabel(plan);
                                planItem.IsExpanded = false;
                                list.Add(planItem);
                                lastItem = planItem;
                            }

                            lastTargetPlan = plan;
                            foreach (IInstruction instruction in plan.PlanInstructions) {
                                TreeViewItem instructionItem = new TreeViewItem();

                                if (instruction is PlanMessage
                                    || instruction is PlanBeforeNewTargetContainer
                                    || instruction is PlanPostExposure) {
                                    continue;
                                }

                                if (instruction is PlanSlew) {
                                    if (profilePreference.EnableSlewCenter) {
                                        instructionItem.Header = GetSlewLabel(plan.PlanTarget, (PlanSlew)instruction);
                                        planItem.Items.Add(instructionItem);
                                    }
                                    continue;
                                }

                                if (instruction is PlanSwitchFilter) {
                                    string filterName = ((PlanSwitchFilter)instruction).exposure.FilterName;
                                    if (filterName != lastFilterName) {
                                        lastFilterName = filterName;
                                        instructionItem.Header = $"Switch Filter: {filterName}";
                                        planItem.Items.Add(instructionItem);
                                    }
                                    continue;
                                }

                                if (instruction is PlanSetReadoutMode) {
                                    int? readoutMode = ((PlanSetReadoutMode)instruction).exposure.ReadoutMode;
                                    if (readoutMode != null && readoutMode > 0) {
                                        instructionItem.Header = $"Set readout mode: {readoutMode}";
                                        planItem.Items.Add(instructionItem);
                                    }
                                    continue;
                                }

                                if (instruction is PlanTakeExposure) {
                                    instructionItem.Header = GetTakeExposureLabel((PlanTakeExposure)instruction);
                                    planItem.Items.Add(instructionItem);
                                    continue;
                                }

                                if (instruction is PlanDither) {
                                    instructionItem.Header = "Dither";
                                    planItem.Items.Add(instructionItem);
                                    continue;
                                }

                                TSLogger.Error($"unknown instruction type in plan preview: {instruction.GetType().FullName}");
                                throw new Exception($"unknown instruction type in plan preview: {instruction.GetType().FullName}");
                            }
                        }

                        AddLastItemEndTime(lastItem, lastTargetPlan?.EndTime);
                        if (lastTargetPlan != null) {
                            planItem = new TreeViewItem();
                            planItem.Header = $"End at {Utils.FormatDateTimeFull(lastTargetPlan.EndTime)}";
                            list.Add(planItem);
                        }

                        InstructionList = list;
                        ShowPlanPreviewResults = false;
                        ShowTimeline = false;
                        ShowMoonAvoidance = false;
                        ShowPlanPreview = true;
                    } catch (Exception ex) {
                        TSLogger.Error($"failed to run plan preview: {ex.Message} {ex.StackTrace}");
                        InstructionList.Clear();
                    }
                }));

                TableLoading = false;
                return true;
            });
        }

        private void AddLastItemEndTime(TreeViewItem lastItem, DateTime? endTime) {
            if (lastItem != null && endTime != null) {
                string header = lastItem.Header.ToString();
                if (!header.StartsWith("Wait")) {
                    lastItem.Header = lastItem.Header + $",  end: {Utils.FormatDateTimeFull(endTime)}";
                }
            }
        }

        private string planPreviewResultsLog;

        public string PlanPreviewResultsLog {
            get => planPreviewResultsLog;
            set {
                planPreviewResultsLog = value;
                RaisePropertyChanged(nameof(PlanPreviewResultsLog));
            }
        }

        private void RunPlanPreviewResults() {
            _ = ExecutePlanPreviewResults();
        }

        private async Task<bool> ExecutePlanPreviewResults() {
            return await Task.Run(() => {
                // Slight delay allows the UI thread to update the spinner property before the dispatcher
                // thread starts ... which seems to block the UI updates.
                TableLoading = true;
                ShowPlanPreviewResults = false;
                ShowPlanPreview = false;
                ShowMoonAvoidance = false;
                ShowTimeline = false;
                Thread.Sleep(50);

                if (PlanDate == DateTime.MinValue || SelectedProfileId == null) {
                    TableLoading = false;
                    return true;
                }

                DateTime atDateTime = PlanDate.Date.AddHours(PlanHours).AddMinutes(PlanMinutes).AddSeconds(PlanSeconds);
                LoadSchedulerPlans(atDateTime, profileService);

                if (SchedulerPlans == null || SchedulerPlans.Count == 0) {
                    TableLoading = false;
                    return true;
                }

                _dispatcher.Invoke(DispatcherPriority.Normal, new Action(() => {
                    try {
                        StringBuilder sb = new StringBuilder();
                        foreach (SchedulerPlan plan in SchedulerPlans) {
                            sb.Append(plan.DetailsLog);
                        }

                        sb.AppendLine("\nRUN COMPLETE - NO MORE TARGETS AVAILABLE");
                        PlanPreviewResultsLog = sb.ToString();
                        ShowPlanPreview = false;
                        ShowTimeline = false;
                        ShowMoonAvoidance = false;
                        ShowPlanPreviewResults = true;
                    } catch (Exception ex) {
                        TSLogger.Error($"failed to run plan preview results: {ex.Message} {ex.StackTrace}");
                        PlanPreviewResultsLog = string.Empty;
                    }
                }));

                TableLoading = false;
                return true;
            });
        }

        // **CUSTOM FORK** Planning insight: build the timeline + scoring explanation for the current preview.
        private void RunPlanInsight() {
            _ = ExecutePlanInsight();
        }

        private async Task<bool> ExecutePlanInsight() {
            return await Task.Run(() => {
                TableLoading = true;
                ShowPlanPreview = false;
                ShowPlanPreviewResults = false;
                ShowTimeline = false;
                ShowMoonAvoidance = false;
                Thread.Sleep(50);

                if (PlanDate == DateTime.MinValue || SelectedProfileId == null) {
                    TableLoading = false;
                    return true;
                }

                DateTime atDateTime = PlanDate.Date.AddHours(PlanHours).AddMinutes(PlanMinutes).AddSeconds(PlanSeconds);
                PlanExplanation result = BuildExplanation(atDateTime);
                if (result == null) {
                    TableLoading = false;
                    return true;
                }

                _dispatcher.Invoke(DispatcherPriority.Normal, new Action(() => {
                    Explanation = result;
                    ShowPlanPreview = false;
                    ShowPlanPreviewResults = false;
                    ShowMoonAvoidance = false;
                    ShowTimeline = true;
                }));

                TableLoading = false;
                return true;
            });
        }

        private PlanExplanation BuildExplanation(DateTime atDateTime) {
            try {
                TSLogger.Debug($"building plan insight for {Utils.FormatDateTimeFull(atDateTime)}, profileId={SelectedProfileId}");

                SchedulerPlanLoader loader = new SchedulerPlanLoader(GetProfile(SelectedProfileId));
                string profileName = ProfileChoices.First(p => p.Key == selectedProfileId).Value;

                List<IProject> probe = loader.LoadActiveProjects(database.GetContext());
                if (Common.IsEmpty(probe)) {
                    MyMessageBox.Show($"No active projects/targets were returned by the planner for {Utils.FormatDateTimeFull(atDateTime)} and{Environment.NewLine}profile '{profileName}' - or no active targets were found with active exposure plans.", "Oops");
                    return null;
                }

                ProfilePreference profilePreference = loader.GetProfilePreferences(database.GetContext());
                Func<List<IProject>> loadProjects = () => MarkForPreview(loader.LoadActiveProjects(database.GetContext()));

                return new PlanExplanationBuilder().Build(atDateTime, profileName, profileService, profilePreference, loadProjects);
            } catch (Exception ex) {
                TSLogger.Error($"failed to build plan insight: {ex.Message} {ex.StackTrace}");
                MyMessageBox.Show("Exception building plan insight - see the TS log for details.", "Oops");
                return null;
            }
        }

        private void RunExportInsight() {
            if (Explanation == null) {
                MyMessageBox.Show("Run 'Insight' first to generate a plan to export.", "Oops");
                return;
            }

            try {
                string dir = Explanation.Export();
                MyMessageBox.Show($"Exported plan explanation (full JSON, condensed digest, and an LLM prompt template) to:{Environment.NewLine}{Environment.NewLine}{dir}", "Exported");
            } catch (Exception ex) {
                TSLogger.Error($"failed to export plan insight: {ex.Message} {ex.StackTrace}");
                MyMessageBox.Show("Exception exporting plan insight - see the TS log for details.", "Oops");
            }
        }

        private AsyncObservableCollection<MoonAvoidanceTargetGroup> moonAvoidanceGroups;

        /// <summary>
        /// The moon avoidance rows as one collapsible dropdown per target, so the per-plan detail is hidden
        /// until asked for.
        /// </summary>
        public AsyncObservableCollection<MoonAvoidanceTargetGroup> MoonAvoidanceGroups {
            get => moonAvoidanceGroups;
            set {
                moonAvoidanceGroups = value;
                RaisePropertyChanged(nameof(MoonAvoidanceGroups));
            }
        }

        private string moonAvoidanceSummary;

        public string MoonAvoidanceSummary {
            get => moonAvoidanceSummary;
            set {
                moonAvoidanceSummary = value;
                RaisePropertyChanged(nameof(MoonAvoidanceSummary));
            }
        }

        private void RunMoonAvoidance() {
            _ = ExecuteMoonAvoidance();
        }

        /// <summary>
        /// Sweep the night for moon avoidance across every active target and exposure plan.  Unlike the plan
        /// preview, this doesn't run the planner - it evaluates avoidance directly, so targets that avoidance
        /// rejects outright (and which therefore never show up in a preview) are still reported.
        /// </summary>
        private async Task<bool> ExecuteMoonAvoidance() {
            return await Task.Run(() => {
                // Slight delay allows the UI thread to update the spinner property before the dispatcher
                // thread starts ... which seems to block the UI updates.
                TableLoading = true;
                ShowPlanPreview = false;
                ShowPlanPreviewResults = false;
                ShowMoonAvoidance = false;
                ShowTimeline = false;
                Thread.Sleep(50);

                if (PlanDate == DateTime.MinValue || SelectedProfileId == null) {
                    TableLoading = false;
                    return true;
                }

                DateTime atDateTime = PlanDate.Date.AddHours(PlanHours).AddMinutes(PlanMinutes).AddSeconds(PlanSeconds);
                MoonAvoidanceAnalysis analysis;

                try {
                    TSLogger.Debug($"running moon avoidance analysis for {Utils.FormatDateTimeFull(atDateTime)}, profileId={SelectedProfileId}");
                    IProfile profile = GetProfile(SelectedProfileId);
                    ObserverInfo observerInfo = new ObserverInfo {
                        Latitude = profile.AstrometrySettings.Latitude,
                        Longitude = profile.AstrometrySettings.Longitude,
                        Elevation = profile.AstrometrySettings.Elevation,
                    };

                    // The analysis walks synthetic times through the twilight cache: isolate it from live state.
                    PreviewContext.Enter();
                    try {
                        SchedulerPlanLoader loader = new SchedulerPlanLoader(profile);
                        List<IProject> projects = MarkForPreview(loader.LoadActiveProjects(database.GetContext()));
                        analysis = new MoonAvoidanceAnalyzer(observerInfo).Analyze(atDateTime, projects);
                    } finally {
                        PreviewContext.Exit();
                    }
                } catch (Exception ex) {
                    TSLogger.Error($"failed to run moon avoidance analysis: {ex.Message} {ex.StackTrace}");
                    MyMessageBox.Show("Exception running moon avoidance analysis - see the TS log for details.", "Oops");
                    TableLoading = false;
                    return true;
                }

                if (analysis == null) {
                    MyMessageBox.Show($"There's no night at this location for {Utils.FormatDateTimeFull(atDateTime)}, so moon avoidance can't be analyzed.", "Oops");
                    TableLoading = false;
                    return true;
                }

                _dispatcher.Invoke(DispatcherPriority.Normal, new Action(() => {
                    try {
                        AsyncObservableCollection<MoonAvoidanceTargetGroup> groups = new AsyncObservableCollection<MoonAvoidanceTargetGroup>();
                        analysis.GetTargetGroups().ForEach(g => groups.Add(g));
                        MoonAvoidanceGroups = groups;

                        MoonAvoidanceSummary = string.Join(Environment.NewLine,
                            analysis.NightSummary, analysis.MoonSummary, analysis.MoonFreeSummary, analysis.CoverageSummary);
                        ShowPlanPreview = false;
                        ShowPlanPreviewResults = false;
                        ShowTimeline = false;
                        ShowMoonAvoidance = true;
                    } catch (Exception ex) {
                        TSLogger.Error($"failed to display moon avoidance analysis: {ex.Message} {ex.StackTrace}");
                    }
                }));

                TableLoading = false;
                return true;
            });
        }

        private AsyncObservableCollection<KeyValuePair<string, string>> GetProfileChoices() {
            Dictionary<string, string> profiles = new Dictionary<string, string>();
            profileService.Profiles.ForEach(p => {
                profiles.Add(p.Id.ToString(), p.Name);
            });

            AsyncObservableCollection<KeyValuePair<string, string>> profileChoices = new AsyncObservableCollection<KeyValuePair<string, string>>();
            foreach (KeyValuePair<string, string> entry in profiles) {
                profileChoices.Add(new KeyValuePair<string, string>(entry.Key, entry.Value));
            }

            return profileChoices;
        }

        private IProfile GetProfile(string profileId) {
            foreach (ProfileMeta profileMeta in profileService.Profiles) {
                if (profileMeta.Id.ToString() == profileId) {
                    return ProfileLoader.Load(profileService, profileMeta);
                }
            }

            TSLogger.Error($"failed to get profile for ID={profileId}");
            throw new Exception($"failed to get profile for ID={profileId}");
        }

        private ProfilePreference GetProfilePreference(string profileId) {
            using (var context = database.GetContext()) {
                return context.GetProfilePreference(profileId, true);
            }
        }

        private string GetTargetLabel(SchedulerPlan plan) {
            string label = $"{plan.PlanTarget.Project.Name} / {plan.PlanTarget.Name}";
            return $"{label} - start: {Utils.FormatDateTimeFull(plan.StartTime)}";
        }

        private string GetSlewLabel(ITarget planTarget, PlanSlew planSlew) {
            string name = "Slew";
            string rotate = $", Rotate: {planTarget.Rotation}°";

            if (planSlew.center) {
                name = "Slew/Rotate/Center";
            }

            return $"{name}: {planTarget.Coordinates.RAString} {planTarget.Coordinates.DecString}{rotate}";
        }

        private string GetTakeExposureLabel(PlanTakeExposure instruction) {
            IExposure planExposure = instruction.exposure;
            StringBuilder sb = new StringBuilder();
            sb.Append("Take Exposure:");
            sb.Append($" {planExposure.ExposureLength} secs, ");
            sb.Append($" Gain={CameraDefault(planExposure.Gain)}, ");
            sb.Append($" Offset={CameraDefault(planExposure.Offset)}, ");
            sb.Append($" Binning={planExposure.BinningMode}");

            return sb.ToString();
        }

        private string CameraDefault(int? value) {
            return value != null ? value.ToString() : "(camera)";
        }
    }
}