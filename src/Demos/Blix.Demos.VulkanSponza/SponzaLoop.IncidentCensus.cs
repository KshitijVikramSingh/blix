using System.Globalization;
using System.Runtime.InteropServices;
using Blix.Graphics;

namespace Blix.Demos.VulkanSponza;

// The incident pass's census (U3, 2026-10-14): every IncidentCensusPeriod frames, its pixels counted by the path they took
// (incident_clipmap.frag, countPath) -- the cells answering alone, or the probes read and why -- and logged. The pass cost
// ~41 ms a frame on Sponza (a Metal System Trace) though the cells were meant to answer alone at rest: this says where
// they did not.
internal sealed partial class SponzaLoop
{
    private const int IncidentCensusPeriod = 240;
    private GpuBufferHandle incidentCensus;
    private long incidentCensusFrame = -1;

    private static readonly string[] IncidentPaths =
        { "sky", "cells alone", "no texel found", "texels not at rest", "no detail up", "a texel without detail", "not whole", "debug view or screen probes" };

    private GpuBufferHandle IncidentCensusBuffer()
    {
        if (incidentCensus.Equals(default(GpuBufferHandle)))
            incidentCensus = Own(device.CreateGpuBuffer(64, new byte[64], "sponza.incident.census"));
        return incidentCensus;
    }

    // Counting this frame (one pass a census; the next waits for its read).
    private bool IncidentCensusNow(bool half)
    {
        if (incidentCensusFrame >= 0 || framesRendered % IncidentCensusPeriod != IncidentCensusPeriod - 1) return false;
        incidentCensusFrame = framesRendered;
        return true;
    }

    // Each frame: a census counted frames ago read, logged and cleared.
    private void ReadIncidentCensus()
    {
        if (incidentCensusFrame < 0 || framesRendered < incidentCensusFrame + device.MaxFramesInFlightCount + 1) return;
        incidentCensusFrame = -1;
        device.WaitIdle();
        var c = MemoryMarshal.Cast<byte, uint>(device.ReadGpuBuffer(incidentCensus, 0, 64).AsSpan()).ToArray();
        device.WriteGpuBuffer(incidentCensus, 0, new byte[64]);
        long surface = 0;
        for (var i = 1; i < IncidentPaths.Length; i++) surface += c[i];
        if (surface == 0) return;
        // The pixels that read the probes (paths 2 on): none, and the clipmap's solve can rest (RecordClipmap).
        long readers = 0;
        for (var i = 2; i < IncidentPaths.Length; i++) readers += c[i];
        // Under a thousandth of the surface (a few seams' pixels where no texel is found) reads the probes as they last
        // stood: waking the solve for them cost ~6.5 ms of closest traces and half a shadow batch a frame (2026-10-20).
        clipmapProbeReaders = readers * 1000 >= surface ? readers : 0;
        string Pct(long n) => (100.0 * n / surface).ToString("0.0", CultureInfo.InvariantCulture) + "%";
        string Fine(long n) => $"{n:N0} ({(100.0 * n / surface).ToString("0.000", CultureInfo.InvariantCulture)}%)";
        var probes = string.Join(", ", Enumerable.Range(2, IncidentPaths.Length - 2).Where(i => c[i] > 0).Select(i => $"{IncidentPaths[i]} {Pct(c[i])}"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] incident census (frame {framesRendered}): {surface:N0} surface pixels -- the cells alone {Pct(c[1])} (two levels read at {Pct(c[8])}; lightmapped {Pct(c[11])}, of them past the atlas {Pct(c[9])}); the probes read: {(probes.Length > 0 ? probes : "none")}; of those, the open-sky cube alone (no probe answered) {Fine(c[12])}, the surface probes {Fine(c[13])}, texels found but mixed with the probes {Fine(c[14])}."));
    }
}
