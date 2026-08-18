using System.Numerics;
using RTSGame.Simulation.Collision;
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
    public Vector2 PatrolStart;
    public Vector2 PatrolEnd;
    public Vector2 HoldPosition;
    public Vector2 GroupSlot;
    public float Radius;
    public float MaximumSpeed;
    public float Acceleration;
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
