# Project Instructions

## Purpose
Build the file browser interview project as a small, working C# and vanilla JavaScript application. Prioritize correctness, readability, bounded resource use, and code that is easy to discuss in review.

## KISS
- Choose the simplest design that satisfies the agreed requirements.
- Prefer platform capabilities and explicit control flow over dependencies, frameworks, and clever abstractions.
- Keep functions focused and cohesive.

## DRY
- Centralize filesystem path validation and shared API rules.
- Keep client URL state and API access in one place.
- Extract repeated logic when it is truly repeated; do not force abstractions over merely similar code.
- Server validation is authoritative. Client validation may repeat checks for usability.

## SOLID, applied pragmatically
- Give each class or module one clear responsibility.
- Separate HTTP handling, filesystem operations, URL state, API access, and DOM rendering.
- Prefer composition and small contracts.
- Add interfaces only where a concrete substitution or testing need exists.

## YAGNI
- Implement only the agreed interview requirements and selected additions.
- Do not add speculative extension points, plugins, caching, indexing, authentication, generalized file-operation infrastructure, or unused configuration.
- Add complexity only to meet a stated requirement or a demonstrated need.

## NASA Power of 10, adapted
These project rules are inspired by NASA's Power of 10 and are not a claim of NASA compliance.
1. Use straightforward control flow; avoid recursion, dynamic execution, and clever flow. Traverse directories iteratively.
2. Bound every loop and work queue. Filesystem search has explicit entry, queue, and elapsed-time limits and honors cancellation.
3. Bound retained memory and rendered rows. Stream file transfers; do not buffer full uploads or downloads.
4. Keep functions cohesive and generally around 60 logical lines or fewer. Split by responsibility, not mechanically.
5. Check meaningful input and invariants at runtime boundaries. Assertions are for internal assumptions; there is no arbitrary assertion quota.
6. Keep state and variable scope narrow. Avoid mutable globals and unnecessary shared state.
7. Handle meaningful return values, filesystem errors, rejected promises, and cancellation; do not silently swallow failures.
8. Keep build configuration simple. Avoid conditional-compilation branches, runtime code generation, and unnecessary build machinery.
9. Avoid unsafe code, pointer manipulation, `eval`, and `new Function`.
10. Enable compiler warnings, resolve project warnings, and run relevant checks and tests before delivery.

Managed allocations are allowed; the static-allocation rule is not applied literally. Document any necessary exception. Test observable behavior and boundary cases rather than mirroring implementation details.
