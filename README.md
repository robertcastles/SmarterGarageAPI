# SmarterGarageAPI

## Deterministic Android/DHU test fixture

Existing simulator mode remains unchanged when `SMARTER_GARAGE_TEST_MODE` is absent.
To run the fixture on HTTPS 3000 and loopback HTTP controls on 3001:

```powershell
$env:SMARTER_GARAGE_TEST_MODE = '1'
$env:SMARTER_GARAGE_USE_DEV_CERT = '1'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet dev-certs https --check
dotnet run --project .\SmarterGarageAPI.csproj -c Debug --no-launch-profile -- 3000 3001
```

If no development certificate exists, create one with `dotnet dev-certs https`.
Trust is optional for the app's explicitly configured home-test TLS bypass. This
option avoids loading the legacy source-configured certificate; do not commit
certificate passwords, certificates, or private keys. Remove the two fixture
environment variables to return to the legacy simulator. Keyboard controls are
disabled in fixture mode. No launch settings or developer configuration is rewritten.

The Android emulator uses `https://10.0.2.2:3000`. The API preserves the existing
device discovery response and status/command shapes. Configure only a fake device
and optional fake test token in the app. Pings return `{"message":"ok"}` without
logging their body. Header diagnostics record **presence only**, never token values.

### Local control API

Controls accept requests only on `http://127.0.0.1:3001` (or IPv6 loopback).
They are denied on HTTPS/the externally reachable API port. Non-control API traffic
is rejected on the unencrypted control port. Never proxy the control port to a network.

- `POST /__test/reset`: body defaults to
  `{"status":"closed","startDelayMs":8000,"motionMs":10000}`. Cancels pending
  motion, invalidates pre-reset delayed commands, and clears scripts/counters.
- `GET /__test/snapshot`: current status/timings/generation, per-endpoint `requests`,
  per-endpoint `authorizationPresent`, and ordered `acceptedCommands` (open/close only).
  Controls themselves are not counted.
- `POST /__test/script`: e.g.
  `{"endpoint":"status","delayMs":100,"statusCode":429,"body":"{}"}`.
  Endpoint categories: `devices`, `status`, `commands`, `ping`. Scripts are consumed
  once in FIFO order per category. Delay-only scripts continue normal handling;
  supplying a status code or body bypasses it. Thus a scripted command error does
  not execute motion. A successful scripted ACCEPTED body also does not execute motion.
  Use raw malformed bodies to exercise client parsing. Timings/delays are bounded
  to 120 seconds; bodies to 64 KiB; queues to 100 entries/category.

OPEN follows closed → opening → open; CLOSE follows open → closing → closed.
An opposite command cancels previous simulated motion rather than starting a
racing thread. Reset/status overrides are test controls, never real garage commands.
Request counters count arrivals, including rejected/scripted requests; accepted
command history counts validated normal executions separately. Snapshot does not
establish credential correctness, only whether an Authorization header was present.

### Validation

With this process running, execute:

```powershell
python .\tools\test_fixture.py
```

Six black-box tests cover open/close counting, header redaction, reset cancellation,
delayed requests, error/malformed scripts, invalid commands, and control boundaries.
The suite resets the fixture afterward. Run it without concurrent Android polling
when making exact count assertions. Python HTTPS bypass is restricted to localhost
in this test script, not a general network client.

The project still targets .NET 7, which is out of support. A supported-framework
migration should be reviewed separately; the current fixture does not silently
change your target framework or existing dependency/configuration edits.
