# xatlas

Vendored from https://github.com/jpcy/xatlas at commit `f700c7790aaa030e794b52ba7791a05c085faf0c`
(2022-07-26): `source/xatlas/xatlas.cpp`, `xatlas.h`, `xatlas_c.h` and `LICENSE`, unmodified.

Built by `src/Blix.Recipes/Blix.Recipes.csproj` (target `BuildXatlas`) into `libblix_xatlas` with
`XATLAS_C_API=1` and `XATLAS_EXPORT_API=1`, so the C API in `xatlas_c.h` is what the cook calls
(`Blix.Recipes/XatlasNative.cs`). Used to unwrap meshes for lightmaps at cook time.
