# User behavioral integration tests

From the workspace, start the isolated demo with `pwsh -File Frontend/demo/Demo.ps1 -Action start`, then run
`Frontend/demo/Test.ps1`. That runner reads ignored local configuration without
printing credentials and sets `USER_TEST_MONGO` and `MUSIC_TEST_STORAGE` for this suite. To run only this
project, configure the isolated Mongo replica set and optional storage emulator, then run:

```powershell
dotnet test User/tests/User.Tests/User.Tests.csproj -c Release
```

Missing configuration fails explicitly. Each factory creates a uniquely named
`spotibuds_test_<guid>` database and drops only that database after completion.
The server must be a Mongo replica set because production mutation paths use
transactions; the connection uses direct mode for the loopback demo topology.
CI starts and removes its own disposable Mongo replica set and Blob emulator,
using a freshly generated test-only storage credential. The avatar test requires
`MUSIC_TEST_STORAGE`; it explicitly reports a skip when this optional storage
configuration is missing. The shared local runner and CI supply it so complete
runs exercise real Blob bytes with zero skips.

The suite exercises the real ASP.NET authentication and authorization pipeline,
signed JWTs, controllers, services, Mongo persistence and unique indexes. Real
SignalR clients use long polling through the in-process server. The only external
HTTP substitutions are the Identity session-status service and Music metadata;
their responses are controlled explicitly to test revocation and failure.

The complete current suite has 19 passing tests with zero skips when both
dependency settings are supplied. Assertions cover rejected forged/expired JWTs and session-store outages,
anonymous and foreign-account writes, private direct and aggregate reads,
empty profile clearing, concurrent follows and chat creation, canonical REST/hub
message delivery and deduplication, membership checks, idempotent read receipts
through the actual `MarkAsRead`, `MarkMessageAsRead` and `MarkAllMessagesAsRead`
hub commands, signed access-token expiry closing the live hub connection,
notification ownership and server-only hub methods, feed actor forgery, retained
history beyond 100 events, deterministic weekly rankings and multi-tab presence.
Additional integrity cases exercise actual avatar decoding and Blob reads,
spoofed/truncated/oversized uploads, previous-image preservation, private avatar
access, typed profile input rejection and persisted Mongo transaction rollback.
Two simultaneous avatar replacements exercise compare-and-swap conflict handling
and actual loser Blob deletion. Catalogue metadata tests reject unknown songs,
forged titles/artists and excess duration, and expose dependency failures honestly.
Negative cases assert persisted state, not only HTTP status.

The companion live scripts under `Frontend/demo` use the actual Identity,
Music, User, PostgreSQL, Mongo, Mailpit and Blob services. Their ignored JSON
reports contain check names and results, never account passwords or tokens.
This suite does not claim production load testing or a remote CI execution.
