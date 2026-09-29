# Hello Blix

The smallest complete project that uses Blix from outside its repository: a
project marker, two launchers, the two build imports, and one program with a
headed game and a headless check of it. `Blix.Test.Apps` copies this tree out of
the checkout, builds it against `BLIX_ROOT`, and runs its gate, so it stays a
working project rather than a snippet.

Copy it into an empty repository, provide Blix at `engine/blix` or through
`BLIX_ROOT`, and run:

```sh
./blix ls
dotnet build src/HelloBlix/HelloBlix.csproj
./blix run hello
./blix test
```

[Workflow](../../docs/workflow.md#using-blix-from-another-repository) explains
each file.
