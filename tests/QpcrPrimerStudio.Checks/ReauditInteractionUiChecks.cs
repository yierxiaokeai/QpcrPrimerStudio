using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class ReauditInteractionUiChecks
{
    internal static int Run(MainWindow owner, string root, string work, ProjectDocument seed)
    {
        var count=0;
        void Check(bool condition,string message) { if(!condition) throw new InvalidOperationException(message); count++; Console.WriteLine("PASS: "+message); }
        static T Copy<T>(T value)=>JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value,ProjectStore.JsonOptions),ProjectStore.JsonOptions)!;
        var a=Copy(seed.Targets[0]); var b=a with { Id="reaudit-ui-B",Sequence=a.Sequence+"ACGT" };
        var run=Copy(seed.Runs[0]); run.Target=a; run.HairpinScreening=null;
        foreach(var candidate in run.Candidates) candidate.Selected=false;
        var vm=new MainViewModel(Path.Combine(root,"src/QpcrPrimerStudio.Desktop/bin/Debug/net10.0-windows/tools"),Path.Combine(work,"reaudit-interaction-ui"));
        vm.Restore(new ProjectDocument { Targets=[a,b],Runs=[run],Parameters=seed.Parameters,AutoSpecificity=false });
        var window=new MainWindow(vm) { Owner=owner,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-20000,Top=-20000 };
        var previousContext=SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
        SemaphoreSlim? heldGate=null;
        try
        {
            window.Show(); window.UpdateLayout(); owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            var list=(ListBox)window.FindName("TargetList");
            var workbench=Visual<WorkbenchView>(window).Single();
            var template=(TextBox)workbench.FindName("TargetSequenceInput");
            void Edit(TextBox input,string text) { input.Text=text; input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource(); owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle); }
            Edit(template,a.Sequence+"原始草稿");
            vm.EditSelectedCommand.Execute(null);
            Check(template.Text.EndsWith("原始草稿") && vm.Status.Contains("草稿"),"The real candidate editing entry preserves an unfinished template in its bound text box");
            Edit(template,a.Sequence);
            var single=new SinglePrimerSearch { Target=a,Primers=[run.Candidates[0].Forward],Parameters=seed.Parameters };
            vm.SinglePrimerSearches.Add(single); vm.SelectedSingleSearch=single;
            Edit(template,a.Sequence+"单引物原稿"); vm.UseSinglePrimerCommand.Execute(null);
            Check(template.Text.EndsWith("单引物原稿") && vm.Status.Contains("草稿"),"The real single-primer loading entry preserves the unfinished same-gene template");
            vm.AddTargetCommand.Execute(null); Edit(template,"新基因 未完成原稿"); vm.TargetName="reaudit-ui-C";
            list.SelectedItem=b; owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(vm.Snapshot().UnassignedDraft?.TemplateText=="新基因 未完成原稿","Selecting a real left-side gene retains an unassigned raw draft");
            vm.AddTargetCommand.Execute(null); owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(template.Text=="新基因 未完成原稿" && vm.TargetName=="reaudit-ui-C","The real Add sequence action restores the independent raw draft in the text box");
            list.SelectedItem=a; owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Edit(template,a.Sequence);
            vm.LoadManualCommand.Execute(null);
            owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            var manual=Visual<TextBox>(workbench).Single(box=>BindingOperations.GetBinding(box,TextBox.TextProperty)?.Path?.Path==nameof(MainViewModel.ManualForward));
            Edit(manual,run.Candidates[0].Forward.Sequence+"N");
            list.SelectedItem=b; owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(manual.Text=="","A gene without a primer draft displays its own empty editor in the real workbench");
            list.SelectedItem=a; owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(manual.Text==run.Candidates[0].Forward.Sequence+"N" && vm.HasUnsavedChanges,"Returning through the real gene list restores raw primer edits and keeps the save warning active");

            vm.Restore(new ProjectDocument { Targets=[a,b],Runs=[run],Parameters=seed.Parameters,AutoSpecificity=false });
            vm.InputTabIndex=1; owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            var grid=Visual<DataGrid>(workbench).Single(control=>control.Name=="CandidateGrid");
            var chosen=run.Candidates[0]; grid.ScrollIntoView(chosen); grid.UpdateLayout();
            var box=Visual<CheckBox>(grid).Single(control=>control.Name=="CandidateSelectionCheckBox" && ReferenceEquals(control.DataContext,chosen));
            var gate=(SemaphoreSlim)typeof(MainViewModel).GetField("projectWriteGate",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(vm)!;
            if(!gate.Wait(0)) throw new InvalidOperationException("Project writer unexpectedly occupied before UI check.");
            heldGate=gate;
            ((IToggleProvider)new ToggleButtonAutomationPeer(box).GetPattern(PatternInterface.Toggle)).Toggle();
            owner.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Check(chosen.Selected && !vm.PendingSelectionSave.IsCompleted,"A real checkbox queues a selection save while the writer is occupied");
            Edit(manual,"after queued save raw F");
            heldGate.Release(); heldGate=null;
            Pump(vm.FlushSelectionSavesAsync(),owner.Dispatcher);
            Check(vm.HasUnsavedChanges && vm.ManualForward=="after queued save raw F","A later manual edit remains dirty after the earlier checkbox snapshot reaches disk");
            var confirmation=false; var closed=false; window.Closed+=(_,_)=>closed=true;
            var timer=new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(10) };
            timer.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.OfType<SaveChangesDialog>().FirstOrDefault(d=>d.IsVisible);
                if(dialog is null) return;
                confirmation=true;
                var cancel=Visual<Button>(dialog).Single(button=>button.Name=="CancelButton");
                ((IInvokeProvider)new ButtonAutomationPeer(cancel).GetPattern(PatternInterface.Invoke)).Invoke();
            };
            timer.Start(); try { window.Close(); } finally { timer.Stop(); }
            Check(confirmation && !closed && vm.ManualForward=="after queued save raw F","Actual window close asks to save a later manual draft and cancelling preserves it");
            return count;
        }
        finally
        {
            heldGate?.Release();
            Pump(vm.FlushSelectionSavesAsync(),owner.Dispatcher);
            vm.Restore(new ProjectDocument()); window.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }
    private static void Pump(Task task,Dispatcher dispatcher)
    {
        if(!task.IsCompleted)
        {
            var frame=new DispatcherFrame(); var deadline=DateTime.UtcNow.AddSeconds(20);
            var timer=new DispatcherTimer(DispatcherPriority.Background,dispatcher) { Interval=TimeSpan.FromMilliseconds(10) };
            timer.Tick+=(_,_)=> { if(task.IsCompleted || DateTime.UtcNow>=deadline) frame.Continue=false; };
            timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            if(!task.IsCompleted) throw new TimeoutException("Independent interaction UI operation did not complete.");
        }
        task.GetAwaiter().GetResult();
    }
    private static IEnumerable<T> Visual<T>(DependencyObject parent) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child=VisualTreeHelper.GetChild(parent,i); if(child is T match) yield return match;
            foreach(var nested in Visual<T>(child)) yield return nested;
        }
    }
}
