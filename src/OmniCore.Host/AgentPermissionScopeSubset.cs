namespace OmniCore.Host;

using OmniCore.Domain;

/// <summary>Conservative exact-rule subset check for inherited agent authority (ADR-0037).</summary>
internal static class AgentPermissionScopeSubset
{
    internal static bool IsSubset(PermissionScope child, PermissionScope parent) =>
        child.Reads.All(parent.Reads.Contains)
        && child.Writes.All(parent.Writes.Contains)
        && RulesAreSubset(child.Process, parent.Process, Same)
        && RulesAreSubset(child.Network, parent.Network, Same)
        && child.Secrets.All(parent.Secrets.Contains)
        && (!child.AllowShell || parent.AllowShell);

    internal static bool IsReadOnly(PermissionScope permissions) =>
        permissions.Writes.Count == 0 && permissions.Process.Count == 0
        && permissions.Network.Count == 0 && permissions.Secrets.Count == 0 && !permissions.AllowShell;

    private static bool Same(ProcessRule left, ProcessRule right) =>
        left.ExecutablePattern.Equals(right.ExecutablePattern, StringComparison.Ordinal)
        && left.ArgvPatterns.SequenceEqual(right.ArgvPatterns, StringComparer.Ordinal);

    private static bool Same(NetworkRule left, NetworkRule right) =>
        left.HostPattern.Equals(right.HostPattern, StringComparison.Ordinal);

    private static bool RulesAreSubset<T>(IReadOnlyList<T> child, IReadOnlyList<T> parent,
        Func<T, T, bool> sameRule)
    {
        // Exact matcher inclusion is intentionally conservative. A child's decision may only
        // restrict the matching parent decision, and an explicit parent Deny must not disappear
        // beside a broader Allow (AgentProfilePermissionPolicy evaluates all matching rules).
        bool DecisionNoBroader(T candidate, T allowed) =>
            (int)(candidate switch
            {
                ProcessRule process => process.Decision,
                NetworkRule network => network.Decision,
                _ => throw new InvalidOperationException("Unsupported permission rule type."),
            }) <= (int)(allowed switch
            {
                ProcessRule process => process.Decision,
                NetworkRule network => network.Decision,
                _ => throw new InvalidOperationException("Unsupported permission rule type."),
            });
        bool IsRestricting(T rule) => rule switch
        {
            ProcessRule process => process.Decision != PermissionDecision.Allow,
            NetworkRule network => network.Decision != PermissionDecision.Allow,
            _ => throw new InvalidOperationException("Unsupported permission rule type."),
        };
        return child.All(rule => parent.Any(candidate => sameRule(rule, candidate)
                && DecisionNoBroader(rule, candidate)))
            // An empty child list denies that entire capability and is therefore always
            // narrower. Otherwise preserve every parent Ask/Deny matcher; dropping one beside
            // a broader Allow would silently remove a restriction.
            && (child.Count == 0 || parent.Where(IsRestricting).All(rule => child.Any(candidate =>
                sameRule(rule, candidate) && DecisionNoBroader(candidate, rule))));
    }
}
