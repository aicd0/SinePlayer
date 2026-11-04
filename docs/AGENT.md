# Agent Rules

## Coding

- Always ask before introducing anything new: a new pattern, a new design, new behavior, a new coding style, a new dependency or API.
- Do not run the full test suite unless explicitly asked. Building x64 is the default verification.

### On a test failure

1. If the test expects an old behavior that the user explicitly asked to change, fix the test expectation.
2. If it is confirmed an implementation bug, that is not part of the design the user asked for and was only
   introduced by the agent's mistake, fix the bug.
3. Otherwise, always ask for the user's expectation before making any changes.

## Building

`dotnet build` does not work here: the MrtCore PriGen targets load `Microsoft.Build.Packaging.Pri.Tasks.dll`
from the Visual Studio MSBuild layout, so the .NET SDK fails with MSB4062. Use that MSBuild:

```
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' src\SinePlayer.sln /p:Platform=x64 /v:q /nologo
```

`Platform=x64` is required. The solution holds the app (`src\SinePlayer`), `SinePlayer.Core`, `SinePlayer.SDK`
and `src\Tests\SinePlayer.Core.Tests`.

Analyzer diagnostics are build errors: IDE0007 (`var` where the type is apparent), IDE0008 (explicit type where
it is not), IDE2001 (embedded statement on its own line), IDE1006 (naming).

## Testing

Only when asked. Build first, then run with `--no-build` and logging, so a failure can be read from the output
instead of needing a re-run:

```
dotnet test src\Tests\SinePlayer.Core.Tests\SinePlayer.Core.Tests.csproj --no-build -p:Platform=x64 --logger "console;verbosity=normal" 2>&1 | Tee-Object -FilePath "$env:TEMP\tests.log"
```

Never test UI behavior. Leave it to the user.
