using System.Numerics;
using System.Globalization;

namespace Blix.Demos.VulkanSponza;

// --cook-gi: the scene's GI bundle (SponzaLoop.TransportCache.cs, ChooseGiBundle) -- every stage that depends on the
// geometry alone, cooked once and loaded by every boot after: the transport's patches, couplings, sun visibility and
// hashed texels; the lightmap's records (rasterized, buried, moved off contacts); the texel seams; the far field's
// lists. Light is the runtime's: nothing that depends on the sun or the sky is cooked here. The run exits once all of
// them are written.
internal sealed partial class SponzaLoop
{
    private bool cookGi;
    // The stages this run still owes the bundle, after the transport cook lands (the far lists and the seams are
    // built from what it leaves).
    private bool cookGiFinished;

    private void CookGiWhenLanded()
    {
        if (!cookGi || cookGiFinished || transportCooked == false || transportCookTask is not null) return;
        cookGiFinished = true;
        // The far lists for this run's patches (cached by EnsureFarLists' builder as a stage of their own).
        if (transportBuildFarLists is { } build) _ = build(FarListReach);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[VulkanSponza] GI bundle cooked at post-load frame {postLoadFrames}: the geometry's stages are written; exiting, as asked."));
        host.RequestClose();
    }
}
