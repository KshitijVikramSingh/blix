# MikkTSpace

`mikktspace.c` and `mikktspace.h` are Morten S. Mikkelsen's reference implementation, unmodified,
from https://github.com/mmikk/MikkTSpace at commit `3e895b49d05ea07e4c2133156cfa94369e19e409`.
Their licence (zlib-style) is in each file's header.

`blix_mikk.c` is Blix's, and is the only file that knows Blix's mesh arrays: a flat entry point the
cook P/Invokes (`Blix.Recipes/MikkTSpaceNative.cs`). It is built by `Blix.Recipes` beside
meshoptimizer and bc7enc, and only the cook uses it.
