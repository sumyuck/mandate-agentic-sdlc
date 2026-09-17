# Run `run_20260917T020240Z_c6e2f4`

- **Request:** The shortener needs rate limiting so one client cannot flood it.

Add sensible limits to the create endpoint and make sure abusive clients get throttled
rather than taking the service down for everyone else.
- **Workflow:** `sdlc@v1`
- **Scenario:** Ambiguous
- **Status:** Failed
- **Events:** 228
- **Audit chain:** run_20260917T020240Z_c6e2f4: chain intact across 228 event(s).

## Stages

| stage | state | attempts | detail |
|---|---|---:|---|
| `architecture` | Succeeded | 1 | Approved; exit gate passed without re-running the stage. |
| `clarification` | Skipped | 1 | Join policy 'All' cannot be satisfied: 'requirements' (guard false). The stage is not required on this path. |
| `code-review` | Succeeded | 1 | Exit gate passed. |
| `documentation` | Succeeded | 1 | Exit gate passed. |
| `impact-analysis` | Succeeded | 1 | Exit gate passed. |
| `implement` | Succeeded | 2 | Exit gate passed. |
| `intake` | Succeeded | 1 | Exit gate passed. |
| `release-readiness` | Pending | 0 |  |
| `requirements` | Succeeded | 2 | Exit gate passed. |
| `security-scan` | Succeeded | 1 | Exit gate passed. |
| `test` | Failed | 3 | Verification ran but produced no figure for test.failures or test.coverage. The toolchain reported: No tests ran (tests/Service.Tests/RateLimitPolicyTests.cs(3,24): error CS0234: The type or namespace name 'RateLimiting' does...). Output: tests/Service.Tests/RateLimitPolicyTests.cs(3,24): error CS0234: The type or namespace name 'RateLimiting' does not exist in the namespace 'System.Threading' (are you missing an assembly reference?) [tests/Service.Tests/Service.Tests.csproj]
tests/Service.Tests/ClientKeyResolverTests.cs(20,59): error CS0234: The type or namespace name 'Extensions' does not exist in the namespace 'Microsoft' (are you missing an assembly reference?) [tests/Service.Tests/Service.Tests.csproj] |

## Decisions

### `requirements-client-identifier-choice`: How should "one client" be identified for the purpose of rate limiting the create endpoint?

**Chosen:** IP address (confidence 0.6, by agent:requirements-analyst)

IP-based limiting is the safe default for a public create endpoint and does not presuppose infrastructure (auth) that isn't confirmed to exist; documented as an assumption to be revisited if the endpoint is or becomes authenticated.

- Rejected **API key / authenticated account id**: No evidence in the request or its framing that the create endpoint requires authentication; assuming an auth layer that may not exist risks building a limiter with no key to read.

### `requirements-rate-limit-algorithm`: Which rate-limiting algorithm should govern the create endpoint?

**Chosen:** Fixed window counter (confidence 0.97, by agent:requirements-analyst)

The requester's recorded clarification explicitly says 'Fixed window of 60 create requests per API key per minute,' so this is not a judgment call, it is a given.

- Rejected **Sliding window log/counter**: Not what the requester specified, and adds implementation complexity (timestamp tracking or interpolation) the clarification did not ask for.
- Rejected **Token bucket**: Permits bursts above the flat 60/window figure the requester gave, which would silently change the guarantee they asked for.

### `requirements-client-identification`: How should a 'client' be identified for the purpose of counting requests?

**Chosen:** API key, with client IP as fallback for unkeyed requests (confidence 0.95, by agent:requirements-analyst)

Directly matches the recorded clarification: 'Only POST /api/v1/links is limited. Requests without an API key are limited by client IP on the same terms.'

- Rejected **API key only, unkeyed requests unlimited**: Leaves the exact flood vector the request describes — an anonymous client hammering the endpoint — completely unthrottled, defeating the stated purpose.
- Rejected **IP only for all requests, ignore API key**: Punishes multiple distinct keyed clients sharing an egress IP (e.g. behind NAT or a corporate proxy) as one client, which the requester did not ask for and which is a worse default than keying by identity when identity is available.

### `requirements-counter-storage`: Where should rate-limit counters be stored?

**Chosen:** In-process memory, per instance (confidence 0.95, by agent:requirements-analyst)

The clarification states this directly. The known consequence — that a client's effective limit multiplies with instance count — is recorded as an explicit out-of-scope limitation, not silently absorbed.

- Rejected **Shared external store (e.g. Redis, database)**: Explicitly excluded by the requester's clarification ('no shared store, no distributed coordination'); would also add an infrastructure dependency and failure mode out of scope for this change.

### `impact-analysis-blast-radius-classification`: Whether this change is medium or high blast radius

**Chosen:** high (confidence 0.75, by agent:codebase-analyst)

The rubric defines high as 'a public contract... or anything on a path where failure is not locally contained.' Both conditions are met here independently: the create endpoint's documented response contract gains a new status and header, and the mechanism (HTTP middleware or endpoint-level policy) sits on the shared ASP.NET Core pipeline where a scoping error bleeds into every other route.

- Rejected **medium**: It still adds a new HTTP status code (429) and a new response header (Retry-After) to a documented, versioned contract in contracts/openapi.yaml, and — per the rubric's own definition — a public contract change is high regardless of how few files are touched. It also sits on a path (the request-handling pipeline in Program.cs) where a scoping mistake does not stay local: ASP.NET Core rate-limiting middleware applied incorrectly affects every route mapped after it, not just the create endpoint.

### `architecture-rate-limiter-mechanism`: What mechanism enforces the 60-requests-per-60-seconds-per-client cap, given it must be concurrency-safe, memory-bounded, and use no external store?

**Chosen:** ASP.NET Core built-in RateLimiter middleware (fixed-window, partitioned by client key) (confidence 0.85, by agent:architect)

It ships in the shared framework (no new package), its FixedWindowRateLimiter is a proven, race-free primitive that satisfies AC10 by construction rather than by careful hand-written locking, it produces Retry-After metadata on rejection for free (AC5), and per-route attachment via RequireRateLimiting keeps every other endpoint (AC8) untouched without relying on careful middleware ordering.

- Rejected **Hand-rolled ConcurrentDictionary<string, WindowState> counter**: Duplicates a primitive the framework already implements correctly under concurrency; a naive read-then-write increment is exactly the race the impact analysis flags as the highest-risk bug (AC10), and eviction/expiry would need to be built and tested from scratch.
- Rejected **External store (Redis / distributed cache) backed counter**: Explicitly out of scope: the clarification states 'no shared store, no distributed coordination,' and the NFRs forbid new external dependencies.
- Rejected **Sliding-window or token-bucket algorithm**: The requirement specifies a fixed window with no burst allowance; this out-of-scope algorithm class is explicitly excluded in docs/requirements.md §3.

### `architecture-check-placement`: Where does the rate-limit decision happen: inside the HTTP layer before LinkService is invoked, or inside LinkService itself?

**Chosen:** HTTP layer (Program.cs / middleware), before LinkService.CreateAsync is ever called (confidence 0.85, by agent:architect)

Rate limiting is about a caller's request rate, not about the validity of the link being created — it belongs with the other HTTP-layer-only decision in this codebase (status-code selection), keeps LinkService's existing test suite and behavior stable, and matches the requirement's exact scope (this one route only).

- Rejected **Inside LinkService.CreateAsync, via a new parameter and CreateOutcomeKind.RateLimited member**: Breaks the signature of a method all 11 existing LinkServiceTests call directly with a single argument, forcing every test to change for a concern (client identity, request timing) that has nothing to do with link-creation domain rules; it also mixes an HTTP/transport concern (who is calling, how fast) into a layer that the existing design explicitly keeps HTTP-agnostic.

