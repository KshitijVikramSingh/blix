# Hello Blix 3D

A character on a lit stage, from outside the Blix repository: the Rogue playing a clip on the
Studio stage, with its sun, shadows, sky, camera and clip on the overlay (press <kbd>`</kbd>), and a
headless check that reads the same character with no GPU. `Blix.Test.Apps` copies this tree out of
the checkout, builds it against `BLIX_ROOT`, and runs its gate, so it stays a working project
rather than a snippet.

Copy it into an empty repository, provide Blix at `engine/blix` or through `BLIX_ROOT`, and run:

```sh
./blix ls
dotnet build src/HelloBlix3D/HelloBlix3D.csproj
./blix run hello-3d
./blix test
```

Drag to orbit, scroll to zoom. Every setting on the overlay is also a flag, so
`./blix run hello-3d --clip Running_A --sun-elevation 12` starts from there, and the values this
example opens with are the ones found on the overlay and then kept in `Program.cs`.

The character is the KayKit Rogue by Kay Lousberg, CC0; see
[`src/HelloBlix3D/Assets/models/CREDITS.txt`](src/HelloBlix3D/Assets/models/CREDITS.txt).
