using System.IO;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using QpcrPrimerStudio.Core;
using QpcrPrimerStudio.Desktop;

internal static class ParameterRecommendationUiChecks
{
    internal static int Run(MainWindow owner, string root, string work, ProjectDocument project)
    {
        var count = 0;
        void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            count++; Console.WriteLine("PASS: " + label);
        }
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(owner.Dispatcher));
        var target = project.Targets[0];
        var executable = Path.Combine(root, "tools/primer3/primer3-2.6.1/src/primer3_core.exe");
        var service = new ParameterRecommendationEngine(executable);
        var initial = new DesignParameters { MinProduct = 250, MaxProduct = 400, Count = 3 };
        var dialog = new SearchCriteriaDialog(initial, target.Sequence.Length, PrimerSearchKind.Pairs, "", "", false,
            (request, token) => service.RecommendAsync(target, request.Parameters, RecommendationSearchKind.Pairs, "", "", token))
            { Owner = owner, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        try
        {
            dialog.Show(); dialog.UpdateLayout();
            var model = (SearchCriteriaModel)dialog.DataContext;
            var recommend = (Button)dialog.FindName("RecommendParametersButton");
            var apply = (Button)dialog.FindName("ApplyRecommendationButton");
            var start = (Button)dialog.FindName("StartSearchButton");
            Check(recommend.IsVisible && recommend.IsEnabled && !apply.IsEnabled,
                "The real search dialog exposes recommendation and keeps Apply disabled until an actual result exists");
            var task = dialog.RecommendAsync();
            dialog.UpdateLayout();
            Check(model.IsRecommending && !start.IsEnabled,
                "Search is disabled while template recommendation is being calculated");
            Await(task, owner.Dispatcher);
            dialog.UpdateLayout();
            Check(model.MinProduct == "250" && model.Count == "3" && apply.IsEnabled,
                "Finishing a recommendation leaves the search draft unchanged until Apply is chosen");
            Invoke(apply, owner.Dispatcher);
            Check(model.MinProduct == "80" && model.MaxProduct == "200" && model.Count == "10",
                "The accessible Apply button loads the verified recommendation into the editable form");
            var changes = model.Read(target.Sequence.Length);
            Check(changes.Parameters.MinTm == 58 && changes.Parameters.MaxTm == 62,
                "The recommended draft carries the actual tested Tm window");
            model.ForwardEnd = "300";
            Check(!apply.IsEnabled, "Editing a constraint invalidates the previous recommendation");
            dialog.Close();
            Check(dialog.Request is null && initial.MinProduct == 250,
                "Closing a recommendation dialog cancels its draft without changing original project parameters");

            var confirmDialog = new SearchCriteriaDialog(initial, 600, PrimerSearchKind.Pairs, "", "", false,
                (request, token) => service.RecommendAsync(target, request.Parameters, RecommendationSearchKind.Pairs, "", "", token))
                { Owner = owner, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            Exception? confirmFailure = null;
            owner.Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await confirmDialog.RecommendAsync();
                    Invoke((Button)confirmDialog.FindName("ApplyRecommendationButton"), owner.Dispatcher);
                    ((SearchCriteriaModel)confirmDialog.DataContext).ApplyToWholeProject = true;
                    Invoke((Button)confirmDialog.FindName("StartSearchButton"), owner.Dispatcher);
                }
                catch (Exception ex) { confirmFailure = ex; confirmDialog.Close(); }
            });
            var confirmed = confirmDialog.ShowDialog();
            if (confirmFailure is not null) throw confirmFailure;
            Check(confirmed == true && confirmDialog.Request is { ApplyToWholeProject: true, Recommendation: not null } &&
                confirmDialog.Request.Parameters.MinProduct == 80,
                "Confirming through the real search button carries applied recommendation evidence and project sharing");

            var pending = new TaskCompletionSource<ParameterRecommendation>();
            CancellationToken delivered = default;
            var cancelDialog = new SearchCriteriaDialog(initial, 600, PrimerSearchKind.Pairs, "", "", false,
                async (_, token) => { delivered = token; return await pending.Task.WaitAsync(token); })
                { Owner = owner, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            cancelDialog.Show();
            try
            {
                var cancelTask = cancelDialog.RecommendAsync();
                cancelDialog.Close();
                Await(cancelTask, owner.Dispatcher);
                Check(delivered.IsCancellationRequested && cancelDialog.Request is null,
                    "Closing during recommendation cancels its running operation and applies no draft");
            }
            finally { if (cancelDialog.IsVisible) cancelDialog.Close(); }

            var failedDialog = new SearchCriteriaDialog(initial, 600, PrimerSearchKind.Pairs, "", "", false,
                (_, _) => Task.FromException<ParameterRecommendation>(new IOException("trial-error")))
                { Owner = owner, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
            failedDialog.Show();
            try
            {
                Await(failedDialog.RecommendAsync(), owner.Dispatcher);
                Check(((TextBlock)failedDialog.FindName("Feedback")).Text.Contains("trial-error") &&
                    !((Button)failedDialog.FindName("ApplyRecommendationButton")).IsEnabled,
                    "Trial failures are displayed and leave recommendation Apply unavailable");
            }
            finally { failedDialog.Close(); }
        }
        finally
        {
            if (dialog.IsVisible) dialog.Close();
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        return count;
    }
    private static void Invoke(Button button, Dispatcher dispatcher)
    {
        var peer = UIElementAutomationPeer.CreatePeerForElement(button) ?? new ButtonAutomationPeer(button);
        ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
        dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Await(Task task, Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false));
        Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
    }
}
