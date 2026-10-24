"""Blix tonemapping lab.

Frame-buffer convention (GT7's, kept so the reference's numbers carry over): linear light where 1.0
is REFERENCE_LUMINANCE (100) cd/m^2. Arrays are float64 with the colour channel last, shape (..., 3).
"""

REFERENCE_LUMINANCE = 100.0  # cd/m^2 <-> 1.0
SDR_PAPER_WHITE = 250.0      # cd/m^2, GT7's SDR reference white
