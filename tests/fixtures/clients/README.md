# Client fixture trees

Each directory is a miniature filesystem a client probe can be pointed at:

```
<client>/<platform>/{home,appdata,localappdata,programfiles}/...
```

`FixtureClientEnvironment` maps `IClientEnvironment`'s special directories onto those
subdirectories, so a probe runs against a fixture exactly as it runs against a real machine.

The point is that the client probe tests need **no** application installed on the machine running
them. The contents are hand-written approximations of real layouts; anywhere the real format is a
guess, the probe carries a `// VERIFY:` comment saying so.

These files contain no real credentials. The token in `ghcli` is a syntactically plausible
placeholder that has never been valid.
