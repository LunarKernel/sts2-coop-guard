using System.Text;

namespace BetterCoop;

internal sealed record DoctorDependency(
    string Id,
    string MinimumVersion);

internal sealed record DoctorModRecord(
    string Id,
    string Version,
    bool Loaded,
    IReadOnlyList<DoctorDependency> Dependencies);

internal sealed record DoctorFinding(
    string Code,
    string Confidence,
    string Evidence,
    string Action);

internal static class ModDependencyDoctor
{
    public const int MaxMods = 256;
    public const int MaxDependenciesPerMod = 64;

    public static IReadOnlyList<DoctorFinding> Analyze(
        IReadOnlyList<DoctorModRecord> input)
    {
        List<DoctorFinding> findings = [];
        DoctorModRecord[] mods = input.Take(MaxMods).ToArray();
        Dictionary<string, DoctorModRecord> byId = mods
            .Where(mod => ValidId(mod.Id))
            .GroupBy(mod => mod.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);

        foreach (DoctorModRecord mod in mods)
        {
            if (!ValidId(mod.Id))
            {
                findings.Add(new(
                    "invalid-id",
                    "Inconclusive",
                    "A manifest contains an invalid dependency identity.",
                    "Repair or reinstall that manifest before dependency analysis."));
                continue;
            }

            if (mod.Dependencies.Count > MaxDependenciesPerMod)
            {
                findings.Add(new(
                    "dependency-limit",
                    "Inconclusive",
                    $"{mod.Id}: dependency count exceeds {MaxDependenciesPerMod}.",
                    "Reduce or repair the manifest dependency list."));
            }

            foreach (DoctorDependency dependency in mod.Dependencies
                         .Take(MaxDependenciesPerMod))
            {
                if (!ValidId(dependency.Id))
                {
                    findings.Add(new(
                        "invalid-dependency-id",
                        "Inconclusive",
                        $"{mod.Id}: dependency identity is invalid.",
                        "Repair or reinstall the declaring manifest."));
                    continue;
                }

                if (!byId.TryGetValue(dependency.Id, out DoctorModRecord? target))
                {
                    findings.Add(new(
                        "missing-dependency",
                        "Confirmed",
                        $"{mod.Id} requires missing dependency {dependency.Id}.",
                        $"Install and enable {dependency.Id}, then restart."));
                    continue;
                }

                if (!target.Loaded)
                {
                    findings.Add(new(
                        "dependency-not-loaded",
                        "Confirmed",
                        $"{mod.Id} requires {dependency.Id}, but it is disabled or failed.",
                        $"Resolve {dependency.Id}'s native load state, then restart."));
                    continue;
                }

                if (!TryVersion(dependency.MinimumVersion, out SemVersion minimum))
                {
                    findings.Add(new(
                        "invalid-constraint",
                        "Inconclusive",
                        $"{mod.Id} declares invalid minimum version '{Bound(dependency.MinimumVersion)}' for {dependency.Id}.",
                        "Repair the declaring manifest before testing multiplayer."));
                    continue;
                }

                if (!TryVersion(target.Version, out SemVersion installed))
                {
                    findings.Add(new(
                        "invalid-version",
                        "Inconclusive",
                        $"{dependency.Id} has invalid version '{Bound(target.Version)}'.",
                        "Repair or reinstall the dependency manifest."));
                    continue;
                }

                if (installed.CompareTo(minimum) < 0)
                {
                    findings.Add(new(
                        "version-too-old",
                        "Confirmed",
                        $"{mod.Id} requires {dependency.Id} >= {minimum}; installed {installed}.",
                        $"Update {dependency.Id}, then restart."));
                }
            }
        }

        foreach (string[] cycle in StronglyConnected(byId)
                     .Where(component => component.Length > 1
                         || HasSelfEdge(component[0], byId)))
        {
            findings.Add(new(
                "dependency-cycle",
                "Confirmed",
                "Dependency cycle: " + string.Join(" -> ", cycle),
                "The cycle is an inseparable unit; repair the manifests before testing."));
        }

        if (input.Count > MaxMods)
        {
            findings.Add(new(
                "mod-limit",
                "Inconclusive",
                $"Mod count exceeds {MaxMods}; dependency analysis stopped at its safety bound.",
                "Reduce the active Mod set and run the Doctor again."));
        }

        return findings;
    }

    private static IEnumerable<string[]> StronglyConnected(
        IReadOnlyDictionary<string, DoctorModRecord> mods)
    {
        Dictionary<string, int> indexes = new(StringComparer.Ordinal);
        Dictionary<string, int> lowLinks = new(StringComparer.Ordinal);
        HashSet<string> onStack = new(StringComparer.Ordinal);
        Stack<string> stack = new();
        List<string[]> components = [];
        int nextIndex = 0;

        void Visit(string id)
        {
            indexes[id] = nextIndex;
            lowLinks[id] = nextIndex++;
            stack.Push(id);
            onStack.Add(id);
            foreach (DoctorDependency dependency in mods[id].Dependencies
                         .Take(MaxDependenciesPerMod))
            {
                if (!mods.ContainsKey(dependency.Id))
                {
                    continue;
                }

                if (!indexes.ContainsKey(dependency.Id))
                {
                    Visit(dependency.Id);
                    lowLinks[id] = Math.Min(
                        lowLinks[id],
                        lowLinks[dependency.Id]);
                }
                else if (onStack.Contains(dependency.Id))
                {
                    lowLinks[id] = Math.Min(
                        lowLinks[id],
                        indexes[dependency.Id]);
                }
            }

            if (lowLinks[id] != indexes[id])
            {
                return;
            }

            List<string> component = [];
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                component.Add(member);
            }
            while (member != id);
            component.Sort(StringComparer.Ordinal);
            components.Add(component.ToArray());
        }

        foreach (string id in mods.Keys.Order(StringComparer.Ordinal))
        {
            if (!indexes.ContainsKey(id))
            {
                Visit(id);
            }
        }

        return components;
    }

    private static bool HasSelfEdge(
        string id,
        IReadOnlyDictionary<string, DoctorModRecord> mods) =>
        mods[id].Dependencies
            .Take(MaxDependenciesPerMod)
            .Any(dependency =>
                string.Equals(dependency.Id, id, StringComparison.Ordinal));

    private static bool ValidId(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && !value.Any(char.IsControl)
        && !value.Contains("..", StringComparison.Ordinal)
        && !Path.IsPathRooted(value);

    private static string Bound(string value) =>
        value.Length <= 64 ? value : value[..64];

    private static bool TryVersion(string value, out SemVersion version)
    {
        version = default;
        string text = value.Trim();
        if (text.StartsWith('v'))
        {
            text = text[1..];
        }

        int metadata = text.IndexOf('+');
        if (metadata >= 0)
        {
            text = text[..metadata];
        }

        string prerelease = string.Empty;
        int dash = text.IndexOf('-');
        if (dash >= 0)
        {
            prerelease = text[(dash + 1)..];
            text = text[..dash];
            if (string.IsNullOrEmpty(prerelease)
                || prerelease.Length > 64
                || prerelease.Split('.').Any(part =>
                    string.IsNullOrEmpty(part)
                    || part.Any(character =>
                        !char.IsAsciiLetterOrDigit(character)
                        && character != '-')))
            {
                return false;
            }
        }

        string[] parts = text.Split('.');
        if (parts.Length is < 1 or > 3)
        {
            return false;
        }

        int[] numbers = [0, 0, 0];
        for (int index = 0; index < parts.Length; index++)
        {
            if (!int.TryParse(
                    parts[index],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out numbers[index])
                || (parts[index].Length > 1 && parts[index][0] == '0'))
            {
                return false;
            }
        }

        version = new(
            numbers[0],
            numbers[1],
            numbers[2],
            prerelease);
        return true;
    }

    private readonly record struct SemVersion(
        int Major,
        int Minor,
        int Patch,
        string Prerelease) : IComparable<SemVersion>
    {
        public int CompareTo(SemVersion other)
        {
            int core = Major.CompareTo(other.Major);
            core = core != 0 ? core : Minor.CompareTo(other.Minor);
            core = core != 0 ? core : Patch.CompareTo(other.Patch);
            if (core != 0 || Prerelease == other.Prerelease)
            {
                return core;
            }

            if (string.IsNullOrEmpty(Prerelease))
            {
                return 1;
            }

            if (string.IsNullOrEmpty(other.Prerelease))
            {
                return -1;
            }

            string[] left = Prerelease.Split('.');
            string[] right = other.Prerelease.Split('.');
            for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
            {
                bool leftNumber = int.TryParse(left[index], out int leftValue);
                bool rightNumber = int.TryParse(right[index], out int rightValue);
                int comparison = leftNumber && rightNumber
                    ? leftValue.CompareTo(rightValue)
                    : leftNumber
                        ? -1
                        : rightNumber
                            ? 1
                            : StringComparer.Ordinal.Compare(
                                left[index],
                                right[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }

        public override string ToString() =>
            $"{Major}.{Minor}.{Patch}"
            + (string.IsNullOrEmpty(Prerelease) ? "" : "-" + Prerelease);
    }
}

internal sealed record ModBisectPlan(
    bool Available,
    string Text,
    int CandidateGroups,
    int MaximumRounds);

internal static class ModBisectPlanner
{
    public const int MaxOutputCharacters = 64 * 1024;

    public static ModBisectPlan Create(
        IReadOnlyList<DoctorModRecord> input,
        IReadOnlyCollection<string> candidateIds)
    {
        DoctorModRecord[] mods = input.Take(ModDependencyDoctor.MaxMods)
            .ToArray();
        string[] candidates = candidateIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(ModDependencyDoctor.MaxMods + 1)
            .ToArray();
        if (input.Count > ModDependencyDoctor.MaxMods
            || candidates.Length is 0 or > ModDependencyDoctor.MaxMods)
        {
            return Unavailable(
                "Candidate or Mod count is empty or exceeds 256.");
        }

        Dictionary<string, DoctorModRecord> byId = mods
            .Where(mod => !string.IsNullOrWhiteSpace(mod.Id))
            .GroupBy(mod => mod.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.Ordinal);
        if (candidates.Any(id =>
                !byId.TryGetValue(id, out DoctorModRecord? mod)
                || !mod.Loaded))
        {
            return Unavailable(
                "Every candidate must be an enabled Mod with a parsed manifest.");
        }

        DoctorFinding? blocker = ModDependencyDoctor.Analyze(mods)
            .FirstOrDefault(finding =>
                finding.Code != "dependency-cycle");
        if (blocker != null)
        {
            return Unavailable(
                $"{blocker.Confidence}: {blocker.Evidence} {blocker.Action}");
        }

        HashSet<string> candidateSet =
            candidates.ToHashSet(StringComparer.Ordinal);
        DoctorModRecord? dependentBaseline = mods.FirstOrDefault(mod =>
            mod.Loaded
            && !candidateSet.Contains(mod.Id)
            && mod.Dependencies.Any(dependency =>
                candidateSet.Contains(dependency.Id)));
        if (dependentBaseline != null)
        {
            return Unavailable(
                $"{dependentBaseline.Id} is outside the candidate set but depends on a candidate. "
                + "Include that dependent Mod in the suspected set before generating a safe plan.");
        }

        Dictionary<string, HashSet<string>> adjacency = candidates.ToDictionary(
            id => id,
            _ => new HashSet<string>(StringComparer.Ordinal),
            StringComparer.Ordinal);
        foreach (string id in candidates)
        {
            foreach (DoctorDependency dependency in byId[id].Dependencies)
            {
                if (!candidateSet.Contains(dependency.Id))
                {
                    continue;
                }

                adjacency[id].Add(dependency.Id);
                adjacency[dependency.Id].Add(id);
            }
        }

        List<string[]> groups = [];
        HashSet<string> visited = new(StringComparer.Ordinal);
        foreach (string id in candidates)
        {
            if (!visited.Add(id))
            {
                continue;
            }

            List<string> group = [];
            Stack<string> pending = new();
            pending.Push(id);
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                group.Add(current);
                foreach (string neighbor in adjacency[current])
                {
                    if (visited.Add(neighbor))
                    {
                        pending.Push(neighbor);
                    }
                }
            }

            group.Sort(StringComparer.Ordinal);
            groups.Add(group.ToArray());
        }

        groups.Sort((left, right) =>
            StringComparer.Ordinal.Compare(left[0], right[0]));
        string[] baseline = mods
            .Where(mod => mod.Loaded && !candidateSet.Contains(mod.Id))
            .Select(mod => mod.Id)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        int maximumRounds = groups.Count == 1
            ? 1
            : (int)Math.Ceiling(Math.Log2(groups.Count)) + 1;
        StringBuilder text = new();
        text.AppendLine("BetterCoop dependency-aware manual A/B plan");
        text.AppendLine(
            "Read-only: this plan does not edit, move, enable, disable or launch anything.");
        text.AppendLine(
            "Every test uses a new run. Record the result manually; correlation is not attribution.");
        text.AppendLine(
            $"Independent candidate groups: {groups.Count}; maximum path: {maximumRounds} rounds.");
        text.AppendLine(
            "Baseline: "
            + (baseline.Length == 0
                ? "<none>"
                : string.Join(", ", baseline)));
        for (int index = 0; index < groups.Count; index++)
        {
            text.AppendLine(
                $"G{index + 1:D3}: {string.Join(", ", groups[index])}");
        }

        text.AppendLine("Decision tree:");
        AppendNode(
            text,
            Enumerable.Range(0, groups.Count).ToArray(),
            "R1",
            depth: 1);
        if (text.Length > MaxOutputCharacters)
        {
            return Unavailable(
                "The dependency-aware plan exceeds the 64 KiB display bound.");
        }

        return new(true, text.ToString(), groups.Count, maximumRounds);
    }

    private static void AppendNode(
        StringBuilder text,
        int[] groupIndexes,
        string node,
        int depth)
    {
        if (groupIndexes.Length == 1)
        {
            text.AppendLine(
                $"{node} (round {depth}, confirm): enable Baseline + G{groupIndexes[0] + 1:D3}; "
                + "start a new run and record whether the issue reproduces.");
            return;
        }

        int split = (groupIndexes.Length + 1) / 2;
        int[] left = groupIndexes[..split];
        int[] right = groupIndexes[split..];
        text.AppendLine(
            $"{node} (round {depth}): enable Baseline + "
            + string.Join(", ", left.Select(index => $"G{index + 1:D3}"))
            + $"; reproduced -> {node}Y, not reproduced -> {node}N.");
        AppendNode(text, left, node + "Y", depth + 1);
        AppendNode(text, right, node + "N", depth + 1);
    }

    private static ModBisectPlan Unavailable(string reason) =>
        new(
            false,
            "BetterCoop dependency-aware manual A/B plan\n"
            + "Plan unavailable: "
            + reason
            + "\nNo Mod or setting was changed.",
            0,
            0);
}
