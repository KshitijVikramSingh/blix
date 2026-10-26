using System.Numerics;
using System.Runtime.InteropServices;
using Blix;
using Blix.Core;
using Blix.Geometry;
using Blix.Graphics;
using Blix.Graphics.MetalHWX;

namespace Blix.Demos.VulkanSponza;

// --ref-bsdf: the references shade every hit with the glTF BSDF (Blix.Shaders/bsdf_gltf.glsl, held to lab/bsdf) instead of the diffuse lobe and the thin sheets
// alone: GGX metal and dielectric, ior, specular, transmission thin-walled or as a SOLID (KHR_materials_volume with a thickness: a refracting interface, the
// medium inside attenuating by Beer-Lambert), diffuse transmission. reference_wave.comp reads, per triangle row, the material it belongs to
// (ReferenceMaterialRows, an index into ReferenceMaterialTable) and the baked specular roughness and metallic of the row (ReferenceRowMr, ray_surface_bake.comp).
//
// Thin or solid is per material, as the spec has it: a transmitting material with a thickness above 0 is a solid, one with none a thin-walled sheet
// (--material name:thickness=0.02 turns a pane into a block; thickness=0 back).
//
// <b>The panes.</b> The frame's rays pass a transmitting pane (its rows carry no coverage: ray_surface_bake.comp), which the GI wants -- the room behind a window is
// lit by the sky through it -- and the reference must not: it is the glass that reflects, tints and refracts. So the reference traces on its OWN tracer, built
// from the same scene with the pane rows given coverage in a copy of the surface words (EnsureReferenceTracer); the frame's tracer, and every cook, never see them.
// Hardware rays only (the software traversal's hits do not name a row).
//
// What it does not model: caustics (the sun is a point or a small disc; light through a solid reaches what is behind it by the sky and the bounces, never by the sun),
// emission, clearcoat, sheen, iridescence and anisotropy (the layers still to come in lab/bsdf), nested solids (one medium at a time).
internal sealed partial class SponzaLoop
{
    private bool refBsdf;
    private GpuBufferHandle referenceMaterialTable, referenceMaterialRows;
    private bool referenceMaterialsMade;
    private MetalRayTracer? referenceTracer;
    private bool referenceTracerFailed;

    private void ReadRefBsdfArgs(AppArgs args) => refBsdf = args.Flag("ref-bsdf");

    // The tables, once the surface bake has run (the row means are read on the GPU, not here).
    private bool EnsureReferenceMaterials()
    {
        if (referenceMaterialsMade) return true;
        if (rayGpuData is not { } data || rayPlacementPbr.Count == 0 || rayMrRows.Equals(default(GpuBufferHandle))) return false;
        var index = new Dictionary<PbrMaterial, uint>(ReferenceEqualityComparer.Instance);
        var table = new List<Vector4>();
        var perPlacement = new uint[rayPlacementPbr.Count];
        var solids = 0;
        var panes = 0;
        for (var p = 0; p < perPlacement.Length; p++)
        {
            var pm = rayPlacementPbr[p];
            if (pm is null) { perPlacement[p] = uint.MaxValue; continue; }
            if (!index.TryGetValue(pm, out var m))
            {
                index[pm] = m = (uint)(table.Count / 6);
                var e = pm.Ext;
                var t = Math.Clamp(Math.Max(e.TransmissionFactor, pm.TransmissionFactor), 0f, 1f);
                var solid = t > 0f && e.ThicknessFactor > 0f;
                var thin = t > 0f && !solid;
                // The medium's attenuation coefficient per metre: -ln(colour) / distance per channel (none at the default infinite distance).
                var sigma = float.IsFinite(e.AttenuationDistance) && e.AttenuationDistance > 0f
                    ? new Vector3(-MathF.Log(Math.Clamp(e.AttenuationColor.X, 1e-6f, 1f)), -MathF.Log(Math.Clamp(e.AttenuationColor.Y, 1e-6f, 1f)),
                        -MathF.Log(Math.Clamp(e.AttenuationColor.Z, 1e-6f, 1f))) / e.AttenuationDistance
                    : Vector3.Zero;
                table.Add(new Vector4(pm.BaseColorFactor.X, pm.BaseColorFactor.Y, pm.BaseColorFactor.Z, pm.MetallicFactor));
                table.Add(new Vector4(pm.RoughnessFactor, e.IndexOfRefraction, e.SpecularFactor, t));
                table.Add(new Vector4(e.SpecularColorFactor, e.DiffuseTransmissionFactor));
                table.Add(new Vector4(e.DiffuseTransmissionColorFactor, DiffuseRoughnessOf(pm)));
                table.Add(new Vector4(sigma, 0f));
                table.Add(new Vector4(solid ? 1f : 0f, thin ? 1f : 0f, 0f, 0f));
                if (solid) solids++;
                if (thin) panes++;
            }
            perPlacement[p] = m;
        }
        if (table.Count == 0) return false;
        var rows = new uint[Math.Max(1, data.RowPlacements.Length)];
        for (var row = 0; row < data.RowPlacements.Length; row++)
        {
            var p = (int)data.RowPlacements[row];
            rows[row] = p < perPlacement.Length ? perPlacement[p] : uint.MaxValue;
        }
        referenceMaterialTable = Own(device.CreateGpuBuffer(table.Count * 16, MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(table)), "sponza.reference.material-table"));
        referenceMaterialRows = Own(device.CreateGpuBuffer(rows.Length * 4, MemoryMarshal.AsBytes(rows.AsSpan()), "sponza.reference.material-rows"));
        referenceMaterialsMade = true;
        Console.WriteLine(string.Create(Inv, $"[VulkanSponza] reference BSDF: {table.Count / 6} materials ({solids} solids with a volume, {panes} thin transmitting), shaded per row by the glTF BSDF."));
        return true;
    }

    // The reference's tracer: the frame's scene with the transmitting panes' rows solid in its own copy of the surface words. Null until it can be built
    // (the frame's tracer first: it shares its queue), and for good where Metal will not build it.
    private MetalRayTracer? EnsureReferenceTracer()
    {
        if (referenceTracer is not null) return referenceTracer;
        if (referenceTracerFailed || frameTracer is null || rayGpuData is not { } data || !RaySurfacesReady || !raySurfacesChecked) return null;
        var tracer = MetalRayTracer.TryCreate(device, out var reason);
        if (tracer is null)
        {
            Console.WriteLine($"[VulkanSponza] reference tracer: unavailable, {reason}; the BSDF reference needs hardware rays.");
            referenceTracerFailed = true;
            return null;
        }
        tracer.ShareQueueWith(frameTracer);
        var rows = data.RowPlacements.Length;
        var bytes = device.ReadGpuBuffer(raySurfaces, 0, rows * 4 + (rayAlphaPlan?.Words.Length ?? 0) * 4);
        var words = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        var panes = 0;
        for (var row = 0; row < rows; row++)
        {
            var p = (int)data.RowPlacements[row];
            if (p >= rayPlacementMaterials.Count || !rayPlacementMaterials[p].Clear) continue;
            words[row] = (words[row] & 0xE0FFFFFFu) | (16u << 24);   // coverage solid (sixteenths 16), the diffuse roughness level kept
            panes++;
        }
        var surfaces = Own(device.CreateGpuBuffer(bytes.Length, bytes, "sponza.reference.surfaces"));
        var blocks = RayQueryGpuData.BlockNames.Select((name, i) => i == 6 ? surfaces : rayBlockBuffers.First(b => b.Name == name).Buffer).ToArray();
        var surfaceWords = words[..rows].ToArray();
        device.WaitIdle();
        var build = tracer.Build(data, blocks, surfaceWords);
        if (rayAlphaPlan is not null) tracer.SetAlphaAtlas(rows, shadows: false);
        Console.WriteLine(string.Create(Inv,
            $"[VulkanSponza] reference tracer: {build.SolidTriangles + build.PartialTriangles:N0} triangles ({panes:N0} pane rows visible), built in {build.CpuPrepareMs + build.BlasGpuMs + build.CompactTlasGpuMs:0} ms; {build.CompactedBytes / 1048576.0:0} MB."));
        referenceTracer = tracer;
        return tracer;
    }
}
