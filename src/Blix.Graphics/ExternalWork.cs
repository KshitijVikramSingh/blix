namespace Blix.Graphics;

/// <summary>How a pass of native Metal work runs in order with the frame around it (<see cref="RenderGraph.External"/>).</summary>
/// <remarks>
/// The device ends the frame's work so far at the pass, submits it signalling <see cref="WaitValue"/> on its frame
/// timeline, hands this to the pass, and orders everything after the pass behind <see cref="SignalValue"/>. The pass
/// encodes a wait for the one and a signal of the other around its own work, on its own queue. Both are GPU-side: the
/// CPU never waits, and the frame's fence still means the whole frame, Metal work included, is done.
/// Only with <see cref="GraphicsFeatures.MetalObjects"/>.
/// </remarks>
/// <param name="MetalSharedEvent">The frame timeline as a raw id&lt;MTLSharedEvent&gt;, not retained; it lives as long as the device.</param>
/// <param name="WaitValue">Reached when everything the frame recorded before the pass has finished.</param>
/// <param name="SignalValue">What the pass must signal when its work is done. It MUST be signalled, even when the work
/// fails, or every later submission on the queue waits forever; and the work must be committed to its queue before the
/// callback returns, because the frame's present depends on that signal. (Vulkan validation cannot see a Metal signal,
/// so it reports VUID-vkQueuePresentKHR-pWaitSemaphores-03268 for every frame that splits: committing first is what
/// makes the order real.)</param>
public readonly record struct ExternalWorkSync(nint MetalSharedEvent, ulong WaitValue, ulong SignalValue);
