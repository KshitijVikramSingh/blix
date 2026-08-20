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

    /// <summary>
    /// This body is pulling a handcart, which is a job rather than a kind of unit.
    /// </summary>
    /// <remarks>
    /// <b>There is no hauler unit.</b> A cart is a role a villager takes: it costs the settlement a sack
    /// of timber, and while the body has it, it is wider, slower and carries more — the
    /// <c>UnitType.HaulerCart</c> body, worn by a person. Which is why this flag exists at all rather than
    /// the roster having two entries: the body's <see cref="Radius"/>, <see cref="MaximumSpeed"/> and
    /// <see cref="CarryCapacity"/> are all overwritten while it holds, and something has to remember to
    /// put them back.
    /// <para>
    /// The design reason is §2's: hauler count should track what the settlement is doing rather than what
    /// it once spawned. A permanent cart unit is a decision made once and paid for forever, whereas a role
    /// is a decision you can see the cost of and change — and a settlement that needs no hauling should
    /// have no carts in it, not seven idle ones.
    /// </para>
    /// </remarks>
    public bool HasCart;
    /// <summary>How much this body eats, relative to a villager. See <c>UnitType.Appetite</c>.</summary>
    public float Appetite;

    /// <summary>How far this body can see, in metres. Trees block it; see <c>SimulationWorld.CanSee</c>.</summary>
    /// <remarks>
    /// On the body rather than on the building, because a settlement's vision is its people — which is how
    /// an outpost comes to push detection outward without a radius being attached to a tower. It extends
    /// your sight exactly as far as the garrison you were willing to take off a field.
    /// </remarks>
    public float SightMetres;

    /// <summary>Damage a second this body deals to something hostile it is standing next to.</summary>
    public float Strength;

    /// <summary>Seconds of damage left in this body. At zero it leaves the world and drops its load.</summary>
    /// <remarks>
    /// Health in seconds-of-damage rather than in an abstract pool, so a fight can be read without a combat
    /// model: five villagers at one damage a second take eight seconds over a forty-health raider, and the
    /// raider's three take twenty over a villager. Both numbers are on the roster and neither needs a curve.
    /// </remarks>
    public float Health;

    /// <summary>
    /// Seconds left of this body's commitment to standing or running.
    /// </summary>
    /// <remarks>
    /// A commitment window, and it is not optional. Group composition changes every tick, so a body on the
    /// margin of "can we win this" would flip between fighting and fleeing forever and do neither — the same
    /// hazard the interrupt grace already exists to stop a player's orders creating. Once decided, a body
    /// holds the decision for a few seconds and then asks again.
    /// </remarks>
    public float Resolve;

    /// <summary>Whether this body's current commitment is to fight rather than to run.</summary>
    public bool Standing;

    /// <summary>
    /// The place this body has committed to defend, while <see cref="Standing"/> holds.
    /// </summary>
    /// <remarks>
    /// So that a body already answering one alarm is not also counted as available for the next. Without
    /// it, every threat on the map is weighed against the same settlement-wide total, and a defence that
    /// has already been raised gets counted twice — which is how two raiders at opposite ends of a village
    /// both look answerable by everybody and neither is actually answered.
    /// </remarks>
    public Vector2 Guarding;

    /// <summary>
    /// Whether something outside the simulation is deciding this body's movement.
    /// </summary>
    /// <remarks>
    /// <b>Set on bodies a script drives, and the interrupt layer leaves them alone.</b> Found by watching:
    /// the civilian defence is written against hostility rather than against raiders, which is the right
    /// line — and it meant the raiders ran it too. A raider standing over a heap sees loot worth protecting
    /// and villagers reaching for it, decides to stand or run, and gets marched somewhere its own director
    /// did not send it, so a raid dissolved into a milling crowd that walked its own way home.
    /// <para>
    /// The fix is not to teach the defence what a raider is. It is to say that a body already under orders
    /// from elsewhere does not also make its own decisions, which is a true statement about ownership and
    /// stays true when the thing over the hill is another player: <em>their</em> people run their own
    /// interrupt layer in their own world, not ours.
    /// </para>
    /// </remarks>
    public bool Directed;

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
    /// <summary>Where this body lives. See <see cref="AgentHome"/>.</summary>
    public AgentHome Home;

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
