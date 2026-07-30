# ACE Codebase Audit — Report Index

Read-only review of the ACEmulator codebase (~330,000 lines, 12 subsystems).
Nothing in the source tree was modified; these documents are analysis only.

**Date:** 2026-07-25 · **Reviewed at:** branch `combined`
**Re-verified:** 2026-07-27 against `combined` (`dbe6c6da`) and `Claude1` (`1e7bd6f0`) — see
[Re-verification](#re-verification-2026-07-27).

**Structure:** exactly one bug report and one performance report per subsystem.
Each file is self-contained — findings are stated in full where they live, not
deferred to another document.

---

## 🔴 Read this first

**`bugs-command.txt` Section A — verified privilege escalation, any player to full admin.**

> **Branch scope (added 2026-07-27):** `CharacterTransferCommands.cs` exists **only on
> `combined` and `RetailCharacterImport`**. It is **not** on `master` or `Claude1`, where
> `restoreretailcharacter` does not exist at all. If you are reading this on `master`, the
> file is absent — that is expected, not a fix. Re-verified on `combined`: still
> `AccessLevel.Player`, unchanged.

`/restoreretailcharacter` (`CharacterTransferCommands.cs:28`) is registered at
`AccessLevel.Player`. Its `PropertyBool` copy loop filters exactly one property
(`Attackable`), so `IsAdmin` / `IsArch` / `IsSentinel` / `IsAdvocate` are copied
verbatim from an arbitrary retail biota. Those same properties back
`Player_Properties.cs:36-69`, which `CommandManager.cs:292-303` reads to authorize
every command. A player restores a retail character flagged `IsAdmin` and logs in
with complete control of the server. The flags persist; nothing clears them.

I verified every link in that chain directly. **If this code is deployed anywhere
with untrusted players, treat it as an active incident** — disable the command,
then audit existing characters for unexpected permission properties.

---

## The 24 reports

| Subsystem | Bugs | Performance |
|---|---|---|
| ACE.Server/Network | [bugs-network.txt](bugs-network.txt) | [perf-network.txt](perf-network.txt) |
| ACE.Server/Managers | [bugs-managers.txt](bugs-managers.txt) | [perf-managers.txt](perf-managers.txt) |
| ACE.Server/Entity | [bugs-entity-server.txt](bugs-entity-server.txt) | [perf-entity-server.txt](perf-entity-server.txt) |
| ACE.Server/WorldObjects | [bugs-worldobjects.txt](bugs-worldobjects.txt) | [perf-worldobjects.txt](perf-worldobjects.txt) |
| ACE.Server/Factories | [bugs-factories.txt](bugs-factories.txt) | [perf-factories.txt](perf-factories.txt) |
| ACE.Server/Command | [bugs-command.txt](bugs-command.txt) | [perf-command.txt](perf-command.txt) |
| ACE.Server/Physics | [bugs-physics.txt](bugs-physics.txt) | [perf-physics.txt](perf-physics.txt) |
| ACE.Database | [bugs-database.txt](bugs-database.txt) | [perf-database.txt](perf-database.txt) |
| ACE.DatLoader | [bugs-datloader.txt](bugs-datloader.txt) | [perf-datloader.txt](perf-datloader.txt) |
| ACE.Entity (shared) | [bugs-entity-shared.txt](bugs-entity-shared.txt) | [perf-entity-shared.txt](perf-entity-shared.txt) |
| ACE.Adapter | [bugs-adapter.txt](bugs-adapter.txt) | [perf-adapter.txt](perf-adapter.txt) |
| ACE.Common | [bugs-common.txt](bugs-common.txt) | [perf-common.txt](perf-common.txt) |

Physics received more passes than any other subsystem — three bug passes and three
performance passes — all consolidated into its two files. In `bugs-physics.txt`,
pass 3's findings are Section G and the full CellArray fix is Section H. In
`perf-physics.txt`, pass 2 (the coverage gap) is Section F and pass 3 (memory
layout and GC behaviour) is Section M.

---

## Highest-value fixes

Severity-ordered, not effort-ordered.

1. **Privilege escalation** — `bugs-command.txt` X.1. Above everything else
   **on `combined`/`RetailCharacterImport`**. Does not apply to `master`/`Claude1`,
   where the file does not exist (see the scope note at the top).
2. **Exception isolation** at the three tick/receive boundaries —
   `bugs-network.txt` N.0a, `bugs-managers.txt` M.0a, `bugs-entity-server.txt` E.0d.
   Three independent audits converged on this. One malformed UDP packet from an
   unauthenticated client terminates the process for everyone.
   *Caveat, since this is the most-cited recommendation here:* catching is not
   free. An exception thrown mid-action leaves game state partially mutated — a
   half-completed transfer, a half-applied teleport — and log-and-continue keeps
   playing on it. It is still clearly the right call (the alternative loses all
   unsaved state for everyone), but choose the **granularity** deliberately:
   per-queued-action isolation contains damage far better than one try/catch
   around the whole tick, and the inbound-packet boundary should drop the packet
   rather than continue processing it.
   *A fourth entry point was later found and verified:* `bugs-factories.txt` G.0 —
   `CreateRandomLootObjects` has a `try/finally` whose `finally` body is entirely
   commented out and which has **no `catch`**, so any loot-generation throw escapes
   through `Creature_Death` into the landblock tick. That re-rates several loot
   findings from "empty corpse" to "part of the world stops ticking".
3. **Item duplication** — `bugs-worldobjects.txt` W.1, a single missing
   `return false` that leaves an item in two containers.
4. **`SingleOrDefault` → `FirstOrDefault`** — `bugs-network.txt` N.0b.
5. **Discarded return value in trade** — `bugs-worldobjects.txt` W.0a.

**Largest performance finding** (separate from the list above): `perf-entity-shared.txt`
E.1 — every Biota property read takes a `ReaderWriterLockSlim`, roughly 10-15x the
cost of the dictionary lookup it guards. A single player-vs-player melee swing costs
~150-220 lock acquisitions. The `[Ephemeral]` fast path does not mitigate it: I
verified only **9 of 404** `PropertyInt` members carry the attribute, and none of the
combat properties do. Plausibly outweighs everything in the other eleven performance
reports combined — and carries the largest blast radius, so measure first.

**Runner-up:** two *process-wide* locks — `LandblockManager.GetLandblock` and
`ObjectMaint.rwLock` (the latter `static`, verified) — between them substantially
negate the parallel landblock architecture. Both are in `perf-physics.txt`.
**Caveat:** the `ObjectMaint` one is real but its obvious fix is not — making the
lock per-instance would deadlock (see the resolved-assumptions note below). The
`LandblockManager` lock is the safer of the two to act on.

**On physics heap pressure specifically:** `perf-physics.txt` Section M establishes
that there is **not a single `struct` in the entire 162-file physics subsystem**
(verified). Every value type — `Sphere` (16B payload), `AFrame` (28B), `Position`
(80B across *two* objects) — is a heap-allocated class, because the C++ port turned
stack values into references wholesale. Section M.12 is the practical part: it
separates cheap Gen0 churn, which should be attacked structurally rather than one
allocation at a time, from the genuinely *promoted* allocations (M.1 visibility
lists, M.9 `LinkedList` nodes, H.2 `CellArray` retention) that are worth fixing
individually. Note that `Position`'s reference semantics also *caused* a real bug —
`bugs-physics.txt` G.14 — so a struct conversion would eliminate that class of
defect, not just its symptom.

---

## Re-verification (2026-07-27)

Every headline finding was re-checked directly against the source on both branches.

### Fixed upstream — 4 findings

Commit **`5385d19d` "Bug fixes 1 (#4470)"** (Mag-nus, 2026-07-29) fixed four documented
findings, in each case applying this audit's recommended fix essentially verbatim. It is
merged into `master` and `Claude1`.

| Finding | File | Fix |
|---|---|---|
| [A.0a](bugs-adapter.txt) | `GDLE/GDLEConverter.cs:534` | IID requirements now go to `IIDRequirements`, not `DIDRequirements` |
| [A.0b](bugs-adapter.txt) | `Lifestoned/LifestonedConverter.cs:462` | dedup guard now checks the destination `WeeniePropertiesSpellBook` |
| [S.1](bugs-entity-shared.txt) | `Models/PropertiesBookPageDataExtensions.cs:68` | `index = value.Count - 1` |
| [S.2](bugs-entity-shared.txt) | `Adapter/WeenieConverter.cs:162-168` | book pages now deep-cloned per element |

⚠️ **None of these four are on `combined`** — the branch this audit was written against.
It is 552 commits ahead of `master` but does not have `5385d19d` merged, so all four
defects are still live there. Verified individually, not inferred.

🔶 **A third book bug is still open on every branch — [`bugs-adapter.txt` A.6](bugs-adapter.txt).**
S.1 and S.2 were the book-page *index* and *aliasing* bugs. A.6 is the book-page
*permission* flag: `GDLE/Models/Page.cs:22-31` has an `IgnoreAuthor` getter that
disagrees with its own setter, so importing GDLE/Lifestoned book content **negates** it.
Since `IgnoreAuthor == true` means "skip the author check — anyone may edit or delete
this page" (`Book.cs:137`, `Book.cs:155`), author-locked pages import as
world-editable. Re-verified 2026-07-27 and **escalated MEDIUM → HIGH**; the one-line fix
and the separate data-repair note are in A.6.

### Still present — everything else

All of the following are unchanged, byte-for-byte, on `combined` (`dbe6c6da`) and — where
the file exists on that branch — on `Claude1` (`1e7bd6f0`):

| Finding | File | Status |
|---|---|---|
| Privilege escalation (`AccessLevel.Player`) | `CharacterTransferCommands.cs:28` | present on `combined` only |
| Receive callback catches only `SocketException` | `ConnectionListener.cs:122` | present |
| `Listen()` recurses from its own catch | `ConnectionListener.cs:83` | present |
| Guaranteed NRE (`== null && !…`) | `MotionInterp.cs:747` | present |
| `List<>` index assign on empty list | `PartArray.cs:367` | present |
| `SetStatic`/`SetDynamic` never `Clear()` | `CellArray.cs:17-38` | present |
| Redundant sphere tests | `Polygon.cs:400-412` | present |
| Quadratic `InsertIntoCell` | `Transition.cs:789` | present |
| Static `rwLock` shared across instances | `ObjectMaint.cs:23` | present |
| `AssignDivide` multiplies | `Effect.cs:107-109` | present |
| `try`/`finally` with no `catch` | `LootGenerationFactory.cs:36-104` | present |
| Unsynchronised static dictionaries | `AllegianceManager.cs:24,29` | present |
| `ServerPerformanceMonitorAutoStart: true` | `Config.js:51` | present (see note) |
| E.0d `ActionQueue` no exception isolation | `Actions/ActionQueue.cs` | present — 0 `catch` in file |
| M.0a tick loop no exception isolation | `WorldManager.cs:332-395` | present — 0 `catch` in file |
| F.0a `Rescale` divide-by-zero → NaN | `CantripChance.cs:268` | present — still no `total == 0` guard |
| W.0a discarded return → silent item loss | `Player_Trade.cs:261-265` | present |
| D.3 character-name TOCTOU | `ShardDatabase.cs:583` | present — still no unique constraint |
| D.4 unsynchronised `scrollsBySpellID` | `WorldDatabaseWithEntityCache.cs:134` | present |
| D.5 SQL escape misses backslash | `SQLWriter.cs:85` | present |
| C.5 `SubtractTicks` validates addition | `DerethDateTime.cs:622` | present |
| M.1 normalises a discarded copy | `CollisionInfo.cs:50-54` | present |

**Method:** each row above was checked by reading the current source on both branches, not
by reasoning about dates. An earlier pass of this section wrongly reported "nothing fixed"
because it only looked for commits *after* the audit date and missed `5385d19d`, which was
already merged into the branch. Fixes can arrive in commits that predate your checkout —
**check content, not chronology.**

**`Config.js` note:** it is **gitignored** (`.gitignore:299`), so it cannot be checked from
git history — only the working-tree file. That file still reads `true` at line 51.
`Config.js.example:54` correctly ships `false`. Any change here is local-only and will not
appear in a diff.

## Design notes

| Report | Question | Verdict |
|---|---|---|
| [design-socket-listener-modernisation.txt](design-socket-listener-modernisation.txt) | Modernise the UDP listener — use System.IO.Pipelines? | **Not Pipelines** (wrong tool for datagrams); modernise with `SocketAddress` overloads, concurrent receives and `ValueTask` async |

## Fixes applied

| Report | Scope | Outcome |
|---|---|---|
| [fixes-applied-2026-07-30.txt](fixes-applied-2026-07-30.txt) | Every fix that fits **one file, ≤3 lines** | **17 fixes / 12 files**, +24/−17 lines; solution builds clean; adapter fixes re-verified by re-running the round-trip suite |

Includes the full list of what was **deliberately skipped** and why — RT.1,
exception isolation, W.0a, M.1, C.6 — plus three places where the documented fix was
**wrong** and I did something different:

- **E.3** — the docs said `else wo.Destroy();`. That *leaks* the object when
  `TryRemoveFromInventory` fails, trading a double-free for a resource leak. Removed
  the inner `Destroy()` instead, so the item is destroyed exactly once.
- **C.7** — the docs said initialise `ShortestEvent` to `double.MaxValue`; that needs
  two edits and makes an event-less monitor print `1.79E+308`. Seeded on the first
  event instead: one line, no display regression.
- **F.0a** — the documented 1-line guard **does not actually fix it**, so it was left
  alone rather than shipped broken. See the report.

## Executed tests

| Report | What was run | Outcome |
|---|---|---|
| [roundtrip-adapter-tests.txt](roundtrip-adapter-tests.txt) | Round-trip tests over all 6 round-trippable `ACE.Adapter` conversion pairs, on .NET 10 | **6 defects** (2 previously undocumented), plus **empirical confirmation of A.6 and A.7** |

Unlike the audit reports, that document records **observed program output**, not
analysis. Two results worth pulling out:

- **A.6 confirmed, and worse than described** — `IgnoreAuthor` inverts in *both*
  directions on a *single* pass. The wire byte is correct, which isolates the defect
  to the getter and confirms "fix the getter, not the setter."
- **Every failure in this subsystem is silent.** Each converter ends in
  `catch { return false; }` with no logging. One consequence found by running it: an
  undocumented NRE (RT.2) *masks* documented finding A.7 entirely, making A.7 look
  unreproducible. Finding any of this required an `AppDomain.FirstChanceException`
  handler to observe exceptions the converters swallow.

## Investigated issues

| Report | Symptom | Status |
|---|---|---|
| [issue-duplicate-allegiance-biotas.txt](issue-duplicate-allegiance-biotas.txt) | Duplicate allegiance biotas saved when a new allegiance is created | **Root cause identified** |

These are root-cause analyses of *observed* symptoms, so they carry stronger
evidence than the audit reports — the failure is known to occur, and the analysis
only has to explain it. The allegiance one traces to a check-then-act where the
existence check runs synchronously against the database while the create is
*queued* onto the serialized worker — so the check can run before the write it is
meant to observe. **It does not require two threads**, which is why it is
intermittent.

## If your goal is CPU / allocation / GC reduction

Start with **[cpu-gc-plan.txt](cpu-gc-plan.txt)** — a cross-cutting execution plan
that pulls the CPU and allocation items out of all 12 performance reports and orders
them by *(expected win × confidence) / risk* rather than by subsystem. It is tiered:

- **Tier 1** — 14 local, low-risk items, several already verified. Safe to apply
  without measurement, and they reduce noise for everything after.
- **Measure** — establish a baseline before Tier 2. This is now the binding
  constraint, not the accuracy of the analysis.
- **Tier 2 / Tier 3** — the larger wins, but Tier 3 contains *every retraction in
  this doc set*. Establish the invariant before touching those.

The section below gives the GC **cost model** the plan's ordering rests on; the
per-subsystem `perf-*.txt` reports give the detail.

## Where to reduce GC pressure

This cuts across six reports, so it lives here rather than in any one of them.

**The ranking is not by allocation volume, and that is the whole point.** A Gen0
collection costs roughly proportional to **survivors**, not to garbage — bump-pointer
allocate, copy out the few survivors, reset. Short-lived Gen0 garbage is close to
free. What actually costs is anything that *survives*: it gets copied into Gen1, then
Gen2, and Gen2 collections are what stall you. So "where are the most allocations" is
the wrong question; "what survives" is the right one, and it inverts the obvious
ordering.

### Tier 1 — Retention. Fix first, because it is not garbage at all.

- **`bugs-physics.txt` H.2** — `CellArray.Cells` and `ShadowObjects` are never
  cleared. These are not allocations that die; they are collections that grow
  monotonically for the lifetime of every physics object. Cells are long-lived
  landblock structures, so this is **Gen2 retention that never stops**. It also makes
  the *work* grow — the `ElementAt` loops are O(n²) over an unboundedly growing n,
  which is why the server degrades with **uptime** rather than with load. A leak
  beats any amount of Gen0 churn, because it raises the cost of every subsequent
  collection permanently. Full replacement class is in that section.
- **`perf-network.txt` P.8** — `cachedPackets` retains every sent packet for 120
  seconds: roughly 1 MB per session, so ~200 MB at 200 players, all promoted to Gen2 —
  when the NAK window (`MaxNumNakSeqIds`) is only 115 packets. Cap by count, not just
  by age.

### Tier 2 — Promotion. Allocations that live long enough to be copied.

- **`perf-physics.txt` M.1** — visibility lists. `GetServerObjects` copies the whole
  landblock plus 8 adjacents, then `.Where().ToList()`, then `.Except()` (builds a
  HashSet), then `.Distinct()` (another). Four-plus collections of hundreds of
  references **per cell transition** — large enough and live long enough to survive a
  Gen0 collection under load. The clearest promotion path in physics; the fix is a
  reusable per-observer list, and since they are per-observer there is no threading
  hazard. *(Frequency corrected: an earlier draft said "per tick" — verified to be
  per cell-transition, roughly an order of magnitude rarer. Still Tier 2, but do not
  expect 60Hz-scale returns.)*
- **`perf-physics.txt` M.9** — `LinkedListNode` allocations hanging off long-lived
  `MotionState`. Nodes churned during sustained combat are prime Gen1 candidates.
- **Broadcast recipient lists** — `ObjectMaint.GetKnownPlayersValuesAsPlayer`. Found
  **independently by three audits** (`perf-network.txt` P.9, `perf-entity-server.txt`
  E.3, `perf-physics.txt` F.2), which is good evidence it is real. O(P²) per landblock
  per second in crowds. Fix once in `ObjectMaint` and all three benefit.

### Tier 3 — Volume. Highest raw rate, but attack it structurally.

- **`perf-network.txt` P.2** is the largest single allocation *rate* found anywhere in
  the audit: `CreateServerFragment` does a full `byte[]` payload copy **per
  recipient** — estimated ~600k `byte[68]`/sec at 100 mutually-visible players, order
  40 MB/sec. It is pure Gen0, so cheap per byte — **but fix it regardless, because the
  same line is a genuine data race**: `SendBundle` runs under `Parallel.ForEach` while
  multiple threads `Seek` the same shared `MemoryStream`. Offset-based fragments
  remove the race and the copy in one change.
- **`perf-physics.txt` Section M** — the physics per-frame churn. `M.0` establishes
  there is **not one `struct` in 162 files**; `Position` costs 80 bytes across two
  objects for 32 bytes of data. Do not pick these off individually — the leverage is
  structural. `M.2` (delete the duplicate `Init()`) halves the largest allocation site
  in one line, then struct conversion (`M.4`, `M.5`) and pooling (`M.3`, which has a
  verified escape hazard — read it before attempting).

### One multiplier that applies to all of it

Allocation on **landblock worker threads** costs more than the same allocation
elsewhere, because a GC pause there stalls the parallel physics tick for every player
in that group. That is why loot generation matters despite being bursty
(`perf-factories.txt` Section H closing note), and it is an argument for prioritising
physics and loot allocations over equally-sized ones on cold paths.

### Confirm before committing effort

None of this is profiled. `dotnet-counters` on `gen-0/1/2-gc-count` and `alloc-rate`
under a populated load settles the entire ranking above — specifically **whether
promotion is actually occurring**, which is the premise for putting Tier 1 and 2 ahead
of Tier 3. Also check `GCSettings.IsServerGC`, since Gen1 cost differs sharply between
configurations. `perf-physics.txt` Section M closes with the full list of what a
profiler would need to establish.

---

## How to read these reports

**Confidence varies, and is stated per finding.** Every report opens with a
verification block. Findings marked **VERIFIED DIRECTLY** were read line by line
and confirmed; two physics trajectory findings were **VERIFIED EMPIRICALLY** by
running a 200,000-shot harness. Everything else came from a subagent audit with a
traced, concrete failure scenario but no independent re-verification — spot-check
before writing a fix.

This matters concretely. Two hypotheses I carried into audits turned out to be
**wrong** (off-by-one RNG in Factories; per-roll table rebuilds), and one subagent
claim was **false** — that `PlayerManager.GetAllOffline()` returns `null`, which it
does not. All three are recorded as non-findings or corrections rather than quietly
dropped, because each would otherwise have misled someone.

**External calibration (2026-07-29).** Four findings were independently fixed
upstream in `5385d19d`, and in all four cases the maintainer's fix matched this
audit's recommendation essentially verbatim — including the exact one-liner
`index = value.Count - 1` and the `page.Clone()` loop. That is a real accuracy
signal, but read it narrowly, because it lands exactly where the pattern below
predicts: **all four were pure local-dataflow defects** — a wrong collection named
two lines from the right one, an off-by-one, a shallow copy where siblings deep-copy.
None required reasoning about threading, lifetimes, or cross-subsystem coupling.

This is the same split that shows up in the retractions above: **claims about local
dataflow have held; claims about system-level coupling have not.** So the four
confirmations should raise your confidence in findings of that shape specifically —
"this line contradicts the two beside it" — and should *not* transfer to the
`[Ephemeral]`, lock-topology, or cache-invalidation recommendations, which is where
every error in this doc set has occurred.

**One entry recommended a fix that would have broken gameplay**, and it is worth
singling out because it is a different and worse failure mode than a factual error.
`perf-worldobjects.txt` Q.13 originally proposed marking `TimeToRot`,
`RegenerationTimestamp` and `GeneratorUpdateTimestamp` as `[Ephemeral]` to stop
per-heartbeat database writes — and rated it "provably safe". But `[Ephemeral]` means
*does not need to be restored after a restart*, and `TimeToRot` is remaining rot time:
making it ephemeral would reset every corpse's decay countdown on every restart, so on
a frequently-restarted shard items would never decay. `RegenerationTimestamp` is
load-bearing too, via a single `== 0` test that distinguishes a fresh generator from
one that has already run. The entry now carries the retraction, the per-property
analysis, and the only real lever (write *cadence*, with an explicit
data-loss-on-crash tradeoff). **The generalisable lesson, which applies across this
whole doc set: "written often, rarely read" is not grounds to stop persisting a value
— check what depends on it across a restart first.**

**Four recommendation patterns in this doc set carry assumption risk.** After the
Q.13 error above, I audited every report for the same shape — proposing to drop,
cache or delete something without checking what depends on it. Findings are
generally safer than *recommendations*, because a finding describes code that exists
and a recommendation predicts a change is safe. Treat these four categories with
extra scrutiny:

1. **"Delete it, there are no callers."** ACE ships a **Harmony-based mod system**
   (`ACE.Server/Mods/`) that loads arbitrary assemblies from disk at runtime
   (`ModContainer.cs:32-40, 201`). Mods can reference any public API, and Harmony
   patches methods at runtime — so "no callers in this solution" does **not** mean
   "no consumers" for anything `public`. Corrected in `perf-physics.txt`
   (`MotionTable` speed dictionaries) and `bugs-physics.txt` (`Command/`): prefer
   `[Obsolete]` or a file-level comment over deletion. *Deleting an unreachable
   branch inside a method is unaffected and remains safe* — that is most of the
   "dead code" entries.
2. **"Cache it, the data is immutable."** Verified false as a generalisation here.
   `bugs-datloader.txt` D.6 documents `Texture.GetBitmap` mutating a **cached**
   `Palette` in place. `perf-datloader.txt` P.4 originally asserted `CellStruct`
   geometry was "immutable and safely shareable" — it has all-public mutable fields
   and no `readonly`. Now flagged: audit every consumer for writes before sharing
   instances, or a per-cell allocation becomes cross-cell corruption.
3. **"Cache it at equip time, it's static per item."** `perf-worldobjects.txt` Q.6
   proposed caching armour level and resistances at equip time while parenthetically
   noting "unless tinkered" — but a player can tinker an item *while wearing it*, so
   an equip-time snapshot goes stale immediately. Now flagged: enumerate every
   mutation source (tinker, imbue, admin command, post-creation mutation) or use a
   version stamp rather than a snapshot.
4. **"Reuse a scratch buffer / pool the object."** Safe only if the object never
   escapes. This one was mostly handled correctly in-line — `perf-physics.txt` M.3
   documents a verified escape hazard (`transition()` returns to five external
   callers), and M.1 carries a "confirm no caller retains the list" caveat — but
   apply the same check to any new pooling.

**Both previously-flagged assumptions have now been resolved by direct verification,
and both were wrong** — one seriously:

- **`ObjectMaint.rwLock` must stay static.** The proposal to make it per-instance
  would **deadlock the server**. There are twelve cross-instance calls inside
  `ObjectMaint.cs` of the form `obj.ObjMaint.<Method>(PhysicsObj)`, maintaining
  visibility "for both parties" — and they are made *while holding the lock*
  (`AddKnownObject`, lines 194-215). With one static lock declared
  `SupportsRecursion` that is safe same-thread re-entry; with per-instance locks it
  is a textbook lock inversion, triggered by two players simply entering each other's
  visibility range on different worker threads. The static lock is **load-bearing,
  not an oversight**. The contention is still real, but the fix must be lock-free
  reads, or hoisting the paired writes out of the lock, or a total lock order — not
  changing the field modifier. Corrected in `perf-physics.txt` F.1, which was
  previously and wrongly labelled "the single best change in this document".
- **The `ObjDesc` invalidation list was incomplete.** Reading both
  `CalculateObjDesc` implementations shows it depends on `ClothingBase`,
  `HeadObjectDID`, `PaletteTemplate`, `SetupTableId` and `Shade`, plus — for
  creatures — `EquippedObjects` and each equipped item's `TopLayerPriority` and
  `VisualClothingPriority`. So equip/dye/cloak/barber misses **(a)** visual-property
  changes on a *worn item*, which must propagate to the wearer, and **(b)**
  `SetupTableId` changes, i.e. `@morph` and every transform — under which a morphed
  creature would keep rendering its old model to every observer. Corrected in
  `perf-worldobjects.txt` P.2, which now recommends a version stamp over an
  enumerated hook list, precisely because enumerating is what failed here.

**A full sweep of the remaining recommendations was then done, and it found one more
of the same kind plus two that need re-framing.** The results are worth stating
because they show *which* kinds of recommendation failed:

- **`perf-physics.txt` O.5 — retracted fix.** It advised "honour the existing
  `CellArrayValid` flag and skip the cell rebuild." Verified: `CellArrayValid` is
  assigned in **eleven** places and **never read anywhere** — write-only dead state,
  the same shape as the `NumCells` counter in the H.2 bug. Its set/clear discipline
  has therefore never been exercised, so there is no evidence it is correct, and
  honouring it could silently skip a required rebuild. Now recommends deriving the
  skip condition directly instead. **An existing mechanism being present is not
  evidence it is correct** — that is the third time that exact assumption has failed
  here (`ObjDesc` invalidation, `CellArrayValid`, and the `[Ephemeral]` attribute).
- **`perf-physics.txt` O.1 — re-framed.** Collapsing the quadratic insertion nesting
  also reduces the *number of collision-resolution attempts*, so it can change
  movement outcomes in tight geometry. Valid and worthwhile, but it is a behaviour
  change to be validated against walking into corners and up stairs — not a free
  refactor, and it should not share a commit with the H.1 fix.
- **Exception isolation — caveated**, see the note under fix #2 above.

**What held up:** the "zero-risk" claims that were pure *local dataflow* — deleting a
provably-redundant `Init()` call (`M.2`), and deleting the duplicated walkable test
(`N.1`, where both methods were verified side-effect-free). **What failed:** every
claim about *system-level coupling* — locking, persistence, cache invalidation. That
is the useful split. Treat a recommendation as reliable in proportion to how local its
reasoning is.

**the recommendations in this doc set are less reliable than the findings**, and the
ones that sound most confident deserve the most scrutiny. Three of the entries
originally labelled "the single best change in this document" have now been retracted
or re-framed.

**No performance report is profiled.** All of it is reasoned from reading call paths
and weighting by call frequency. The codebase ships instrumentation —
`ServerPerformanceMonitor`, `RateMonitor` — and those counters should be the
baseline before and after any change. Note `Config.js:51` on this checkout sets
`ServerPerformanceMonitorAutoStart: true` while the shipped `.example` default is
`false`, so that instrumentation's own overhead is live here.

**"Investigated, not a bug" sections are deliberate.** Every report has one. They
record patterns that look wrong but are intentional, with the reason — `Chest.Close`
apparent infinite recursion (C# member lookup excludes the overridden base), the
`ElementAt` grow-during-iteration in physics (a snapshot would drop transit cells),
`ThreadSafeRandom`'s inclusive bounds. They exist so later passes don't re-burn
effort.

**Coverage is not saturated — and this is now measured, not assumed.** Physics had
three bug passes, and *each found more than the last*: 5, then 10, then 14. Pass 3
found the most because it was aimed deliberately at ~7,900 lines the earlier passes
never read. That is not the code getting worse; it is evidence that breadth-first
sampling of a large hand-ported subsystem does not converge quickly. **Every other
subsystem has had exactly one pass**, so read each report as a lower bound.

**The most productive lens on ported code is the porting artifact.** Physics is a
hand-port of the original C++ client engine, and nearly every high-severity finding
in it is a C idiom whose semantics silently changed in translation: a C array plus
count becoming a .NET collection whose count no longer clears it; C value semantics
becoming C# reference aliasing; `ref` applied to a struct *parameter copy* so the
result is discarded; unset `out` params defaulting to `NaN`; a decompiled `goto`
graph with every branch left commented out. Leading with that lens found far more
than generic bug patterns did.

**Severity is scoped to reachability.** `bugs-datloader.txt` describes ~170 parsers
trusting file-supplied lengths. That reads alarmingly in isolation, but dat files
are operator-supplied and parsed at startup from local disk — not a remote attack
surface. Contrast `bugs-network.txt`, where the same *class* of defect is reachable
by unauthenticated remote input. Don't quote either without its reachability context.

---

## Cross-cutting themes

Worth knowing before picking up individual fixes.

**Non-atomic transfers.** Nine of fourteen WorldObjects findings share one shape:
remove from source, add to destination unchecked, no rollback. The correct pattern
exists in-tree in exactly one place (`Player_Inventory.cs:3342-3356`). A single
checked-transfer helper would close the whole class.

**Unvalidated counts as allocation sizes or loop bounds.** Dominates
`bugs-datloader.txt`, appears in `bugs-network.txt` (remote), `bugs-factories.txt`
(chargen), and `bugs-database.txt`. In DatLoader one change to
`UnpackableExtensions.cs` hardens ~40 of 51 parsers at once.

**Guards that don't match what the code reads.** Where one branch guards correctly
and an adjacent branch doesn't, it's a copy-paste slip, not intent — confirmed
repeatedly across `bugs-command.txt` and `bugs-managers.txt`.

**Fixes that resolve a bug and a performance problem together.** The physics
`CellArray` rewrite, DatLoader's `streamMutex` and block walk,
`UnpackableExtensions`, and the network fragment race (`perf-network.txt` P.2 fixes
a genuine data race while removing the largest per-recipient allocation). Sequence
these together rather than fixing each twice.

**One bug is currently acting as an optimization.** `bugs-physics.txt` G.13 —
`AFrame.Equals` compares a value against itself, so pure rotation reads as
"unchanged" and skips `transition()` entirely. Fixing it is correct, but it will
route every rotating creature through the full collision path and *increase*
per-frame cost. It must land alongside the allocation fixes in `perf-physics.txt`,
or it will look like the fix caused a regression. Full note in `perf-physics.txt`
Section E.

**Work proportional to content rather than activity.** The landblock tick, broadcast
recipient lists, and physics cell tracking all scale with total objects rather than
active ones. The codebase already solved this correctly once — the sorted next-tick
lists in `Landblock` — and the same principle applies elsewhere.

---

## Suggested next steps

1. **Merge `5385d19d` into `combined`.** Four defects fixed on `master` are still
   live there, including the two book bugs that compound each other (S.1 + S.2).
   This is now the cheapest available win.
2. Address the privilege escalation (on the branches where it applies), then the
   exception isolation — still four unguarded entry points, all re-verified present.
3. Spot-verify any finding before acting on it.
4. Profile against `ServerPerformanceMonitor` before acting on any perf report.
5. Consider a fourth physics pass. Both remaining gaps are named in
   `bugs-physics.txt` Section F: `Common/` (7,677 lines, untouched by pass 3) then
   `MotionTable.cs` against the original client's `CMotionTable` semantics.
6. **Round-trip test for `ACE.Adapter` (convert A→B→A and diff) — now the highest-
   value test to write.** Both fixed adapter findings (A.0a, A.0b) were
   wrong-collection defects that a round-trip diff would have caught mechanically.
   They were instead found by reading and fixed by hand, which means the *next* one
   will be too. This is the only item on this list that prevents recurrence rather
   than fixing an instance.
