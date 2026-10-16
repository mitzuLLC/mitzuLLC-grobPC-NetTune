# grobPC NetTune

RSS queue of the active network adapter → **one CPU**, registry profile import, verify, and **exact** backup/restore.
A WPF port of the `grobPC-NetTune.cmd` tool, built and protected like **grobyq** (ConfuserEx2, Inno Setup, CI).

**AerialOS edition** — grobPC NetTune runs **only on AerialOS** (the gaming Windows build by ZERO_WAKE & mitzu LLC).
There is no license override: off AerialOS it shows the gate and exits.

> by **mitzu LLC** · grobPC — [aerialos.vercel.app](https://aerialos.vercel.app) · [grobpc.com](https://grobpc.com) · [x.com/grobPC](https://x.com/grobPC)

---

## What it does

Pins the receive-side-scaling (RSS) queue of one physical adapter onto a single logical processor, so network DPC/ISR
land where you put them — the v7.0 interrupt-affinity idea, applied to the NIC. It:

- reads CPU topology (cores, SMT siblings, P/E class, processor groups) via `GetSystemCpuSetInformation`;
- resolves the adapter's driver key `Control\Class\{4d36e972-…}\NNNN` by `NetCfgInstanceId` (fallback `DriverDesc`),
  cross-checked against `Enum\…\Driver` so it never writes into another adapter's key;
- imports the LAN / Wi-Fi / NDIS `.reg` profiles (RSS CPU and profile substituted), classifying every value
  **T1-STD / VENDOR / UNKNOWN / SKIP**;
- runs `Enable-NetAdapterRss` + `Set-NetAdapterRss` (1 processor, 1 queue), retrying without `-NumberOfReceiveQueues`
  when the driver rejects it;
- restarts the adapter and **verifies**: RSS state, advanced keywords, the registry profile, and the
  `IndirectionTable` (every entry must be on the target CPU);
- **backs up before the first write** and restores **exactly** (types, data, and values that did not exist are deleted —
  something `reg import` cannot do).

Nothing is a magic preset: the tool shows orientation and asks before every write. The final proof of where network
DPC/ISR run is a **WPR/WPA trace**, not this tool's table and not ping.

## Safety

- **Dry-run** toggle: preview every change, write nothing.
- Writes only to the adapter's own `Class\NNNN` key and `NDIS\Parameters` (a hard `Assert-WritablePath` guard);
  `MessageNumberLimit` is **never** written into the class key (its place is `Interrupt Management`, use grobyq).
- Backups in `Documents\grobPC NetTune\NetTune-backup`; logs in `Documents\grobPC NetTune\logs`.
- Requires administrator (writes HKLM driver keys, runs `Set-NetAdapterRss` / `netsh`).

## Layout

```
src/NetTune.Core     net8.0 engine — registry, profiles, CPU topology, adapters/target, RSS, snapshot, verify.
                     Platform-neutral and unit-tested on Linux (no Windows types).
tests/NetTune.Tests  console runner of the self-test: sections A–E + restore flow (82 checks) on an in-memory registry.
app/                 net8.0-windows WPF app: the engine + the Windows collectors (Win/) and the dark UI.
app/Win/             WinRegistry (64-bit view), WinNet (NetAdapter cmdlets / netsh bridge), WinCpu (CpuSets P/Invoke),
                     AerialGate (AerialOS detection), Admin.
build/               ConfuserEx profile, Inno Setup script, AerialOS-marker.reg.
.github/workflows/   Windows build → test → obfuscate → /selftest smoke → (sign) → Inno → Release.
```

## Build

```powershell
# engine + tests (cross-platform)
dotnet run -c Release --project tests/NetTune.Tests        # -> "Self-test: 82 passed, 0 failed"

# the app (Windows only)
dotnet publish app/NetTune.App.csproj -c Release -r win-x64 --self-contained false -o publish
```

The release pipeline runs on a push to `release/<version>` (or a `v<version>` tag) and attaches
`grobPC-NetTune-setup.exe` to a GitHub Release.

### Switches

| Switch | Effect |
|--------|--------|
| `/selftest` | headless: engine checks + AerialOS rules + a JIT pass over every method. Exit 0 = pass, 3 = fail. |
| `/dryrun`   | start with dry-run on. |
| `/aerial`   | show what the AerialOS check sees on this PC, then exit. |

## Credits

Authored methodology: **AerialOS** by ZERO_WAKE & mitzu (& TsaG1ch) / grobPC. Not a third-party tweak pack.
