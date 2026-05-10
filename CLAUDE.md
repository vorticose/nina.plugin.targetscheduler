# CLAUDE.md

## Repo layout

This is a **custom fork** of `tcpalmer/nina.plugin.targetscheduler`. Two remotes:

- `origin` → `vorticose/nina.plugin.targetscheduler` (the fork; default branch `fork/main`)
- `upstream` → `tcpalmer/nina.plugin.targetscheduler` (track-only)

## Branches

- **`fork/main`** — fork trunk. All custom features (Maintain Exposure Ratio, weighted rotation, deficit correction, dev-deploy script, `**CUSTOM FORK**` AssemblyDescription marker) live here. Default branch on `origin`. **Branch new work from here, not from `main`.**
- `main` — clean upstream mirror, used for rebasing fork commits onto upstream releases. Do not commit features here.
- `feature/exposure-ratio` — historical name for what is now `fork/main`; kept as alias.

## Branching rule

When creating any new branch (claude worktrees, feature branches, fixes), branch from `fork/main`. If you branch from `main` by mistake, the build's `EnsureCustomForkMarker` MSBuild target will hard-error before PostBuild can deploy a stripped DLL into the NINA plugin folder — but you'll have wasted a build cycle. Just use `fork/main`.

## Build / deploy

`dotnet build NINA.Plugin.TargetScheduler.sln -c Release` triggers `PostBuild` in [NINA.Plugin.TargetScheduler.csproj](NINA.Plugin.TargetScheduler/NINA.Plugin.TargetScheduler.csproj), which xcopies the DLLs to `%LOCALAPPDATA%\NINA\Plugins\3.0.0\NINA.Plugin.TargetScheduler\`. The `EnsureCustomForkMarker` guard fires first — refuses to deploy unless `AssemblyInfo.cs` contains `**CUSTOM FORK**`.

To deploy to the rig (RBFocus), build locally then `scp -O` the three DLLs to `C:\Users\RBFocus\AppData\Local\NINA\Plugins\3.0.0\NINA.Plugin.TargetScheduler\` — never deploy without explicit OK (NINA may be mid-imaging).

## Stock plugin

NINA's official Target Scheduler installs to `...\Plugins\3.0.0\Target Scheduler\` (with a space). The fork installs to `...\Plugins\3.0.0\NINA.Plugin.TargetScheduler\` (assembly name). They conflict on API port 8188 — keep only the fork installed.
