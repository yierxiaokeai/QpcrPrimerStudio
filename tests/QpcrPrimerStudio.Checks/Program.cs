using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClosedXML.Excel;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class Program
{
    private static int passed;
    private static string root = "";
    private static string work = "";
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            root = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../.."));
            work = Path.Combine(root, "artifacts", "checks", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(work);
            RunAsync().GetAwaiter().GetResult();
            Console.WriteLine($"PASS: {passed} checks. Evidence: {work}");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task RunAsync()
    {
        var executable = Path.Combine(root, "tools", "primer3", "primer3-2.6.1", "src", "primer3_core.exe");
        var random = new Random(1729);
        var sequence = new string(Enumerable.Range(0, 600).Select(_ => "ACGT"[random.Next(4)]).ToArray());
        var target = new SequenceTarget { Id = "geneA.t1", Sequence = sequence };
        var parameters = new DesignParameters();
        var run = await new Primer3Engine(executable).DesignAsync(target, parameters, CancellationToken.None);
        Assert(run.Candidates.Count > 0, "Real Primer3 generates candidates");
        var candidate = run.Candidates.First();
        Assert(sequence.Substring(candidate.Forward.Start - 1, candidate.Forward.Sequence.Length) == candidate.Forward.Sequence, "Forward coordinates");
        Assert(SequenceFiles.ReverseComplement(sequence.Substring(candidate.Reverse.Start - 1, candidate.Reverse.Sequence.Length)) == candidate.Reverse.Sequence, "Reverse coordinates and orientation");
        Assert(candidate.Amplicon.Length == candidate.ProductLength && candidate.ProductLength is >= 80 and <= 200, "Amplicon length");
        var direct = await ProcessRunner.RunAsync(executable, ["--strict_tags", "--default_version=2"], run.RawInput, CancellationToken.None);
        Assert(direct.Output == run.RawOutput, "Official engine output reproducibility");
        var evaluated = await new Primer3Engine(executable).DesignAsync(target, parameters, CancellationToken.None, candidate.Forward.Sequence, candidate.Reverse.Sequence);
        Assert(evaluated.Candidates.Count == 1 && evaluated.Candidates[0].Amplicon == candidate.Amplicon, "Existing primer pair evaluation");
        passed += await PrimerWorkflowChecks.RunAsync(root, work, target, run, executable);
        passed += await HairpinScreeningChecks.RunAsync(root, work, target, run, executable);
        passed += await ParameterRecommendationChecks.RunAsync(root, work, target, run, executable);
        passed += await SmartExportChecks.RunAsync(root, work, target, run, executable);
        passed += await SmartBindingChecks.RunAsync(root, work, target, run);
        var thermo = await new Thermodynamics(Path.Combine(Path.GetDirectoryName(executable)!, "ntthal.exe"))
            .AnalyzeAsync(candidate.Forward.Sequence, candidate.Reverse.Sequence, parameters, CancellationToken.None);
        Assert(thermo.Contains("3′ 异二聚体") && thermo.Contains("dG"), "Real structure and thermodynamic outputs");
        await File.WriteAllTextAsync(Path.Combine(work, "thermodynamics.txt"), thermo);
        var fastaPath = Path.Combine(work, "templates.fasta");
        await File.WriteAllTextAsync(fastaPath, $">geneA.t1\n{sequence}\n>geneB.t1\n{sequence}\n");
        Assert(SequenceFiles.Read(fastaPath).Count == 2, "Mature FASTA import");
        var genBankPath = Path.Combine(work, "template.gb");
        var genBankSequence = new Bio.Sequence(Bio.Alphabets.DNA, sequence) { ID = target.Id };
        genBankSequence.Metadata["GenBank"] = new Bio.IO.GenBank.GenBankMetadata
        {
            Locus = new Bio.IO.GenBank.GenBankLocusInfo { Name = target.Id, SequenceLength = sequence.Length,
                SequenceType = "bp", MoleculeType = Bio.IO.GenBank.MoleculeType.DNA,
                StrandTopology = Bio.IO.GenBank.SequenceStrandTopology.Linear, Date = new DateTime(2026, 1, 1) }
        };
        using (var genBankStream = File.Create(genBankPath))
            new Bio.IO.GenBank.GenBankFormatter().Format(genBankStream, genBankSequence);
        Assert(SequenceFiles.Read(genBankPath).Single().Sequence == sequence, "Mature GenBank import with bundled registration compatibility");
        var project = new ProjectDocument { Targets = [target], Runs = [run], Parameters = parameters };
        var projectPath = Path.Combine(work, "project.qpcrproject");
        await ProjectStore.SaveAsync(projectPath, project);
        var reloaded = await ProjectStore.LoadAsync(projectPath);
        Assert(reloaded.Runs[0].RawOutput == run.RawOutput && reloaded.Runs[0].Candidates[0].Reverse == candidate.Reverse, "Project round trip");
        await ProjectStore.SaveAsync(projectPath, project);
        Assert(ProjectBackups.List(projectPath).Count == 1, "Atomic save preserves the previous project in a compressed recovery record");
        passed += await SelectionAutoSaveChecks.RunAsync(root, work, project);
        passed += await AuditFixChecks.RunAsync(root, work, project, executable);
        passed += ReauditWorkflowChecks.Run(root, work, project);
        passed += await ReauditInteractionChecks.RunAsync(root, work, project);
        passed += await ReauditOperationsChecks.RunAsync(root, work, project);
        passed += await ReauditAlgorithmChecks.RunAsync(root, work, project, executable);
        var library = new PrimerLibrary(Path.Combine(work, "library.sqlite"));
        library.Save(candidate); library.Save(candidate);
        Assert(library.Read("geneA").Count == 1 && library.Read()[0].Forward == candidate.Forward, "SQLite library upsert and reload");
        var xlsxPath = Path.Combine(work, "primers.xlsx");
        ResultExporter.Export(xlsxPath, project, false);
        using (var workbook = new XLWorkbook(xlsxPath)) Assert(workbook.Worksheet("Primers").Cell(2, 4).GetString() == candidate.Forward.Sequence, "XLSX save and reload");
        var csvPath = Path.Combine(work, "primers.csv");
        ResultExporter.Export(csvPath, project, false);
        Assert(File.ReadAllText(csvPath).Contains(candidate.Reverse.Sequence), "CSV export");
        var sites = new List<BindingSite>
        {
            new("F", "geneA.t1", 10, 29, true, 0, 0), new("R", "geneA.t1", 100, 119, false, 0, 0),
            new("F", "geneB.t1", 10, 29, true, 1, 0), new("R", "geneB.t1", 100, 119, false, 2, 0),
            new("F", "away", 10, 29, false, 0, 0), new("R", "away", 100, 119, true, 0, 0)
        };
        var products = SpecificityEngine.PairSites(sites, ["geneA.t1"], "转录本", 200);
        Assert(products.Count == 2 && products.Count(p => p.Expected) == 1 && products.All(p => p.SubjectId != "away"), "Paired geometry and expected targets");
        await AssertThrowsAsync<ArgumentException>(() => new Primer3Engine(executable).DesignAsync(target, parameters with { RequireJunction = true }, CancellationToken.None), "Missing junction annotation stops constrained design");
        await AssertThrowsAsync<ArgumentException>(() => new Primer3Engine(executable).DesignAsync(target, parameters with { MinTm = double.NaN }, CancellationToken.None), "Nonfinite parameters rejected");
        await AssertThrowsAsync<FileNotFoundException>(() => ProcessRunner.RunAsync("missing-engine.exe", [], null, CancellationToken.None), "Missing engine does not produce successful results");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await AssertThrowsAsync<OperationCanceledException>(() => new Primer3Engine(executable).DesignAsync(target, parameters, cancelled.Token), "Cancellation is explicit");
        await File.WriteAllTextAsync(Path.Combine(work, "engine-input.txt"), run.RawInput);
        await File.WriteAllTextAsync(Path.Combine(work, "engine-output.txt"), run.RawOutput);
        await CheckReferenceAndQualityAsync(target, candidate, parameters, executable);
        await CheckBlastAsync(target, candidate, fastaPath);
        RenderWindow(project);
        await CheckBatchAsync();
    }
    private static async Task CheckBatchAsync()
    {
        var random = new Random(54321);
        var targets = Enumerable.Range(1, 100).Select(i => new SequenceTarget { Id = $"batch{i:D3}",
            Sequence = new string(Enumerable.Range(0, 600).Select(_ => "ACGT"[random.Next(4)]).ToArray()),
            ParameterOverride = i == 50 ? new DesignParameters { MinLength = 14 } : null }).ToList();
        var vm = new MainViewModel(Path.Combine(root, "src", "QpcrPrimerStudio.Desktop", "bin", "Debug", "net10.0-windows", "tools"), Path.Combine(work, "batch-storage"));
        var timer = System.Diagnostics.Stopwatch.StartNew();
        await vm.DesignTargetsAsync(targets, new(), CancellationToken.None); timer.Stop();
        Assert(vm.Runs.Count == 100 && vm.Runs.Select(r => r.Target.Id).Distinct().Count() == 100, "100-target batch preserves every target status");
        Assert(vm.Runs.Single(r => r.Target.Id == "batch050").State == "失败" && vm.Runs.Where(r => r.Target.Id != "batch050").All(r => r.State != "失败"), "Invalid per-target parameters fail locally without stopping batch");
        var summary = new { Targets = 100, SequenceLength = 600, Success = vm.Runs.Count(r => r.State == "完成"),
            Failed = vm.Runs.Count(r => r.State == "失败"), Seconds = timer.Elapsed.TotalSeconds,
            Scope = "Synthetic templates, Primer3 and QC only; no BLAST/reference project. Includes one deliberately invalid parameter override." };
        await File.WriteAllTextAsync(Path.Combine(work, "batch-summary.json"), JsonSerializer.Serialize(summary, ProjectStore.JsonOptions));
        Console.WriteLine($"Batch elapsed {timer.Elapsed.TotalSeconds:F2} s; success {summary.Success}; failure {summary.Failed}.");
    }
    private static async Task CheckBlastAsync(SequenceTarget target, PrimerCandidate candidate, string fasta)
    {
        var bin = Path.Combine(root, "tools", "blast", "ncbi-blast-2.14.0+", "bin");
        var engine = new SpecificityEngine(new("", Path.Combine(bin, "blastn.exe"), Path.Combine(bin, "makeblastdb.exe"), Path.Combine(bin, "blastdbcmd.exe")));
        var db = await engine.BuildDatabaseAsync(fasta, "integration transcript database", "转录本", Path.Combine(work, "databases"), CancellationToken.None);
        var report = await engine.CheckAsync(candidate, target, db, new(), Path.Combine(work, "blast-jobs"), CancellationToken.None);
        Assert(report.Products.Any(p => p.Expected && p.SubjectId == target.Id), "Real BLAST finds intended paired product");
        Assert(report.Products.Any(p => !p.Expected && p.SubjectId == "geneB.t1"), "Real BLAST detects paralog product");
        Assert(report.RawXml.Contains("BlastOutput"), "Specificity preserves native search evidence");
    }
    private static async Task CheckReferenceAndQualityAsync(SequenceTarget target, PrimerCandidate candidate, DesignParameters p, string executable)
    {
        var genome = target.Sequence[..300] + new string('A', 1100) + target.Sequence[300..];
        var genomePath = Path.Combine(work, "genome.fa"); await File.WriteAllTextAsync(genomePath, $">chr1\n{genome}\n");
        var transcriptPath = Path.Combine(work, "annotated.fa");
        await File.WriteAllTextAsync(transcriptPath, $">geneA.t1\n{target.Sequence}\n>geneA.t2\n{target.Sequence[..300] + target.Sequence[350..]}\n>geneMinus.t1\n{SequenceFiles.ReverseComplement(target.Sequence)}\n");
        var gffPath = Path.Combine(work, "annotation.gff3");
        await File.WriteAllTextAsync(gffPath, "##gff-version 3\n" +
            "chr1\tchecks\tgene\t1\t1700\t.\t+\t.\tID=geneA\n" +
            "chr1\tchecks\tmRNA\t1\t1700\t.\t+\t.\tID=geneA.t1;Parent=geneA\n" +
            "chr1\tchecks\texon\t1\t300\t.\t+\t.\tParent=geneA.t1\n" +
            "chr1\tchecks\texon\t1401\t1700\t.\t+\t.\tParent=geneA.t1\n" +
            "chr1\tchecks\tCDS\t51\t300\t.\t+\t0\tParent=geneA.t1\n" +
            "chr1\tchecks\tCDS\t1401\t1650\t.\t+\t2\tParent=geneA.t1\n" +
            "chr1\tchecks\tmRNA\t1\t1700\t.\t+\t.\tID=geneA.t2;Parent=geneA\n" +
            "chr1\tchecks\texon\t1\t300\t.\t+\t.\tParent=geneA.t2\n" +
            "chr1\tchecks\texon\t1451\t1700\t.\t+\t.\tParent=geneA.t2\n" +
            "chr1\tchecks\tgene\t1\t1700\t.\t-\t.\tID=geneMinus\n" +
            "chr1\tchecks\tmRNA\t1\t1700\t.\t-\t.\tID=geneMinus.t1;Parent=geneMinus\n" +
            "chr1\tchecks\texon\t1\t300\t.\t-\t.\tParent=geneMinus.t1\n" +
            "chr1\tchecks\texon\t1401\t1700\t.\t-\t.\tParent=geneMinus.t1\n");
        var variantPosition = candidate.Forward.End <= 300 ? candidate.Forward.End : candidate.Forward.End + 1100;
        var vcfPath = Path.Combine(work, "variants.vcf"); var refBase = genome[variantPosition - 1]; var altBase = refBase == 'A' ? 'C' : 'A';
        await File.WriteAllTextAsync(vcfPath, $"##fileformat=VCFv4.3\n##INFO=<ID=AF,Number=A,Type=Float,Description=\"Allele frequency\">\n#CHROM\tPOS\tID\tREF\tALT\tQUAL\tFILTER\tINFO\nchr1\t{variantPosition}\trsTest\t{refBase}\t{altBase}\t.\tPASS\tAF=0.25\n");
        var importer = new ReferenceImporter(Path.Combine(root, "tools", "reference"));
        var reference = await importer.ImportAsync(new("Synthetic species", "checks-v1", transcriptPath, genomePath, gffPath, vcfPath), Path.Combine(work, "reference"), CancellationToken.None);
        Assert(reference.Transcripts.Count == 3 && reference.Find(target.Id)!.Junctions.SequenceEqual([300]), "GFF3 exon and transcript mapping");
        var minus = reference.Find("geneMinus.t1")!;
        Assert(minus.Exons[0].GenomicPosition(1) == 1700 && minus.Exons[1].GenomicPosition(600) == 1, "Negative strand reconstruction and coordinates");
        Assert(reference.Variants.Single().Frequency == 0.25 && reference.ProjectVariants(reference.Find(target.Id)!).Single().Start == candidate.Forward.End, "VCF REF check and transcript projection");
        var settings = new TargetRegionSettings { Mode = TargetRegionMode.CdsOnly, AvoidFivePrime = 0, AvoidThreePrime = 0 };
        var region = TargetRegionEngine.Plan(target, settings, reference);
        Assert(region.Allowed.Count == 1 && region.Allowed[0] == new SequenceRegion(51, 500), "CDS-only region across exon junction");
        var utr = TargetRegionEngine.Plan(target, settings with { Mode = TargetRegionMode.ThreePrimeUtr }, reference);
        Assert(utr.Allowed.Single() == new SequenceRegion(551, 50), "3′ UTR target region");
        var total = TargetRegionEngine.Plan(target, settings with { Mode = TargetRegionMode.EntireTranscript, Expression = ExpressionMode.TotalGene }, reference);
        Assert(total.RequiredTranscripts.Count == 2 && total.Excluded.Contains(new SequenceRegion(301, 50)), "Gene-level shared exon regions");
        var isoform = TargetRegionEngine.Plan(target, settings with { Mode = TargetRegionMode.EntireTranscript, Expression = ExpressionMode.IsoformSpecific }, reference);
        Assert(isoform.UniqueJunctions.SequenceEqual([300]), "Isoform-specific unique junction");
        var original = candidate.Assessment;
        var quality = CandidateQualityEngine.Evaluate(candidate, target, p, settings with { Mode = TargetRegionMode.EntireTranscript },
            TargetRegionEngine.Plan(target, settings with { Mode = TargetRegionMode.EntireTranscript }, reference), reference);
        Assert(quality.Variants.Any(v => v.Rejected && v.PositionFromThreePrime == 1), "3′ terminal SNP is hard rejected");
        Assert(quality.Components.Count(c => c.Quality is null) >= 2 && quality.EvidenceCoverage < 100, "Missing specificity is not treated as passed");
        candidate.Assessment = new();
        var paired = new PrimerCandidate { Forward = candidate.Forward with { Start = 100, End = 119 }, Reverse = candidate.Reverse with { Start = 500, End = 519 },
            Amplicon = target.Sequence.Substring(99, 420), ProductLength = 420 };
        var gdna = CandidateQualityEngine.Evaluate(paired, target, p, settings with { Mode = TargetRegionMode.EntireTranscript }, total, reference).Gdna;
        Assert(gdna.Level == "B" && gdna.IntronicBases == 1100, "gDNA long-intron discrimination");
        var junction = candidate.Forward.End - p.JunctionOverlap3;
        var junctionRun = await new Primer3Engine(executable).DesignAsync(target with { Junctions = [junction] }, p with { RequireJunction = true }, CancellationToken.None,
            candidate.Forward.Sequence, candidate.Reverse.Sequence);
        Assert(junctionRun.Candidates.Count == 1 && junctionRun.Candidates[0].JunctionEvidence.Contains("跨连接位点"), "Primer3 junction coordinate convention matches cDNA evidence");
        var noGenome = await importer.ImportAsync(new("Transcriptome only", "checks-v1", transcriptPath), Path.Combine(work, "transcript-only"), CancellationToken.None);
        Assert(!noGenome.HasGenome && noGenome.Warnings.Any(w => w.Contains("Unknown")), "Transcriptome-only unavailable checks are explicit");
        await AssertThrowsAsync<ArgumentException>(() => Task.Run(() => TargetRegionEngine.Plan(target, settings, noGenome)), "CDS-only refuses absent annotation");
        var broken = Path.Combine(work, "broken.gff3"); await File.WriteAllTextAsync(broken, "not a GFF3 file\n");
        await AssertThrowsAsync<InvalidOperationException>(() => importer.ImportAsync(new("Broken", "checks", transcriptPath, genomePath, broken), Path.Combine(work, "broken-reference"), CancellationToken.None), "Malformed GFF3 is rejected");
        candidate.Assessment = original;
    }
    private static void RenderWindow(ProjectDocument project)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App { IsIntegrationTest = true }; app.InitializeComponent();
                var vm = new MainViewModel(Path.Combine(root, "src", "QpcrPrimerStudio.Desktop", "bin", "Debug", "net10.0-windows", "tools"), Path.Combine(work, "ui-storage"));
                var window = new MainWindow(vm) { ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 }; vm.Restore(project);
                window.Show(); window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                var visual = (FrameworkElement)window.Content;
                var background = new DrawingVisual();
                using (var drawing = background.RenderOpen()) { drawing.DrawRectangle(window.Background, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight)); drawing.DrawRectangle(new VisualBrush(visual), null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight)); }
                var image = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32); image.Render(background);
                var pixels = new byte[image.PixelWidth * image.PixelHeight * 4]; image.CopyPixels(pixels, image.PixelWidth * 4, 0);
                Assert(Enumerable.Range(0, pixels.Length / 4).Count(i => pixels[i * 4 + 3] > 0) > pixels.Length / 8, "WPF render contains visible pixels");
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using var output = File.Create(Path.Combine(work, "workspace.png")); encoder.Save(output);
                Assert(window.IsInitialized && vm.Candidates.Count > 0, "WPF workspace loads real results");
                Assert(!vm.AdvancedMode, "Workspace starts in simple mode");
                var templatePanel = VisualChildren<System.Windows.Controls.Expander>(window).Single(e => Equals(e.Header, "靶标序列与引物输入"));
                var resultGrid = VisualChildren<System.Windows.Controls.DataGrid>(window).Single(g => g.Name == "CandidateGrid");
                Assert(!templatePanel.IsExpanded && resultGrid.ActualHeight > 200, "Existing results have a readable default workspace");
                Assert(resultGrid.FontWeight == FontWeights.Normal, "Result body keeps normal text weight");
                var visibleCells = VisualChildren<System.Windows.Controls.TextBlock>(resultGrid).Where(t => t.IsVisible).ToList();
                Assert(visibleCells.Any(t => t.Text.Contains("Tm ") && t.Text.Contains(" nt")) && visibleCells.Any(t => t.Text.StartsWith("发卡 Tm")),
                    "Tm, primer length and hairpin metrics are visible by default");
                Assert(resultGrid.RowDetailsVisibilityMode == System.Windows.Controls.DataGridRowDetailsVisibilityMode.VisibleWhenSelected
                    && visibleCells.Any(t => t.Text == "Forward 发卡结构"), "Selected hairpin structures appear inside the candidate list");
                var summaryConverter = new CandidateListConverter();
                var missingSpecificity = new PrimerCandidate { Forward = vm.Candidates[0].Forward, Reverse = vm.Candidates[0].Reverse };
                Assert(summaryConverter.Convert(missingSpecificity, typeof(string), "Mismatches", CultureInfo.InvariantCulture).Equals("未检查"),
                    "Missing specificity remains unchecked in mismatch summary");
                var mismatchConverter = new CandidateMismatchConverter();
                var sourceCandidate = vm.Candidates[0]; var sourceTarget = vm.SelectedRun!.Target;
                Assert(mismatchConverter.Convert([sourceCandidate, sourceTarget], typeof(string), null!, CultureInfo.InvariantCulture)
                    .ToString()!.Contains("模板 0/0 · 3′ 0/0\n数据库 未检查"),
                    "Template exact matches are displayed separately from unchecked database matches");
                static char Other(char c) => c == 'A' ? 'C' : 'A';
                var altered = new PrimerCandidate { TargetId = sourceCandidate.TargetId, Provenance = sourceCandidate.Provenance,
                    Forward = sourceCandidate.Forward with { Sequence = sourceCandidate.Forward.Sequence[..^1] + Other(sourceCandidate.Forward.Sequence[^1]) },
                    Reverse = sourceCandidate.Reverse with { Sequence = Other(sourceCandidate.Reverse.Sequence[0]) + sourceCandidate.Reverse.Sequence[1..] } };
                Assert(mismatchConverter.Convert([altered, sourceTarget], typeof(string), null!, CultureInfo.InvariantCulture)
                    .ToString()!.Contains("模板 1/1 · 3′ 1/0"), "Template mismatch calculation preserves reverse orientation and terminal counts");
                Assert(mismatchConverter.Convert([sourceCandidate, sourceTarget with { Sequence = sourceTarget.Sequence[..^1] }], typeof(string), null!, CultureInfo.InvariantCulture)
                    .ToString()!.Contains("模板 指纹不匹配"), "Changed template provenance is not reported as zero mismatches");
                var fSite = new BindingSite("F", "off", 1, 20, true, 1, 0);
                var rSite = new BindingSite("R", "off", 100, 119, false, 2, 1);
                missingSpecificity.Specificity.Add(new SpecificityReport { Status = "完成", Products = [new PotentialProduct("off", 1, 119, "F", "R", 3, false) { ForwardSite = fSite, ReverseSite = rSite }] });
                Assert(summaryConverter.Convert(missingSpecificity, typeof(string), "Mismatches", CultureInfo.InvariantCulture).ToString()!.Contains("非靶 1/2 · 3′ 0/1"),
                    "Mismatch summary preserves forward, reverse and terminal counts");
                var pageTitle = VisualChildren<System.Windows.Controls.TextBlock>(window).Single(t => t.Text == "引物设计");
                Assert(((SolidColorBrush)pageTitle.Foreground).Color == ((SolidColorBrush)Application.Current.FindResource("InkBrush")).Color,
                    "Navigation selection color stays in the navigation label");
                var title = (System.Windows.Controls.TextBlock)window.FindName("HeaderTitleText");
                var logo = (System.Windows.Controls.Image)window.FindName("HeaderLogo");
                var progress = (System.Windows.Controls.ProgressBar)window.FindName("TaskProgress");
                Assert(window.WindowStyle == WindowStyle.None && title.Text.StartsWith("一二 ") && window.Title == "qPCR Primer Studio", "Integrated title has a local-only prefix");
                Assert(logo.Source is BitmapSource bitmapLogo && bitmapLogo.PixelWidth >= logo.ActualWidth * 4, "Header logo uses a high-resolution source");
                Assert(!progress.IsVisible, "Idle task progress is hidden");
                vm.IsBusy = true; window.UpdateLayout();
                Assert(progress.IsVisible, "Running task progress is visible");
                vm.IsBusy = false;
                CheckSequenceInputFeedback(window, vm);
                CheckHairpinScreeningUi(window, vm);
                CheckCompactLayouts(window, vm);
                CheckHelpAndSaveDialog(window);
                passed += SelectionAutoSaveUiChecks.Run(window, root, work, project);
                passed += ParameterRecommendationUiChecks.Run(window, root, work, project);
                passed += TargetNavigationUiChecks.Run(window, root, work, project);
                passed += ReauditInteractionUiChecks.Run(window, root, work, project);
                app.Shutdown();
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw new InvalidOperationException("WPF rendering check failed", error);
    }
    private static void CheckSequenceInputFeedback(MainWindow window, MainViewModel vm)
    {
        var original = vm.TemplateText; var originalTarget = vm.SelectedTarget; var runCount = vm.Runs.Count;
        var contaminated = original + "\r\n将 Debian 13 的 APT 软件源切换为中科大（USTC）";
        vm.TemplateText = contaminated;
        vm.ConfigureSearchCommand.ExecuteAsync(null).GetAwaiter().GetResult();
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        var input = VisualChildren<System.Windows.Controls.TextBox>(window).Single(t => t.Name == "TargetSequenceInput");
        var feedback = VisualChildren<System.Windows.Controls.TextBlock>(window).Single(t => t.Name == "TemplateInputFeedback");
        Assert(vm.TemplateText == contaminated && vm.SelectedTarget == originalTarget && vm.Runs.Count == runCount &&
            vm.Status.Contains("无效字符") && !vm.Status.Contains("Specified argument"),
            "Search rejects pasted prose before changing the target or creating a task");
        Assert(feedback.IsVisible && feedback.Text.Contains("第 2 行、第 1 列") && input.SelectionStart == original.Length + 2 && input.SelectedText == "将",
            "Invalid template input has visible Chinese feedback and selects the offending character");
        CaptureLayout(window, "invalid-sequence-feedback.png");
        vm.ApplyTemplateCommand.Execute(null);
        Assert(vm.SelectedTarget == originalTarget && vm.TemplateInputError.Length > 0,
            "Saving a contaminated template preserves the prior valid target");
        vm.TemplateText = original;
        Assert(vm.TemplateInputError == "" && vm.TemplateErrorOffset == -1, "Editing the template clears the old input issue");
        vm.ApplyTemplateCommand.Execute(null); vm.InputExpanded = false; window.UpdateLayout();
        Assert(vm.Status.Contains("模板已保存") && vm.SelectedTarget?.Sequence == original,
            "A corrected template saves successfully after an input failure");
    }
    private static void CheckHairpinScreeningUi(MainWindow window, MainViewModel vm)
    {
        void Complete(Func<Task> action)
        {
            var previousContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
            try
            {
                var task = action();
                if (!task.IsCompleted)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    task.ContinueWith(_ => window.Dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                task.GetAwaiter().GetResult(); window.UpdateLayout();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }
        var grid = VisualChildren<System.Windows.Controls.DataGrid>(window).Single(g => g.Name == "CandidateGrid");
        var total = vm.Candidates.Count;
        Complete(() => vm.ScreenHairpinsCommand.ExecuteAsync(null));
        Assert(vm.HairpinSettings.AnnealingC == 60 && grid.Items.Count == total && vm.HasHairpinScreening,
            "One-click screening defaults to the experiment's 60 C and keeps low-temperature hairpins");
        Complete(() => vm.ApplyHairpinScreeningAsync(new HairpinScreenSettings(30, CheckTerminalHairpins: false)));
        var report = vm.SelectedRun!.HairpinScreening!; var excluded = report.Decisions.Count(d => d.Excluded);
        Assert(grid.Items.Count == total - excluded && excluded > 0 && vm.Candidates.Count == total &&
            VisualChildren<System.Windows.Controls.CheckBox>(window).Any(c => Equals(c.Content, "显示已排除") && c.IsVisible),
            "Applying screening hides risky rows and exposes a reversible review control");
        CaptureLayout(window, "hairpin-filtered.png");
        vm.ShowHairpinExcluded = true; vm.SelectedCandidate = vm.Candidates.First(c => report.DecisionFor(c)?.Excluded == true); window.UpdateLayout();
        Assert(grid.Items.Count == total && vm.QualityText.Contains("已排除") && vm.QualityText.Contains("ΔG₃₇"),
            "Showing excluded candidates restores rows and exposes the actual exclusion evidence");
        var loaded = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(vm.Snapshot()))!;
        vm.Restore(loaded); window.UpdateLayout();
        Assert(!vm.ShowHairpinExcluded && grid.Items.Count == total - excluded && vm.HasHairpinScreening,
            "Restoring a project reapplies the saved screening to the result view");
        vm.ClearHairpinScreeningCommand.Execute(null); window.UpdateLayout();
        Assert(grid.Items.Count == total && !vm.HasHairpinScreening && vm.HairpinFilterSummary == "",
            "Clearing the filter restores the complete result view");
        var popup = new HairpinSettingsDialog(new())
            { Owner = window, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        popup.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            var input = (System.Windows.Controls.TextBox)popup.FindName("AnnealingInput");
            var apply = (System.Windows.Controls.Button)popup.FindName("ApplySettingsButton");
            input.Text = "NaN"; apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Assert(popup.IsVisible && ((System.Windows.Controls.TextBlock)popup.FindName("Feedback")).Text.Length > 0,
                "Hairpin settings stay open and explain an invalid annealing temperature");
            input.Text = "60"; CaptureLayout(popup, "hairpin-settings.png");
            apply.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        });
        popup.ShowDialog(); Assert(popup.Settings?.AnnealingC == 60, "Hairpin settings confirm the actual experiment temperature");
        vm.HairpinSettings = new();
    }
    private static void CheckCompactLayouts(MainWindow window, MainViewModel vm)
    {
        window.MinWidth = 900; window.MinHeight = 600;
        vm.EditorAnalysisText = vm.StructureText;
        double narrowSequenceWidth = 0;
        foreach (var size in new[] { new Size(900, 620), new Size(1080, 700), new Size(1366, 768), new Size(1800, 1000) })
        {
            window.Width = size.Width; window.Height = size.Height; vm.AdvancedMode = false;
            // Render the requested logical canvas even when the host monitor limits native window size.
            var canvas = (FrameworkElement)window.Content;
            canvas.Width = size.Width; canvas.Height = size.Height;
            window.UpdateLayout();
            var workspace = (System.Windows.Controls.TabControl)window.FindName("WorkspaceTabs");
            workspace.SelectedIndex = 0; window.UpdateLayout();
            var simpleGrid = VisualChildren<System.Windows.Controls.DataGrid>(workspace).Single(g => g.Name == "CandidateGrid");
            var savedCandidate = vm.SelectedCandidate; var savedParameters = JsonSerializer.Serialize(vm.ReadParameters());
            var score = simpleGrid.Columns.Single(c => c.SortMemberPath == "Assessment.Score");
            Assert(score.Visibility == Visibility.Visible && score.DisplayIndex < simpleGrid.FrozenColumnCount &&
                simpleGrid.Columns.Count(c => c.Visibility == Visibility.Visible) == 8 &&
                !VisualChildren<CandidateInspectorView>(workspace).Single().IsVisible &&
                ((System.Windows.Controls.TabItem)workspace.Items[1]).Visibility == Visibility.Collapsed,
                $"Simple mode keeps the score fixed and focuses the workspace at {size}");
            var scoreText = VisualChildren<System.Windows.Controls.TextBlock>(workspace).Single(t => t.Name == "SelectedScoreText");
            Assert(scoreText.IsVisible && scoreText.Text.StartsWith("选中评分"), $"Selected score is visible in simple mode at {size}");
            var exportActions = VisualChildren<System.Windows.Controls.StackPanel>(workspace).Single(t => t.Name == "ExportActions");
            var selectedOnly = VisualChildren<System.Windows.Controls.CheckBox>(exportActions).Single();
            var smartExport = VisualChildren<System.Windows.Controls.Button>(exportActions).Single();
            Assert(exportActions.Orientation == System.Windows.Controls.Orientation.Horizontal && selectedOnly.IsVisible && smartExport.IsVisible &&
                smartExport.TranslatePoint(new Point(), exportActions).X >= selectedOnly.ActualWidth,
                $"Smart export stays beside checked-only export on a single row at {size}");
            var horizontal = VisualChildren<System.Windows.Controls.ScrollViewer>(simpleGrid).First();
            horizontal.ScrollToRightEnd(); window.UpdateLayout();
            Assert(VisualChildren<System.Windows.Controls.DataGridCell>(simpleGrid)
                .Any(c => c.Column == score && c.IsVisible && c.TranslatePoint(new Point(), simpleGrid).X < 140),
                $"Score cells remain on the left while the simple candidate list scrolls at {size}");
            horizontal.ScrollToLeftEnd(); window.UpdateLayout();
            CheckVisibleButtons(window, size); CaptureLayout(window, $"simple-{size.Width:F0}x{size.Height:F0}.png");
            vm.AdvancedMode = true; window.UpdateLayout();
            Assert(vm.SelectedCandidate == savedCandidate && JsonSerializer.Serialize(vm.ReadParameters()) == savedParameters &&
                simpleGrid.Columns.All(c => c.Visibility == Visibility.Visible),
                $"Professional mode restores all columns and preserves data at {size}");
            for (var page = 0; page < workspace.Items.Count; page++)
            {
                workspace.SelectedIndex = page; window.UpdateLayout();
                foreach (var expander in VisualChildren<System.Windows.Controls.Expander>(workspace).Where(e => e.IsVisible)) expander.IsExpanded = true;
                window.UpdateLayout();
                var tabs = VisualChildren<System.Windows.Controls.TabControl>(workspace).Where(t => t.IsVisible).ToList();
                foreach (var tab in tabs)
                {
                    for (var index = 0; index < tab.Items.Count; index++)
                    {
                        tab.SelectedIndex = index; CheckVisibleButtons(window, size);
                    }
                    tab.SelectedIndex = 0;
                }
                CheckVisibleButtons(window, size);
                if (page == 0)
                {
                    var sequence = VisualChildren<System.Windows.Controls.TextBox>(workspace).Single(e => e.Name == "TargetSequenceInput");
                    var workbench = VisualChildren<WorkbenchView>(workspace).Single();
                    var inspector = VisualChildren<CandidateInspectorView>(workspace).Single();
                    var list = VisualChildren<System.Windows.Controls.DataGrid>(workspace).Single(g => g.Name == "CandidateGrid");
                    var hasInspectorRoom = workbench.ActualWidth >= 1000 && list.ActualHeight >= 280;
                    Assert(inspector.IsVisible == hasInspectorRoom && (!hasInspectorRoom ||
                        inspector.TranslatePoint(new Point(inspector.ActualWidth, 0), workspace).X <= workspace.ActualWidth + 1),
                        $"Current pair inspector adapts to available workspace at {size}");
                    if (hasInspectorRoom && vm.Candidates.Count > 1)
                    {
                        var previous = vm.SelectedCandidate;
                        vm.SelectedCandidate = vm.Candidates[1]; window.UpdateLayout();
                        var track = VisualChildren<SequenceTrack>(inspector).Single();
                        Assert(track.Candidate == vm.SelectedCandidate && VisualChildren<System.Windows.Controls.TextBlock>(inspector)
                            .Any(t => t.Text == "发卡 Tm °C"), $"Selecting a candidate updates inspector properties and sequence track at {size}");
                        vm.SelectedCandidate = previous; window.UpdateLayout();
                    }
                    var expected = VisualChildren<System.Windows.Controls.TextBox>(workspace).Single(e => e.Name == "ExpectedTranscriptsInput");
                    var search = VisualChildren<System.Windows.Controls.Button>(workspace).Single(e => e.Name == "DesignCurrentButton");
                    var expectedCenter = expected.TranslatePoint(new Point(0, expected.ActualHeight / 2), workspace);
                    var searchCenter = search.TranslatePoint(new Point(0, search.ActualHeight / 2), workspace);
                    Assert(sequence.ActualHeight is >= 80 and <= 100 && expected.ActualWidth >= 60 &&
                        Math.Abs(expectedCenter.Y - searchCenter.Y) <= 1 &&
                        expected.TranslatePoint(new Point(expected.ActualWidth, 0), workspace).X <= searchCenter.X,
                        $"Compact sequence input and expected transcripts alongside search at {size}");
                    var retry = (System.Windows.Controls.Button)window.FindName("RetryFailedButton");
                    Assert(retry.IsVisible && retry.TranslatePoint(new Point(), canvas).X < workspace.TranslatePoint(new Point(), canvas).X,
                        $"Failed target retry is in the sidebar at {size}");
                }
                foreach (var scroll in VisualChildren<System.Windows.Controls.ScrollViewer>(workspace)
                    .Where(s => s.IsVisible && s.Style == Application.Current.FindResource("ReservedScrollViewer")))
                {
                    foreach (var offset in new[] { 0d, scroll.ScrollableHeight / 2, scroll.ScrollableHeight })
                    {
                        scroll.ScrollToVerticalOffset(offset); window.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                        var viewport = (FrameworkElement)scroll.Template.FindName("PART_ScrollContentPresenter", scroll);
                        var bar = (FrameworkElement)scroll.Template.FindName("PART_VerticalScrollBar", scroll);
                        if (bar.IsVisible && viewport.TranslatePoint(new Point(viewport.ActualWidth, 0), scroll).X > bar.TranslatePoint(new Point(0, 0), scroll).X + 0.5)
                            throw new InvalidOperationException($"Scrollbar overlaps content at {size} on page {page}.");
                        CheckVisibleButtons(window, size);
                    }
                    scroll.ScrollToTop();
                }
                CaptureLayout(window, $"page-{page}-{size.Width:F0}x{size.Height:F0}.png");
            }
            workspace.SelectedIndex = 0; vm.IsBusy = true; CheckVisibleButtons(window, size); vm.IsBusy = false;
            foreach (var expander in VisualChildren<System.Windows.Controls.Expander>(workspace).Where(e => e.IsVisible)) expander.IsExpanded = false;
            CaptureLayout(window, $"compact-{size.Width:F0}x{size.Height:F0}.png");
            var toolbar = VisualChildren<FrameworkElement>(workspace).Single(e => e.Name == "DesignToolbar");
            Assert(VisualChildren<System.Windows.Controls.Button>(workspace).Single(e => e.Name == "DesignCurrentButton").IsVisible,
                $"Design action remains visible with input collapsed at {size}");
            Assert(toolbar.ActualHeight <= 40, $"Design toolbar stays on one line at {size.Width}x{size.Height}");
            var candidates = VisualChildren<System.Windows.Controls.DataGrid>(workspace).Single(g => g.Name == "CandidateGrid");
            var forward = candidates.Columns.Single(c => c.SortMemberPath == "Forward.Sequence");
            if (size.Width == 900) narrowSequenceWidth = forward.ActualWidth;
            if (size.Width == 1800)
            {
                Assert(forward.ActualWidth > narrowSequenceWidth + 10 && candidates.Columns.Sum(c => c.ActualWidth) >= candidates.ActualWidth - 24,
                    $"Candidate columns expand to use a wide window (F {forward.ActualWidth:F1}, narrow {narrowSequenceWidth:F1}, total {candidates.Columns.Sum(c => c.ActualWidth):F1}, grid {candidates.ActualWidth:F1})");
            }
            Assert(true, $"Buttons do not overlap across tabs at {size.Width}x{size.Height}");
            Assert(true, $"Scrollbars reserve space at top, middle and bottom at {size.Width}x{size.Height}");
        }
        var converter = new DesignOptionConverter();
        var mainTabs = (System.Windows.Controls.TabControl)window.FindName("WorkspaceTabs");
        mainTabs.SelectedIndex = 1; vm.ResultsTabIndex = 7; vm.AdvancedMode = false; window.UpdateLayout();
        Assert(mainTabs.SelectedIndex == 0 && vm.ResultsTabIndex == 0,
            "Returning to simple mode moves selection away from hidden pages");
        Assert(converter.Convert(TargetRegionMode.PreferCds, typeof(string), null!, CultureInfo.InvariantCulture).Equals("优先 CDS 编码区")
            && converter.Convert(ExpressionMode.TotalGene, typeof(string), null!, CultureInfo.InvariantCulture).Equals("基因总表达"), "Design options have readable labels");
    }
    private static void CheckHelpAndSaveDialog(MainWindow owner)
    {
        foreach (var advanced in new[] { false, true })
        {
            var closeSearch = new SearchCriteriaDialog(new(), 600, PrimerSearchKind.CompatibleWithSense, "AGAGACATCCTCCACCAACT", "", advanced)
                { Owner = owner, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            var closeReport = WindowFrameVerification.CheckDialogClose(closeSearch);
            Assert(closeReport.CloseHitTest == 1 && closeReport.CloseAction && closeSearch.Request is null,
                $"Search close button receives mouse hits and cancels the draft ({(advanced ? "professional" : "simple")})");
        }
        var closeHairpin = new HairpinSettingsDialog(new())
            { Owner = owner, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        var hairpinCloseReport = WindowFrameVerification.CheckDialogClose(closeHairpin);
        Assert(hairpinCloseReport.CloseHitTest == 1 && hairpinCloseReport.CloseAction && closeHairpin.Settings is null,
            "Hairpin settings close button receives mouse hits and cancels the draft");
        var searchCancelled = new SearchCriteriaDialog(new(), 600, PrimerSearchKind.Pairs, "", "")
            { Owner = owner, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        searchCancelled.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            ((SearchCriteriaModel)searchCancelled.DataContext).MinLength = "19";
            searchCancelled.Close();
        });
        searchCancelled.ShowDialog();
        Assert(searchCancelled.Request is null, "Cancelling Search Criteria does not return changed settings");
        var criteria = new SearchCriteriaDialog(new(), 600, PrimerSearchKind.Pairs, "", "")
            { Owner = owner, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        var invalidStayedOpen = false;
        criteria.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            var model = (SearchCriteriaModel)criteria.DataContext;
            var fields = (FrameworkElement)criteria.FindName("AdvancedSearchFields");
            criteria.UpdateLayout();
            Assert(!model.AdvancedMode && !fields.IsVisible, "Simple search shows only basic editable criteria");
            var preserved = model.Read(600).Parameters;
            CaptureLayout(criteria, "search-simple.png");
            model.AdvancedMode = true; criteria.UpdateLayout();
            Assert(fields.IsVisible, "Professional search exposes Tm, GC and return count");
            model.MinTm = "57.5"; model.Count = "12"; model.AdvancedMode = false; criteria.UpdateLayout();
            var simpleParameters = model.Read(600).Parameters;
            Assert(!fields.IsVisible && simpleParameters.MinTm == 57.5 && simpleParameters.Count == 12 &&
                simpleParameters.MonovalentMm == preserved.MonovalentMm && simpleParameters.MaxHairpinTm == preserved.MaxHairpinTm,
                "Mode switching keeps edited and inherited reaction/structure parameters");
            model.AdvancedMode = true;
            var button = (System.Windows.Controls.Button)criteria.FindName("StartSearchButton");
            model.Count = "0"; button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            invalidStayedOpen = criteria.IsVisible && ((System.Windows.Controls.TextBlock)criteria.FindName("Feedback")).Text.Length > 0;
            model.Count = "10"; model.MinProduct = "100"; model.MaxProduct = "150";
            CaptureLayout(criteria, "search-criteria.png");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        });
        criteria.ShowDialog();
        Assert(invalidStayedOpen && criteria.Request?.Parameters.MinProduct == 100 && criteria.Request.Parameters.MaxProduct == 150,
            "Search Criteria validates inputs and returns confirmed product range");
        foreach (var topic in HelpWindow.Topics)
        {
            var markdown = MarkdownHelp.Load(topic.Id);
            var document = MarkdownHelp.Render(markdown);
            Assert(document.Blocks.Count > 5 && new System.Windows.Documents.TextRange(document.ContentStart, document.ContentEnd).Text.Length > 300,
                $"Embedded Markdown renders: {topic.Title}");
        }
        var help = new HelpWindow { Owner = owner, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        help.Show(); help.UpdateLayout();
        CaptureLayout(help, "help.png");
        var search = VisualChildren<System.Windows.Controls.TextBox>(help).Single();
        var chapters = VisualChildren<System.Windows.Controls.ListBox>(help).Single();
        search.Text = "GC clamp";
        Assert(chapters.Items.Count == 1, "Help search filters Markdown content");
        help.Close();
        foreach (var choice in new[] { "SaveButton", "DiscardButton", "CancelButton" })
        {
            var saved = false;
            var dialog = new SaveChangesDialog(() => { saved = true; return Task.FromResult(true); }, () => "")
                { Owner = owner, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            dialog.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
            {
                if (choice == "SaveButton") CaptureLayout(dialog, "save-dialog.png");
                var button = VisualChildren<System.Windows.Controls.Button>(dialog).Single(b => b.Name == choice);
                var provider = (System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(button)
                    .GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke);
                provider.Invoke();
            });
            var result = dialog.ShowDialog();
            Assert((result == true) == (choice != "CancelButton") && saved == (choice == "SaveButton"), $"Save dialog action: {choice}");
        }
        var unsuccessful = new SaveChangesDialog(() => Task.FromResult(false), () => "保存已取消")
            { Owner = owner, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        var stayedOpen = false;
        unsuccessful.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            var button = VisualChildren<System.Windows.Controls.Button>(unsuccessful).Single(b => b.Name == "SaveButton");
            button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            stayedOpen = unsuccessful.IsVisible && VisualChildren<System.Windows.Controls.TextBlock>(unsuccessful).Any(t => t.Text.Contains("保存未完成") && t.IsVisible);
            unsuccessful.Close();
        });
        unsuccessful.ShowDialog(); Assert(stayedOpen, "Unfinished save keeps confirmation open");
    }
    private static void CheckVisibleButtons(MainWindow window, Size size)
    {
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        var visual = (FrameworkElement)window.Content;
        var client = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
        var buttons = VisualChildren<System.Windows.Controls.Button>(visual).Where(b => b.IsVisible && b.ActualWidth > 0)
            .Select(b => (Button: b, Bounds: VisibleBounds(b, visual, client))).Where(b => !b.Bounds.IsEmpty).ToList();
        for (var i = 0; i < buttons.Count; i++)
        for (var j = i + 1; j < buttons.Count; j++)
        {
            var overlap = Rect.Intersect(buttons[i].Bounds, buttons[j].Bounds);
            if (!overlap.IsEmpty && overlap.Width > 1 && overlap.Height > 1)
                throw new InvalidOperationException($"Overlapping buttons at {size}: {buttons[i].Button.Content} / {buttons[j].Button.Content}");
        }
    }
    private static void CaptureLayout(Window window, string name)
    {
        window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
        var content = (FrameworkElement)window.Content;
        var background = new DrawingVisual();
        using (var drawing = background.RenderOpen()) { drawing.DrawRectangle(window.Background, null, new Rect(content.RenderSize)); drawing.DrawRectangle(new VisualBrush(content), null, new Rect(content.RenderSize)); }
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(background);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(work, name)); encoder.Save(output);
    }
    private static Rect VisibleBounds(FrameworkElement element, FrameworkElement root, Rect client)
    {
        var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
        bounds.Intersect(client);
        for (DependencyObject? ancestor = VisualTreeHelper.GetParent(element); ancestor is not null && ancestor != root; ancestor = VisualTreeHelper.GetParent(ancestor))
            if (ancestor is System.Windows.Controls.ScrollContentPresenter viewport)
                bounds.Intersect(viewport.TransformToAncestor(root).TransformBounds(new Rect(viewport.RenderSize)));
        return bounds;
    }
    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
    private static void Assert(bool condition, string label) { if (!condition) throw new InvalidOperationException("FAIL: " + label); passed++; Console.WriteLine("PASS " + label); }
    private static async Task AssertThrowsAsync<T>(Func<Task> action, string label) where T : Exception
    { try { await action(); } catch (T) { Assert(true, label); return; } throw new InvalidOperationException("FAIL: " + label); }
}
