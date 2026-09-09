using System.Numerics;
using RTSGame.Simulation.Collision;
using RTSGame.Simulation.Economy;
using RTSGame.Simulation.Jobs;
using RTSGame.Simulation.Navigation;

namespace RTSGame.Simulation.Agents;

internal struct AgentState
{
    public AgentId Id;
    /// <summary>What this person permanently is; jobs and temporary equipment never infer or replace it.</summary>
    public AgentRole Role;
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
    /// How far into its current blow or stroke this body is, in seconds. Zero between acts.
    /// </summary>
    /// <remarks>
    /// <b>One field for whatever act the body is performing</b> — a blow if it is fighting, a stroke if it
    /// is working. A body does neither at once, so two fields would be two names for one clock, which is the
    /// fault this codebase keeps paying for. Renamed from <c>SwingCharge</c> in §205 when work joined.
    /// <para>
    /// <b>Because "harm happens when two bodies are near each other" is a proximity modal, and it is older
    /// than the rigs.</b> §198. It made sense when a body was a cylinder: harm was
    /// <c>Strength x deltaSeconds</c> for every tick two capsules overlapped, and there was nothing to
    /// synchronise it with. Nearly every fault in the combat arc is downstream of it — a chase that parked
    /// three and a half centimetres outside the reach and could never land a blow (§192), an arrival test at
    /// 1.11 m against a reach of 0.814 m (§194), and <c>landed/N</c> reading 5 of 4 for four sections
    /// because "landed" meant *in contact and admitted* rather than *hit something* (§189).
    /// <para>
    /// A blow with a duration dissolves all three. Reach is asked <em>once</em>, at the instant the blow
    /// lands, instead of every tick of an overlap; a landing is an event, so it can be counted and drawn;
    /// and the strike animation has a clock to share rather than a drain to approximate.
    /// </para>
    /// <para>
    /// <b>Lost on losing contact, not banked.</b> A swing has to be seen through, so stepping out of reach
    /// mid-swing means it misses — which is the property that makes dodging mean anything and is not
    /// expressible at all while harm is continuous.
    /// </para>
    /// <para>
    /// Fingerprinted and saved without being asked: <c>AgentState</c> goes to disk as raw bytes behind a
    /// layout signature built by reflection, so this field enters the census and invalidates older saves on
    /// its own. See <c>WorldSave.BodyLayoutSignature</c>.
    /// </para>
    /// </para>
    /// </remarks>
    public float ActCharge;

    /// <summary>
    /// Fractions of a unit this body has freed but not yet taken, in its current act.
    /// </summary>
    /// <remarks>
    /// <b>The body's own, which is the whole of §204.</b> The accrual used to live on the node — a trunk
    /// carried one shared counter, several cutters fed it at several times the rate, and it popped whole
    /// units out on its own schedule to whichever body happened to call in on that tick. So the moment wood
    /// left a trunk belonged to the trunk, and no cutter's animation could be synchronised with it because
    /// it was not that cutter's event. §174's test asserting the chop pose plays whenever wood leaves passes
    /// for that reason and no other: the pose is continuous, so every popping tick is a posing tick.
    /// <para>
    /// Per body, a stroke is an event with an owner. Wood leaves the trunk on somebody's axe-fall.
    /// </para>
    /// </remarks>
    public float WorkPending;

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
    /// Whether this body is on its way to put a load down before it does anything else.
    /// </summary>
    /// <remarks>
    /// <b>A flag beside the id rather than a sentinel in it</b>, because <c>default(NodeId)</c> is zero and
    /// node zero is a real node — usually the granary. That trap has been walked into once already, when
    /// a default assignment pointed every idle body at node zero, and a bool that defaults to false cannot
    /// be walked into at all.
    /// <para>
    /// It exists because a stow has to outlive the reason for it. The first version was driven from the
    /// defence's own decision each tick, so a body that carried its load out of sight of the raid stopped
    /// being asked to finish — it walked to the depot and stood there holding forty grain for the rest of
    /// the session. An errand needs somewhere to live while it is being run.
    /// </para>
    /// </remarks>
    public bool PuttingDown;

    /// <summary>Where the load is going, while <see cref="PuttingDown"/> holds.</summary>
    public NodeId StowInto;

    /// <summary>
    /// Whether this body is inside a building and therefore not in the world at all.
    /// </summary>
    /// <remarks>
    /// <b>A raider looting a granary is in the granary.</b> Which turns out to be the whole mechanic: while
    /// it is in there it cannot be shoved off the door by the crowd that came to stop it, and it cannot be
    /// fought either — so looting is a window that runs to completion, and the fight happens when it comes
    /// out carrying something. That is a far better shape than the alternative, which was a shoving match
    /// at the door whose outcome depended on crowd physics.
    /// <para>
    /// Not drawn, because it is indoors, and not a target and not an obstacle: its four collider proxies are
    /// disabled rather than removed, so it comes back as the same body with the same handles. It stays in
    /// the world's roster the entire time, which is deliberate — the settlement can still see that its
    /// granary is being robbed, and the defence gathers outside while it happens.
    /// </para>
    /// </remarks>
    public bool Sheltered;

    /// <summary>
    /// The body this one is fighting, while <see cref="HasQuarry"/> holds.
    /// </summary>
    /// <remarks>
    /// <b>Concentration by commitment.</b> A third of all the harm in a raid was going into thieves that
    /// walked away wounded, because a defender re-chose its target every few seconds and took whatever was
    /// nearest — so a raid that scattered scattered the damage with it and left every raider just under the
    /// threshold, which is why changing a raider's health by a sixth changed nothing measurable.
    /// <para>
    /// A flag beside the id again, because <c>default(AgentId)</c> is body zero and body zero is somebody.
    /// </para>
    /// </remarks>
    public AgentId Quarry;

    public bool HasQuarry;



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

    /// <summary>
    /// This body is walking to the nearest cell its cohort's flow field can price, and means to rejoin it.
    /// </summary>
    /// <remarks>
    /// <b>Explicit because two attempts at deriving it broke crowd tests.</b> The condition was first taken as
    /// "Destination differs from RequestedDestination", which is also true whenever AssignPath resolves an
    /// unwalkable goal to a nearby cell, and then as "a group member with no path left", which is also true of
    /// bodies the congestion machinery is about to repath. Both pulled bodies off deliberate individual routes
    /// and onto the shared gradient, and the pen and chokepoint tests said so — four of them. A body's
    /// intention is not reliably inferable from its geometry, so it is written down.
    /// </remarks>
    public bool SeekingFieldEntry;
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
