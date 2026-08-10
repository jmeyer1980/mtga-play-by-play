# WinGet manifests

Manifests for the Windows Package Manager, kept here so they're version-controlled
and reviewable before anything is submitted upstream.

```
jmeyer1980.MtgaPlayByPlay.yaml               version manifest
jmeyer1980.MtgaPlayByPlay.installer.yaml     installer + hash
jmeyer1980.MtgaPlayByPlay.locale.en-US.yaml  description, license, tags
```

The package is a **portable zip**: winget extracts it and puts `mtga-pbp` on your
PATH, so there is no installer, no elevation and no uninstall entry beyond winget's
own. `winget uninstall jmeyer1980.MtgaPlayByPlay` removes it cleanly.

## Status

Not yet submitted to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs),
so `winget install jmeyer1980.MtgaPlayByPlay` will not resolve until it is.

Verified so far, against v0.1.0:

- `winget validate --manifest packaging/winget` → succeeds
- installer URL returns HTTP 200 unauthenticated
- `InstallerSha256` matches the published zip
- `RelativeFilePath: mtga-pbp.exe` matches the zip's actual layout

**Not** verified: an end-to-end `winget install`. That needs
`winget settings --enable LocalManifestFiles` run as administrator, which is a
machine security setting best changed deliberately by you rather than by tooling.

## Testing the install locally

Run once, as administrator:

```powershell
winget settings --enable LocalManifestFiles
```

Then, from the repository root:

```powershell
winget install --manifest packaging/winget
mtga-pbp                       # should resolve on PATH in a new shell
winget uninstall jmeyer1980.MtgaPlayByPlay
```

## Submitting upstream

No registration is needed beyond a GitHub account — no fee, no Microsoft Partner
signup. Submission is a pull request that goes through automated validation (a bot
installs the package in a sandbox VM) and then human review.

The least error-prone route is Microsoft's own tool, which fills in the hash and
opens the PR for you:

```powershell
winget install Microsoft.WingetCreate
wingetcreate update jmeyer1980.MtgaPlayByPlay --version 0.1.0 --urls <installer-url> --submit
```

Two things worth expecting:

- The binary is **unsigned**, so users will see a SmartScreen prompt on first run.
  Automated validation tolerates this; a code-signing certificate is the only real
  fix, and it costs a few hundred dollars a year.
- Once merged, the PR and package are permanently public under your GitHub name.

## Updating for a new release

Bump `PackageVersion` in all three files, and in the installer manifest update
`InstallerUrl`, `InstallerSha256` and `ReleaseDate`. The hash is printed by the
release workflow and published as `<zip>.sha256` on the release; winget wants it
uppercase. Then re-run `winget validate --manifest packaging/winget`.
