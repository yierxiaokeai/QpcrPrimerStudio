using QpcrPrimerStudio.Core;

namespace QpcrPrimerStudio.Desktop;

public partial class MainViewModel
{
    private Dictionary<string, TemplateEditorDraft> targetDrafts = new(StringComparer.Ordinal);
    private TemplateEditorDraft? unassignedDraft;
    private bool loadingPrimerDraft;
    private bool primerDraftModified;
    private TemplateEditorDraft CurrentEditorDraft() => new(SelectedTarget?.Id, TargetName, TemplateText, JunctionText, ExpectedText)
    {
        CoverageText = CoverageText, ManualForward = ManualForward, ManualReverse = ManualReverse,
        ParentCandidateId = editParent?.Id, EditTemplateSha256 = editTemplateHash ?? SelectedTarget?.Sha256, PrimerDraftModified = primerDraftModified,
        InputTabIndex = InputTabIndex, SearchKind = lastSearchKind.ToString()
    };
    partial void OnSelectedTargetChanging(SequenceTarget? oldValue, SequenceTarget? newValue)
    {
        if (!loading && oldValue is not null && oldValue.Id != newValue?.Id)
            targetDrafts[oldValue.Id] = CurrentEditorDraft();
        else if (!loading && oldValue is null && newValue is not null && HasUnassignedInput())
            unassignedDraft = CurrentEditorDraft();
    }
    private Dictionary<string, TemplateEditorDraft> SnapshotTargetDrafts()
    {
        var result = new Dictionary<string, TemplateEditorDraft>(targetDrafts, StringComparer.Ordinal);
        if (SelectedTarget is not null) result[SelectedTarget.Id] = CurrentEditorDraft();
        return result;
    }
    private void RestoreTargetDraft(string id)
    {
        if (!targetDrafts.TryGetValue(id, out var draft)) return;
        TargetName = draft.TargetName; TemplateText = draft.TemplateText;
        JunctionText = draft.JunctionText; ExpectedText = draft.ExpectedText; CoverageText = draft.CoverageText;
    }
    private bool HasUnassignedInput() => TargetName != "target_" + (Targets.Count + 1) ||
        !string.IsNullOrEmpty(TemplateText) || !string.IsNullOrEmpty(JunctionText) ||
        !string.IsNullOrEmpty(ExpectedText) || !string.IsNullOrEmpty(CoverageText) || !string.IsNullOrEmpty(ManualForward) || !string.IsNullOrEmpty(ManualReverse);
    private TemplateEditorDraft? SnapshotUnassignedDraft() => SelectedTarget is null && HasUnassignedInput() ? CurrentEditorDraft() : unassignedDraft;
    private void RestorePrimerDraft(string? targetId)
    {
        var draft = targetId is null ? unassignedDraft : targetDrafts.GetValueOrDefault(targetId);
        LoadPrimerDraft(draft);
    }
    private void LoadPrimerDraft(TemplateEditorDraft? draft)
    {
        loadingPrimerDraft = true;
        try
        {
            ManualForward = draft?.ManualForward ?? ""; ManualReverse = draft?.ManualReverse ?? "";
            editTemplateHash = draft?.EditTemplateSha256;
            editParent = draft?.ParentCandidateId is { } id ? Runs.Where(r => r.Target.Id == draft.SelectedTargetId && r.Target.Sha256 == draft.EditTemplateSha256)
                .SelectMany(r => r.Candidates).FirstOrDefault(c => c.Id == id) : null;
            primerDraftModified = draft?.PrimerDraftModified ?? false;
            InputTabIndex = draft?.InputTabIndex == 1 ? 1 : 0;
            lastSearchKind = Enum.TryParse<PrimerSearchKind>(draft?.SearchKind, out var kind) && Enum.IsDefined(kind) ? kind : PrimerSearchKind.Pairs;
            EditSummary = editParent is null ? "手工引物草稿已载入；Analyze 计算自身属性，Apply 核对模板并保存结果。" :
                $"已恢复 {editParent.TargetId} #{editParent.Rank} 的手工草稿 · 原候选修订 {editParent.Revision}。";
        }
        finally { loadingPrimerDraft = false; }
    }
    private void RequireCommittedTemplateForEditing(SequenceTarget target)
    {
        if (SelectedTarget?.Id != target.Id) return;
        if (TargetName != target.Id || TemplateText != target.Sequence || JunctionText != string.Join(",", target.Junctions) ||
            ExpectedText != string.Join(",", target.ExpectedSubjects) || CoverageText != string.Join(",", CoverageFor(target)))
            throw new InvalidOperationException("当前靶标有未保存的序列或目标信息草稿，请先保存序列再载入引物；原始输入已保留。");
    }
    private void RequirePrimerDraftPreserved(string forward, string reverse)
    {
        if (primerDraftModified && (ManualForward.Length > 0 || ManualReverse.Length > 0) && (ManualForward != forward || ManualReverse != reverse))
            throw new InvalidOperationException("当前手工引物有未应用的修改，请先应用重算，或清空手工输入后载入；手工草稿已保留。");
    }
}
