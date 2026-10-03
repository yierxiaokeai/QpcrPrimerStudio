using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    private ReferenceWorkspace? reference;
    private List<AssayRecord> assays = [];
    private TaskCompletionSource<bool>? pauseGate;
    public TargetRegionMode[] RegionModes { get; } = Enum.GetValues<TargetRegionMode>();
    public ExpressionMode[] ExpressionModes { get; } = Enum.GetValues<ExpressionMode>();
    [ObservableProperty] private TargetRegionMode regionMode = TargetRegionMode.PreferCds;
    [ObservableProperty] private ExpressionMode expressionMode = ExpressionMode.TemplateOnly;
    [ObservableProperty] private string avoidFivePrime = "50";
    [ObservableProperty] private string avoidThreePrime = "50";
    [ObservableProperty] private bool avoidVariants = true;
    [ObservableProperty] private bool rejectTerminalVariants = true;
    [ObservableProperty] private string variantTerminalBases = "5";
    [ObservableProperty] private string referenceSummary = "尚未选择参考项目";
    [ObservableProperty] private string qualityText = "选择候选查看评分、覆盖与排除原因。";
    [ObservableProperty] private bool advancedMode;
    partial void OnAdvancedModeChanged(bool value)
    {
        if (!value && ResultsTabIndex >= 6) ResultsTabIndex = 0;
    }
    [ObservableProperty] private bool autoSpecificity = true;
    [ObservableProperty] private string scoreWeightsText = "{}";
    [ObservableProperty] private string coverageText = "";
    [ObservableProperty] private bool isPaused;
    [ObservableProperty] private double sequenceZoom = 1;
    [ObservableProperty] private TranscriptAnnotation? displayAnnotation;
    [ObservableProperty] private List<TranscriptVariant> displayVariants = [];

    public TargetRegionSettings ReadRegionSettings() => new()
    {
        Mode = RegionMode, Expression = ExpressionMode, AvoidFivePrime = Int(AvoidFivePrime), AvoidThreePrime = Int(AvoidThreePrime),
        AvoidVariants = AvoidVariants, RejectTerminalVariants = RejectTerminalVariants, VariantTerminalBases = Int(VariantTerminalBases),
        RequiredTranscripts = CoverageText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        ScoreWeights = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(ScoreWeightsText) ?? throw new ArgumentException("评分权重需为 JSON 对象。")
    };
    private void LoadRegions(TargetRegionSettings p)
    {
        RegionMode = p.Mode; ExpressionMode = p.Expression; AvoidFivePrime = p.AvoidFivePrime.ToString(); AvoidThreePrime = p.AvoidThreePrime.ToString();
        AvoidVariants = p.AvoidVariants; RejectTerminalVariants = p.RejectTerminalVariants; VariantTerminalBases = p.VariantTerminalBases.ToString();
        ScoreWeightsText = System.Text.Json.JsonSerializer.Serialize(p.ScoreWeights);
        CoverageText = string.Join(",", p.RequiredTranscripts);
    }
    private List<string> CoverageFor(SequenceTarget target) => target.CoverageTranscripts.Count > 0 ? target.CoverageTranscripts.ToList()
        : target.ExpectedSubjects.Where(id => reference?.Find(id) is { } transcript && transcript.GeneId == reference?.Find(target.Id)?.GeneId).ToList();
    private TargetRegionSettings RegionsForTarget(TargetRegionSettings common, SequenceTarget target) => common with
    {
        RequiredTranscripts = common.Expression == ExpressionMode.SelectedTranscripts ? CoverageFor(target) : []
    };
    public void BindDatabase(DatabaseManifest database)
    {
        databases[database.Kind] = database;
        UpdateDatabaseSummary(); dirty = true;
    }
    private void RefreshReference()
    {
        ReferenceSummary = reference is null ? "尚未选择参考项目" : $"{reference.Name} · {reference.Version}\n{reference.Transcripts.Count:N0} 条转录本 · {(reference.HasGenome ? "含基因组" : "Transcriptome-only")}\n" + string.Join("\n", reference.Warnings);
        RefreshTracks();
    }
    private void RefreshTracks()
    {
        DisplayAnnotation = DisplayTarget is null ? null : reference?.Find(DisplayTarget.Id);
        if (DisplayAnnotation?.Sequence != DisplayTarget?.Sequence) DisplayAnnotation = null;
        DisplayVariants = DisplayAnnotation is null ? [] : reference!.ProjectVariants(DisplayAnnotation);
    }
    partial void OnDisplayTargetChanged(SequenceTarget? value) => RefreshTracks();
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task ImportReferenceAsync()
    {
        var form = new ReferenceDialog { Owner = Application.Current.MainWindow };
        if (form.ShowDialog() != true) return;
        await Busy(async token =>
        {
            var request = form.Request; var folder = Path.Combine(storageRoot, "references", Guid.NewGuid().ToString("N"));
            Status = "正在核对参考序列与注释并建立索引…";
            var imported = await Task.Run(() => new ReferenceImporter(Path.Combine(Path.GetDirectoryName(paths.Primer3)!, "..", "reference"))
                .ImportAsync(request, folder, token), token);
            var engine = new SpecificityEngine(paths);
            var transcriptDb = await engine.BuildDatabaseAsync(request.Transcripts, request.Name + " transcripts", "转录本", folder, token);
            DatabaseManifest? genomeDb = request.Genome is null ? null : await engine.BuildDatabaseAsync(request.Genome, request.Name + " genome", "基因组", folder, token);
            reference = imported; databases.Clear(); databases["转录本"] = transcriptDb;
            if (genomeDb is not null) databases["基因组"] = genomeDb;
            RefreshReference(); UpdateDatabaseSummary(); dirty = true; Status = "参考项目已校验并建立，按基因或转录本 ID 添加靶标。";
        });
    }
    [RelayCommand(CanExecute = nameof(CanWork))] private void OpenReference() => TryAction(() =>
    {
        var dialog = new OpenFileDialog { Filter = "参考项目|*.qpcrreference", InitialDirectory = Path.Combine(storageRoot, "references") };
        if (dialog.ShowDialog() != true) return;
        var loaded = ReferenceImporter.Load(dialog.FileName);
        foreach (var asset in loaded.Assets) if (!File.Exists(asset.Path) || Hashing.File(asset.Path) != asset.Sha256)
            throw new InvalidDataException($"参考文件缺失或已变化：{asset.Path}");
        var incomingDatabases = new Dictionary<string, DatabaseManifest>();
        foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(dialog.FileName)!, "database.json", SearchOption.AllDirectories))
        { var db = SpecificityEngine.ReadDatabase(path); incomingDatabases[db.Kind] = db; }
        reference = loaded; databases.Clear(); foreach (var db in incomingDatabases) databases[db.Key] = db.Value;
        RefreshReference(); UpdateDatabaseSummary(); dirty = true; Status = "参考项目已打开并核对文件指纹。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private void AddReferenceTarget() => TryAction(() =>
    {
        if (reference is null) throw new InvalidOperationException("请先建立或打开参考项目。");
        var form = new FieldDialog("选择参考靶标", ["Gene ID 或 Transcript ID（精确匹配）"]) { Owner = Application.Current.MainWindow };
        if (form.ShowDialog() != true) return;
        var id = form.Values[0].Trim();
        var matches = reference.Transcripts.Where(t => t.Id == id || t.GeneId == id).ToList();
        if (matches.Count == 0) throw new ArgumentException("参考项目中没有此 ID。");
        var transcript = matches.OrderByDescending(t => t.Cds.Sum(c => c.Length)).ThenByDescending(t => t.Sequence.Length).First();
        var target = new SequenceTarget { Id = transcript.Id, Sequence = transcript.Sequence, GeneId = transcript.GeneId,
            Source = reference.Name + " " + reference.Version, Junctions = transcript.Junctions, ExonBlocks = transcript.Exons,
            ReferenceGenomeSha256 = reference.Assets.FirstOrDefault(a => a.Role == "Genome")?.Sha256 ?? "", ExpectedSubjects = matches.Select(t => t.Id).ToList() };
        var existing = Targets.FirstOrDefault(t => t.Id == target.Id);
        if (existing is null) Targets.Add(target); else target = existing;
        SelectedTarget = target; ExpressionMode = matches.Count > 1 ? ExpressionMode.TotalGene : ExpressionMode.TemplateOnly;
        dirty = true; Status = $"已载入 {transcript.Id}，基因 {transcript.GeneId}，所选目标 {matches.Count} 条转录本。";
    });
    [RelayCommand] private void PauseResume()
    {
        if (!IsBusy) return;
        if (IsPaused) { pauseGate?.TrySetResult(true); pauseGate = null; IsPaused = false; Status = "继续批量任务。"; }
        else { pauseGate = new(TaskCreationOptions.RunContinuationsAsynchronously); IsPaused = true; Status = "将在当前靶标结束后暂停。"; }
    }
    private async Task WaitForResume(CancellationToken token) { if (pauseGate is { } gate) await gate.Task.WaitAsync(token); }
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task RetryFailedAsync() => await Busy(async token =>
    {
        var targets = Targets.Where(t => Runs.LastOrDefault(r => r.Target.Id == t.Id && r.Target.Sha256 == t.Sha256)?.State
            is "失败" or "无候选" or "取消" or "候选全部被质量规则排除").ToList();
        if (targets.Count == 0) throw new InvalidOperationException("没有失败或取消的靶标。");
        await DesignTargetsAsync(targets, ReadParameters(), token);
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task RelaxCurrentAsync() => await Busy(async token =>
    {
        if (SelectedRun is null) throw new InvalidOperationException("请选择需要重试的任务。");
        var p = SelectedRun.UsedParameters ?? ReadParameters();
        var next = p with { MinTm = Math.Max(50, p.MinTm - 1), MaxTm = Math.Min(75, p.MaxTm + 1),
            MinGc = Math.Max(25, p.MinGc - 5), MaxGc = Math.Min(75, p.MaxGc + 5), MaxProduct = Math.Min(350, p.MaxProduct + 25),
            ExcludedRegions = ReadParameters().ExcludedRegions };
        var target = SelectedRun.Target with { ParameterOverride = null };
        await DesignTargetsAsync([target], next, token);
        Status = "已新建一次放宽 Tm/GC/产物长度的设计；原结果与原参数保留。变异与特异性规则维持当前设置。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private void LockCandidate() => TryAction(() =>
    { var c = RequiredCandidate(); c.Locked = true; dirty = true; Status = "已锁定候选；重算检查时将创建新修订。"; });
    private PrimerCandidate EditableCandidate(PrimerCandidate candidate)
    {
        if (!candidate.Locked) return candidate;
        var copy = System.Text.Json.JsonSerializer.Deserialize<PrimerCandidate>(System.Text.Json.JsonSerializer.Serialize(candidate))!;
        copy.Id = Guid.NewGuid().ToString("N"); copy.ParentCandidateId = candidate.Id; copy.Revision++; copy.Locked = false;
        var run = Runs.First(r => r.Candidates.Contains(candidate)); run.Candidates.Add(copy);
        if (SelectedRun == run)
        {
            Candidates.Add(copy);
            if (SelectedCandidate == candidate) SelectedCandidate = copy;
            RefreshResultContext();
        }
        dirty = true; return copy;
    }
    [RelayCommand(CanExecute = nameof(CanWork))] private void TargetOverride() => TryAction(() =>
    {
        ApplyTemplate(); var old = SelectedTarget!; var target = old with { ParameterOverride = ReadParameters() };
        Targets[Targets.IndexOf(old)] = target; SelectedTarget = target; dirty = true; Status = "当前设计参数已保存为此靶标的独立设置。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private void RecordAssay() => TryAction(() =>
    {
        var c = RequiredCandidate();
        var form = new FieldDialog("MIQE 实验记录 · " + c.TargetId, ["状态：Designed / Ordered / Tested / Validated", "样本材料", "仪器", "反应条件",
            "扩增效率（实测 %）", "标准曲线 R²", "熔解曲线", "凝胶 / 产物确认", "NTC", "−RT", "内参基因", "生物学重复数", "技术重复数", "备注"]) { Owner = Application.Current.MainWindow };
        if (form.ShowDialog() != true) return;
        var v = form.Values;
        if (v[0] is not ("Designed" or "Ordered" or "Tested" or "Validated")) throw new ArgumentException("请填写所列的实验状态。");
        if (v[0] == "Validated" && new[] { 1, 2, 3, 4, 5, 6, 8, 9, 10, 11, 12 }.Any(i => string.IsNullOrWhiteSpace(v[i])))
            throw new ArgumentException("Validated 状态需要填写材料、仪器/反应条件、实测效率、R²、熔解曲线、NTC、−RT、内参与重复数；仍由实验人员确认结论。");
        assays.Add(new() { CandidateId = c.Id, Status = v[0], SampleMaterial = v[1], Instrument = v[2], ReactionConditions = v[3],
            Efficiency = v[4], RSquared = v[5], MeltCurve = v[6], Gel = v[7], Ntc = v[8], NoRt = v[9], ReferenceGene = v[10],
            BiologicalReplicates = v[11], TechnicalReplicates = v[12], Notes = v[13] });
        dirty = true; Status = "实验记录已添加；导出 Excel 时写入 MIQE Assays 表。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task SavePresetAsync() => await Busy(async token =>
    {
        var dialog = new SaveFileDialog { Filter = "参数预设|*.qpcrpreset", FileName = "SYBR-RT-qPCR.qpcrpreset" };
        if (dialog.ShowDialog() != true) return;
        await ProjectStore.SaveAsync(dialog.FileName, new ProjectDocument { Name = Path.GetFileNameWithoutExtension(dialog.FileName),
            Parameters = ReadParameters(), RegionSettings = ReadRegionSettings(), AutoSpecificity = AutoSpecificity }, token);
        Status = "参数预设已保存，可用于其他项目。";
    });
    [RelayCommand(CanExecute = nameof(CanWork))] private async Task LoadPresetAsync() => await Busy(async token =>
    {
        var dialog = new OpenFileDialog { Filter = "参数预设|*.qpcrpreset" };
        if (dialog.ShowDialog() != true) return;
        var preset = await ProjectStore.LoadAsync(dialog.FileName, token);
        LoadParameters(preset.Parameters); LoadRegions(preset.RegionSettings); AutoSpecificity = preset.AutoSpecificity; dirty = true;
        Status = "预设已载入：" + preset.Name;
    });
    private async Task CheckRunSpecificityAsync(TargetRun run, CancellationToken token)
    {
        if (!AutoSpecificity || databases.Count == 0) return;
        var settings = new SpecificitySettings(Int(MaxMismatches), Int(MaxThreePrimeMismatches), 5, Int(SpecificityMaxProduct), Int(MaxSubjects));
        var plan = TargetRegionEngine.Plan(run.Target, run.RegionSettings, reference);
        foreach (var candidate in run.Candidates.Where(c => c.Assessment.Accepted))
        foreach (var db in databases.Values)
        {
            await WaitForResume(token); token.ThrowIfCancellationRequested();
            Status = $"{run.Target.Id} #{candidate.Rank}：{db.Kind}特异性检查…";
            try
            {
                candidate.Specificity.Add(await new SpecificityEngine(paths).CheckAsync(candidate, run.Target, db, settings, storageRoot, token));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { candidate.Specificity.Add(new() { Kind = db.Kind, Database = db, Settings = settings, Status = "检查失败", Evidence = ex.Message }); }
            CandidateQualityEngine.Evaluate(candidate, run.Target, run.UsedParameters!, run.RegionSettings, plan, reference);
        }
        run.RejectionCounts = run.Candidates.SelectMany(c => c.Assessment.Rejections).GroupBy(r => r).ToDictionary(g => g.Key, g => g.Count());
        if (run.Candidates.Count > 0 && run.AcceptedCount == 0) run.State = "候选全部被质量规则排除";
        run.Message += $"\n特异性筛查后保留 {run.AcceptedCount}/{run.Candidates.Count} 对。\n" + string.Join("\n", run.RejectionCounts.Select(kv => $"{kv.Value} 对：{kv.Key}"));
    }
}
