using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Blix.Core;
using Blix.Diagnostics;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --rtgi-census [--rtgi-census-out DIR]: a measurement, not GI. The cache is to be cooked per world chunk -- its cells'
// layout from the geometry alone, the light real-time -- and what to ship, what to rebuild at load and what to work out
// at run time, and the chunks' size, are to be chosen from numbers (2026-10-30). Its rules became the cook's
// (SponzaLoop.RtgiCook.cs, step 1a, 2026-10-30): the census now runs the cook's first stages -- geometry, size field,
// cells, the code here shared -- and reports from them:
//
//   geometry  the static instances' triangles, their world area and bounds, and which are alpha-tested (on a worker).
//             Every surface is taken on both sides, and each side kept or dropped by its own samples: buried (most of
//             its rays met back faces -- one-sided, but an alpha-tested triangle's back face a front) or over the void
//             (most of its rays left downward, CensusVoidDown: the underside of terrain). An alpha-tested surface keeps
//             both. Sides chosen by the material's double-sided flag (San Miguel's and the jungle's exports mark every
//             material so) or by open sheets (most of San Miguel's and Bistro's area: walls of one plane, parts that
//             touch without sharing edges) made nearly everything two-sided and turned the buried test off
//   size      the cell-size field, coarse first: surface samples by area at 2 m (CensusPassSpacing), then at 0.5 m and
//             0.25 m only where the pass above found the geometry tight (a size under CensusRefineFactor of its
//             spacing, or buried) -- a running sum of area along each run of triangles (or of a triangle's pieces in
//             the refined regions) drops a sample at a random point where it crosses the next spacing^2, so tiny
//             triangles share them; every triangle's area counts once a side. Each sample traces CensusRays
//             cosine rays through the Metal batch (rtgi_cook.comp kinds 0, 1; a chunk a frame): its harmonic hit distance
//             (escapes adding nothing to the sum), its back-face share (over half:
//             buried, inside something; an alpha-tested triangle's back face counts as a front), its rays escaping
//             downward, its nearest hit, and from them the cache's cell size, clamp(0.1 x harmonic, 5 cm, the cache's
//             top level)
//   cells     the cook's (SponzaLoop.RtgiCook.cs BuildCookCells): every triangle, each side, in pieces, each with a
//             lattice at the finest pass's spacing found near it, each lattice point's nearest sample from the finest
//             pass that has one near on the same side (by hash grids, searched outward where none is); points a quarter
//             of the local size apart, each point's cell at the level the cooked size table gives it (the smallest
//             size in its cube; none where that side is dropped) and every level above it -- the cells the cooked cache
//             holds, by level, those that are some surface's own (finest) level, and the junction cells among them:
//             another surface within min(1.5 cell sizes, 0.375 m) of the point (its sample's nearest hit), the only
//             ones the cook's inside test is for (on a worker)
//   pricing   bytes and rays by the per-item costs below, the times at the measured trace rate, whole and per chunk of
//             16, 32 and 64 m; the runtime bytes at today's slot, at a proposed compact resident record (all cells, and
//             the chunks within 32 / 64 m of the start camera) plus a transient training pool
//
// written to DIR/<scene>.json and .txt, a line to the log, and the demo closes. The cache is left untrained meanwhile
// (RecordRtgi: the cook alone on the GPU), so the trace's time is the census's own. The report runs only with the flag. Approximations: cells keyed by coordinates and side alone (no regions); the cell grid's
// origin as rtgi_cache.glsl's; the ray scene's meshes (its LOD) are the geometry; past --rtgi-census-most samples (12 M)
// a pass's samples are a random share of its full set, each standing for the rest's area.
internal sealed partial class SponzaLoop
{
    private bool rtgiCensus;
    private string rtgiCensusOut = "census";
    // --rtgi-census-cell-share F: the cells counted over a random share of the triangle runs and scaled up by 1 / F
    // (1: all). The jungle's count, whole, outgrew a 16 GB machine.
    private float rtgiCensusCellShare = 1f;
    private const float CensusReach = 64f, CensusNear = 0.25f, CensusLift = 0.01f;
    private const int CensusRays = 64;
    // The size field's passes (m between samples), coarse first; a pass below refines where the one above found a size
    // under CensusRefineFactor of its own spacing (or buried): its samples too far apart for the cells there.
    private static readonly float[] CensusPassSpacing = { 2f, 0.5f, 0.25f };
    private const float CensusRefineFactor = 2f;
    // A side over the void: more than CensusVoidShare of its rays escaped with a direction below -CensusVoidDown in y.
    private const float CensusVoidDown = 0.1f, CensusVoidShare = 0.5f;
    // A side buried: more than this share of its rays met back faces (--rtgi-census-buried-share F; 0.3). At 0.5 the
    // inner back sides of crevices' wall shells, seeing the floor running under them for half their rays, stayed live (cells
    // inside the walls: 73.5k -> 84.0k); at 0.3 crevices matches the one-sided count (2026-10-30).
    private static float CensusBuriedShare = 0.3f;
    // A sample on a point's plane: off it by at most this (m) plus this share of its distance along it (CensusGrid).
    private const float CensusPlaneOff = 0.02f, CensusPlaneSlope = 0.3f;
    // The slice of a 60 Hz frame the cook and the settling would be given: 2 ms of 16.7.
    private const double CensusSlice = 2.0 / 16.7;
    // The per-item costs priced (bytes, rays), stated in the report.
    private const int CensusShipSizeField = 1;       // B a size-field sample (every pass's)
    private const int CensusShipCell = 2;            // B a cell: the chosen candidate + buried, its neighbour visibility (8 bits)
    private const int CensusNearCompact = 24;        // B a level-0 cell: the near transfer, compact
    private const int CensusNearToday = 256;         // B a level-0 cell: today's 32 per-ray links (8 B each)
    private const int CensusInsideRays = 16 * 8;     // rays a junction cell: the inside test, 16 candidates x 8 rays
    // A junction cell: its sample's nearest hit within this many of the cell's size, and never past CensusJunctionMost:
    // at 1.5 sizes alone every own-level cell from ~3 m up counted (anything met within metres).
    private const float CensusJunctionReach = 1.5f, CensusJunctionMost = 0.375f;
    // The cell count's pieces: big triangles split until no edge is longer than this many lattice steps.
    private const int CensusPieceLattices = 8;
    private const int CensusNeighbourRays = 8;       // rays a cell: its neighbours' visibility
    private const int CensusNearRays = 32;           // rays a level-0 cell: the near transfer (optional)
    // The cache's GPU bytes a slot today (SponzaLoop.Rtgi.cs RecordRtgi): keys 4, accum 64 (RTGI_ACC 16 words), direction
    // 16, confidence 16, hit-inv 4, record 32, seq 4, stands 4, resolved 32, moments 48, stamps 8, cells 16, filtered 16
    // (the world volume's cell visibility, 8, went with it, 2026-10-30).
    private static readonly (string Name, int Bytes)[] CensusSlotBytes =
    {
        ("keys", 4), ("accum", 64), ("direction", 16), ("confidence", 16), ("hit-inv", 4), ("record", 32), ("seq", 4),
        ("stands", 4), ("resolved", 32), ("moments", 48), ("stamps", 8), ("cells", 16), ("filtered", 16),
    };
    // The proposed compact resident record (bits): what the readout, the hit lookups and the blend read every frame,
    // and the history that must outlive a training frame. The training's scratch -- the frame's accumulator (64 B), the
    // full moments (48 B), the dirty-list stamps -- in a transient pool of CensusPoolSlots slots, a cell taking one
    // when trained and giving it back at its resolve (the slot index in the resident record's flags word).
    private static readonly (string Name, int Bits)[] CensusCompactBits =
    {
        ("key: coords at level 3 x 18, level 4, side 3, spare 3", 64),
        ("shown light rgb (the blend's), fp16 x 3", 48), ("sky visibility, fp16", 16),
        ("resolved irradiance rgb (the running mean), fp16 x 3", 48), ("sun visibility, fp16", 16),
        ("fast mean rgb (change test), fp16 x 3", 48), ("luminance second moment, fp16", 16),
        ("irradiance direction, octahedral 2 x 8", 16), ("direction length, unorm 8", 8), ("near visibility, unorm 8", 8),
        ("inverse hit distance, fp16", 16), ("relative error, fp16", 16),
        ("samples held, fp16", 16), ("sun samples held, fp16", 16),
        ("parent weight (stands), fp16", 16), ("frames since trained, u16", 16),
        ("last trained frame, u24 + sun epoch u8", 32),
        ("far light's share, unorm 8", 8), ("done + flags, 8", 8), ("blue-noise sequence, u8", 8), ("cooked: candidate + buried, neighbour visibility", 16),
        ("transient pool slot (or none), u20 + spare 4", 24), ("spare", 32),
    };
    private static int CensusCompactBytes => CensusCompactBits.Sum(f => f.Bits) / 8;
    private static readonly (string Name, int Bytes)[] CensusPoolBytes =
    {
        ("accum (the frame's samples, RTGI_ACC 16 words)", 64), ("moments, full", 48), ("dirty stamps", 8), ("owner cell (back-pointer)", 4), ("spare", 4),
    };
    private const int CensusPoolSlots = 262_144;

    private Task<string>? rtgiCensusReport;

    private void ParseRtgiCensus(AppArgs args)
    {
        rtgiCensus = args.Flag("rtgi-census");
        if (args.String("rtgi-census-out") is { Length: > 0 } dir) rtgiCensusOut = dir;
        if (args.Long("rtgi-census-most") is { } most) rtgiCookMost = Math.Max(65536, most);
        if (args.Float("rtgi-census-buried-share") is { } buriedShare) CensusBuriedShare = Math.Clamp(buriedShare, 0.05f, 1f);
        if (args.Float("rtgi-census-cell-share") is { } cellShare) rtgiCensusCellShare = Math.Clamp(cellShare, 0.001f, 1f);
    }

    // The cook's cells stage done under --rtgi-census: the report from them on a worker; the demo closes once written.
    private void StartCensusReport(CookCells cells)
    {
        var geom = cookGeometry!;
        var passes = cookPasses.ToArray();
        var name = scene.Name;
        var dir = rtgiCensusOut;
        var dynamic = cookDynamic;
        var cell = rtgiCell;
        var start = scene.StartPosition;
        DebugProgress.Begin("rtgi census: report", 0);
        rtgiCensusReport = Task.Run(() => WriteCensusReport(name, dir, geom, passes, dynamic, cell, start, cells));
    }

    // Each frame under --rtgi-census: the report written, logged, and the demo closed.
    private void TickRtgiCensus()
    {
        if (rtgiCensusReport is not { IsCompleted: true } report) return;
        rtgiCensusReport = null;
        DebugProgress.End("rtgi census: report");
        Console.WriteLine(report.IsFaulted ? $"[VulkanSponza] rtgi census: failed: {report.Exception}" : report.Result);
        host.RequestClose();
    }

    // The census's geometry: the static instances, per instance whether its material is alpha-tested (and, for the
    // report, double-sided), the totals, and the GPU's per-placement alpha-tested bits.
    private sealed record CensusGeometry(
        RayQueryScene.Instance[] Instances, bool[] Alpha, bool[] Flagged,
        long Triangles, long Degenerate, double Area, Vector3 Min, Vector3 Max,
        (long Tris, double Area) ByFlag, (long Tris, double Area) ByAlpha, uint[] SidedBits, double Ms);

    // The triangles in runs of at most `run`, the workers' items (one mesh may be most of a scene).
    private static (int Inst, int First, int Count)[] CensusWork(RayQueryScene.Instance[] instances, int run = 65536)
    {
        var work = new List<(int, int, int)>();
        for (var i = 0; i < instances.Length; i++)
        {
            var tris = instances[i].Mesh.TriangleCount;
            for (var f = 0; f < tris; f += run) work.Add((i, f, Math.Min(run, tris - f)));
        }
        return work.ToArray();
    }

    private static (Vector3 A, Vector3 B, Vector3 C) CensusTriangle(in RayQueryScene.Instance inst, int t)
    {
        var pos = inst.Mesh.Positions;
        var idx = inst.Mesh.Indices;
        return (Vector3.Transform(pos[idx[3 * t]], inst.World), Vector3.Transform(pos[idx[3 * t + 1]], inst.World),
            Vector3.Transform(pos[idx[3 * t + 2]], inst.World));
    }

    private static CensusGeometry BuildCensusGeometry(RayQueryScene.Instance[] placements, int[] kept, bool[] alpha, bool[] flagged)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var instances = kept.Select(i => placements[i]).ToArray();
        // The GPU's bits: per placement its first bit (and one past the last placement's), then a bit a triangle.
        var bases = new uint[placements.Length + 1];
        for (var p = 0; p < placements.Length; p++) bases[p + 1] = bases[p] + (uint)placements[p].Mesh.TriangleCount;
        var words = new uint[(bases[^1] + 31) / 32];
        Parallel.For(0, placements.Length, p =>
        {
            if (!alpha[p]) return;
            for (var t = 0; t < placements[p].Mesh.TriangleCount; t++)
            {
                var b = bases[p] + (uint)t;
                Interlocked.Or(ref words[b >> 5], 1u << (int)(b & 31));
            }
        });
        var bits = new uint[bases.Length + words.Length];
        bases.CopyTo(bits, 0);
        words.CopyTo(bits, bases.Length);

        var instAlpha = kept.Select(i => alpha[i]).ToArray();
        var instFlag = kept.Select(i => flagged[i]).ToArray();
        long triangles = 0, degenerate = 0;
        double area = 0;
        var tallies = new (long Tris, double Area)[2];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var gate = new object();
        var work = CensusWork(instances);
        Parallel.For(0, work.Length, w =>
        {
            var item = work[w];
            long tris = 0, degen = 0;
            double a = 0;
            var local = new (long Tris, double Area)[2];
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);
            var inst = instances[item.Inst];
            for (var t = item.First; t < item.First + item.Count; t++)
            {
                var (pa, pb, pc) = CensusTriangle(inst, t);
                tris++;
                lo = Vector3.Min(lo, Vector3.Min(pa, Vector3.Min(pb, pc)));
                hi = Vector3.Max(hi, Vector3.Max(pa, Vector3.Max(pb, pc)));
                var len = Vector3.Cross(pb - pa, pc - pa).Length();
                if (!(len > 1e-12f)) { degen++; continue; }
                var ta = 0.5 * len;
                a += ta;
                if (instFlag[item.Inst]) { local[0].Tris++; local[0].Area += ta; }
                if (instAlpha[item.Inst]) { local[1].Tris++; local[1].Area += ta; }
            }
            lock (gate)
            {
                triangles += tris; degenerate += degen; area += a;
                for (var k = 0; k < 2; k++) { tallies[k].Tris += local[k].Tris; tallies[k].Area += local[k].Area; }
                min = Vector3.Min(min, lo); max = Vector3.Max(max, hi);
            }
        });
        return new CensusGeometry(instances, instAlpha, instFlag, triangles, degenerate, area, min, max,
            tallies[0], tallies[1], bits, clock.Elapsed.TotalMilliseconds);
    }

    // The area sampling's walk over a run of triangles (or pieces): `area` added (a side's), the samples it drops --
    // each where the running sum crosses the next spacing^2, from an offset of the run's own.
    private struct CensusAreaWalk
    {
        private double sum, next;
        private readonly double step;
        public CensusAreaWalk(uint seed, float spacing)
        {
            step = (double)spacing * spacing;
            sum = 0;
            next = NearHash(seed) / 4294967296.0 * step;
        }
        public int Add(double area)
        {
            sum += area;
            var n = 0;
            while (sum >= next) { n++; next += step; }
            return n;
        }
    }

    // A uniform random point of the triangle from two hashes.
    private static Vector3 CensusPointIn(Vector3 a, Vector3 b, Vector3 c, uint h)
    {
        var r1 = NearHash(h) / 4294967296f;
        var r2 = NearHash(h ^ 0x68E31DA4u) / 4294967296f;
        var s = MathF.Sqrt(r1);
        return a * (1f - s) + b * (s * (1f - r2)) + c * (s * r2);
    }

    private static uint CensusOctPack(Vector3 n)
    {
        var s = MathF.Abs(n.X) + MathF.Abs(n.Y) + MathF.Abs(n.Z);
        float x = n.X / s, y = n.Y / s;
        if (n.Z < 0f)
        {
            var ox = (1f - MathF.Abs(y)) * (x >= 0f ? 1f : -1f);
            var oy = (1f - MathF.Abs(x)) * (y >= 0f ? 1f : -1f);
            x = ox; y = oy;
        }
        var qx = (uint)(int)MathF.Round(Math.Clamp(x, -1f, 1f) * 32767f) & 0xFFFFu;
        var qy = (uint)(int)MathF.Round(Math.Clamp(y, -1f, 1f) * 32767f) & 0xFFFFu;
        return qx | (qy << 16);
    }

    private static float CensusLongest(Vector3 a, Vector3 b, Vector3 c) =>
        MathF.Sqrt(MathF.Max((b - a).LengthSquared(), MathF.Max((c - b).LengthSquared(), (a - c).LengthSquared())));

    // The triangle halved across its longest edge.
    private static ((Vector3, Vector3, Vector3), (Vector3, Vector3, Vector3)) CensusBisect(Vector3 a, Vector3 b, Vector3 c)
    {
        float ab = (b - a).LengthSquared(), bc = (c - b).LengthSquared(), ca = (a - c).LengthSquared();
        if (ab >= bc && ab >= ca) { var m = (a + b) * 0.5f; return ((a, m, c), (m, b, c)); }
        if (bc >= ca) { var m = (b + c) * 0.5f; return ((a, b, m), (a, m, c)); }
        var k = (c + a) * 0.5f;
        return ((a, b, k), (k, b, c));
    }

    // A pass of the size field: its samples (for the GPU, and what the CPU reads), then, traced, each sample's results.
    private sealed class CensusPass
    {
        public int Index;
        public float Spacing;
        public long Full;
        public double Keep, Ms;
        public Vector4[] Gpu = Array.Empty<Vector4>();
        public Vector3[] Pos = Array.Empty<Vector3>();
        public byte[] Side = Array.Empty<byte>();
        // Its side's normal (the cook's smoothing keeps to a sample's plane).
        public Vector3[] Normal = Array.Empty<Vector3>();
        // Whether its triangle is alpha-tested (both sides kept whatever their samples say).
        public bool[] Alpha = Array.Empty<bool>();
        // The area each stands for (its spacing^2 over the share traced).
        public float Area;
        public uint[] Results = Array.Empty<uint>();
        public float[] Size = Array.Empty<float>(), Nearest = Array.Empty<float>();
        // Buried, over the void, and dropped (either, but not alpha-tested).
        public bool[] Buried = Array.Empty<bool>(), Void = Array.Empty<bool>(), Dead = Array.Empty<bool>(), Refine = Array.Empty<bool>();
        public CensusGrid? Grid;
        public double GpuMs, WallMs;
        public int Frames;
        public long Bad;
        // The spacing of the samples traced.
        public float Effective => Spacing / MathF.Sqrt((float)Math.Min(1.0, Keep));
    }

    // A pass's samples: by area over every triangle (the first), or over the pieces of them the passes above refine.
    private static CensusPass BuildCensusPass(CensusGeometry geom, int index, IReadOnlyList<CensusPass> above, long most)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var spacing = CensusPassSpacing[index];
        var instances = geom.Instances;
        var work = CensusWork(instances);
        // The share traced from the area it will cover: all (both sides of the two-sided), or the last pass's refined.
        double expected;
        if (above.Count == 0) expected = 2 * geom.Area / (spacing * spacing);
        else
        {
            var last = above[^1];
            expected = last.Refine.Count(r => r) * (double)last.Area / (spacing * spacing);
        }
        var keep = expected > most ? most / expected : 1.0;
        var keepBits = keep >= 1.0 ? uint.MaxValue : (uint)(keep * uint.MaxValue);
        var lists = new List<(Vector3 P, Vector3 N, byte Side, bool Alpha)>[work.Length];
        var fulls = new long[work.Length];
        Parallel.For(0, work.Length, w =>
        {
            var item = work[w];
            var inst = instances[item.Inst];
            var list = new List<(Vector3, Vector3, byte, bool)>();
            var walk = new CensusAreaWalk((uint)w * 0x9E3779B1u + 0x51u + (uint)index * 0x2545F491u, spacing);
            var alpha = geom.Alpha[item.Inst];
            var dropped = 0u;
            long full = 0;
            var stack = new Stack<(Vector3 A, Vector3 B, Vector3 C, int Q)>();
            for (var t = item.First; t < item.First + item.Count; t++)
            {
                var (ta, tb, tc) = CensusTriangle(inst, t);
                var cross = Vector3.Cross(tb - ta, tc - ta);
                var len = cross.Length();
                if (!(len > 1e-12f)) continue;
                for (var face = 0; face < 2; face++)
                {
                    var nrm = face == 0 ? cross / len : -cross / len;
                    var side = NearSide(nrm);
                    stack.Push((ta, tb, tc, 0));
                    while (stack.TryPop(out var piece))
                    {
                        var (a, b, c, q) = piece;
                        if (q < above.Count)
                        {
                            // Gated by the pass above: in pieces no longer than twice its spacing, each kept where the
                            // nearest of its samples (to the piece's middle) asked for more.
                            var gate = above[q];
                            if (CensusLongest(a, b, c) > 2f * gate.Spacing)
                            {
                                var (h0, h1) = CensusBisect(a, b, c);
                                stack.Push((h0.Item1, h0.Item2, h0.Item3, q));
                                stack.Push((h1.Item1, h1.Item2, h1.Item3, q));
                                continue;
                            }
                            var (j, _) = gate.Grid!.Nearest((a + b + c) / 3f, side, 2, sameOnly: true, normal: nrm);
                            if (j < 0 || gate.Refine[j]) stack.Push((a, b, c, q + 1));
                            continue;
                        }
                        var n = walk.Add(0.5 * Vector3.Cross(b - a, c - a).Length());
                        for (var k = 0; k < n; k++, dropped++)
                        {
                            full++;
                            var h = NearHash((uint)w * 0x85EBCA77u ^ NearHash(dropped + 0x7F4A7C15u + (uint)index * 0x165667B1u));
                            if (keepBits != uint.MaxValue && NearHash(h ^ 0x2545F491u) > keepBits) continue;
                            list.Add((CensusPointIn(a, b, c, h), nrm, (byte)side, alpha));
                        }
                    }
                }
            }
            lists[w] = list;
            fulls[w] = full;
        });
        var count = lists.Sum(l => l.Count);
        var pass = new CensusPass
        {
            Index = index, Spacing = spacing, Full = fulls.Sum(), Keep = keep, Area = (float)(spacing * spacing / keep),
            Gpu = new Vector4[count], Pos = new Vector3[count], Side = new byte[count], Alpha = new bool[count], Normal = new Vector3[count],
        };
        var at = 0;
        foreach (var list in lists)
            foreach (var (p, nrm, side, alpha) in list)
            {
                pass.Gpu[at] = new Vector4(p, BitConverter.UInt32BitsToSingle(CensusOctPack(nrm)));
                pass.Pos[at] = p; pass.Side[at] = side; pass.Alpha[at] = alpha; pass.Normal[at] = nrm;
                at++;
            }
        pass.Ms = clock.Elapsed.TotalMilliseconds;
        return pass;
    }

    // A pass's results taken up: each sample's size, buried or over the void (its side dropped, unless alpha-tested),
    // nearest hit; whether it refines (a pass below it); its grid.
    private static void TakeCensusResults(CensusPass pass, float finest, float topSize)
    {
        var n = pass.Pos.Length;
        pass.Size = new float[n];
        pass.Nearest = new float[n];
        pass.Buried = new bool[n];
        pass.Void = new bool[n];
        pass.Dead = new bool[n];
        pass.Refine = new bool[n];
        var refines = pass.Index < CensusPassSpacing.Length - 1;
        long bad = 0;
        for (var i = 0; i < n; i++)
        {
            var invSum = BitConverter.UInt32BitsToSingle(pass.Results[4 * i]);
            var counts = pass.Results[4 * i + 1];
            int met = (int)(counts & 0xFF), back = (int)((counts >> 8) & 0xFF);
            pass.Nearest[i] = met > 0 ? BitConverter.UInt32BitsToSingle(pass.Results[4 * i + 2]) : float.PositiveInfinity;
            var harmonic = invSum > 0f ? CensusRays / invSum : float.PositiveInfinity;
            pass.Size[i] = Math.Clamp(0.1f * harmonic, finest, topSize);
            pass.Buried[i] = back > CensusBuriedShare * CensusRays;
            pass.Void[i] = (pass.Results[4 * i + 3] >> 16) > CensusVoidShare * CensusRays;
            pass.Dead[i] = !pass.Alpha[i] && (pass.Buried[i] || pass.Void[i]);
            pass.Refine[i] = refines && (pass.Buried[i] || pass.Size[i] < CensusRefineFactor * pass.Spacing);
            bad += pass.Results[4 * i + 3] & 0xFFFFu;
        }
        pass.Bad = bad;
        pass.Grid = new CensusGrid(pass.Pos, pass.Side, pass.Effective);
    }

    // A pass's samples' grid for the nearest: cells of its traced spacing, the samples sorted by cell.
    private sealed class CensusGrid
    {
        public readonly float Size;
        private readonly Vector3[] pos;
        private readonly byte[] side;
        private readonly int[] order;
        private readonly Dictionary<long, (int Start, int Count)> cells = new();

        public CensusGrid(Vector3[] pos, byte[] side, float size)
        {
            this.pos = pos; this.side = side; Size = size;
            var keys = new long[pos.Length];
            order = new int[pos.Length];
            for (var i = 0; i < pos.Length; i++) { keys[i] = KeyOf(pos[i]); order[i] = i; }
            Array.Sort(keys, order);
            for (var i = 0; i < keys.Length;)
            {
                var j = i;
                while (j < keys.Length && keys[j] == keys[i]) j++;
                cells[keys[i]] = (i, j - i);
                i = j;
            }
        }

        // The weighted mean of values[offset + i] over the live samples facing wantSide within radius of q on its plane
        // (as Nearest's), each weighed 1 - its distance over the radius: the cook's smoothing of the size field.
        public (double Sum, double Weight) Average(Vector3 q, uint wantSide, Vector3 normal, float radius, float[] values, bool[] dead, int offset)
        {
            int cx = (int)MathF.Floor(q.X / Size), cy = (int)MathF.Floor(q.Y / Size), cz = (int)MathF.Floor(q.Z / Size);
            var reach = Math.Max(1, (int)MathF.Ceiling(radius / Size));
            double sum = 0, weight = 0;
            var r2 = radius * radius;
            for (var dz = -reach; dz <= reach; dz++)
            for (var dy = -reach; dy <= reach; dy++)
            for (var dx = -reach; dx <= reach; dx++)
            {
                if (!cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var run)) continue;
                for (var k = run.Start; k < run.Start + run.Count; k++)
                {
                    var i = order[k];
                    if (side[i] != wantSide || dead[offset + i]) continue;
                    var d2 = Vector3.DistanceSquared(pos[i], q);
                    if (d2 > r2) continue;
                    var off = MathF.Abs(Vector3.Dot(pos[i] - q, normal));
                    if (off > CensusPlaneOff + CensusPlaneSlope * MathF.Sqrt(MathF.Max(d2 - off * off, 0f))) continue;
                    var w = 1.0 - MathF.Sqrt(d2) / radius + 1e-3;
                    sum += w * values[offset + i];
                    weight += w;
                }
            }
            return (sum, weight);
        }

        private long KeyOf(Vector3 p) => Key((int)MathF.Floor(p.X / Size), (int)MathF.Floor(p.Y / Size), (int)MathF.Floor(p.Z / Size));
        private static long Key(int x, int y, int z) => ((long)(x + (1 << 20)) << 42) | ((long)(y + (1 << 20)) << 21) | (long)(z + (1 << 20));

        // The nearest sample facing the same side, else (unless `sameOnly`) the nearest of any, searched outward to `most`
        // cells; and its squared distance (-1: none). Given the point's normal, only samples on its plane: off it by at
        // most CensusPlaneOff + CensusPlaneSlope of their distance along it. A side is its normal's dominant axis, so a
        // thin wall's back side and its other face are one side a few centimetres apart: the dead back side took the live
        // face's samples, and the crevices testbed's walls grew cells inside them.
        public (int Index, float D2) Nearest(Vector3 q, uint wantSide, int most, bool sameOnly = false, Vector3 normal = default)
        {
            int cx = (int)MathF.Floor(q.X / Size), cy = (int)MathF.Floor(q.Y / Size), cz = (int)MathF.Floor(q.Z / Size);
            foreach (var reach in CensusReaches)
            {
                if (reach > most) break;
                int same = -1, any = -1;
                float bestSame = float.MaxValue, bestAny = float.MaxValue;
                for (var dz = -reach; dz <= reach; dz++)
                for (var dy = -reach; dy <= reach; dy++)
                for (var dx = -reach; dx <= reach; dx++)
                {
                    if (!cells.TryGetValue(Key(cx + dx, cy + dy, cz + dz), out var run)) continue;
                    for (var k = run.Start; k < run.Start + run.Count; k++)
                    {
                        var i = order[k];
                        var d2 = Vector3.DistanceSquared(pos[i], q);
                        if (normal != default)
                        {
                            var off = MathF.Abs(Vector3.Dot(pos[i] - q, normal));
                            if (off > CensusPlaneOff + CensusPlaneSlope * MathF.Sqrt(MathF.Max(d2 - off * off, 0f))) continue;
                        }
                        if (d2 < bestAny) { bestAny = d2; any = i; }
                        if (side[i] == wantSide && d2 < bestSame) { bestSame = d2; same = i; }
                    }
                }
                if (same >= 0) return (same, bestSame);
                if (any >= 0 && !sameOnly) return (any, bestAny);
            }
            return (-1, float.PositiveInfinity);
        }
    }

    private static readonly int[] CensusReaches = { 1, 2, 4, 8 };

    // A cell's key: its coordinates at its level (x 19 bits, y and z 20), its side, and bit 0 a flag (a junction in the
    // own-level lists, own-level in the all-levels ones): sorted, a cell's two flag values lie side by side.
    private static long CensusCellKey(int x, int y, int z, uint side) =>
        (((long)(x + (1 << 18)) & 0x7FFFF) << 44) | (((long)(y + (1 << 19)) & 0xFFFFF) << 24) | (((long)(z + (1 << 19)) & 0xFFFFF) << 4) | ((long)side << 1);

    private static (int X, int Y, int Z, uint Side) CensusCellUnkey(long k) =>
        ((int)((k >> 44) & 0x7FFFF) - (1 << 18), (int)((k >> 24) & 0xFFFFF) - (1 << 19), (int)((k >> 4) & 0xFFFFF) - (1 << 19), (uint)((k >> 1) & 7));

    private static long CensusParent(long k)
    {
        var (x, y, z, side) = CensusCellUnkey(k);
        return CensusCellKey(x >> 1, y >> 1, z >> 1, side);
    }

    // Sorted, each cell once, its flag the OR of its copies'.
    private static long[] CensusSortUnique(long[] keys, int length)
    {
        Array.Sort(keys, 0, length);
        var at = 0;
        for (var i = 0; i < length; i++)
        {
            if (at > 0 && (keys[at - 1] >> 1) == (keys[i] >> 1)) keys[at - 1] |= keys[i] & 1;
            else keys[at++] = keys[i];
        }
        Array.Resize(ref keys, at);
        return keys;
    }

    private sealed class CensusChunk
    {
        public double Cells, Own, Fine, Level0, Junction, Coarse;
    }

    private static string WriteCensusReport(string sceneName, string dir, CensusGeometry census, CensusPass[] passes,
        int dynamicCount, float finest, Vector3 start, CookCells cooked)
    {
        var cellShare = cooked.Share;
        var instances = census.Instances;
        var levels = RtgiTopLevel + 1;
        var topSize = finest * MathF.Pow(2f, RtgiTopLevel);
        int LevelOf(float size) => Math.Clamp((int)MathF.Floor(MathF.Log2(size / finest) + 1e-4f), 0, RtgiTopLevel);
        float SizeOfLevel(int l) => finest * MathF.Pow(2f, l);
        var lastPass = passes.Length - 1;

        // Every pass's samples as one list (offset by pass), and the size field's area statistics: a sample stands for
        // its area where nothing below refined it (the last pass's always).
        var offsets = new int[passes.Length + 1];
        for (var p = 0; p < passes.Length; p++) offsets[p + 1] = offsets[p] + passes[p].Pos.Length;
        var total = offsets[^1];
        var size = new float[total];
        var dead = new bool[total];
        var nearest = new float[total];
        var passOf = new byte[total];
        double areaBuried = 0, areaVoid = 0, areaAlphaKept = 0, raysVoid = 0, raysMet = 0, raysBack = 0, raysSidedBack = 0, raysNear = 0, sampleWeight = 0;
        var areaAtLevel = new double[levels];
        var harmonicBands = new[] { 0.25, 0.5, 1, 2, 4, 8, 16, 32, 64, 128, 256, 512, double.MaxValue };
        var areaInBand = new double[harmonicBands.Length];
        long bad = 0;
        for (var p = 0; p < passes.Length; p++)
        {
            var pass = passes[p];
            bad += pass.Bad;
            for (var i = 0; i < pass.Pos.Length; i++)
            {
                var g = offsets[p] + i;
                size[g] = pass.Size[i]; dead[g] = pass.Dead[i]; nearest[g] = pass.Nearest[i]; passOf[g] = (byte)p;
                if (pass.Refine[i]) continue;
                var invSum = BitConverter.UInt32BitsToSingle(pass.Results[4 * i]);
                var counts = pass.Results[4 * i + 1];
                int met = (int)(counts & 0xFF), back = (int)((counts >> 8) & 0xFF), near = (int)((counts >> 16) & 0xFF), sidedBack = (int)(counts >> 24);
                var a = pass.Area;
                raysMet += met * (double)a; raysBack += back * (double)a; raysSidedBack += sidedBack * (double)a; raysNear += near * (double)a; sampleWeight += a;
                raysVoid += (pass.Results[4 * i + 3] >> 16) * (double)a;
                if (pass.Alpha[i] && (pass.Buried[i] || pass.Void[i])) areaAlphaKept += a;
                if (pass.Dead[i]) { if (pass.Buried[i]) areaBuried += a; else areaVoid += a; continue; }
                areaAtLevel[LevelOf(pass.Size[i])] += a;
                var harmonic = invSum > 0f ? CensusRays / invSum : float.PositiveInfinity;
                var b = 0;
                while (b < harmonicBands.Length - 1 && harmonic > harmonicBands[b]) b++;
                areaInBand[b] += a;
            }
        }
        // The cells: the cook's (SponzaLoop.RtgiCook.cs's cells stage) -- every surface point's cell at the level the cooked
        // size table gives it, and every level above; own-level ones flagged junction where the point's sample met a
        // surface within min(CensusJunctionReach cell sizes, CensusJunctionMost).
        var all = cooked.All;
        var own = cooked.Own;
        var scale = 1.0 / cellShare;
        long points = cooked.Counts.Points, pointsBuried = cooked.Counts.Buried, latticeFilled = cooked.Counts.Filled, latticeNone = cooked.Counts.None;
        var enumerateMs = cooked.TableMs + cooked.EnumerateMs;

        // The totals priced.
        var cellsAt = all.Select(s => (long)(s.Length * scale)).ToArray();
        var ownAt = own.Select(s => (long)(s.Length * scale)).ToArray();
        var junctionAt = own.Select(s => (long)(s.LongCount(k => (k & 1) != 0) * scale)).ToArray();
        long cells = cellsAt.Sum(), ownCells = ownAt.Sum(), level0 = cellsAt[0], fine = cellsAt[0] + cellsAt[1], junctions = junctionAt.Sum();
        long sizeSamples = passes.Sum(p => p.Full);
        var slotBytes = CensusSlotBytes.Sum(s => s.Bytes);
        var compactBytes = CensusCompactBytes;
        var poolBytes = CensusPoolBytes.Sum(s => s.Bytes);
        long pool = (long)CensusPoolSlots * poolBytes;
        long traced = passes.Sum(p => (long)p.Pos.Length * CensusRays);
        var gpuMs = passes.Sum(p => p.GpuMs);
        var wallMs = passes.Sum(p => p.WallMs);
        var rate = gpuMs > 0 ? traced / (gpuMs / 1000.0) : traced / (wallMs / 1000.0);
        var wallRate = traced / (wallMs / 1000.0);
        double Full(double rays) => rays / rate;
        double Sliced(double rays) => rays / (rate * CensusSlice);
        const double Mb = 1048576.0;
        long shipped = sizeSamples * CensusShipSizeField + cells * CensusShipCell;
        long nearCompact = level0 * CensusNearCompact, nearToday = level0 * CensusNearToday;
        long runtime = cells * slotBytes, runtimeCompact = cells * compactBytes;
        long cookSize = sizeSamples * CensusRays, cookInside = junctions * CensusInsideRays, cookNeighbours = cells * CensusNeighbourRays;
        long cookRays = cookSize + cookInside + cookNeighbours, nearRays = level0 * CensusNearRays;
        long settle8 = ownCells * 8, settle64 = ownCells * 64;

        // The chunks: each cell by its centre, each size sample by its point; resident, those whose box lies within R of
        // the start camera (cells bigger than the chunk always).
        var chunkSizes = new[] { 16f, 32f, 64f };
        var reaches = new[] { 32f, 64f };
        var chunkStats = new Dictionary<string, object>[chunkSizes.Length];
        Parallel.For(0, chunkSizes.Length, c =>
        {
            var cs = chunkSizes[c];
            var chunks = new Dictionary<(int, int, int), CensusChunk>();
            CensusChunk At(Vector3 p)
            {
                var key = ((int)MathF.Floor(p.X / cs), (int)MathF.Floor(p.Y / cs), (int)MathF.Floor(p.Z / cs));
                if (!chunks.TryGetValue(key, out var e)) chunks[key] = e = new CensusChunk();
                return e;
            }
            // Cells bigger than the chunk (parents only, a few) belong to no one chunk: counted apart.
            long global = 0;
            for (var l = 0; l < levels; l++)
            {
                var cellSize = SizeOfLevel(l);
                if (cellSize > cs) { global += (long)(all[l].Length * scale); continue; }
                // The level's own cells walked beside its cells (both sorted) for their junction flags.
                var ownKeys = own[l];
                var o = 0;
                foreach (var k in all[l])
                {
                    var (x, y, z, side) = CensusCellUnkey(k);
                    while (o < ownKeys.Length && (ownKeys[o] >> 1) < (k >> 1)) o++;
                    var isJunction = o < ownKeys.Length && (ownKeys[o] >> 1) == (k >> 1) && (ownKeys[o] & 1) != 0;
                    var e = At((new Vector3(x, y, z) + new Vector3(0.5f)) * cellSize + NearGridOrigin(side));
                    e.Cells += scale;
                    if (l <= 1) e.Fine += scale;
                    if (l == 0) e.Level0 += scale;
                    if ((k & 1) != 0) e.Own += scale;
                    if (isJunction) e.Junction += scale;
                }
            }
            foreach (var pass in passes)
            {
                var perSample = pass.Keep > 0 ? 1.0 / pass.Keep : 1.0;
                foreach (var p in pass.Pos) At(p).Coarse += perSample;
            }
            var list = chunks.Values.ToArray();
            double Mean(Func<CensusChunk, double> f) => list.Length > 0 ? list.Average(f) : 0;
            double Max(Func<CensusChunk, double> f) => list.Length > 0 ? list.Max(f) : 0;
            double Ship(CensusChunk e) => e.Coarse * CensusShipSizeField + e.Cells * CensusShipCell;
            double Cook(CensusChunk e) => e.Coarse * CensusRays + e.Junction * CensusInsideRays + e.Cells * CensusNeighbourRays;
            var stats = new Dictionary<string, object>
            {
                ["chunk_m"] = cs,
                ["chunks"] = list.Length, ["cells_bigger_than_chunk"] = global,
                ["cells_mean"] = Mean(e => e.Cells), ["cells_max"] = Max(e => e.Cells),
                ["own_cells_mean"] = Mean(e => e.Own), ["own_cells_max"] = Max(e => e.Own),
                ["junction_cells_mean"] = Mean(e => e.Junction), ["junction_cells_max"] = Max(e => e.Junction),
                ["shipped_kb_mean"] = Mean(Ship) / 1024, ["shipped_kb_max"] = Max(Ship) / 1024,
                ["runtime_today_mb_mean"] = Mean(e => e.Cells) * slotBytes / Mb, ["runtime_today_mb_max"] = Max(e => e.Cells) * slotBytes / Mb,
                ["runtime_compact_mb_mean"] = Mean(e => e.Cells) * compactBytes / Mb, ["runtime_compact_mb_max"] = Max(e => e.Cells) * compactBytes / Mb,
                ["cook_ms_slice_mean"] = Mean(e => Sliced(Cook(e))) * 1000, ["cook_ms_slice_max"] = Max(e => Sliced(Cook(e))) * 1000,
                ["settle8_ms_slice_mean"] = Mean(e => Sliced(e.Own * 8.0)) * 1000, ["settle8_ms_slice_max"] = Max(e => Sliced(e.Own * 8.0)) * 1000,
                ["settle64_ms_slice_mean"] = Mean(e => Sliced(e.Own * 64.0)) * 1000, ["settle64_ms_slice_max"] = Max(e => Sliced(e.Own * 64.0)) * 1000,
            };
            foreach (var reach in reaches)
            {
                long resident = global, residentChunks = 0;
                foreach (var ((x, y, z), e) in chunks)
                {
                    var lo = new Vector3(x, y, z) * cs;
                    var d = Vector3.Distance(Vector3.Clamp(start, lo, lo + new Vector3(cs)), start);
                    if (d > reach) continue;
                    resident += (long)e.Cells;
                    residentChunks++;
                }
                stats[$"resident_r{reach:0}_chunks"] = residentChunks;
                stats[$"resident_r{reach:0}_cells"] = resident;
                stats[$"resident_r{reach:0}_compact_plus_pool_mb"] = (resident * compactBytes + pool) / Mb;
            }
            chunkStats[c] = stats;
        });

        var extent = census.Max - census.Min;
        var share = (double v) => v / Math.Max(sampleWeight, 1e-9);
        object Tally((long Tris, double Area) t) => new Dictionary<string, object> { ["triangles"] = t.Tris, ["area_m2"] = t.Area };
        var passStats = passes.Select(p => (object)new Dictionary<string, object>
        {
            ["spacing_m"] = p.Spacing, ["traced_spacing_m"] = p.Effective, ["samples"] = p.Full, ["samples_traced"] = p.Pos.Length,
            ["traced_share"] = p.Keep, ["size_field_rays"] = p.Full * CensusRays, ["rays_traced"] = (long)p.Pos.Length * CensusRays,
            ["refined_share"] = p.Refine.Length > 0 ? p.Refine.Count(r => r) / (double)p.Refine.Length : 0,
            ["place_ms"] = p.Ms, ["trace_gpu_ms"] = p.GpuMs, ["trace_wall_ms"] = p.WallMs, ["trace_frames"] = p.Frames,
        }).ToArray();
        var report = new Dictionary<string, object>
        {
            ["scene"] = sceneName,
            ["geometry"] = new Dictionary<string, object>
            {
                ["instances"] = instances.Length, ["dynamic_instances_left_out"] = dynamicCount,
                ["triangles"] = census.Triangles, ["degenerate_triangles"] = census.Degenerate, ["area_m2"] = census.Area,
                ["sides"] = "both, each kept by its own samples (alpha-tested: both kept)",
                ["alpha_tested"] = Tally(census.ByAlpha), ["double_sided_flag_for_reference"] = Tally(census.ByFlag),
                ["geometry_ms"] = census.Ms,
                ["bounds_min"] = new[] { census.Min.X, census.Min.Y, census.Min.Z }, ["bounds_max"] = new[] { census.Max.X, census.Max.Y, census.Max.Z },
                ["extent_m"] = new[] { extent.X, extent.Y, extent.Z },
                ["start_camera"] = new[] { start.X, start.Y, start.Z },
            },
            ["coarse"] = new Dictionary<string, object>
            {
                ["sampling"] = "by area, coarse first", ["passes"] = passStats, ["refine_rule"] = $"buried, or size < {CensusRefineFactor} x the pass's spacing",
                ["rays_per_sample"] = CensusRays, ["reach_m"] = CensusReach, ["lift_m"] = CensusLift, ["size_cap_m"] = topSize,
                ["samples"] = sizeSamples, ["samples_traced"] = total, ["size_field_rays"] = cookSize,
                ["rays_traced"] = traced, ["trace_gpu_ms"] = gpuMs, ["trace_wall_ms"] = wallMs,
                ["rate_rays_per_s_gpu"] = rate, ["rate_rays_per_s_wall"] = wallRate, ["hits_with_bad_triangle_index"] = bad,
                ["area_buried_share"] = share(areaBuried),
                ["area_void_share"] = share(areaVoid),
                ["area_alpha_kept_share"] = share(areaAlphaKept),
                ["buried_rule"] = $"more than {CensusBuriedShare} of a side's rays met back faces (alpha-tested back faces as fronts)",
                ["void_rule"] = $"more than {CensusVoidShare} of a side's rays escaped with direction y < -{CensusVoidDown}",
                ["ray_void_share"] = share(raysVoid) / CensusRays,
                ["ray_met_share"] = share(raysMet) / CensusRays,
                ["ray_back_share"] = share(raysBack) / CensusRays,
                ["ray_alpha_back_share"] = share(raysSidedBack) / CensusRays,
                ["ray_within_0_25m_share"] = share(raysNear) / CensusRays,
                ["area_m2_by_level"] = areaAtLevel,
                ["harmonic_band_upper_m"] = harmonicBands.Select(b => b == double.MaxValue ? -1 : b).ToArray(), ["area_m2_by_harmonic_band"] = areaInBand,
            },
            ["cells"] = new Dictionary<string, object>
            {
                ["finest_m"] = finest, ["top_level"] = RtgiTopLevel, ["top_m"] = topSize,
                ["by_level"] = cellsAt, ["own_by_level"] = ownAt, ["junction_by_level"] = junctionAt,
                ["total"] = cells, ["own_total"] = ownCells, ["junction_total"] = junctions, ["level0_1"] = fine,
                ["cell_share"] = cellShare,
                ["points"] = points, ["points_buried_skipped"] = pointsBuried,
                ["lattice_points_filled_outward"] = latticeFilled, ["triangle_sides_without_sample"] = latticeNone,
                ["enumerate_ms"] = enumerateMs,
            },
            ["costs"] = new Dictionary<string, object>
            {
                ["ship_size_field_b_per_sample"] = CensusShipSizeField, ["ship_b_per_cell"] = CensusShipCell,
                ["near_compact_b_per_level0_cell"] = CensusNearCompact, ["near_today_b_per_level0_cell"] = CensusNearToday,
                ["runtime_b_per_slot"] = slotBytes, ["runtime_slot_breakdown"] = CensusSlotBytes.ToDictionary(s => s.Name, s => (object)s.Bytes),
                ["compact_b_per_cell"] = compactBytes, ["compact_layout_bits"] = CensusCompactBits.ToDictionary(f => f.Name, f => (object)f.Bits),
                ["pool_slots"] = CensusPoolSlots, ["pool_b_per_slot"] = poolBytes, ["pool_layout"] = CensusPoolBytes.ToDictionary(f => f.Name, f => (object)f.Bytes),
                ["rays_per_size_sample"] = CensusRays, ["inside_rays_per_junction_cell"] = CensusInsideRays,
                ["junction_rule"] = $"own-level cell whose point's sample met a surface within min({CensusJunctionReach} x the cell size, {CensusJunctionMost} m)",
                ["neighbour_rays_per_cell"] = CensusNeighbourRays, ["near_rays_per_level0_cell"] = CensusNearRays,
                ["settle_rays_per_sample"] = 1, ["slice_share"] = CensusSlice,
            },
            ["pricing"] = new Dictionary<string, object>
            {
                ["shipped_mb"] = shipped / Mb,
                ["near_transfer_compact_mb"] = nearCompact / Mb, ["near_transfer_today_mb"] = nearToday / Mb,
                ["runtime_gpu_mb"] = runtime / Mb, ["runtime_compact_all_mb"] = runtimeCompact / Mb, ["pool_mb"] = pool / Mb,
                ["cook_rays"] = cookRays, ["cook_rays_size_field"] = cookSize, ["cook_rays_inside"] = cookInside, ["cook_rays_neighbours"] = cookNeighbours,
                ["cook_s_full"] = Full(cookRays), ["cook_s_slice"] = Sliced(cookRays),
                ["near_rays"] = nearRays, ["near_s_full"] = Full(nearRays), ["near_s_slice"] = Sliced(nearRays),
                ["settle8_rays"] = settle8, ["settle8_s_full"] = Full(settle8), ["settle8_s_slice"] = Sliced(settle8),
                ["settle64_rays"] = settle64, ["settle64_s_full"] = Full(settle64), ["settle64_s_slice"] = Sliced(settle64),
            },
            ["chunks"] = chunkStats,
        };

        Directory.CreateDirectory(dir);
        var basePath = Path.Combine(dir, sceneName);
        using (var stream = File.Create(basePath + ".json"))
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            CensusJson(json, report);

        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        void Line(FormattableString s) => text.AppendLine(s.ToString(inv));
        Line($"RTGI census: {sceneName} ({DateTime.Now:yyyy-MM-dd HH:mm})");
        Line($"");
        Line($"Geometry: {instances.Length:N0} static instances ({dynamicCount} dynamic left out), {census.Triangles:N0} triangles ({census.Degenerate:N0} degenerate), {census.Area:N0} m2 (in {census.Ms / 1000:0.0} s)");
        Line($"  sides: both, each kept by its own samples; alpha-tested (both kept) {census.ByAlpha.Tris:N0} triangles / {census.ByAlpha.Area:N0} m2 (the double-sided flag's {census.ByFlag.Tris:N0} / {census.ByFlag.Area:N0} m2, not used)");
        Line($"  bounds ({census.Min.X:0.0}, {census.Min.Y:0.0}, {census.Min.Z:0.0}) .. ({census.Max.X:0.0}, {census.Max.Y:0.0}, {census.Max.Z:0.0}), extent {extent.X:0.0} x {extent.Y:0.0} x {extent.Z:0.0} m; start camera ({start.X:0.0}, {start.Y:0.0}, {start.Z:0.0})");
        Line($"");
        Line($"Size field: by area, coarse first ({string.Join(" -> ", passes.Select(p => p.Spacing.ToString("0.##", inv) + " m"))}; refined where buried or size < {CensusRefineFactor} x the pass's spacing), {CensusRays} cosine rays each to {CensusReach:0} m, lifted {CensusLift * 100:0} cm; size clamp(0.1 x harmonic, {finest * 100:0} cm, {topSize:0.0} m = level {RtgiTopLevel})");
        foreach (var p in passes)
            Line($"  pass {p.Index} at {p.Spacing:0.##} m: {p.Full:N0} samples ({p.Full * CensusRays / 1e6:0.0} M rays), {p.Pos.Length:N0} traced (share {p.Keep:0.####}, spacing {p.Effective:0.##} m), {100.0 * p.Refine.Count(r => r) / Math.Max(1, p.Refine.Length):0.0}% refined; GPU {p.GpuMs:0} ms, wall {p.WallMs / 1000:0.0} s over {p.Frames} frames");
        Line($"  all: {sizeSamples:N0} samples, {cookSize / 1e6:0.0} M size-field rays; traced {traced / 1e6:0.0} M rays, GPU {gpuMs:0} ms ({rate / 1e6:0} M rays/s), wall {wallMs / 1000:0.0} s ({wallRate / 1e6:0} M rays/s); {bad:N0} hits with a triangle index past their placement's");
        Line($"  by area, both sides: dropped buried {100 * share(areaBuried):0.0}% (> {CensusBuriedShare:P0} of rays on back faces), dropped over the void {100 * share(areaVoid):0.0}% (> {CensusVoidShare:P0} of rays escaping below y = -{CensusVoidDown}), alpha-tested kept despite either {100 * share(areaAlphaKept):0.0}%; rays met {100 * share(raysMet) / CensusRays:0.0}%, back faces {100 * share(raysBack) / CensusRays:0.0}%, alpha-tested back faces (as fronts) {100 * share(raysSidedBack) / CensusRays:0.0}%, into the void {100 * share(raysVoid) / CensusRays:0.0}%, within 25 cm {100 * share(raysNear) / CensusRays:0.0}%");
        text.Append("  live area by size level (m2):");
        for (var l = 0; l < levels; l++) text.Append(inv, $" L{l} {areaAtLevel[l]:N0}");
        text.AppendLine();
        text.Append("  live area by harmonic distance (m2):");
        for (var b = 0; b < harmonicBands.Length; b++) text.Append(inv, $" {(b == harmonicBands.Length - 1 ? ">512" : "<=" + harmonicBands[b].ToString(inv))} {areaInBand[b]:N0}");
        text.AppendLine();
        Line($"");
        if (cellShare < 1f) Line($"Cells counted over a random {cellShare:P0} of the triangle runs, scaled up by {scale:0.##} (the points and fills below are unscaled; chunk counts are per counted run, scaled).");
        Line($"Cells (enumerated in {enumerateMs / 1000:0.0} s, {points:N0} points, {pointsBuried:N0} on dropped sides skipped; {latticeFilled:N0} lattice points filled outward, {latticeNone:N0} triangle pieces with no sample within 8 coarse cells):");
        for (var l = 0; l < levels; l++)
            Line($"  L{l,-2} {SizeOfLevel(l),7:0.00} m  {cellsAt[l],14:N0} cells  {ownAt[l],14:N0} own-level  {junctionAt[l],12:N0} junction");
        Line($"  total {cells:N0}, own-level {ownCells:N0}, junction {junctions:N0} (L0+L1 cells {fine:N0})");
        Line($"");
        Line($"Costs: ship {CensusShipSizeField} B/size sample + {CensusShipCell} B/cell; near transfer {CensusNearCompact} B (compact) / {CensusNearToday} B (today) per L0 cell; runtime {slotBytes} B/slot today, {compactBytes} B compact + a {CensusPoolSlots:N0}-slot pool of {poolBytes} B");
        Line($"  rays: {CensusRays}/size sample, {CensusInsideRays}/junction cell (inside test; junction: nearest hit < min({CensusJunctionReach} x cell size, {CensusJunctionMost} m)), {CensusNeighbourRays}/cell (neighbours), {CensusNearRays}/L0 cell (near, optional); settle 1 ray/sample on own-level cells");
        Line($"  slot today: {string.Join(", ", CensusSlotBytes.Select(s => $"{s.Name} {s.Bytes}"))}");
        Line($"  compact record ({compactBytes} B): {string.Join("; ", CensusCompactBits.Select(f => $"{f.Name} [{f.Bits}]"))}");
        Line($"  pool slot ({poolBytes} B): {string.Join(", ", CensusPoolBytes.Select(f => $"{f.Name} {f.Bytes}"))}");
        Line($"");
        Line($"Pricing (rate {rate / 1e6:0} M rays/s; slice {CensusSlice * 100:0.0}%):");
        Line($"  shipped {shipped / Mb:0.00} MB; near transfer {nearCompact / Mb:0.00} MB compact / {nearToday / Mb:0.00} MB today");
        Line($"  runtime GPU: today's layout {runtime / Mb:0.0} MB; compact, all resident {runtimeCompact / Mb:0.0} MB (+ pool {pool / Mb:0} MB)");
        Line($"  cook {cookRays / 1e6:0.0} M rays (size {cookSize / 1e6:0.0} M, inside {cookInside / 1e6:0.0} M [all L0+L1: {fine * CensusInsideRays / 1e6:0.0} M], neighbours {cookNeighbours / 1e6:0.0} M): {Full(cookRays):0.00} s full, {Sliced(cookRays):0.0} s at 2 ms/frame");
        Line($"  near transfer {nearRays / 1e6:0.0} M rays: {Full(nearRays):0.00} s full, {Sliced(nearRays):0.0} s sliced");
        Line($"  settle 8: {settle8 / 1e6:0.0} M rays, {Full(settle8):0.00} s full, {Sliced(settle8):0.0} s sliced; settle 64: {settle64 / 1e6:0.0} M rays, {Full(settle64):0.00} s full, {Sliced(settle64):0.0} s sliced");
        Line($"");
        Line($"Chunks (by cell centre; times at the 2 ms slice; resident: chunks within R of the start camera, compact + pool):");
        foreach (var c in chunkStats)
        {
            Line($"  {c["chunk_m"],2} m: {c["chunks"],6} chunks ({c["cells_bigger_than_chunk"]} bigger cells apart); cells mean {c["cells_mean"]:N0} max {c["cells_max"]:N0}; junction mean {c["junction_cells_mean"]:N0} max {c["junction_cells_max"]:N0}; shipped KB mean {c["shipped_kb_mean"]:0.0} max {c["shipped_kb_max"]:0.0}; cook ms mean {c["cook_ms_slice_mean"]:0} max {c["cook_ms_slice_max"]:0}; settle8 ms mean {c["settle8_ms_slice_mean"]:0.0} max {c["settle8_ms_slice_max"]:0.0}; settle64 ms mean {c["settle64_ms_slice_mean"]:0} max {c["settle64_ms_slice_max"]:0}");
            Line($"        resident R 32 m: {c["resident_r32_chunks"]} chunks, {c["resident_r32_cells"]:N0} cells, {c["resident_r32_compact_plus_pool_mb"]:0.0} MB; R 64 m: {c["resident_r64_chunks"]} chunks, {c["resident_r64_cells"]:N0} cells, {c["resident_r64_compact_plus_pool_mb"]:0.0} MB");
        }
        File.WriteAllText(basePath + ".txt", text.ToString());

        return string.Create(inv,
            $"[VulkanSponza] rtgi census {sceneName}: {census.Triangles:N0} tris, {census.Area:N0} m2 a side, {sizeSamples:N0} size samples ({cookSize / 1e6:0.0} M rays), dropped buried {100 * share(areaBuried):0.0}% / void {100 * share(areaVoid):0.0}%, cells {cells:N0} (L0 {cellsAt[0]:N0}, own {ownCells:N0}, junction {junctions:N0}), shipped {shipped / Mb:0.0} MB, runtime {runtime / Mb:0} MB today / {runtimeCompact / Mb:0} MB compact, cook {Full(cookRays):0.0} s / {Sliced(cookRays):0} s sliced, settle64 {Full(settle64):0.0} s / {Sliced(settle64):0} s sliced, {rate / 1e6:0} M rays/s; written to {basePath}.json/.txt.");
    }

    private static void CensusJson(Utf8JsonWriter w, object? value)
    {
        switch (value)
        {
            case null: w.WriteNullValue(); break;
            case string s: w.WriteStringValue(s); break;
            case bool b: w.WriteBooleanValue(b); break;
            case int i: w.WriteNumberValue(i); break;
            case long l: w.WriteNumberValue(l); break;
            case float f: w.WriteNumberValue(float.IsFinite(f) ? f : 0f); break;
            case double d: w.WriteNumberValue(double.IsFinite(d) ? d : 0.0); break;
            case System.Collections.IDictionary dict:
                w.WriteStartObject();
                foreach (System.Collections.DictionaryEntry e in dict) { w.WritePropertyName((string)e.Key); CensusJson(w, e.Value); }
                w.WriteEndObject();
                break;
            case System.Collections.IEnumerable list:
                w.WriteStartArray();
                foreach (var e in list) CensusJson(w, e);
                w.WriteEndArray();
                break;
            default: w.WriteStringValue(value.ToString()); break;
        }
    }
}
