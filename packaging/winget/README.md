# WinGet manifests

Manifests for the Windows Package Manager, kept here so they're version-controlled
and reviewable before anything is submitted upstream.

```
manifests/jmeyer1980.MtgaPlayByPlay.yaml               version manifest
manifests/jmeyer1980.MtgaPlayByPlay.installer.yaml     installer + hash
manifests/jmeyer1980.MtgaPlayByPlay.locale.en-US.yaml  description, license, tags
```

They sit in their own directory because `winget validate` parses **every** file in
the directory it is given. With this README beside them it tried to read prose as
YAML and failed on the colon in "portable zip:" — a confusing error that says
nothing about the manifests, which were fine all along.

The package is a **portable zip**: winget extracts it and puts `mtga-pbp` on your
PATH, so there is no installer, no elevation and no uninstall entry beyond winget's
own. `winget uninstall jmeyer1980.MtgaPlayByPlay` removes it cleanly.

## Status

Not submitted to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs), so
`winget install jmeyer1980.MtgaPlayByPlay` does not resolve. **This is a decision, not
an unfinished step**, and the manifests are kept current and validated regardless.

The executable is unsigned, so Windows raises SmartScreen the first time it runs. A zip
you download and check against a published `.sha256`, from a project that says plainly
it is unsigned, is honest about that. A package manager entry that installs silently
and *then* trips SmartScreen is a worse deal for the person installing it.

Signing was priced rather than assumed. Azure Trusted Signing — renamed Azure Artifact
Signing in 2026 — is about $9.99/month and now open to individual developers, so cost
is not really the obstacle. Reputation is: it accrues gradually the way it does for an
OV certificate, rather than granting the instant pass old EV certificates did. Paying
would not reliably spare early adopters the prompt, which is the whole reason to sign.
Individual signups have also run into an Entra ID P2 licence requirement for creating
the signing role.

Revisit if the binary ever gets signed and builds reputation. Note that the usual
automation for this — [winget-releaser](https://github.com/vedantmgoyal9/winget-releaser)
— only automates *updates*: it needs one version already published upstream to use as a
template, so it could not be the first step in any case.

Verified against v0.3.0:

- `winget validate --manifest packaging/winget/manifests` → succeeds
- installer URL returns HTTP 200 unauthenticated, 34,274,077 bytes
- `InstallerSha256` matches the published zip, hashed from the downloaded
  artifact rather than trusting the workflow's own `.sha256`
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
winget install --manifest packaging/winget/manifests
mtga-pbp                       # should resolve on PATH in a new shell
winget uninstall jmeyer1980.MtgaPlayByPlay
```

## Submitting upstream, if that is ever revisited

Kept here so the route is known, not because it is queued — see Status above.

No registration is needed beyond a GitHub account — no fee, no Microsoft Partner
signup. Submission is a pull request that goes through automated validation (a bot
installs the package in a sandbox VM) and then human review.

The least error-prone route is Microsoft's own tool, which fills in the hash and
opens the PR for you:

```powershell
winget install Microsoft.WingetCreate
wingetcreate update jmeyer1980.MtgaPlayByPlay --version 0.3.0 --urls <installer-url> --submit
```

Two things worth expecting:

- Automated validation tolerates an unsigned binary, so it is not what would block a
  submission — the SmartScreen prompt users meet afterwards is the reason this is on
  hold.
- Once merged, the PR and package are permanently public under your GitHub name.

## Updating for a new release

Bump `PackageVersion` in all three files, and in the installer manifest update
`InstallerUrl`, `InstallerSha256` and `ReleaseDate`. The hash is printed by the
release workflow and published as `<zip>.sha256` on the release; winget wants it
uppercase. Then re-run `winget validate --manifest packaging/winget/manifests`.

This has to happen **after** the release publishes, not before — the hash can only be
computed from the built zip, and a guessed one is worse than a stale one.
