using System.Collections;
using System.Reflection;

namespace QpcrPrimerStudio.Core;

public static class ProjectValidation
{
    public static void Validate(ProjectDocument project)
    {
        // Check every persisted non-nullable member before any computed property or UI mutation.
        var nullability = new NullabilityInfoContext();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object? value, string path, int depth)
        {
            if (value is null) throw new InvalidDataException($"工程数据缺少 {path}。");
            if (depth > 100) throw new InvalidDataException("工程数据嵌套过深。");
            if (value is double number && !double.IsFinite(number)) throw new InvalidDataException($"{path} 包含无效数值。");
            var type = value.GetType();
            if (type.IsValueType || value is string || !visited.Add(value)) return;
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary) { Walk(entry.Key, path + ".key", depth + 1); Walk(entry.Value, path + ".value", depth + 1); }
                return;
            }
            if (value is IEnumerable sequence)
            {
                foreach (var item in sequence) Walk(item, path + "[]", depth + 1);
                return;
            }
            foreach (var property in type.GetProperties().Where(p => p.CanWrite && p.GetIndexParameters().Length == 0 &&
                p.GetCustomAttribute<System.Text.Json.Serialization.JsonIgnoreAttribute>() is null))
            {
                var child = property.GetValue(value);
                if (child is null && nullability.Create(property).ReadState == NullabilityState.Nullable) continue;
                Walk(child, path + "." + property.Name, depth + 1);
            }
        }
        Walk(project, "Project", 0);
        if (project.SchemaVersion != 1) throw new InvalidDataException("不支持此项目文件版本。");
        void Target(SequenceTarget target)
        {
            try { target.Validate(); } catch (ArgumentException ex) { throw new InvalidDataException("工程靶标无效：" + ex.Message, ex); }
            Exons(target.ExonBlocks, target.Sequence.Length);
        }
        void Parameters(DesignParameters parameters, int length)
        {
            if ((long)parameters.IncludedStart + parameters.IncludedLength - 1 > length ||
                parameters.ExcludedRegions.Any(r => (long)r.Start + r.Length - 1 > length))
                throw new InvalidDataException("工程设计区域超出模板范围。");
            try { parameters.Validate(length); } catch (ArgumentException ex) { throw new InvalidDataException("工程设计参数无效：" + ex.Message, ex); }
        }
        void Exons(List<ExonBlock> exons, int length)
        {
            foreach (var e in exons)
                if (string.IsNullOrWhiteSpace(e.SeqId) || e.Start < 1 || e.End < e.Start || e.End > length ||
                    e.GenomicStart < 1 || e.GenomicEnd < e.GenomicStart || e.GenomicEnd - e.GenomicStart != (long)e.End - e.Start)
                    throw new InvalidDataException("工程 exon 基因组与 cDNA 坐标无效或长度不对应。");
            var ordered = exons.OrderBy(e => e.Start).ToList();
            for (var i = 1; i < ordered.Count; i++)
                if (ordered[i].Start <= ordered[i - 1].End || ordered[i].SeqId != ordered[0].SeqId || ordered[i].Plus != ordered[0].Plus)
                    throw new InvalidDataException("工程 exon 存在重叠或链/染色体不一致。");
        }
        void Metrics(PrimerMetrics primer, string template, bool reverse)
        {
            if (primer.Sequence.Length == 0 || primer.Sequence.Any(c => !"ACGTRYSWKMBDHVN".Contains(c)) ||
                primer.Start < 1 || primer.End < primer.Start || primer.End > template.Length || primer.Gc is < 0 or > 100 ||
                (long)primer.End - primer.Start + 1 != primer.Sequence.Length)
                throw new InvalidDataException("工程引物序列、坐标或 GC 数据无效。");
            var binding = template.Substring(primer.Start - 1, primer.Sequence.Length);
            if (reverse) binding = SequenceFiles.ReverseComplement(binding);
            // Preserve IUPAC histories where the stored template includes an unresolved base.
            int Bases(char c) => c switch { 'A' => 1, 'C' => 2, 'G' => 4, 'T' => 8, 'R' => 5, 'Y' => 10, 'S' => 6, 'W' => 9,
                'K' => 12, 'M' => 3, 'B' => 14, 'D' => 13, 'H' => 11, 'V' => 7, 'N' => 15, _ => 0 };
            if (binding.Where((b, i) => (Bases(b) & Bases(primer.Sequence[i])) == 0).Any())
                throw new InvalidDataException("工程引物序列与模板结合位点不对应。");
        }
        void Regions(TargetRegionSettings settings)
        {
            if (!Enum.IsDefined(settings.Mode) || !Enum.IsDefined(settings.Expression) || settings.AvoidFivePrime < 0 || settings.AvoidThreePrime < 0 ||
                settings.VariantTerminalBases < 1 || settings.ScoreWeights.Values.Any(v => v < 0)) throw new InvalidDataException("工程区域策略无效。");
        }
        void HairpinSettings(HairpinScreenSettings settings)
        {
            try { settings.Validate(); } catch (ArgumentException ex) { throw new InvalidDataException("工程发卡设置无效：" + ex.Message, ex); }
        }
        Parameters(project.Parameters, int.MaxValue);
        foreach (var target in project.Targets)
        {
            Target(target);
            if (target.ParameterOverride is { } parameters) Parameters(parameters, int.MaxValue);
        }
        if (project.Targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() != project.Targets.Count)
            throw new InvalidDataException("项目包含重复靶标。");
        var ids = project.Targets.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        Regions(project.RegionSettings); HairpinSettings(project.HairpinSettings);
        foreach (var run in project.Runs)
        {
            Target(run.Target); Regions(run.RegionSettings);
            if (!ids.Contains(run.Target.Id)) throw new InvalidDataException("任务引用了工程中不存在的靶标：" + run.Target.Id);
            if (run.Candidates.Count > 0)
            {
                Parameters(run.UsedParameters ?? run.Target.ParameterOverride ?? project.Parameters, run.Target.Sequence.Length);
                if (run.HairpinScreening is { } screen) { Parameters(screen.Parameters, run.Target.Sequence.Length); HairpinSettings(screen.Settings); }
            }
            if (run.Candidates.Select(c => c.Id).Distinct().Count() != run.Candidates.Count) throw new InvalidDataException("任务包含重复候选 ID。");
            foreach (var c in run.Candidates)
            {
                if (string.IsNullOrWhiteSpace(c.Id) || c.TargetId != run.Target.Id || c.ProductLength <= 0 || c.Revision < 1)
                    throw new InvalidDataException("候选身份、靶标引用或产物长度无效。");
                Metrics(c.Forward, run.Target.Sequence, false); Metrics(c.Reverse, run.Target.Sequence, true);
                if (c.Reverse.Start <= c.Forward.Start || c.Reverse.End <= c.Forward.End || (long)c.Reverse.End - c.Forward.Start + 1 != c.ProductLength ||
                    c.Amplicon.Length != c.ProductLength || c.Amplicon != run.Target.Sequence.Substring(c.Forward.Start - 1, c.ProductLength))
                    throw new InvalidDataException("工程候选扩增子、引物顺序或产物长度不对应。");
                if (c.Provenance is { } provenance)
                {
                    Parameters(provenance.Parameters, run.Target.Sequence.Length);
                    if (provenance.TemplateSha256.Length > 0 && provenance.TemplateSha256 != run.Target.Sha256)
                        throw new InvalidDataException("工程候选来源记录与历史模板指纹不对应。");
                }
                if (c.Assessment.Score is < 0 or > 100 || c.Assessment.EvidenceCoverage is < 0 or > 100) throw new InvalidDataException("候选评分无效。");
            }
        }
        var candidateIds = project.Runs.SelectMany(r => r.Candidates).Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        if (candidateIds.Count != project.Runs.Sum(r => r.Candidates.Count)) throw new InvalidDataException("工程的不同任务包含重复候选 ID。");
        foreach (var run in project.Runs)
        foreach (var candidate in run.Candidates.Where(c => c.ParentCandidateId is not null))
            if (!project.Runs.Any(parentRun => parentRun.Target.Id == run.Target.Id && parentRun.Target.Sha256 == run.Target.Sha256 &&
                parentRun.Candidates.Any(parent => parent.Id == candidate.ParentCandidateId && parent.Id != candidate.Id)))
                throw new InvalidDataException("候选父修订与所属靶标或模板不对应。");
        foreach (var record in project.Assays)
            if (!candidateIds.Contains(record.CandidateId)) throw new InvalidDataException("实验记录引用了不存在的候选。");
        foreach (var record in project.Experiments)
            if (!candidateIds.Contains(record.CandidateId)) throw new InvalidDataException("实验记录引用了不存在的候选。");
        foreach (var search in project.SinglePrimerSearches)
        {
            Target(search.Target); Regions(search.Regions);
            if (!ids.Contains(search.Target.Id)) throw new InvalidDataException("单引物历史引用了不存在的靶标。");
            if (search.Primers.Count > 0) Parameters(search.Parameters, search.Target.Sequence.Length);
            foreach (var primer in search.Primers) Metrics(primer, search.Target.Sequence, search.Reverse);
        }
        void Draft(TemplateEditorDraft draft)
        {
            if (draft.InputTabIndex is < 0 or > 1 || !Enum.TryParse<RecommendationSearchKind>(draft.SearchKind, out var kind) || !Enum.IsDefined(kind))
                throw new InvalidDataException("工程草稿页签或搜索类型无效。");
            if (draft.SelectedTargetId is { } selected && !ids.Contains(selected)) throw new InvalidDataException("编辑草稿引用了不存在的靶标。");
            if (draft.ParentCandidateId is { } parent && !project.Runs.Any(run => run.Target.Id == draft.SelectedTargetId &&
                run.Target.Sha256 == draft.EditTemplateSha256 && run.Candidates.Any(c => c.Id == parent)))
                throw new InvalidDataException("工程手工草稿父候选、所属靶标或模板指纹不对应。");
        }
        foreach (var (id, draft) in project.TargetDrafts)
        {
            if (!ids.Contains(id) || draft.SelectedTargetId != id) throw new InvalidDataException("序列草稿所属靶标无效。");
            Draft(draft);
        }
        if (project.EditorDraft is { } editor) Draft(editor);
        if (project.UnassignedDraft is { } unassigned)
        {
            if (unassigned.SelectedTargetId is not null) throw new InvalidDataException("未归属草稿不能引用已提交靶标。");
            Draft(unassigned);
        }
        if (project.Databases.Select(d => d.Kind).Distinct().Count() != project.Databases.Count) throw new InvalidDataException("工程包含重复类型的数据库绑定。");
        if (project.Reference is { } reference)
        {
            if (reference.SchemaVersion != 1) throw new InvalidDataException("不支持此参考工程版本。");
            if (reference.Variants.Any(v => string.IsNullOrWhiteSpace(v.SeqId) || v.Position < 1 || v.End < v.Position ||
                v.Ref.Length == 0 || v.Alt.Length == 0 || v.Frequency is < 0 or > 1)) throw new InvalidDataException("工程参考变异坐标或频率无效。");
            if (reference.Transcripts.Select(t => t.Id).Distinct().Count() != reference.Transcripts.Count) throw new InvalidDataException("参考包含重复转录本 ID。");
            foreach (var t in reference.Transcripts)
            {
                Target(new() { Id = t.Id, Sequence = t.Sequence });
                Exons(t.Exons, t.Sequence.Length);
                if (t.Cds.Any(r => r.Start < 1 || r.Length < 1 || (long)r.Start + r.Length - 1 > t.Sequence.Length)) throw new InvalidDataException("参考 CDS 坐标无效。");
                try { _ = reference.ProjectVariants(t); } catch (Exception ex) when (ex is OverflowException or ArgumentException)
                { throw new InvalidDataException("工程参考变异不能安全投影至 cDNA。", ex); }
            }
        }
    }
}
