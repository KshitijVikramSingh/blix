# Third-party notices

Blix includes vendored source and restores third-party packages. Those works
remain under their own licenses; the Apache License 2.0 applies to Blix itself
and does not replace those terms.

## Vendored source

| Component | License | License text |
| --- | --- | --- |
| `bc7enc` / `rgbcx` by Richard Geldreich, Jr. | MIT or public domain, at the recipient's option | [`third_party/bc7enc/LICENSE`](third_party/bc7enc/LICENSE) |
| `meshoptimizer` by Arseny Kapoulkine | MIT | [`third_party/meshoptimizer/LICENSE.md`](third_party/meshoptimizer/LICENSE.md) |
| `xatlas` by Jonathan Young (from thekla_atlas, Thekla Inc. and NVIDIA) | MIT | [`third_party/xatlas/LICENSE`](third_party/xatlas/LICENSE) |

## Derived source

Ported rather than vendored: no file from these works is in the repository, but their arithmetic is, so their notices are
reproduced here.

| Component | Where it is used | License |
| --- | --- | --- |
| GT Tone Mapping ("GT7") sample implementation by Polyphony Digital Inc., from the SIGGRAPH 2025 talk "Driving Toward Reality: Physically Based Tone Mapping" ([course page](https://blog.selfshadow.com/publications/s2025-shading-course/)) | `src/Blix.Shaders/gt7.glsl`, `src/Blix.Graphics.Images/Tonemap.cs` (the GT7 methods), `lab/tonemap/tmlab/gt7.py` | MIT, below |

The GT7 curve, its ICtCp chroma handling and its frame-buffer units follow that sample. Blix adds an eighth curve parameter
(`shoulderTail`, where 0 is the published shoulder exactly), a luminance-preserving gamut map, the entry points, and fitted
parameter sets, which are Blix's own work under Blix's license.

```text
MIT License

Copyright (c) 2025 Polyphony Digital Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Direct package dependencies

| Package family | License | Project |
| --- | --- | --- |
| BCnEncoder.Net | MIT or Unlicense | [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET) |
| ImGui.NET | MIT | [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) |
| OpenTK.Audio.OpenAL | MIT | [OpenTK](https://github.com/opentk/opentk) |
| SharpGLTF | MIT | [SharpGLTF](https://github.com/vpenades/SharpGLTF) |
| Silk.NET | MIT | [Silk.NET](https://github.com/dotnet/Silk.NET) |
| StbImageSharp and StbTrueTypeSharp | Public domain | [StbSharp](https://github.com/StbSharp) |
| System.Reflection.Metadata | MIT | [.NET Runtime](https://github.com/dotnet/runtime) |

## Bundled assets

| Asset | Source | Terms |
| --- | --- | --- |
| `website/assets/launch.svg` | [GetIllustrations.com](https://www.getillustrations.com) | Licensed for use with visible attribution; the credit appears on the site itself, below Getting started |

Transitive package dependencies may carry additional notices. Their restored
package metadata and upstream distributions are authoritative for those terms.
