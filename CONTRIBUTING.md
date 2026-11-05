# Contributing

Thanks for taking the time to help. This is a small, maintainer-led project;
issues and pull requests are welcome, but response times are not guaranteed.

## Before you open a PR

1. Open an issue first for anything larger than a small fix, so the approach
   can be agreed on.
2. Build and test locally:

   ```powershell
   dotnet build Fuse.sln -c Release
   dotnet test src/Fuse.Core.Tests/Fuse.Core.Tests.csproj -c Release
   ```

3. Keep changes focused. One concern per pull request.

## Code guidelines

- Match the surrounding style: file-scoped namespaces, nullable enabled,
  Chinese comments where the surrounding code uses them.
- No new dependency that is incompatible with GPL-3.0.
- Do not commit build output (`bin/`, `obj/`, `dist/`), local configuration,
  logs, or debug symbols.
- New behavior in the core library should come with a unit test.
- Never add secrets, API keys or personal paths.

## Commit messages

Short imperative subject, optional body explaining the why. Example:

```
Fix instance directory for isolated version layouts

Versions stored under versions/<id> were treated as a shared root, which
made mod lists and save folders appear empty.
```

## License

By contributing you agree that your contribution is licensed under
GPL-3.0, the license of this project.
