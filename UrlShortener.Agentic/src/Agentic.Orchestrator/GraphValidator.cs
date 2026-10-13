namespace Agentic.Orchestrator;

public static class GraphValidator
{
    public static IReadOnlyList<StageNode> TopologicalSort(WorkflowDefinition definition)
    {
        var stages = definition.Stages ?? throw new WorkflowValidationException("Workflow has no stages.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stage in stages)
        {
            if (string.IsNullOrWhiteSpace(stage.Id))
                throw new WorkflowValidationException("Every stage needs an id.");
            if (!seen.Add(stage.Id))
                throw new WorkflowValidationException($"Duplicate stage id '{stage.Id}'.");
            stage.DependsOn ??= [];
            stage.AllowedWritePaths ??= [];
        }

        var byId = stages.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var indegree = stages.ToDictionary(s => s.Id, _ => 0, StringComparer.Ordinal);
        var dependents = stages.ToDictionary(s => s.Id, _ => new List<string>(), StringComparer.Ordinal);

        foreach (var stage in stages)
        {
            foreach (var dependency in stage.DependsOn)
            {
                if (!byId.ContainsKey(dependency))
                    throw new WorkflowValidationException($"Stage '{stage.Id}' depends on missing stage '{dependency}'.");
                indegree[stage.Id]++;
                dependents[dependency].Add(stage.Id);
            }
        }

        var pending = new SortedSet<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        var ordered = new List<StageNode>();
        while (pending.Count > 0)
        {
            var id = pending.Min!;
            pending.Remove(id);
            ordered.Add(byId[id]);
            foreach (var child in dependents[id])
            {
                indegree[child]--;
                if (indegree[child] == 0)
                    pending.Add(child);
            }
        }

        if (ordered.Count != stages.Count)
            throw new CyclicGraphException("Cyclic graph rejected.");

        return ordered;
    }

    public static IReadOnlyList<string> DownstreamOf(WorkflowDefinition definition, string stageId)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(stageId);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            foreach (var stage in definition.Stages)
            {
                if (stage.DependsOn.Contains(current) && result.Add(stage.Id))
                    stack.Push(stage.Id);
            }
        }

        return result.ToList();
    }
}
