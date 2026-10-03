using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class TargetNavigationUiChecks
{
    internal static int Run(MainWindow owner, string root, string work, ProjectDocument seed)
    {
        var count = 0;
        void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); count++; Console.WriteLine("PASS: " + message); }
        static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        var first = Copy(seed.Targets[0]); var second = first with { Id = "navigation-second", Sequence = first.Sequence + "ACGT" };
        var empty = first with { Id = "navigation-empty" }; var changed = first with { Id = "navigation-changed", Sequence = first.Sequence + "A" };
        var failed = first with { Id = "navigation-failed" };
        var runA = Copy(seed.Runs[0]); runA.Target = first; runA.HairpinScreening = null; runA.Candidates[0].Selected = true;
        var runB = Copy(runA); runB.Target = second;
        foreach (var c in runB.Candidates)
        {
            c.Id = Guid.NewGuid().ToString("N"); c.TargetId = second.Id; c.Selected = false;
            if (c.Provenance is { } provenance) c.Provenance = provenance with { TemplateSha256 = second.Sha256 };
        }
        var old = Copy(runA); old.Target = changed with { Sequence = first.Sequence };
        foreach (var c in old.Candidates) { c.Id = Guid.NewGuid().ToString("N"); c.TargetId = changed.Id; }
        var failure = new TargetRun { Target = failed, State = "失败", Message = "deliberate navigation failure" };
        var project = new ProjectDocument { Targets = [first, second, empty, changed, failed], Runs = [runA, old, runB, failure], Parameters = seed.Parameters, AutoSpecificity = false };
        var vm = new MainViewModel(Path.Combine(root, "src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"), Path.Combine(work, "navigation-profile"));
        vm.Restore(project);
        var window = new MainWindow(vm) { Owner = owner, ShowActivated = false, ShowInTaskbar = false, Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual };
        try
        {
            window.Show(); window.UpdateLayout(); var list = (ListBox)window.FindName("TargetList");
            var selectedOnly = Visual<CheckBox>(window).Single(c => Equals(c.Content, "仅导出勾选项"));
            Check(vm.SelectedOnly && selectedOnly.IsChecked == true, "Ordinary export defaults to checked primers in both the model and actual checkbox");
            var grid = Visual<DataGrid>(window).Single(c => c.Name == "CandidateGrid"); var context = Visual<TextBlock>(window).Single(c => c.Name == "ResultContextText");
            void Click(SequenceTarget t)
            {
                list.ScrollIntoView(t); list.UpdateLayout();
                var peer = new ListBoxAutomationPeer(list).GetChildren()[list.Items.IndexOf(t)];
                var provider = peer.GetPattern(PatternInterface.SelectionItem) as ISelectionItemProvider
                    ?? throw new InvalidOperationException("The gene item did not expose its accessible selection action.");
                provider.Select();
                owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            }
            Check(vm.SelectedRun == runA && vm.Candidates[0] == runA.Candidates[0], "Restoring a project shows the current selected gene's matching results");
            Click(second);
            Check(vm.SelectedRun == runB && vm.TemplateText == second.Sequence && vm.DisplayTarget == second && grid.Items.Count == runB.Candidates.Count &&
                grid.Items.Cast<PrimerCandidate>().All(c => c.TargetId == second.Id) && context.Text.StartsWith(second.Id),
                "Selecting a real left-side gene item synchronizes template, run, table, sequence display and result label");
            vm.ManualForward = "ACGTACGTACGTACGTACGT"; Click(first);
            Check(vm.SelectedRun == runA && runA.Candidates[0].Selected && vm.SelectedCandidate == runA.Candidates[0] && vm.ManualForward == "",
                "Returning to a gene restores its results and saved checkbox state while clearing another gene's primer editor");
            Click(empty);
            Check(vm.SelectedRun is null && vm.SelectedCandidate is null && grid.Items.Count == 0 && vm.DisplayTarget == empty && vm.StructureText.Contains("没有选中") &&
                vm.EvidenceText.Contains("没有选中") && vm.SequenceContext.Contains("没有可显示") && context.Text.Contains("尚未进行"),
                "A gene without a design clears every stale result and analysis view");
            Click(changed);
            Check(vm.SelectedRun is null && grid.Items.Count == 0 && context.Text.Contains("模板已更新"), "An edited template does not show old-template candidates as current results");
            Click(failed);
            Check(vm.SelectedRun == failure && grid.Items.Count == 0 && context.Text.Contains("失败") && vm.LogText.Contains(failure.Message),
                "A failed gene shows its own empty result and failure details");
            vm.SelectedRun = runB; window.UpdateLayout();
            Check(vm.SelectedTarget == second && vm.TemplateText == second.Sequence && context.Text.StartsWith(second.Id), "Selecting a historical task synchronizes the left-side gene selection");
            vm.SelectedRun = old; window.UpdateLayout();
            Check(vm.SelectedTarget == changed && vm.TemplateText == changed.Sequence && vm.DisplayTarget == old.Target && context.Text.Contains("历史模板结果"),
                "An old task retains its original sequence display and explicitly labels historical-template results");
            Click(first); var snapshot = vm.Snapshot(); snapshot.EditorDraft = new(first.Id, first.Id, first.Sequence + "\n未完成输入", "", "");
            vm.Restore(snapshot);
            Check(vm.SelectedOnly, "Opening or restoring a project keeps checked-only export as the default");
            Check(vm.TemplateText.EndsWith("未完成输入") && vm.SelectedRun == runA && vm.SelectedTarget == first,
                "Navigation restoration preserves an unfinished editor draft alongside the matching saved-template results");
            var singleForward = new SinglePrimerSearch { Target = second, Primers = [runB.Candidates[0].Forward], Parameters = seed.Parameters };
            var singleReverse = new SinglePrimerSearch { Target = second, Reverse = true, Primers = [runB.Candidates[0].Reverse], Parameters = seed.Parameters };
            var singleOld = new SinglePrimerSearch { Target = changed with { Sequence = first.Sequence }, Reverse = true, Primers = [old.Candidates[0].Reverse], Parameters = seed.Parameters };
            vm.SinglePrimerSearches.Add(singleForward); vm.SinglePrimerSearches.Add(singleReverse); vm.SinglePrimerSearches.Add(singleOld);
            vm.ResultsTabIndex = 1; window.UpdateLayout();
            var history = Visual<ComboBox>(window).Single(c => ReferenceEquals(c.ItemsSource, vm.SinglePrimerSearches));
            history.SelectedItem = singleForward; window.UpdateLayout();
            Check(vm.SelectedTarget == second && vm.TemplateText == second.Sequence && vm.SelectedSingleSearch == singleForward && vm.SinglePrimers.Single() == singleForward.Primers[0] && context.Text.StartsWith(second.Id),
                "The actual single-primer history dropdown synchronizes gene, template, primer list and result context");
            history.SelectedItem = singleReverse; window.UpdateLayout();
            Check(vm.SelectedSingleSearch == singleReverse && vm.ResultsTabIndex == 1 && context.Text.Contains("R 单引物"), "Selecting another direction preserves the selected single search without navigation feedback");
            vm.ResultsTabIndex = 4; vm.ShowResultsCommand.Execute(null); window.UpdateLayout();
            Check(vm.ResultsTabIndex == 1 && vm.SelectedSingleSearch == singleReverse, "Returning from analysis restores the selected single-primer list and direction");
            history.SelectedItem = singleOld; window.UpdateLayout();
            Check(vm.SelectedTarget == changed && vm.TemplateText == changed.Sequence && vm.DisplayTarget == singleOld.Target && context.Text.Contains("历史模板结果"),
                "Historical single-primer results use their original template and are labeled explicitly");
            Click(first);
            Check(vm.TemplateText.EndsWith("未完成输入"), "Returning through the real gene list preserves its raw unfinished draft");
            var backups = new BackupWindow(Path.Combine(work, "no-backup-project.qpcrproject")) { Owner = window, ShowInTaskbar = false };
            backups.Loaded += (_, _) => backups.Dispatcher.BeginInvoke(() =>
            {
                var close = Visual<Button>(backups).Single(b => Equals(b.Content, "关闭"));
                ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)).Invoke();
            }, DispatcherPriority.ApplicationIdle);
            var backupAccepted = backups.ShowDialog();
            Check(backupAccepted != true && !backups.IsVisible, "Backup management's actual close button closes its modal window");
            var backupPath = Path.Combine(work, "navigation-backup.qpcrproject");
            void Pump(Task task)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                timer.Tick += (_, _) => { if (task.IsCompleted) { timer.Stop(); frame.Continue = false; } };
                timer.Start(); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
            }
            Pump(ProjectStore.SaveAsync(backupPath, vm.Snapshot()));
            vm.ProjectPath = backupPath; vm.TemplateText += "\n新修改";
            Pump(vm.AutoSaveSelectionAsync(vm.Runs[0].Candidates[0], true));
            vm.TemplateText += "\n未保存修改";
            bool confirmationCanSave = false; Exception? dialogFailure = null;
            var automationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(15) };
            var acted = new HashSet<Window>();
            automationTimer.Tick += (_, _) =>
            {
                try
                {
                    foreach (var dialog in Application.Current.Windows.OfType<Window>().ToList())
                    {
                        if (!dialog.IsVisible || acted.Contains(dialog)) continue;
                        if (dialog is BackupWindow)
                        {
                            acted.Add(dialog); Visual<ListBox>(dialog).Single().SelectedIndex = 0;
                            var accept = Visual<Button>(dialog).Single(b => Equals(b.Content, "恢复所选"));
                            ((IInvokeProvider)new ButtonAutomationPeer(accept).GetPattern(PatternInterface.Invoke)).Invoke();
                        }
                        else if (dialog is SaveChangesDialog)
                        {
                            acted.Add(dialog); confirmationCanSave = vm.CanWork() && vm.SaveProjectCommand.CanExecute(null);
                            var cancel = Visual<Button>(dialog).Single(b => b.Name == "CancelButton");
                            ((IInvokeProvider)new ButtonAutomationPeer(cancel).GetPattern(PatternInterface.Invoke)).Invoke();
                        }
                    }
                }
                catch (Exception ex) { dialogFailure = ex; automationTimer.Stop(); foreach (var dialog in acted.Where(d => d.IsVisible)) dialog.Close(); }
            };
            automationTimer.Start();
            try { Pump(vm.ManageBackupsCommand.ExecuteAsync(null)); }
            finally { automationTimer.Stop(); }
            if (dialogFailure is not null) throw dialogFailure;
            Check(confirmationCanSave && vm.ProjectPath == backupPath && vm.TemplateText.EndsWith("未保存修改"),
                "Backup recovery confirmation keeps saving available and cancelling preserves the current raw draft");
            var plan = new SmartExportPlan(project, [], new SmartExportReport { Genes = [new("missing", "", "", null, null, "", "", null, "未导出", "尚未设计")] });
            var preview = new SmartExportDialog(plan) { Owner = window, ShowInTaskbar = false };
            var enabled = true; preview.Loaded += (_, _) => preview.Dispatcher.BeginInvoke(() =>
            {
                enabled = ((Button)preview.FindName("ExportSmartButton")).IsEnabled;
                var close = (Button)preview.FindName("CloseDialogButton");
                ((IInvokeProvider)new ButtonAutomationPeer(close).GetPattern(PatternInterface.Invoke)).Invoke();
            }, DispatcherPriority.ApplicationIdle);
            var accepted = preview.ShowDialog();
            Check(accepted != true && !preview.IsVisible && !enabled, "An empty intelligent-export preview cannot export and its real close button works");
        }
        finally
        {
            typeof(MainViewModel).GetField("dirty", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, false);
            window.Close();
        }
        return count;
    }
    private static IEnumerable<T> Visual<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value;
            foreach (var descendant in Visual<T>(child)) yield return descendant;
        }
    }
}
