// Prints each screen's EDR headroom: what macOS will currently let content exceed SDR white by.
//
//   swift tools/edr_probe.swift
//
// "current" is the live headroom and reads 1.0 until something on screen is using extended range (an HDR window that has
// been displayed for a moment; an early read is 1.0), and it moves with the brightness setting. "potential" is the most the
// display could grant (at the lowest brightness). Read by tmlab/display.py.
import AppKit

for (i, s) in NSScreen.screens.enumerated() {
    let cur = s.maximumExtendedDynamicRangeColorComponentValue
    let pot = s.maximumPotentialExtendedDynamicRangeColorComponentValue
    print("screen \(i) '\(s.localizedName)': current \(cur) potential \(pot) scale \(s.backingScaleFactor)")
}
