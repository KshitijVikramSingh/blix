using System.Numerics;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Navigation;

namespace RTSGame.Simulation.Agents;

internal struct AgentState
{
    public AgentId Id;
    public AgentLocomotionState LocomotionState;
    public FactionId Faction;
    public AgentColliderSet Colliders;
    public Vector2 PreviousPosition;
    public Vector2 Position;
    public Vector2 Velocity;
    public Vector2 Facing;
    public Vector2 PreferredVelocity;
    public Vector2 Destination;
    public Vector2 RequestedDestination;
    public Vector2 RepathAvoidanceCenter;
    /// <summary>Constriction this body has given up on, while <see cref="AbandonedApertureSeconds"/> lasts.</summary>
    public Vector2 AbandonedAperture;
    /// <summary>Seconds left of refusing to route through <see cref="AbandonedAperture"/>.</summary>
    public float AbandonedApertureSeconds;
    public Vector2 PatrolStart;
    public Vector2 PatrolEnd;
    public Vector2 HoldPosition;
    public Vector2 GroupSlot;
    public float Radius;
    /// <summary>Radius the navigation layer routes this body at; see <c>UnitType</c>.</summary>
    /// <remarks>
    /// Its class's radius rather than its own, so the decomposition and the flow fields are cached
    /// once per class instead of once per unit type. Exact rather than approximate: two radii
    /// inside one clearance rung have identical walkable sets. Path queries take this; terrain
    /// clamps, colliders and anything about where the body physically is take <see cref="Radius"/>.
    /// </remarks>
    public float NavigationRadius;
    /// <summary>Smallest circle this body can turn in, or zero if it may pivot on the spot.</summary>
    public float TurningRadius;
    /// <summary>What this body can carry, for the hauling layer.</summary>
    public int CarryCapacity;
    /// <summary>How much this body eats, relative to a villager. See <c>UnitType.Appetite</c>.</summary>
    public float Appetite;
    public float MaximumSpeed;
    public float Acceleration;
    /// <summary>Rate this body sheds speed at; see AgentDefaults.Deceleration.</summary>
    public float Deceleration;
    public float MaximumTurnSpeed;
    public PathHandle Path;
    public int WaypointIndex;
    public int NavigationRevision;
    public int MoveGroupId;
    public int ProgressSampleWaypointIndex;
    public int CrowdedArrivalAttempts;
    public int CrowdedArrivalContactFrames;
    public float StuckSeconds;
    public float YieldStoppedSeconds;
    public float CongestionYieldSeconds;
    public float RepathCooldown;
    public float LastDestinationDistance;
    public float ProgressSampleDistance;
    public float ProgressSampleSeconds;
    public float CrowdPressureSeconds;
    public float BehaviorUpdateCooldown;
    public float HoldReturnCooldown;
    public AgentId BehaviorTarget;
    /// <summary>Which store feeds this body. See <see cref="AgentSupply"/>.</summary>
    public AgentSupply Supply;

    /// <summary>What this unit is committed to, doing, and currently being kept from.</summary>
    /// <remarks>
    /// Three layers in one struct: see <see cref="AgentJobs"/>. It lives on the body rather
    /// than in a table beside it so that saving a unit saves its job, and so that the
    /// determinism fingerprint reads it without anybody having to add it to a list.
    /// </remarks>
    public AgentJobs Jobs;
    public bool RepathRequested;
    public bool HasRepathAvoidance;
    public bool PatrolTowardEnd;
    public bool ReturningToHold;
    /// <summary>Member has left the shared route and is heading for its own slot.</summary>
    public bool ApproachingSlot;
    /// <summary>Steering from the shared cost field rather than from a stored path.</summary>
    public bool UsesFlowTransit;
    /// <summary>Consecutive ticks the shared field asked for a step the body could not take.</summary>
    public int FlowStepRejections;
    /// <summary>Seconds spent under orders while producing no intent to move at all.</summary>
    public float NoIntentSeconds;
    /// <summary>Throttle on route retries while intentless, kept separate so it cannot reset the above.</summary>
    public float NoIntentRetryCooldown;
    /// <summary>Formation position this member holds while travelling, relative to the cohort centroid.</summary>
    public Vector2 FormationOffset;
    /// <summary>Low-passed flow direction, so a body does not snap when the shared field is rebuilt.</summary>
    public Vector2 SmoothedFlow;
    /// <summary>Congestion revision this member is currently routing against.</summary>
    public int AdoptedCongestionRevision;
    /// <summary>Seconds remaining before this member takes up the latest routing.</summary>
    public float RouteAdoptionDelay;
    /// <summary>Seconds this member will stay on its chosen route before reconsidering.</summary>
    public float RouteCommitSeconds;
    public bool HasDestination;
    public bool HadAgentContactThisTick;
    public bool CrowdedArrivalBlockedThisTick;
    public bool ArrivedThisTick;
    public bool SteeringStepRejectedThisTick;
    public bool PreferredStepRejectedThisTick;
    public bool AvoidanceBlockedThisTick;
    /// <summary>False once the unit has been removed; its id is never reused.</summary>
    public bool IsAlive;

    public readonly bool IsVisiblyYielding
    {
        get
        {
            if (CongestionYieldSeconds > 0f) return true;
            return YieldStoppedSeconds >= 0.18f;
        }
    }
}
