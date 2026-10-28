# ADR-0002: The demo uses ag-grid-enterprise in evaluation mode

Status: Accepted   Date: 2026-10-16
Work items: #2441, #2444   Classification: Public

## Context
TRD P-4 asks for a demo (React, AG Grid, a sample backend) in `examples/`. The server-side row model, row grouping and the
set filter, which the demo must show, belong to AG Grid Enterprise, a commercial package under a proprietary license. TRD OS-6
allows only listed open licenses, and the project's rules require approval for a new library. AG Grid Enterprise runs
without a license key in evaluation mode (a watermark and console messages).

## Decision
The demo's frontend (`examples/demo/web`) depends on `ag-grid-community` and `ag-grid-enterprise` and runs in evaluation mode.
No license key is stored in the repository or in CI. A maintainer who owns a license can put a key in a gitignored `.env.local`.
The demo is not part of any published package, and no Craticula package depends on AG Grid (NG4).

## Alternatives
- Community edition only: no server-side row model, so the demo could not show the library's purpose.
- Buy a license and store the key: a secret and a cost for a showcase, rejected.
- A screenshot or recorded demo: cannot be tried by the reader, which is the point of the request.

## Consequences
- The demo shows an AG Grid evaluation watermark. The README says so.
- The license check covers the NuGet packages under `csharp/` only, so it does not see `examples/demo/web`; this record lists the
  packages that are not on the allowlist instead.
- The web lockfile installs more than one package outside the OS-6 allowlist (next section).

## Packages outside the OS-6 allowlist
Read from `examples/demo/web/package-lock.json` (`license` field of each package; everything else is MIT, ISC, Apache-2.0,
BSD-* or 0BSD):

| Package | Version | License | Role |
|---|---|---|---|
| ag-grid-enterprise | 36.2.0 | Commercial | runtime in the demo (the grid itself, evaluation mode) |
| ag-charts-enterprise | 14.2.0 | Commercial | runtime, optional dependency of ag-grid-enterprise; the page uses no chart feature |
| lightningcss (and its 11 platform packages: android-arm64, darwin-arm64, darwin-x64, freebsd-x64, linux-arm-gnueabihf, linux-arm64-gnu, linux-arm64-musl, linux-x64-gnu, linux-x64-musl, win32-arm64-msvc, win32-x64-msvc) | 1.33.0 | MPL-2.0 | dev tooling (Vite CSS), never shipped |
| caniuse-lite | 1.0.30001816 | CC-BY-4.0 | dev tooling (browser data), never shipped |
| minimatch | 10.2.6 | BlueOak-1.0.0 | dev tooling (ESLint), never shipped |

The MPL-2.0 dev tooling is accepted case by case by the maintainer for this demo (OS-6 lists MPL-2.0 as case by case). The maintainer
approved the demo exception on 2026-10-16.

## Compliance
- Secrets: none stored. PII: none (synthetic data). Third parties: npm packages of a commercial vendor, evaluation use.
- Approval: the maintainer approved the demo exception on 2026-10-16.
