using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<SequenceTarget> Targets { get; } = [];
    public ObservableCollection<TargetRun> Runs { get; } = [];
    public ObservableCollection<PrimerCandidate> Candidates { get; } = [];
    public string[] DatabaseKinds { get; } = ["转录本", "基因组"];
    private readonly string storageRoot;
    private readonly EnginePaths paths;
    private readonly PrimerLibrary library;
    private readonly Dictionary<string, DatabaseManifest> databases = [];
    private List<ExperimentRecord> experiments = [];
    private CancellationTokenSource? cancellation;
    private bool dirty;
    private bool loading;
    private bool dark;

    [ObservableProperty] private SequenceTarget? selectedTarget;
    [ObservableProperty] private SequenceTarget? displayTarget;
    [ObservableProperty] private TargetRun? selectedRun;
    [ObservableProperty] private PrimerCandidate? selectedCandidate;
    [ObservableProperty] private string targetName = "target_1";
    [ObservableProperty] private string templateText = "";
    [ObservableProperty] private string junctionText = "";
    [ObservableProperty] private string expectedText = "";
    [ObservableProperty] private string manualForward = "";
    [ObservableProperty] private string manualReverse = "";
    [ObservableProperty] private string sequenceContext = "选择候选后显示结合位点。";
    [ObservableProperty] private string structureText = "选择候选进行结构计算。结构 Tm 与引物结合 Tm 分别显示。";
    [ObservableProperty] private string evidenceText = "特异性尚未检查。请选择转录本或基因组数据库。";
    [ObservableProperty] private string candidateNotes = "";
    [ObservableProperty] private string logText = "";
    [ObservableProperty] private string projectPath = "尚未保存";
    [ObservableProperty] private string status = "准备就绪 · SYBR Green RT-qPCR";
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private double progress;
    [ObservableProperty] private bool selectedOnly = true;
    [ObservableProperty] private string databaseKind = "转录本";
    [ObservableProperty] private string databaseSummary = "尚未选择数据库";

    [ObservableProperty] private string minLength = "18";
    [ObservableProperty] private string optLength = "20";
    [ObservableProperty] private string maxLength = "24";
    [ObservableProperty] private string minTm = "58";
    [ObservableProperty] private string optTm = "60";
    [ObservableProperty] private string maxTm = "62";
    [ObservableProperty] private string minGc = "40";
    [ObservableProperty] private string maxGc = "60";
    [ObservableProperty] private string maxTmDifference = "2";
    [ObservableProperty] private string minProduct = "80";
    [ObservableProperty] private string maxProduct = "200";
    [ObservableProperty] private string returnCount = "10";
    [ObservableProperty] private string monovalentMm = "50";
    [ObservableProperty] private string magnesiumMm = "1.5";
    [ObservableProperty] private string dntpMm = "0.6";
    [ObservableProperty] private string primerNm = "250";
    [ObservableProperty] private string maxHairpinTm = "47";
    [ObservableProperty] private string maxSelfAnyTm = "47";
    [ObservableProperty] private string maxSelfEndTm = "35";
    [ObservableProperty] private string maxPairAnyTm = "47";
    [ObservableProperty] private string maxPairEndTm = "35";
    [ObservableProperty] private string maxHomopolymer = "4";
    [ObservableProperty] private string gcClamp = "0";
    [ObservableProperty] private string includedStart = "1";
    [ObservableProperty] private string includedLength = "0";
    [ObservableProperty] private string excludedText = "";
    [ObservableProperty] private bool requireJunction;
    [ObservableProperty] private string junctionOverlap5 = "7";
    [ObservableProperty] private string junctionOverlap3 = "4";
    [ObservableProperty] private string maxMismatches = "3";
    [ObservableProperty] private string maxThreePrimeMismatches = "1";
    [ObservableProperty] private string specificityMaxProduct = "5000";
    [ObservableProperty] private string maxSubjects = "10000";

    public MainViewModel(string? engineFolder = null, string? storage = null)
    {
        storageRoot = storage ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QpcrPrimerStudio");
        Directory.CreateDirectory(storageRoot);
        paths = EnginePaths.FromFolder(engineFolder ?? Path.Combine(AppContext.BaseDirectory, "tools"));
        library = new PrimerLibrary(Path.Combine(storageRoot, "primer-library.sqlite"));
        PropertyChanged += (_, e) =>
        {
            if (loading || e.PropertyName is null) return;
            if (typeof(DesignParameters).GetProperty(e.PropertyName) is not null || e.PropertyName is nameof(ReturnCount) or nameof(ExcludedText)
                or nameof(JunctionText) or nameof(ExpectedText) or nameof(CoverageText) or nameof(RegionMode) or nameof(ExpressionMode) or nameof(AvoidFivePrime)
                or nameof(AvoidThreePrime) or nameof(AvoidVariants) or nameof(RejectTerminalVariants) or nameof(VariantTerminalBases)) dirty = true;
            if (e.PropertyName is nameof(ScoreWeightsText) or nameof(AutoSpecificity)) dirty = true;
        };
        if (!File.Exists(paths.Primer3)) Status = "Primer3 工具缺失，请运行项目工具安装脚本。";
    }
    public bool CanWork() => !IsBusy;
    partial void OnIsBusyChanged(bool value)
    {
        foreach (var property in GetType().GetProperties().Where(p => p.Name.EndsWith("Command")))
            if (property.GetValue(this) is IRelayCommand command) command.NotifyCanExecuteChanged();
    }
    partial void OnTemplateTextChanged(string value) { ClearTemplateInputError(); if (!loading) dirty = true; }
    partial void OnTargetNameChanged(string value) { if (!loading) dirty = true; }
    partial void OnSelectedTargetChanged(SequenceTarget? value)
    {
        if (value is null) return;
        var wasLoading = loading;
        loading = true;
        TargetName = value.Id; TemplateText = value.Sequence;
        JunctionText = string.Join(",", value.Junctions); ExpectedText = string.Join(",", value.ExpectedSubjects);
        CoverageText = string.Join(",", CoverageFor(value));
        RestoreTargetDraft(value.Id);
        DisplayTarget = value;
        loading = wasLoading;
        if (!loading) NavigateToTargetResults(value);
    }
    partial void OnSelectedRunChanged(TargetRun? value)
    {
        if (value is null) { ClearCurrentResults(); RefreshHairpinView(true); return; }
        SynchronizeTargetWithRun(value);
        Candidates.Clear(); foreach (var candidate in value.Candidates) Candidates.Add(candidate);
        DisplayTarget = value.Target;
        SelectedCandidate = Candidates.FirstOrDefault();
        InputExpanded = Candidates.Count == 0;
        ResultsTabIndex = 0;
        LogText = value.Message + "\n\n完整输入：\n" + value.RawInput;
        RefreshHairpinView(true);
        RefreshResultContext();
    }
    partial void OnSelectedCandidateChanged(PrimerCandidate? value)
    {
        if (value is null) { ClearCandidateDetails(); return; }
        CandidateNotes = value.Notes;
        SelectedOligoInspection = null;
        QualityText = value.Assessment.Details;
        StructureText = $"{value.TargetId} · 候选 {value.Rank}\n" +
            $"Forward Tm {value.Forward.Tm:F2} °C；Reverse Tm {value.Reverse.Tm:F2} °C\n" +
            $"Forward 发卡 Tm {value.Forward.HairpinTm:F2} °C；Reverse 发卡 Tm {value.Reverse.HairpinTm:F2} °C\n" +
            $"自二聚体 ANY：F {value.Forward.SelfAnyTm:F2}，R {value.Reverse.SelfAnyTm:F2} °C\n" +
            $"自二聚体 END：F {value.Forward.SelfEndTm:F2}，R {value.Reverse.SelfEndTm:F2} °C\n" +
            $"异二聚体 ANY {value.PairAnyTm:F2}；END {value.PairEndTm:F2} °C\n\n" +
            value.Forward.HairpinStructure + "\n" + value.Reverse.HairpinStructure + "\n" + value.PairStructure;
        EvidenceText = value.JunctionEvidence + "\n\n" + string.Join("\n\n", value.Specificity.Select(FormatEvidence));
        var run = Runs.FirstOrDefault(r => r.Candidates.Contains(value));
        if (run is not null) DisplayTarget = run.Target;
        if (DisplayTarget is not null)
        {
            var f = value.Forward; var r = value.Reverse;
            SequenceContext = $"Forward {f.Start}–{f.End} (+)   {f.Sequence}\nReverse {r.Start}–{r.End} (−)   {r.Sequence}\n" +
                $"扩增子 {f.Start}–{r.End}，{value.ProductLength} bp\n\n" + Chunk(value.Amplicon, f.Start);
        }
        RefreshHairpinQuality();
    }
    partial void OnDatabaseKindChanged(string value) => UpdateDatabaseSummary();
    private void UpdateDatabaseSummary() => DatabaseSummary = databases.TryGetValue(DatabaseKind, out var db)
        ? $"{db.Name} · {db.SequenceCount:N0} 条\n{db.CreatedAt:yyyy-MM-dd}" : "尚未选择此类型数据库";

    public DesignParameters ReadParameters() => new()
    {
        MinLength = Int(MinLength), OptLength = Int(OptLength), MaxLength = Int(MaxLength),
        MinTm = Num(MinTm), OptTm = Num(OptTm), MaxTm = Num(MaxTm), MinGc = Num(MinGc), MaxGc = Num(MaxGc),
        MaxTmDifference = Num(MaxTmDifference), MinProduct = Int(MinProduct), MaxProduct = Int(MaxProduct), Count = Int(ReturnCount),
        MonovalentMm = Num(MonovalentMm), MagnesiumMm = Num(MagnesiumMm), DntpMm = Num(DntpMm), PrimerNm = Num(PrimerNm),
        MaxHairpinTm = Num(MaxHairpinTm), MaxSelfAnyTm = Num(MaxSelfAnyTm), MaxSelfEndTm = Num(MaxSelfEndTm),
        MaxPairAnyTm = Num(MaxPairAnyTm), MaxPairEndTm = Num(MaxPairEndTm), MaxHomopolymer = Int(MaxHomopolymer), GcClamp = Int(GcClamp),
        IncludedStart = Int(IncludedStart), IncludedLength = Int(IncludedLength), RequireJunction = RequireJunction,
        ForwardRegionStart = Int(ForwardRegionStart), ForwardRegionLength = Int(ForwardRegionLength),
        ReverseRegionStart = Int(ReverseRegionStart), ReverseRegionLength = Int(ReverseRegionLength),
        JunctionOverlap5 = Int(JunctionOverlap5), JunctionOverlap3 = Int(JunctionOverlap3),
        ExcludedRegions = ExcludedText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => { var pair = part.Split(','); if (pair.Length != 2) throw new ArgumentException("排除区域格式须为起点,长度；多项用分号。"); return new SequenceRegion(Int(pair[0]), Int(pair[1])); }).ToList()
    };
    private void LoadParameters(DesignParameters parameters)
    {
        foreach (var property in typeof(DesignParameters).GetProperties())
        {
            var name = property.Name == "Count" ? "ReturnCount" : property.Name;
            var destination = GetType().GetProperty(name);
            if (destination?.PropertyType == typeof(string)) destination.SetValue(this, Convert.ToString(property.GetValue(parameters), CultureInfo.InvariantCulture));
            else if (destination?.PropertyType == typeof(bool)) destination.SetValue(this, property.GetValue(parameters));
        }
        ExcludedText = string.Join(";", parameters.ExcludedRegions.Select(r => $"{r.Start},{r.Length}"));
    }
    public ProjectDocument Snapshot() => new() { Targets = Targets.ToList(), Runs = Runs.ToList(), Parameters = ReadParameters(), Experiments = experiments.ToList(),
        Reference = reference, RegionSettings = ReadRegionSettings(), Assays = assays.ToList(), Databases = databases.Values.ToList(), AutoSpecificity = AutoSpecificity,
        SinglePrimerSearches = SinglePrimerSearches.ToList(), OligoInspections = OligoInspections.ToList(), HairpinSettings = HairpinSettings,
        EditorDraft = CurrentEditorDraft(), TargetDrafts = SnapshotTargetDrafts(), UnassignedDraft = SnapshotUnassignedDraft(), ParameterRecommendations = parameterRecommendations.ToList(),
        UseSharedSearchParameters = UseSharedSearchParameters, SmartExportReports = smartExportReports.ToList() };
    public void Restore(ProjectDocument project)
    {
        ProjectValidation.Validate(project);
        ResetSelectionAutoSave();
        InitializeProjectVersion(project);
        loading = true;
        try
        {
        SelectedTarget = null; SelectedRun = null; SelectedCandidate = null; DisplayTarget = null;
        TargetName = "target_1"; TemplateText = ""; JunctionText = ""; ExpectedText = ""; CoverageText = "";
        targetDrafts = new(project.TargetDrafts, StringComparer.Ordinal);
        unassignedDraft = project.UnassignedDraft;
        editParent = null; editTemplateHash = null;
        lastSearchKind = PrimerSearchKind.Pairs;
        ManualForward = ""; ManualReverse = "";
        EditSummary = "两条序列均按 5′→3′ 输入；重算会保留原候选。";
        InputTabIndex = 0; ResultsTabIndex = 0; InputExpanded = true;
        Targets.Clear(); Runs.Clear(); Candidates.Clear();
        foreach (var target in project.Targets) Targets.Add(target);
        foreach (var run in project.Runs) Runs.Add(run);
        SinglePrimerSearches.Clear(); foreach (var search in project.SinglePrimerSearches) SinglePrimerSearches.Add(search);
        SelectedSingleSearch = SinglePrimerSearches.LastOrDefault();
        OligoInspections.Clear(); foreach (var inspection in project.OligoInspections) OligoInspections.Add(inspection);
        SelectedOligoInspection = null;
        experiments = project.Experiments;
        parameterRecommendations = project.ParameterRecommendations.ToList();
        smartExportReports = project.SmartExportReports.ToList();
        UseSharedSearchParameters = project.UseSharedSearchParameters;
        SelectedOnly = true;
        LoadParameters(project.Parameters); LoadRegions(project.RegionSettings); HairpinSettings = project.HairpinSettings;
        reference = project.Reference; assays = project.Assays.ToList(); databases.Clear(); foreach (var db in project.Databases) databases[db.Kind] = db;
        AutoSpecificity = project.AutoSpecificity; RefreshReference(); UpdateDatabaseSummary();
        SelectedTarget = Targets.FirstOrDefault(); SelectedRun = Runs.LastOrDefault();
        if (project.EditorDraft is { } draft)
        {
            SelectedTarget = Targets.FirstOrDefault(t => t.Id == draft.SelectedTargetId);
            TargetName = draft.TargetName; TemplateText = draft.TemplateText;
            JunctionText = draft.JunctionText; ExpectedText = draft.ExpectedText;
            CoverageText = draft.CoverageText;
            if (SelectedTarget is not null) targetDrafts[SelectedTarget.Id] = draft;
            else unassignedDraft = draft;
        }
        }
        finally { loading = false; }
        NavigateToTargetResults(SelectedTarget);
        LoadPrimerDraft(project.EditorDraft ?? targetDrafts.GetValueOrDefault(SelectedTarget?.Id ?? ""));
        dirty = false;
    }
    private void ApplyTemplate()
    {
        var id = TargetName.Trim();
        if (Targets.Any(t => t.Id == id) && SelectedTarget?.Id != id)
            throw new InvalidOperationException($"靶标 ID“{id}”已属于其他序列，请填写独立 ID；已有靶标与原稿均已保留。");
        var wasUnassigned = SelectedTarget is null;
        var target = new SequenceTarget { Id = TargetName.Trim(), Sequence = NormalizeTemplate(),
            Source = SelectedTarget?.Source ?? "粘贴序列", GeneId = SelectedTarget?.GeneId ?? "", ParameterOverride = SelectedTarget?.ParameterOverride,
            ExonBlocks = SelectedTarget?.ExonBlocks ?? [],
            ReferenceGenomeSha256 = SelectedTarget?.ReferenceGenomeSha256 ?? "",
            Junctions = JunctionText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Int).Distinct().Order().ToList(),
            ExpectedSubjects = ExpectedText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            CoverageTranscripts = CoverageText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() };
        target.Validate();
        var old = Targets.FirstOrDefault(t => t.Id == target.Id);
        if (old is not null) Targets[Targets.IndexOf(old)] = target; else Targets.Add(target);
        targetDrafts[target.Id] = CurrentEditorDraft() with { SelectedTargetId = target.Id, TargetName = target.Id, TemplateText = target.Sequence,
            JunctionText = string.Join(",", target.Junctions), ExpectedText = string.Join(",", target.ExpectedSubjects), CoverageText = string.Join(",", target.CoverageTranscripts),
            ParentCandidateId = SelectedTarget?.Id == target.Id ? editParent?.Id : null };
        SelectedTarget = target; dirty = true;
        if (wasUnassigned) unassignedDraft = null;
    }
    [RelayCommand(CanExecute = nameof(CanWork))] private void ApplyTemplateAction() => TryAction(() => { ApplyTemplate(); Status = "模板已保存；已有任务保留原输入。"; });
    public IRelayCommand ApplyTemplateCommand => ApplyTemplateActionCommand;
    [RelayCommand(CanExecute = nameof(CanWork))] private void AddTarget()
    {
        if (SelectedTarget is null && HasUnassignedInput()) unassignedDraft = CurrentEditorDraft();
        SelectedTarget = null; SelectedRun = null; SelectedCandidate = null; DisplayTarget = null; Candidates.Clear();
        navigationTargetId = null;
        TargetName = "target_" + (Targets.Count + 1); TemplateText = ""; JunctionText = ""; ExpectedText = ""; CoverageText = "";
        LoadPrimerDraft(null);
        InputExpanded = true; InputTabIndex = 0; ResultsTabIndex = 0;
        Status = "填写下一个基因的独立 ID 和序列；已有设计保留在任务记录中。";
        if (unassignedDraft is { } draft)
        {
            TargetName = draft.TargetName; TemplateText = draft.TemplateText; JunctionText = draft.JunctionText;
            ExpectedText = draft.ExpectedText; CoverageText = draft.CoverageText; LoadPrimerDraft(draft);
            Status = "已恢复未保存的新序列草稿；填写独立 ID 并保存序列后可继续设计。";
        }
    }
    [RelayCommand(CanExecute = nameof(CanWork))] private void Import() => TryAction(() =>
    {
        var dialog = new OpenFileDialog { Filter = "序列文件|*.fa;*.fasta;*.fna;*.fas;*.gb;*.gbk;*.genbank|所有文件|*.*", Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        var incoming = dialog.FileNames.SelectMany(SequenceFiles.Read).ToList();
        if (incoming.Select(t => t.Id).Distinct().Count() != incoming.Count || incoming.Any(t => Targets.Any(existing => existing.Id == t.Id)))
            throw new InvalidDataException("导入靶标 ID 与项目或其他文件重复，请先修正 ID。");
        foreach (var target in incoming) Targets.Add(target);
        SelectedTarget = incoming.First(); dirty = true; Status = $"已导入 {incoming.Count} 个靶标。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task DesignCurrentAsync() => await Busy(async token => { ApplyTemplate(); await DesignTargetsAsync([SelectedTarget!], ReadParameters(), token); });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task DesignAllAsync() => await Busy(async token =>
    {
        if (!string.IsNullOrWhiteSpace(TemplateText)) ApplyTemplate();
        if (Targets.Count == 0) throw new InvalidOperationException("请先导入或填写模板序列。");
        await DesignTargetsAsync(Targets.ToList(), ReadParameters(), token);
    });
    public async Task DesignTargetsAsync(List<SequenceTarget> targets, DesignParameters parameters, CancellationToken token)
    {
        var engine = new Primer3Engine(paths.Primer3);
        var commonRegions = ReadRegionSettings();
        var jobReference = reference;
        var jobInputs = targets.Select(t => RegionsForTarget(commonRegions, t)).ToList();
        for (var i = 0; i < targets.Count; i++)
        {
            await WaitForResume(token);
            var target = targets[i];
            if (token.IsCancellationRequested)
            {
                for (var j = i; j < targets.Count; j++) Runs.Add(new TargetRun { Target = targets[j], State = "取消", Message = "用户取消，尚未开始此靶标。" });
                token.ThrowIfCancellationRequested();
            }
            Status = $"设计 {i + 1}/{targets.Count}：{target.Id}";
            TargetRun? run = null;
            try { run = await engine.DesignAsync(target, parameters, token, regions: jobInputs[i], reference: jobReference); await CheckRunSpecificityAsync(run, token); }
            catch (OperationCanceledException) { run ??= new TargetRun { Target = target }; run.State = "取消"; run.Message += "\n用户取消，已生成候选与已完成检查保留。";
                Runs.Add(run); SelectedRun = run; dirty = true; for (var j = i + 1; j < targets.Count; j++) Runs.Add(new TargetRun { Target = targets[j], State = "取消" }); throw; }
            catch (Exception ex) { run = new TargetRun { Target = target, State = "失败", Message = ex.Message }; }
            Runs.Add(run); SelectedRun = run; dirty = true; Progress = (i + 1) * 100d / targets.Count;
        }
        Status = $"设计结束：{targets.Count} 个靶标；点击智能导出可为每个基因保留最高评分的一对。";
    }
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task AnalyzeManualAsync() => await Busy(async token =>
    {
        var parameters = ReadParameters();
        var inspection = await new Primer3Engine(paths.Primer3).InspectOligosAsync(ManualForward, ManualReverse, parameters, token);
        var structure = await new Thermodynamics(Path.Combine(Path.GetDirectoryName(paths.Primer3)!, "ntthal.exe"))
            .AnalyzeAsync(inspection.Forward, inspection.Reverse, parameters, token);
        inspection = inspection with { Report = inspection.Report + "\n\n" + structure };
        OligoInspections.Add(inspection); SelectedOligoInspection = inspection; SetEditorAnalysis(inspection); ResultsTabIndex = 4; dirty = true;
        Status = "序列与结构分析完成；模板结合位点和产物需另行重算检查。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task AnalyzeSelectedAsync() => await Busy(async token =>
    { var c = EditableCandidate(RequiredCandidate());
        var parameters = c.Provenance?.Parameters ?? ReadParameters();
        StructureText = await new Thermodynamics(Path.Combine(Path.GetDirectoryName(paths.Primer3)!, "ntthal.exe")).AnalyzeAsync(c.Forward.Sequence, c.Reverse.Sequence, parameters, token);
        c.ThermodynamicEvidence = StructureText; dirty = true; Status = "结构计算完成，使用候选记录的反应条件。"; });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task BuildDatabaseAsync()
    {
        var dialog = new OpenFileDialog { Filter = "参考 FASTA|*.fa;*.fasta;*.fna;*.fas|所有文件|*.*" };
        if (dialog.ShowDialog() != true) return;
        await Busy(async token =>
        {
            Status = "正在建立 " + DatabaseKind + " 数据库…";
            var db = await new SpecificityEngine(paths).BuildDatabaseAsync(dialog.FileName, Path.GetFileNameWithoutExtension(dialog.FileName), DatabaseKind, Path.Combine(storageRoot, "databases"), token);
            BindDatabase(db); Status = "数据库已建立。";
        });
    }
    [RelayCommand(CanExecute = nameof(CanWork))] private void OpenDatabase() => TryAction(() =>
    {
        var dialog = new OpenFileDialog { Filter = "数据库清单|database.json", InitialDirectory = Path.Combine(storageRoot, "databases") };
        if (dialog.ShowDialog() != true) return;
        var db = SpecificityEngine.ReadDatabase(dialog.FileName); DatabaseKind = db.Kind; BindDatabase(db);
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task CheckCurrentAsync()
    { if (SelectedCandidate is null) { Status = "请先选择候选。"; return; } await CheckCandidates([SelectedCandidate]); }
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task CheckAllAsync() => await CheckCandidates(Runs.SelectMany(r => r.Candidates).ToList());
    private async Task CheckCandidates(List<PrimerCandidate> candidates) => await Busy(async token =>
    {
        if (candidates.Count == 0) throw new InvalidOperationException("没有待检查的候选。");
        if (!databases.TryGetValue(DatabaseKind, out var db)) throw new InvalidOperationException("请先建立或打开此类型数据库。");
        var settings = new SpecificitySettings(Int(MaxMismatches), Int(MaxThreePrimeMismatches), 5, Int(SpecificityMaxProduct), Int(MaxSubjects));
        for (var i = 0; i < candidates.Count; i++)
        {
            await WaitForResume(token); token.ThrowIfCancellationRequested(); var c = EditableCandidate(candidates[i]);
            var run = Runs.First(r => r.Candidates.Contains(c)); var target = run.Target;
            Status = $"{DatabaseKind} 检查 {i + 1}/{candidates.Count}：{c.TargetId} #{c.Rank}";
            SpecificityReport report;
            try { report = await new SpecificityEngine(paths).CheckAsync(c, target, db, settings, Path.Combine(storageRoot, "jobs"), token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { report = new SpecificityReport { Kind = db.Kind, Database = db, Settings = settings, Status = "检查失败", Evidence = ex.Message }; }
            c.Specificity.RemoveAll(r => r.Kind == db.Kind); c.Specificity.Add(report); dirty = true;
            CandidateQualityEngine.Refresh(c, run, ReadParameters(), reference);
            run.RejectionCounts = run.Candidates.SelectMany(item => item.Assessment.Rejections).GroupBy(reason => reason).ToDictionary(g => g.Key, g => g.Count());
            if (run.AcceptedCount == 0) run.State = "候选全部被质量规则排除";
            else if (run.State == "候选全部被质量规则排除") run.State = "完成";
            Progress = (i + 1) * 100d / candidates.Count;
        }
        var current = SelectedRun; SelectedRun = null; SelectedRun = current;
        Status = "检查结束；请查看数据库覆盖、预期产物及风险证据。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private void Favorite() => TryAction(() => { library.Save(RequiredCandidate()); Status = "候选已保存到本地引物库。"; });
    [RelayCommand(CanExecute = nameof(CanWork))] private void Library() => TryAction(() => new LibraryWindow(library.Read()) { Owner = Application.Current.MainWindow }.ShowDialog());
    [RelayCommand(CanExecute = nameof(CanWork))] private void Notes() => TryAction(() =>
    {
        var notes = CandidateNotes;
        var candidate = EditableCandidate(RequiredCandidate());
        candidate.Notes = notes; CandidateNotes = notes;
        dirty = true; Status = "备注已更新，请保存项目。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private void Experiment() => TryAction(() =>
    {
        var c = RequiredCandidate();
        var form = new FieldDialog("实验记录 · " + c.TargetId, ["熔解曲线", "产物确认", "标准曲线", "效率（填写测量值与单位）", "NTC", "no-RT", "实验备注"]);
        form.Owner = Application.Current.MainWindow;
        if (form.ShowDialog() != true) return;
        var v = form.Values; experiments.Add(new(c.Id, DateTimeOffset.Now, v[0], v[1], v[2], v[3], v[4], v[5], v[6]));
        dirty = true; Status = "实验记录已添加，请保存项目。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task SaveProjectAsync() => await Busy(async token =>
    {
        await FlushSelectionSavesAsync();
        var dialog = new SaveFileDialog { Filter = "qPCR 项目|*.qpcrproject", FileName = ProjectPath == "尚未保存" ? "qPCR-project.qpcrproject" : Path.GetFileName(ProjectPath) };
        if (dialog.ShowDialog() != true) return;
        var snapshotBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Snapshot(), ProjectStore.JsonOptions);
        var snapshot = System.Text.Json.JsonSerializer.Deserialize<ProjectDocument>(snapshotBytes, ProjectStore.JsonOptions)!;
        var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(snapshotBytes));
        await WriteProjectSnapshotAsync(dialog.FileName, snapshot, autoSaveSession, token);
        ProjectPath = dialog.FileName; dirty = !SnapshotMatches(fingerprint);
        AutoSaveStatus = recoveryPointerWarning is null ? "项目已保存" : "项目已保存；自动恢复入口更新失败";
        Status = "项目已保存，并完成重载校验。" + (recoveryPointerWarning is null ? "" : "\n" + recoveryPointerWarning);
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task OpenProjectAsync()
    {
        await FlushSelectionSavesAsync();
        if (!ConfirmDiscard()) return;
        var dialog = new OpenFileDialog { Filter = "qPCR 项目|*.qpcrproject" };
        if (dialog.ShowDialog() != true) return;
        await Busy(async token => { var project = await ProjectStore.LoadAsync(dialog.FileName, token); Restore(project); ProjectPath = dialog.FileName; Status = "项目已打开。"; });
    }
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task NewProjectAsync() { await FlushSelectionSavesAsync(); if (!ConfirmDiscard()) return; Restore(new ProjectDocument()); TargetName = "target_1"; TemplateText = ""; ProjectPath = "尚未保存"; dirty = false; }
    [RelayCommand(CanExecute = nameof(CanWork))] private void Export() => TryAction(() =>
    {
        var dialog = new SaveFileDialog { Filter = "Excel 工作簿|*.xlsx|CSV 表格|*.csv", FileName = "qPCR-primers.xlsx" };
        if (dialog.ShowDialog() != true) return;
        ResultExporter.Export(dialog.FileName, Snapshot(), SelectedOnly); Status = "结果已导出：" + dialog.FileName;
    });
    [RelayCommand] private void Cancel() => cancellation?.Cancel();
    [RelayCommand] private void ToggleTheme()
    {
        dark = !dark;
        ThemeSupport.SetDark(dark);
        var colors = dark ? new[] { "#101B28", "#192838", "#EAF1F8", "#B1C2D3", "#304357" }
            : ["#F4F7FB", "#FFFFFF", "#192D42", "#5F7185", "#DCE4EE"];
        var keys = new[] { "CanvasBrush", "CardBrush", "InkBrush", "MutedBrush", "LineBrush" };
        for (var i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        Application.Current.Resources["ShellBrush"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(dark ? "#70101B28" : "#70F4F7FB"));
        foreach (var window in Application.Current.Windows.OfType<MainWindow>()) window.Presentation.UpdateTheme(dark);
    }
    public bool ConfirmDiscard()
    {
        if (!dirty) return true;
        var dialog = new SaveChangesDialog(async () => { await SaveProjectCommand.ExecuteAsync(null); return !dirty; }, () => Status)
            { Owner = Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow };
        return dialog.ShowDialog() == true;
    }
    public bool RequestClose()
    {
        if (IsBusy) { Status = "任务正在运行，请先取消并等待结束。"; return false; }
        return ConfirmDiscard();
    }
    private async Task Busy(Func<CancellationToken, Task> work)
    {
        if (IsBusy) return;
        IsBusy = true; Progress = 0; cancellation = new();
        try { await work(cancellation.Token); }
        catch (OperationCanceledException) { Status = "任务已取消；已完成结果保留。"; }
        catch (Exception ex) { ReportError(ex); }
        finally { cancellation.Dispose(); cancellation = null; pauseGate?.TrySetResult(true); pauseGate = null; IsPaused = false; IsBusy = false; }
    }
    private void TryAction(Action action) { try { action(); } catch (Exception ex) { ReportError(ex); } }
    private void ReportError(Exception ex) { Status = "操作失败：" + ex.Message; LogText += $"\n{DateTimeOffset.Now:O}\n{ex}"; File.AppendAllText(Path.Combine(storageRoot, "errors.log"), $"{DateTimeOffset.Now:O}\n{ex}\n"); }
    private PrimerCandidate RequiredCandidate() => SelectedCandidate ?? throw new InvalidOperationException("请先选择候选。");
    private static int Int(string value) => int.Parse(value.Trim(), CultureInfo.InvariantCulture);
    private static double Num(string value) => double.Parse(value.Trim(), CultureInfo.InvariantCulture);
    private static string Chunk(string sequence, int start)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < sequence.Length; i += 60) builder.AppendLine($"{start + i,7}  {sequence.Substring(i, Math.Min(60, sequence.Length - i))}");
        return builder.ToString();
    }
    private static string FormatEvidence(SpecificityReport report) => report.Kind + "：" + report.Status + "\n" + report.Evidence + "\n" +
        string.Join("\n", report.Products.Take(500).Select(p => $"{p.SubjectId} {p.Start}–{p.End} ({p.End - p.Start + 1} bp) {p.FirstPrimer}/{p.SecondPrimer} 错配 {p.Mismatches} {(p.Expected ? "预期" : p.Risk )}")) +
        "\n\n错配位点（距 3′端，1=末端碱基；权重为启发式）：\n" + string.Join("\n", report.Sites.Where(s => s.MismatchPositions.Count > 0).Take(500)
            .Select(s => $"{s.SubjectId} {s.QueryId}：" + string.Join("；", s.MismatchPositions.Select(m => $"−{m.PositionFromThreePrime} {m.PrimerBase}/{m.SubjectBase} (w={m.Weight})"))));
}
