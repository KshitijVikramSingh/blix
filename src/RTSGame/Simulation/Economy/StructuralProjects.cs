using RTSGame.Simulation.Agents;

namespace RTSGame.Simulation.Economy;

/// <summary>Work that changes a completed structure without pretending it was never built.</summary>
internal enum StructuralProjectKind
{
    None,
    Repair,
    Upgrade,
}

/// <summary>
/// The shared physical rule for construction, repair and upgrade: deliver, incorporate with labour,
/// and leave anything not incorporated as stock at the stable node.
/// </summary>
internal static class StructuralProjects
{
    private static int Sack => UnitType.Villager.CarryCapacity;

    public static float MaxConditionFor(NodeKind kind) => kind switch
    {
        NodeKind.Granary => 600f,
        NodeKind.House => 250f,
        NodeKind.ForwardDepot => 200f,
        NodeKind.PalisadeWall => 100f,
        NodeKind.StoneWall => 250f,
        NodeKind.Barracks => 500f,
        _ => 0f,
    };

    /// <summary>Starts a repair whose cost is frozen by the condition at this moment.</summary>
    public static bool BeginRepair(ref EconomyNode node)
    {
        if (!node.IsAlive || !node.IsBuilt || !node.IsStructure || node.HasStructuralProject) return false;
        if (node.Condition >= node.MaxCondition - 0.0001f) return false;
        node.StructuralProject = StructuralProjectKind.Repair;
        node.StructuralTarget = node.Kind;
        node.StructuralWork = 0f;
        node.StructuralConsumed = default;
        node.StructuralStartCondition = Math.Clamp(node.Condition, 0f, node.MaxCondition);
        return LabourFor(in node) > 0f;
    }

    /// <summary>Begins the one proved upgrade: a sound palisade remains itself until it becomes stone.</summary>
    public static bool BeginUpgrade(ref EconomyNode node, NodeKind target)
    {
        if (!node.IsAlive || !node.IsBuilt || node.HasStructuralProject) return false;
        if (node.Kind != NodeKind.PalisadeWall || target != NodeKind.StoneWall) return false;
        if (node.Condition < node.MaxCondition - 0.0001f) return false;
        node.StructuralProject = StructuralProjectKind.Upgrade;
        node.StructuralTarget = target;
        node.StructuralWork = 0f;
        node.StructuralConsumed = default;
        node.StructuralStartCondition = node.Condition;
        return true;
    }

    public static NodeStock CostFor(in EconomyNode node)
    {
        if (node.IsUnderConstruction) return Construction.CostFor(node.Kind);
        return node.StructuralProject switch
        {
            StructuralProjectKind.Repair => ScaledRepairCost(in node),
            StructuralProjectKind.Upgrade => UpgradeCost(node.Kind, node.StructuralTarget),
            _ => default,
        };
    }

    public static float LabourFor(in EconomyNode node)
    {
        if (node.IsUnderConstruction) return Construction.LabourFor(node.Kind);
        return node.StructuralProject switch
        {
            StructuralProjectKind.Repair => RepairLabour(in node),
            StructuralProjectKind.Upgrade when
                node.Kind == NodeKind.PalisadeWall && node.StructuralTarget == NodeKind.StoneWall => 600f,
            _ => 0f,
        };
    }

    public static float WorkFor(in EconomyNode node) =>
        node.IsUnderConstruction ? node.BuildWork : node.StructuralWork;

    public static void SetWork(ref EconomyNode node, float work)
    {
        if (node.IsUnderConstruction) node.BuildWork = work;
        else node.StructuralWork = work;
    }

    public static int ConsumedFor(in EconomyNode node, Resource resource) =>
        node.IsUnderConstruction ? node.BuildConsumed[resource] : node.StructuralConsumed[resource];

    public static void AddConsumed(ref EconomyNode node, Resource resource, int units)
    {
        if (node.IsUnderConstruction) node.BuildConsumed.Add(resource, units);
        else node.StructuralConsumed.Add(resource, units);
    }

    /// <summary>Applies visible condition during repair and resolves identity atomically at completion.</summary>
    public static void ApplyProgress(ref EconomyNode node, float work, float labour)
    {
        if (node.IsUnderConstruction) return;
        if (node.StructuralProject == StructuralProjectKind.Repair)
        {
            var share = labour <= 0f ? 1f : Math.Clamp(work / labour, 0f, 1f);
            node.Condition = node.StructuralStartCondition +
                             (node.MaxCondition - node.StructuralStartCondition) * share;
        }
    }

    /// <summary>Finishes the operation; returns true only when a new building was raised.</summary>
    public static bool Complete(ref EconomyNode node, bool construction)
    {
        if (construction)
        {
            node.Condition = node.MaxCondition;
            return true;
        }

        switch (node.StructuralProject)
        {
            case StructuralProjectKind.Repair:
                node.Condition = node.MaxCondition;
                break;
            case StructuralProjectKind.Upgrade:
                node.Kind = node.StructuralTarget;
                node.MaxCondition = MaxConditionFor(node.Kind);
                node.Condition = node.MaxCondition;
                break;
            default:
                return false;
        }

        node.StructuralProject = StructuralProjectKind.None;
        node.StructuralTarget = node.Kind;
        node.StructuralWork = 0f;
        node.StructuralConsumed = default;
        node.StructuralStartCondition = node.Condition;
        return false;
    }

    public static string StateOf(in EconomyNode node)
    {
        var labour = MathF.Max(1f, LabourFor(in node));
        var progress = WorkFor(in node) / labour * 100f;
        var verb = node.IsUnderConstruction
            ? "building"
            : node.StructuralProject == StructuralProjectKind.Repair ? "repairing" : "upgrading";
        var state = node.Hands == 0
            ? progress > 0f ? $"paused at {progress:F0}%" : "waiting for hands"
            : $"{verb} {progress:F0}%";
        var missing = new List<string>();
        foreach (var resource in Resources.All)
        {
            var short_ = node.Wanted(resource);
            if (short_ <= 0) continue;
            missing.Add($"{short_} {(resource == Resource.Wood ? "timber" : resource.ToString().ToLowerInvariant())}");
        }

        return missing.Count > 0 ? $"{state}, needs {string.Join(" and ", missing)}" : state;
    }

    private static NodeStock UpgradeCost(NodeKind from, NodeKind to) =>
        from == NodeKind.PalisadeWall && to == NodeKind.StoneWall
            ? new NodeStock { Stone = 4 * Sack }
            : default;

    private static NodeStock ScaledRepairCost(in EconomyNode node)
    {
        var full = FullRepairCost(node.Kind);
        var share = MissingConditionShare(in node);
        foreach (var resource in Resources.All)
        {
            if (full[resource] > 0) full[resource] = Math.Max(1, (int)MathF.Ceiling(full[resource] * share));
        }

        return full;
    }

    private static NodeStock FullRepairCost(NodeKind kind) => kind switch
    {
        NodeKind.Granary => new NodeStock { Wood = 4 * Sack, Stone = Sack },
        NodeKind.House => new NodeStock { Wood = Sack },
        NodeKind.ForwardDepot => new NodeStock { Wood = Sack },
        NodeKind.PalisadeWall => new NodeStock { Wood = Sack },
        NodeKind.StoneWall => new NodeStock { Stone = 2 * Sack },
        NodeKind.Barracks => new NodeStock { Wood = 3 * Sack, Stone = Sack },
        _ => default,
    };

    private static float RepairLabour(in EconomyNode node)
    {
        var full = node.Kind switch
        {
            NodeKind.Granary => 600f,
            NodeKind.House => 300f,
            NodeKind.ForwardDepot => 180f,
            NodeKind.PalisadeWall => 180f,
            NodeKind.StoneWall => 300f,
            NodeKind.Barracks => 480f,
            _ => 0f,
        };
        return full * MissingConditionShare(in node);
    }

    private static float MissingConditionShare(in EconomyNode node) =>
        node.MaxCondition <= 0f
            ? 0f
            : Math.Clamp((node.MaxCondition - node.StructuralStartCondition) / node.MaxCondition, 0f, 1f);
}
