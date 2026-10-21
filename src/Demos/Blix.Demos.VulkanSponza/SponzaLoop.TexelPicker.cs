using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Blix.Core;
using Blix.Graphics;
using ImGuiNET;

namespace Blix.Demos.VulkanSponza;

// Looking at what the cook made, live (2026-10-21). Two tools:
//   the texel picker  hover a pixel (P freezes it): the incident pass records every texel that pixel reads, level by
//                     level, with its weight (incident_clipmap.frag, TexelPick) -- the same lookup the image used -- and
//                     a panel lists each one's record (position, normal, kind), its cooked data (sky visibility, contact
//                     visibility near and short, openness by octant), its light (the parts, the resolved answer) and
//                     which stage wrote it.
//   provenance        a byte a texel id: the stage that last wrote its sun part (low nibble) and its sky part (high),
//                     stamped by every writer (provenance.glsl). "Texel view" shows it: each pixel the colour of its
//                     heaviest finest texel's stage (through Show: Bounce radiance (raw), which it switches to).
internal sealed partial class SponzaLoop
{
    private static readonly string[] ProvenanceNames =
    {
        "never written", "coarse sweep", "refining round", "fine gather", "visible and changed",
        "fine copy", "instant layer", "ratio relight", "provisional", "landed on the CPU",
    };
    private static readonly string[] TexelViewNames =
    {
        "Off", "Provenance: sun part", "Provenance: sky part",
        "Breakdown: the finest level alone", "Breakdown: the coarser level alone",
        "Breakdown: the coarser level's share (orange; blue: the probes')", "Breakdown: path (green texels alone, yellow with the probes, red probes)",
        "Patches: the light a gather ray reads landing here", "Patches: layout (a colour each)",
        "Against the path trace (--gi-reference; grey alike, blue darker, red brighter, full at 25%)",
        "Texels' facing (green facing the pixel, yellow grazing, orange-red read from records facing away)",
        "Rays gathered (blue 128, green 256, yellow 512, red 1,024+; grey copied)",
    };

    // The incident pass's patch bindings (the patch views; placeholders without the live patch light).
    private GpuBufferHandle patchViewNone;
    private IEnumerable<ShaderBufferBinding> PatchViewBindings()
    {
        if (patchViewNone.Equals(default(GpuBufferHandle))) patchViewNone = Own(device.CreateGpuBuffer(16, new byte[] { 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255 }, "sponza.patch-view.none"));
        var on = !liveTexelPatch.Equals(default(GpuBufferHandle)) && !livePatchLightBuffer.Equals(default(GpuBufferHandle));
        yield return new ShaderBufferBinding("TexelPatch", on ? liveTexelPatch : patchViewNone);
        yield return new ShaderBufferBinding("SunNodePatchLight", on ? livePatchLightBuffer : patchViewNone);
    }
    private float PatchViewColumns => liveTexelPatch.Equals(default(GpuBufferHandle)) || livePatchLightBuffer.Equals(default(GpuBufferHandle)) ? 0f : LiveComponents;

    private GpuBufferHandle texelProvenanceBuffer;
    private int texelProvenanceBytes;
    private int texelView;
    private bool texelPicker, texelPickFrozen;
    // --texel-pick x,y (frame fractions): the picker on, frozen there, and its panel printed to the log once the light is at
    // rest (and again every few seconds) -- the picker without a screen to read it from.
    private bool texelPickLog;
    private long landedFrame = -1;
    private readonly System.Diagnostics.Stopwatch texelPickLogClock = new();

    private void ReadTexelPickArgs(AppArgs args)
    {
        // --view-dump: the view dump once the light has landed (SponzaLoop.ViewDump.cs).
        viewDumpAtLanding = args.Flag("view-dump");
        // --view-dump-at N: at post-load frame N instead (the path trace's samples by then), if the light has landed.
        if (args.Int("view-dump-at") is { } vda) { viewDumpAtLanding = true; viewDumpAtFrame = vda; }
        if (args.String("texel-pick") is not { } at) return;
        var parts = at.Split(',');
        if (parts.Length != 2 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            throw new AppArgsException("--texel-pick takes x,y, the frame's fractions (0-1).");
        texelPicker = texelPickFrozen = texelPickLog = true;
        texelPickFraction = new Vector2(x, y);
    }

    // RenderFrame's end, with --texel-pick: the panel's lines to the log.
    private void LogTexelPick()
    {
        // The cook landed a second ago: the pick reads back frames late, its rows a quarter second.
        if (!texelPickLog || !texelResting || landedFrame < 0 || framesRendered < landedFrame + 60) return;
        if (texelPickLogClock.IsRunning && texelPickLogClock.Elapsed.TotalSeconds < 5) return;
        UpdateTexelPick();
        if (texelPickPixel is not { } px) return;
        texelPickLogClock.Restart();
        var inv = CultureInfo.InvariantCulture;
        var lum = 0.2126f * px.Answer.X + 0.7152f * px.Answer.Y + 0.0722f * px.Answer.Z;
        Console.WriteLine(string.Create(inv,
            $"[pick] ({texelPickFraction.X:0.0000},{texelPickFraction.Y:0.0000}) at ({px.World.X:0.000},{px.World.Y:0.000},{px.World.Z:0.000}) n ({px.Normal.X:0.00},{px.Normal.Y:0.00},{px.Normal.Z:0.00}): light {lum:0.0000}, sky {px.Sky:0.00}, level wanted {px.LevelWanted:0.00}, path {px.Path}{(px.Reference > 0 ? $", path trace {px.Reference:0.0000} +-{px.ReferenceError:0.0000} ({100 * (lum / px.Reference - 1):+0.0}%)" : "")}"));
        foreach (var r in texelPickRows)
            Console.WriteLine(string.Create(inv,
                $"[pick]   {r.Share * 100,3:0}% L{r.Level} {r.Kind} #{r.Id}: off {r.AlongCm:+0.0;-0.0} cm (across {r.AcrossCm:0.0}), facing {r.Facing:0.00}, sky {r.Sky:0.00}, contact {r.Contact:0.00}/{r.ContactShort:0.00}, light {r.Light:0.0000}{(r.Lit ? "" : " NONE")}, patch #{r.Patch} ({r.PatchLight:0.0000}), by {ProvenanceShort(r.ProvSun)}/{ProvenanceShort(r.ProvSky)}{(r.Problems.Count > 0 ? " -- " + string.Join("; ", r.Problems.Select(q => q.Text)) : "")}"));
    }
    private Vector2 texelPickFraction = new(0.5f);
    private readonly GpuBufferHandle[] texelPickBuffers = new GpuBufferHandle[4];
    private readonly long[] texelPickFrames = { -1, -1, -1, -1 };
    private uint[]? texelPickLast;
    private readonly System.Diagnostics.Stopwatch texelPickClock = System.Diagnostics.Stopwatch.StartNew();
    private string texelPickIds = "";
    private const int TexelPickWords = 40 + 4 * 34;

    // The provenance: every writer of the parts and the incident pass bind it (zeroed: never written).
    private ShaderBufferBinding ProvenanceBinding()
    {
        // Made again once the texels outgrow it: bound before they were up, it was 16 bytes for good and nothing was
        // stamped (2026-10-21).
        var bytes = Math.Max(16, (texelCount + 3) / 4 * 4);
        if (texelProvenanceBuffer.Equals(default(GpuBufferHandle)) || texelProvenanceBytes < bytes)
        {
            if (!texelProvenanceBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(texelProvenanceBuffer);
            texelProvenanceBuffer = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.texels.provenance"));
            texelProvenanceBytes = bytes;
        }
        return new ShaderBufferBinding("TexelProvenance", texelProvenanceBuffer);
    }

    // Each texel's passes at its last gather, 16 bits a texel (slice_resolve.comp): the rays view, the view dump.
    private GpuBufferHandle texelRaysBuffer;
    private int texelRaysBytes;
    private ShaderBufferBinding TexelRaysBinding()
    {
        var bytes = Math.Max(16, (texelCount + 1) / 2 * 4);
        if (texelRaysBuffer.Equals(default(GpuBufferHandle)) || texelRaysBytes < bytes)
        {
            if (!texelRaysBuffer.Equals(default(GpuBufferHandle))) RetireGpuBuffer(texelRaysBuffer);
            texelRaysBuffer = Own(device.CreateGpuBuffer(bytes, new byte[bytes], "sponza.texels.rays"));
            texelRaysBytes = bytes;
        }
        return new ShaderBufferBinding("TexelRays", texelRaysBuffer);
    }

    // slice_resolve.comp's code (uKeep.z): which gather this is.
    private float GatherProvenanceCode => visibleChangedGathering ? 4f : fineGathering ? 3f : sunNodeRefineRound > 0 ? 2f : 1f;

    // The incident pass runs whole while either tool is on: a reused or skipped frame would show nothing of it.
    private bool TexelToolsOn => texelView != 0 || texelPicker;

    // uReference.w for the view: 3-4 provenance, 5-8 the breakdown (incident_clipmap.frag).
    // Not while a view dump waits (SponzaLoop.ViewDump.cs): a view's colours are written where the field is.
    private float TexelViewReferenceW(float otherwise) => texelView == 0 || viewDumpWaiting > 0 ? otherwise : texelView + 2f;

    // RecordIncidentClipmap: this frame's pick (the pixel's code for uTexelDebug.w, and the buffer); the slot whose frame
    // is past the frames in flight read first.
    private (float Code, ShaderBufferBinding Buffer) TexelPickInputs(int frameWidth, int frameHeight)
    {
        var slots = texelPickBuffers.Length;
        for (var i = 0; i < slots; i++)
            if (texelPickBuffers[i].Equals(default(GpuBufferHandle)))
                texelPickBuffers[i] = Own(device.CreateGpuBuffer(TexelPickWords * 4, new byte[TexelPickWords * 4], "sponza.texels.pick"));
        var slot = (int)(framesRendered % slots);
        if (!texelPicker) return (0f, new ShaderBufferBinding("TexelPick", texelPickBuffers[slot]));
        var inFlight = Math.Max(1, device.MaxFramesInFlightCount);
        if (texelPickFrames[slot] >= 0 && framesRendered - texelPickFrames[slot] >= inFlight)
            texelPickLast = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(texelPickBuffers[slot], 0, TexelPickWords * 4).AsSpan()).ToArray();
        texelPickFrames[slot] = framesRendered;
        var x = Math.Clamp((int)(texelPickFraction.X * frameWidth), 0, frameWidth - 1);
        var y = Math.Clamp((int)(texelPickFraction.Y * frameHeight), 0, frameHeight - 1);
        return (1f + x + 4096f * y, new ShaderBufferBinding("TexelPick", texelPickBuffers[slot]));
    }

    // One texel the picked pixel reads, as the panel shows it; Problems with 1 a warning, 2 bad.
    private sealed record PickRow(int Level, uint Id, float Share, string Kind, float AlongCm, float AcrossCm, float Facing,
        float Sky, float Contact, float ContactShort, float Light, bool Lit, float Confidence, int ProvSun, int ProvSky,
        int Patch = -1, float PatchLight = -1f)
    {
        public List<(string Text, int Severity)> Problems { get; } = new();
        public int Severity => Problems.Count == 0 ? 0 : Problems.Max(q => q.Severity);
    }

    private sealed record PickPixel(Vector3 World, Vector3 Normal, float Distance, float LevelWanted, bool Lightmapped,
        bool Found, float Confidence, Vector3 Answer, float Sky,
        int Finest, Vector3 FineLight, float FineConfidence, float FineSky, int Coarsest, Vector3 CoarseLight, float CoarseConfidence,
        float CoarseSky, float Fraction, float FineTake, int Path, Vector4 Probes, float Reference, float ReferenceError);

    private PickPixel? texelPickPixel;
    private List<PickRow> texelPickRows = new();

    // DrawUi: the pixel follows the mouse (off the overlay) until P freezes it, and the panel.
    private void DrawTexelPicker()
    {
        if (!texelPicker) return;
        var io = ImGui.GetIO();
        if (ImGui.IsKeyPressed(ImGuiKey.P) && !io.WantCaptureKeyboard) texelPickFrozen = !texelPickFrozen;
        if (!texelPickFrozen && !io.WantCaptureMouse && io.DisplaySize.X > 0 && io.DisplaySize.Y > 0)
            texelPickFraction = new Vector2(io.MousePos.X / io.DisplaySize.X, io.MousePos.Y / io.DisplaySize.Y);
        // A marker where the pick is (magenta once frozen).
        var at = texelPickFraction * io.DisplaySize;
        var draw = ImGui.GetForegroundDrawList();
        var colour = texelPickFrozen ? 0xFFFF00FFu : 0xFFFFFFFFu;
        draw.AddCircle(at, 6f, colour, 12, 1.5f);
        draw.AddLine(at - new Vector2(12f, 0f), at - new Vector2(4f, 0f), colour);
        draw.AddLine(at + new Vector2(4f, 0f), at + new Vector2(12f, 0f), colour);
        UpdateTexelPick();
        ImGui.SetNextWindowSize(new Vector2(900f, 420f), ImGuiCond.FirstUseEver);
        if (ImGui.Begin("Texel picker"))
        {
            ImGui.Checkbox("Frozen (P)", ref texelPickFrozen);
            ImGui.SameLine();
            // The whole view's pixels and texels to a folder (SponzaLoop.ViewDump.cs; tools/gi-model/view_dump.py reads it).
            if (ImGui.Button("Dump view")) viewDumpPending = true;
            if (viewDumpLast is { } last) { ImGui.SameLine(); ImGui.TextUnformatted(last); }
            DrawTexelPickBody();
        }
        ImGui.End();
    }

    // ImGui's TextColored takes its text as a printf format: every '%' ate what followed it ("+9.74031264124ff it").
    private static void PickText(Vector4 colour, string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, colour);
        ImGui.TextUnformatted(text);
        ImGui.PopStyleColor();
    }

    private static readonly Vector4 PickRed = new(1f, 0.35f, 0.3f, 1f), PickYellow = new(1f, 0.85f, 0.3f, 1f), PickGrey = new(0.6f, 0.6f, 0.6f, 1f);

    private void DrawTexelPickBody()
    {
        var inv = CultureInfo.InvariantCulture;
        if (texelPickPixel is not { } px) { ImGui.TextUnformatted(texelPickLast is null ? "Waiting for a pick..." : "Sky: no surface here."); return; }
        var rows = texelPickRows;
        var lum = 0.2126f * px.Answer.X + 0.7152f * px.Answer.Y + 0.0722f * px.Answer.Z;
        // The summary: what the pixel shows, where from, and how many of its texels look wrong.
        ImGui.TextUnformatted(string.Create(inv,
            $"This pixel shows: light {lum:0.0000}, sky visibility {(px.Sky >= 0 ? px.Sky.ToString("0.00", inv) : "-")}, confidence {px.Confidence:0.00}"));
        // Against the path trace here (--gi-reference), as far as it has converged.
        if (px.Reference > 0f)
        {
            var off = lum / px.Reference - 1f;
            var noise = px.ReferenceError > 0f ? px.ReferenceError / px.Reference : 0f;
            // What the reference can tell: the difference past twice its own standard error.
            var telling = MathF.Max(MathF.Abs(off) - 2f * noise, 0f);
            var line = string.Create(inv,
                $"The path trace here: {px.Reference:0.0000} +-{noise * 100:0.0}% (its own noise, {referenceSamples} frames) -- this pixel is {off * 100:+0.0;-0.0}% off it{(telling <= 0f ? ", within the reference's noise" : "")}");
            if (telling > 0.05f) PickText(PickRed, line);
            else if (telling > 0.02f) PickText(PickYellow, line);
            else ImGui.TextUnformatted(line);
        }
        else PickText(PickGrey, "The path trace here: none (boot with --gi-reference, G toggles it).");
        var levels = string.Join(" + ", rows.GroupBy(r => r.Level).Select(g =>
            $"level {g.Key} ({g.Count()} {(g.First().Kind.StartsWith("atlas", StringComparison.Ordinal) ? "atlas texels" : "cells")})"));
        ImGui.TextUnformatted(string.Create(inv,
            $"It reads {(rows.Count == 0 ? "no texels" : levels)}; {px.Distance:0.0} m away, level wanted {px.LevelWanted:0.00}{(px.Lightmapped ? "" : ", no lightmap here (hashed cells only)")}"));
        var bad = rows.Count(r => r.Severity == 2);
        var odd = rows.Count(r => r.Severity == 1);
        if (bad + odd == 0) PickText(PickGrey, "Nothing looks wrong with its texels.");
        else PickText(bad > 0 ? PickRed : PickYellow, $"{bad} texel{(bad == 1 ? "" : "s")} look wrong, {odd} look odd -- see the right column.");
        DrawTexelPickBreakdown(px);
        ImGui.Separator();
        if (!ImGui.BeginTable("texels", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY)) return;
        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var name in new[] { "share", "texel", "off surface", "sky vis", "contact", "light", "patch (its light)", "written by (sun / sky)", "problems" })
            ImGui.TableSetupColumn(name);
        ImGui.TableHeadersRow();
        foreach (var r in rows)
        {
            ImGui.TableNextRow();
            if (r.Severity > 0) ImGui.TableSetBgColor(ImGuiTableBgTarget.RowBg1, r.Severity == 2 ? 0x402020C0u : 0x30209090u);
            void Cell(int c, string text, Vector4? tint = null)
            {
                ImGui.TableSetColumnIndex(c);
                if (tint is { } t) PickText(t, text); else ImGui.TextUnformatted(text);
            }
            Cell(0, string.Create(inv, $"{r.Share * 100:0}%"));
            Cell(1, $"L{r.Level} {r.Kind} #{r.Id}");
            Cell(2, string.Create(inv, $"{r.AlongCm:+0.0;-0.0} cm"), MathF.Abs(r.AlongCm) > 5f ? PickRed : MathF.Abs(r.AlongCm) > 2f ? PickYellow : null);
            Cell(3, r.Sky < 0 ? "-" : r.Sky.ToString("0.00", inv));
            Cell(4, r.Contact < 0 ? "-" : string.Create(inv, $"{r.Contact:0.00} / {r.ContactShort:0.00}"));
            Cell(5, r.Lit ? string.Create(inv, $"{r.Light:0.0000}") : "none", r.Lit ? null : PickRed);
            Cell(6, r.Patch < 0 ? "-" : string.Create(inv, $"#{r.Patch} ({r.PatchLight:0.0000})"));
            Cell(7, $"{ProvenanceShort(r.ProvSun)} / {ProvenanceShort(r.ProvSky)}");
            ImGui.TableSetColumnIndex(8);
            for (var k = 0; k < r.Problems.Count; k++)
            {
                if (k > 0) ImGui.SameLine();
                var (text, severity) = r.Problems[k];
                PickText(severity == 2 ? PickRed : PickYellow, text + (k < r.Problems.Count - 1 ? ";" : ""));
            }
        }
        ImGui.EndTable();
        PickText(PickGrey, "share: the pixel's weight on it.  off surface: its sample point above (+) or below (-) the pixel's surface.");
        PickText(PickGrey, "sky vis: cooked share of sky seen.  contact: cooked openness within 1 m / 20 cm.  light: its resolved light.");
        PickText(PickGrey, "patch: the transport patch it belongs to, and that patch's live light (what a gather ray landing on it reads).");
    }

    // How the incident pass made the pixel's answer: each level's light, confidence and sky visibility, how they blend, and
    // whether the probes come in -- the spots that are no texel's own are made here.
    private void DrawTexelPickBreakdown(PickPixel px)
    {
        var inv = CultureInfo.InvariantCulture;
        static float L(Vector3 c) => 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z;
        ImGui.Separator();
        ImGui.TextUnformatted("How the answer is made:");
        string Level(int level, Vector3 light, float confidence, float sky) => level < 0 ? "not read"
            : confidence < 0 ? $"level {level}: nothing answered"
            : string.Create(inv, $"level {level}: light {L(light):0.0000}, confidence {confidence:0.00}, sky visibility {(sky >= 0 ? sky.ToString("0.00", inv) : "-")}");
        ImGui.TextUnformatted("  finest:  " + Level(px.Finest, px.FineLight, px.FineConfidence, px.FineSky));
        ImGui.TextUnformatted("  coarser: " + Level(px.Coarsest, px.CoarseLight, px.CoarseConfidence, px.CoarseSky));
        var coarseShare = px.Coarsest >= 0 && px.CoarseConfidence >= 0 ? 1f - px.FineTake : 0f;
        var coarseTint = coarseShare > 0.25f ? PickYellow : (Vector4?)null;
        var line = string.Create(inv, $"  blend: the coarser level's share {coarseShare * 100:0}% (the finest is past its band's start by {px.Fraction * 100:0}%)");
        if (coarseTint is { } ct) PickText(ct, line); else ImGui.TextUnformatted(line);
        // The coarser level's light against the finest's: far apart, every pixel near the band edge is a mix of both.
        if (px.FineConfidence >= 0 && px.CoarseConfidence >= 0 && L(px.FineLight) > 1e-6f)
        {
            var ratio = L(px.CoarseLight) / L(px.FineLight);
            if (ratio < 0.6f || ratio > 1.6f)
                PickText(coarseShare > 0.05f ? PickRed : PickYellow, string.Create(inv,
                    $"  the coarser level reads {ratio:0.00}x the finest's light{(coarseShare > 0.05f ? " -- and it is mixed in here" : "")}"));
        }
        switch (px.Path)
        {
            case 1: PickText(PickGrey, "  path: the texels answer alone (light and sky visibility)."); break;
            case 2:
                PickText(PickYellow, string.Create(inv,
                    $"  path: texels with the probes -- the light {(1f - px.Confidence) * 100:0}% the probes', and the sky visibility the probes'{(px.Probes.W >= 0 ? string.Create(inv, $" ({px.Probes.W:0.00}); the probes alone read light {L(new Vector3(px.Probes.X, px.Probes.Y, px.Probes.Z)):0.0000}") : "")}"));
                break;
            default: PickText(PickRed, "  path: the probes alone (no texel answered)."); break;
        }
    }

    private static string ProvenanceShort(int code) => code switch
    {
        < 0 => "-", 0 => "never", 1 => "sweep", 2 => "refine", 3 => "fine gather", 4 => "tier 1",
        5 => "fine copy", 6 => "instant", 7 => "ratio", 8 => "provisional", _ => "cpu",
    };

    // The pick's rows from the last read-back, at most four times a second (or when its texels change).
    private void UpdateTexelPick()
    {
        if (texelPickLast is not { } p) { texelPickPixel = null; return; }
        float F(int i) => BitConverter.UInt32BitsToSingle(p[i]);
        var levels = (int)Math.Min(p[0], 4u);
        var ids = new List<(int Level, uint Id, float Weight)>();
        for (var b = 0; b < levels; b++)
        {
            var at = 40 + b * 34;
            var n = (int)Math.Min(p[at + 1], 16u);
            for (var i = 0; i < n; i++) ids.Add(((int)p[at], p[at + 2 + 2 * i], BitConverter.UInt32BitsToSingle(p[at + 3 + 2 * i])));
        }
        var key = string.Join(",", ids.Select(t => t.Id)) + "|" + p[9];
        if (key == texelPickIds && texelPickClock.Elapsed.TotalSeconds < 0.25) return;
        texelPickIds = key;
        texelPickClock.Restart();
        if (p[9] == 2u) { texelPickPixel = null; texelPickRows = new(); return; }
        var px = new PickPixel(new Vector3(F(1), F(2), F(3)), new Vector3(F(4), F(5), F(6)), F(7), F(15), p[8] != 0, p[9] == 1,
            F(10), new Vector3(F(11), F(12), F(13)), F(14),
            (int)p[28] - 1, new Vector3(F(16), F(17), F(18)), F(19), F(20), (int)p[29] - 1, new Vector3(F(21), F(22), F(23)), F(24), F(25),
            F(26), F(27), (int)p[30], new Vector4(F(31), F(32), F(33), F(34)), F(35), F(36));
        var rows = new List<PickRow>();
        foreach (var group in ids.GroupBy(t => t.Level))
        {
            var total = MathF.Max(group.Sum(t => t.Weight), 1e-9f);
            foreach (var (level, id, weight) in group.OrderByDescending(t => t.Weight).Take(8))
                if (TexelPickRow(level, id, weight / total, px) is { } row) rows.Add(row);
        }
        // Against the others at its level: a texel far darker than the rest is what puts a blotch in the image.
        foreach (var group in rows.GroupBy(r => r.Level))
        {
            var lit = group.Where(r => r.Lit).Select(r => r.Light).OrderBy(v => v).ToList();
            if (lit.Count < 2) continue;
            var median = lit[lit.Count / 2];
            foreach (var r in group.Where(r => r.Lit && median > 1e-6f))
            {
                var ratio = r.Light / median;
                if (ratio < 0.5f) r.Problems.Add(("much darker than the others", 2));
                else if (ratio < 0.75f) r.Problems.Add(("darker than the others", 1));
                else if (ratio > 2f) r.Problems.Add(("brighter than the others", 1));
            }
            var skies = group.Where(r => r.Sky >= 0).Select(r => r.Sky).OrderBy(v => v).ToList();
            if (skies.Count >= 2)
            {
                var skyMedian = skies[skies.Count / 2];
                foreach (var r in group.Where(r => r.Sky >= 0 && skyMedian > 0.05f && r.Sky < 0.5f * skyMedian))
                    r.Problems.Add(("sees much less sky than the others", 2));
            }
        }
        texelPickPixel = px;
        texelPickRows = rows;
    }

    private PickRow? TexelPickRow(int level, uint id, float share, PickPixel px)
    {
        if (id >= texelCount) return null;
        // A buffer not made, or a placeholder too small for this id (a stage that has not run): zeros, and -1 for a byte.
        byte[]? Read(GpuBufferHandle b, int offset, int count)
        {
            if (b.Equals(default(GpuBufferHandle))) return null;
            try { return device.ReadGpuBuffer(b, offset, count); }
            catch (ArgumentOutOfRangeException) { return null; }
        }
        float[] Floats(GpuBufferHandle b, int offset, int count) => Read(b, offset, count * 4) is { } r ? MemoryMarshal.Cast<byte, float>(r.AsSpan()).ToArray() : new float[count];
        uint[] Words(GpuBufferHandle b, int offset, int count) => Read(b, offset, count * 4) is { } r ? MemoryMarshal.Cast<byte, uint>(r.AsSpan()).ToArray() : new uint[count];
        int Byte(GpuBufferHandle b) => Read(b, (int)(id / 4 * 4), 4) is { } r ? (int)((BitConverter.ToUInt32(r, 0) >> (8 * (int)(id % 4))) & 255u) : -1;
        static float Lum(uint w) { var c = UnpackRgb9e5(w); return 0.2126f * c.X + 0.7152f * c.Y + 0.0722f * c.Z; }
        var rec = Floats(texelBuffer, (int)id * 32, 8);
        var pos = new Vector3(rec[0], rec[1], rec[2]);
        var nrm = new Vector3(rec[4], rec[5], rec[6]);
        var atlas = lightmapTexels && id >= lightmapFirstRecord;
        var buried = atlas && MathF.Abs(rec[3]) < 2f;
        var kind = atlas ? (rec[3] < 0f ? "atlas (2-sided)" : "atlas") : "cell";
        var offset = pos - px.World;
        var along = Vector3.Dot(offset, px.Normal);
        var across = (offset - along * px.Normal).Length();
        var facing = nrm.LengthSquared() > 1e-12f ? Vector3.Dot(Vector3.Normalize(nrm), px.Normal) : 0f;
        // Cooked: sky visibility (by its square root), contact visibility (linear).
        var skyByte = Byte(texelSkyVisBuffer);
        var aoByte = Byte(texelAoBuffer);
        var aoShortByte = Byte(texelAoShortBuffer);
        var resolved = Words(texelResolvedBuffer, (int)id * 8, 2);
        var prov = Byte(texelProvenanceBuffer);
        var patch = liveTexelPatch.Equals(default(GpuBufferHandle)) ? -1 : (int)Words(liveTexelPatch, (int)id * 4, 1)[0];
        var patchLight = -1f;
        if (patch >= 0 && !livePatchLightBuffer.Equals(default(GpuBufferHandle)))
        {
            var pw = Words(livePatchLightBuffer, patch * LiveComponents * 8, LiveComponents * 2);
            var sum = Vector3.Zero;
            for (var k = 0; k < LiveComponents; k++)
            {
                sum += new Vector3((float)BitConverter.UInt16BitsToHalf((ushort)(pw[2 * k] & 0xFFFF)), (float)BitConverter.UInt16BitsToHalf((ushort)(pw[2 * k] >> 16)),
                    (float)BitConverter.UInt16BitsToHalf((ushort)(pw[2 * k + 1] & 0xFFFF)));
            }
            patchLight = 0.2126f * sum.X + 0.7152f * sum.Y + 0.0722f * sum.Z;
        }
        var row = new PickRow(level, id, share, kind, along * 100f, across * 100f, facing,
            skyByte <= 0 ? -1f : MathF.Pow((skyByte - 1) / 254f, 2f),
            aoByte <= 0 ? -1f : (aoByte - 1) / 254f, aoShortByte <= 0 ? -1f : (aoShortByte - 1) / 254f,
            Lum(resolved[0]), (resolved[1] >> 16) != 0, (resolved[1] & 0xFFFF) / 65535f,
            prov < 0 ? -1 : prov & 15, prov < 0 ? -1 : prov >> 4, patch, patchLight);
        if (buried) row.Problems.Add(("buried inside geometry", 2));
        if (MathF.Abs(row.AlongCm) > 5f) row.Problems.Add(($"sits {MathF.Abs(row.AlongCm):0} cm {(row.AlongCm < 0 ? "below" : "above")} the surface", 2));
        else if (MathF.Abs(row.AlongCm) > 2f) row.Problems.Add(($"sits {MathF.Abs(row.AlongCm):0} cm {(row.AlongCm < 0 ? "below" : "above")} the surface", 1));
        if (facing < 0.7f) row.Problems.Add(("faces another way", 1));
        if (!row.Lit) row.Problems.Add(("has no light", 2));
        if (row.ProvSun == 0) row.Problems.Add(("never written", 1));
        return row;
    }

    // texel.glsl's texelDetailUnpack, the sky visibility (by its square root).
    private static float TexelDetailSky(uint w) { var s = ((w >> 16) & 0xFFu) / 255f; return s * s; }

}
