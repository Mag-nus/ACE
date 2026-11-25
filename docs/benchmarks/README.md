# Benchmarks for the measured deep dive (2026-11-25)

These are the harnesses behind `perf-physics.txt` Section Q and `perf-network.txt`
Section G. They are standalone console projects. They are not part of `ACE.sln`
and are not built by the solution.

## Building

Each project references a **Release build of ACE.Server** through the MSBuild
property `AceBin`. By default that is `Source/ACE.Server/bin/x64/Release/net10.0`.

```
cd Source
dotnet build ACE.Server/ACE.Server.csproj -c Release

cd ../docs/benchmarks/<project>
dotnet build -c Release
dotnet bin/Release/net10.0/<Project>.dll <mode>
```

To point a project at another build, pass `-p:AceBin=/path/to/bin`.

All numbers in the reports were taken against `Claude1` @ `867315772`, the state
of upstream master through #4553, while it still used log4net. Some harnesses
reference `log4net.dll` or the log4net bridge. After the Serilog switch (#4558)
those references, and NT.12's logging measurement, need updating.

## Reading the results

- **Allocation figures are exact and deterministic.** They are measured with
  `GC.GetAllocatedBytesForCurrentThread`.
- **Timings are not absolute.** The original runs shared a 4-vCPU VM, so compare
  timings only as ratios within one run.
- **Run one benchmark process at a time.**
- **Let tiered compilation finish before measuring.** A benchmark that ends
  within ~100 ms measures Tier-0 code, where `Enum.HasFlag` boxes and shows
  phantom allocations. The harnesses warm up past tiering.
- **No dat files or database were used.**
  - Physics geometry is fed to the real dat parsers as synthetic byte streams.
  - Messages are built with a stub WorldObject and an uninitialised Session.
  - Where a real type could not be constructed, the code was copied verbatim
    into the harness, as each report says.

## Projects

| Project | Findings | Modes |
|---|---|---|
| `net-transport/` (Bench) | NT.1-NT.12 | `inbound`, `outbound`, `tick`, `socket`, `micro` |
| `net-messages/` (Probe) | NM.1-NM.12 | `sizes`, `parts`, `proto`, `caps`, `fanout`, `ench`, `inbound` |
| `physics-core/Bench` | PC.1-PC.14 | `sizes`, `spherepath`, `physobj`, `aframe`, `coll`, `log`, `lock`, `proto`, `pool`, `collend`, `objsize` (no argument runs all, ~5 min) |
| `physics-geom/` (GeomBench) | PG.1-PG.12 | `mem`, `math`, `poly`, `cache`, `box`, `setup`, `bsp`, `lookup`, `celllist` |

### net-transport

| Mode | What it measures |
|---|---|
| `inbound` | Real ClientPacket parsing against the span-based V2 and V3 prototypes. Packets are built with the server's own ServerPacket code and an ISAAC stream. |
| `outbound` | A verbatim copy of SendBundle / FlushPackets / SendPacketRaw against the serialise-once prototype, with byte-for-byte parity checks. |
| `tick` | The idle session tick, the P.4 fix exactly as written, and the dirty-mask prototype; also a sweep of the Parallel.ForEach degree of parallelism. |
| `socket` | Loopback UDP: APM receive, `ReceiveFromAsync(SocketAddress)` and a blocking `ReceiveFrom` thread; `SendTo` with an EndPoint against a SocketAddress. |
| `micro` | Hash32, IsEnabled, the dispatch closure, params arrays, bundle reuse, retransmit-cache ack variants and statistics contention. |

### net-messages

`declared_caps.txt` lists every message type's declared capacity. `caps` runs the
reflection scan behind NM.2. `proto` checks each prototype for byte identity
before measuring it.

### physics-geom

`celllist` uses Harmony to count real `LScape.get_landcell` calls and to emulate
the PG.1 one-line fix.

### physics-core

- `Bench/results.txt` is the output of the run behind Section Q.
- `Bench/TransitionPoolProto.cs` holds the PC.1 pooled-Reset prototype and its
  reflection diff.
- `AB/` is the PC.4 class-against-struct A/B. `Shared/AB.cs` is compiled twice:
  - `A/` builds it against the normal ACE.Server.
  - `B/` builds it against an ACE.Server compiled from a copy of `Source/` with
    `aframe-struct/aframe-struct.patch` applied. Pass that build's path as
    `-p:StructAceServer=/path/to/ACE.Server.dll`.
  - `results-class.txt` and `results-struct.txt` are the original outputs.
- `aframe-struct/` holds:
  - the patch, which applies cleanly to `867315772` with `git apply`;
  - the compile-error lists, for a plain struct and with `ref this` mutators;
  - the Roslyn scan output (`scan_AFrame.txt`, `scan_Position.txt`) that
    PC.2-PC.5 cite.
- `RefScan/` is the Roslyn semantic scan. It references the SDK's own Roslyn
  assemblies by absolute path, so adjust `RefScan.csproj` for your SDK version.
  Usage:
  `dotnet bin/Release/net10.0/RefScan.dll <Source/ACE.Server dir> <ACE bin dir> <output dir>`
