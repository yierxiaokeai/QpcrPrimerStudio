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

internal static class SelectionAutoSaveUiChecks
{
    internal static int Run(MainWindow owner, string root, string work, ProjectDocument seed)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS: " + label);
        }
        owner.Dispatcher.VerifyAccess();
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
        var storage = Path.Combine(work, "selection-autosave-ui-profile");
        var engines = Path.Combine(root, "src", "QpcrPrimerStudio.Desktop", "bin", "Debug", "net10.0-windows", "tools");
        var project = JsonSerializer.Deserialize<ProjectDocument>(JsonSerializer.Serialize(seed, ProjectStore.JsonOptions), ProjectStore.JsonOptions)!;
        project.EditorDraft = null;
        foreach (var run in project.Runs)
        {
            run.HairpinScreening = null;
            foreach (var candidate in run.Candidates) candidate.Selected = false;
        }
        var vm = new MainViewModel(engines, storage);
        vm.Restore(project);
        var window = new MainWindow(vm)
        {
            Owner = owner, ShowActivated = false, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
        };
        var closed = false;
        window.Closed += (_, _) => closed = true;
        SemaphoreSlim? heldGate = null;
        try
        {
            window.Show(); window.UpdateLayout();
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var grid = VisualChildren<DataGrid>(window).Single(control => control.Name == "CandidateGrid");
            var candidate = (PrimerCandidate)grid.Items[0];
            grid.ScrollIntoView(candidate); grid.UpdateLayout();
            var box = VisualChildren<CheckBox>(grid).Single(control => control.Name == "CandidateSelectionCheckBox" && ReferenceEquals(control.DataContext, candidate));
            Check(box.IsVisible && box.IsEnabled && box.IsChecked == false, "The real candidate checkbox starts visible, enabled and unselected");
            Check(vm.ProjectPath == "尚未保存" && vm.PendingSelectionSave.IsCompleted && !File.Exists(Path.Combine(storage, "last-auto-save.json")),
                "Rendering restored candidates does not start a selection save or create a recovery pointer");

            Toggle(box, owner.Dispatcher);
            Await(vm.FlushSelectionSavesAsync(), owner.Dispatcher);
            var saved = Await(ProjectStore.LoadAsync(vm.ProjectPath), owner.Dispatcher);
            Console.WriteLine($"Selection UI state: checkbox={box.IsChecked}; candidate={candidate.Selected}; saved={saved.Runs.SelectMany(run => run.Candidates).Single(item => item.Id == candidate.Id).Selected}; status={vm.AutoSaveStatus}");
            Check(box.IsChecked == true && candidate.Selected && saved.Runs.SelectMany(run => run.Candidates).Single(item => item.Id == candidate.Id).Selected,
                "An accessible checkbox toggle updates its binding and automatically persists the selected candidate");

            Toggle(box, owner.Dispatcher);
            Await(vm.FlushSelectionSavesAsync(), owner.Dispatcher);
            saved = Await(ProjectStore.LoadAsync(vm.ProjectPath), owner.Dispatcher);
            Check(box.IsChecked == false && !candidate.Selected && !saved.Runs.SelectMany(run => run.Candidates).Single(item => item.Id == candidate.Id).Selected,
                "Clearing the real checkbox persists the unselected state");

            vm.IsBusy = true;
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(!box.IsEnabled, "Candidate selection is disabled while a project operation is busy");
            vm.IsBusy = false;
            owner.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            Check(box.IsEnabled, "Candidate selection becomes available again when the project operation finishes");

            // Hold the actual writer gate so the close/save ordering is deterministic on fast disks.
            var gate = (SemaphoreSlim)typeof(MainViewModel).GetField("projectWriteGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            if (!gate.Wait(0)) throw new InvalidOperationException("The project writer was still occupied after flushing completed saves.");
            heldGate = gate;
            Toggle(box, owner.Dispatcher);
            Check(candidate.Selected && !vm.PendingSelectionSave.IsCompleted, "A checkbox toggle can leave a pending save without blocking the UI thread");
            window.Close();
            Check(!closed && window.IsVisible, "Closing the window waits while its latest selection is still queued for disk storage");
            heldGate.Release(); heldGate = null;
            PumpUntil(() => closed, owner.Dispatcher, "The selection-save window did not close after its queued write finished.");
            Check(vm.PendingSelectionSave.IsCompleted && !vm.HasUnsavedChanges && !window.IsVisible,
                "The window closes normally after the latest selection has been written");

            var recovered = new MainViewModel(engines, storage);
            Check(Await(recovered.RecoverLastAutoSaveAsync(), owner.Dispatcher), "A new view model recovers the project saved during window shutdown");
            Check(recovered.Runs.SelectMany(run => run.Candidates).Single(item => item.Id == candidate.Id).Selected && owner.IsVisible,
                "The recovered project retains the last checked candidate and the original application window remains open");
        }
        finally
        {
            heldGate?.Release();
            try
            {
                Await(vm.FlushSelectionSavesAsync(), owner.Dispatcher);
                if (!closed)
                {
                    vm.IsBusy = false;
                    vm.Restore(new ProjectDocument());
                    window.Close();
                }
            }
            finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        }
        return count;
    }

    private static void Toggle(CheckBox checkbox, Dispatcher dispatcher)
    {
        if (!checkbox.IsEnabled) throw new InvalidOperationException("The checkbox is disabled.");
        var provider = (IToggleProvider)new ToggleButtonAutomationPeer(checkbox).GetPattern(PatternInterface.Toggle);
        provider.Toggle();
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static T Await<T>(Task<T> task, Dispatcher dispatcher)
    {
        PumpUntil(() => task.IsCompleted, dispatcher, "An asynchronous selection UI check did not complete.");
        return task.GetAwaiter().GetResult();
    }

    private static void Await(Task task, Dispatcher dispatcher)
    {
        PumpUntil(() => task.IsCompleted, dispatcher, "An asynchronous selection UI check did not complete.");
        task.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> completed, Dispatcher dispatcher, string timeoutMessage)
    {
        if (completed()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (completed() || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (!completed()) throw new TimeoutException(timeoutMessage);
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
}
