# winget and Scoop manifests

## winget (`winget/<version>/`)
The manifests of the first version for [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)
(`manifests/f/FelixMUrphy/TaskHub/<version>/`). Package `FelixMUrphy.TaskHub`, installer type `exe`, per-user,
silent switches `--install --quiet`, uninstall from the *Apps & features* entry (`DisplayName` / `Publisher` `TaskHub`).

- Check before a submission: `winget validate --manifest packaging\winget\<version>` and
  `winget install --manifest packaging\winget\<version>` in Windows Sandbox (needs `winget settings --enable LocalManifestFiles`).
- The first version goes in by hand (a PR from a fork of winget-pkgs). Later versions: the `winget` job in
  `.github/workflows/ci.yml` opens the PR after each release, once the repository secret `WINGET_TOKEN` exists
  (classic PAT with `public_repo`, of the account that owns the fork).

## Scoop (`scoop/taskhub.json`)
For an own bucket (Scoop's Extras asks for a reasonably known app, ~100+ stars). Runs the exe portable (no `--install`:
Scoop can't track the Run key, PATH and protocol the installer writes); data stays in `%APPDATA%\TaskHub`.
`checkver` / `autoupdate` let the bucket's Excavator workflow pick up new releases by itself.
