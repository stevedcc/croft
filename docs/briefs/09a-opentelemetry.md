# Brief 09a: Croft reports what it is doing (OpenTelemetry)

Read AGENTS.md first and follow it. Implement inline (`executing-plans`), one commit per item, TDD,
break-it-once for each key test, E2E twice at the end, the final whole-branch review. Other briefs
may run at the same time; when the orchestrator says another brief was accepted, `git fetch origin`
and `git merge --no-edit origin/blog-framework` (never rebase). Known overlap: both apps'
`Program.cs` and the k8s base deployments; add to them, don't restructure them.

**Owner-visible outcome:** with `OTEL_EXPORTER_OTLP_ENDPOINT` set, Web and API send metrics,
traces and logs to an OpenTelemetry collector, so the owner's Grafana (VictoriaMetrics +
VictoriaLogs, set up separately) shows request rates, latency, errors, Blazor circuits, database
queries, and one trace per reader request from Web through the API to SQL. Without the variable,
nothing is exported and nothing changes. A 404 or a validation error is no longer logged as an
error.

**Allowed new packages** (latest stable releases, same version line across the solution; the
AGENTS.md "no package upgrades" rule does not stop these, and existing packages stay as pinned):
`OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`,
`OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`,
`OpenTelemetry.Instrumentation.Runtime`, `Npgsql.OpenTelemetry` (the version matching the Npgsql
that `Npgsql.EntityFrameworkCore.PostgreSQL` 10.0.3 resolves to), and for tests only
`OpenTelemetry.Exporter.InMemory`. Nothing else; no preview versions.

## Item 1: one telemetry setup for both apps, off unless configured

**Now:** both apps log to the console in plain text through `ILogger`; there is no OpenTelemetry,
no metrics export, no tracing. Logging already uses source-generated `[LoggerMessage]` methods
(12, in 8 files) with structured fields.

**Do:**
- One shared extension (in `Croft.Shared` or a small shared place both apps already reference;
  say where in the commit) that both `Program.cs` files call, e.g.
  `builder.AddCroftTelemetry("croft-web")` / `("croft-api")`.
- Resource: `service.name` (`croft-web` / `croft-api`, overridable by the standard
  `OTEL_SERVICE_NAME`), `service.version` (the assembly's informational version), and whatever
  `OTEL_RESOURCE_ATTRIBUTES` adds.
- Metrics: ASP.NET Core and Kestrel meters, the .NET 10 Blazor/SignalR meters in Web
  (`Microsoft.AspNetCore.Components*`, `Microsoft.AspNetCore.Http.Connections`,
  `Microsoft.AspNetCore.SignalR*`; confirm the exact meter names against what .NET 10 emits, by
  listing them in a test), HttpClient, runtime, Npgsql (both apps: Web has its own database since
  08a).
- Traces: ASP.NET Core server spans, HttpClient client spans (so the API span is a child of the
  Web span: W3C `traceparent` propagation, which HttpClient does by default; prove it), Npgsql
  spans. Don't trace `/health*` requests (probes every few seconds would drown everything else).
- Logs: `ILogger` through the OpenTelemetry logging provider with formatted message and scopes,
  so each log record carries its structured fields and the active trace and span ids. The console
  logger stays.
- Export: OTLP only, and only when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (the standard OTLP
  environment variables configure everything else). Unset: no exporter is registered and the apps
  behave exactly as today, including startup time within noise.
- Privacy (the site's privacy statement promises this): no exported span, metric or log attribute
  carries the client IP address, a token, a cookie value, an email address, or a query-string
  value (search terms are reader input). Check what the ASP.NET Core and HttpClient
  instrumentation emit by default in these versions, and remove or redact what crosses that line
  (enrich/filter hooks), rather than relying on assumptions.

**Tests** (in-memory exporter, each test sets up its own host or factory):
- Unset endpoint: no OTLP exporter is registered in either app (inspect the configured providers);
  a request still succeeds.
- A request to a public page on Web that calls the API produces a Web server span, a Web HttpClient
  span, and an API server span with the same trace id, the API span's parent being the HttpClient
  span. An API request that queries the database produces an Npgsql span under the API server span.
- `/health/live` and `/health/ready` produce no spans.
- Metrics: after one request, `http.server.request.duration` is recorded with route and status code;
  in Web, at least one Blazor circuit metric is recorded after a circuit opens (bUnit can't open a
  circuit: use the E2E host or a test server with a real circuit, whichever is simpler, and say
  which).
- Logs: an existing `[LoggerMessage]` log written inside a request is exported with its named
  fields as attributes and the request's trace id.
- Privacy: a request from a known client IP, with a cookie, an `Authorization` header and a query
  string `?q=secretterm`: none of these values appears in any exported span, metric or log
  attribute (search all attribute values as strings).
- Break-it-once: remove the `/health` filter, the health test fails; disable the redaction, the
  privacy test fails.

## Item 2: a 404 is not an error

**Now:** `Croft.Api/Errors/ApiExceptionHandler.cs` logs every handled exception at `Error`
("An unhandled exception has occurred while executing the request."), including the ones it maps
to 400, 403, 404 and 409. With alerts on error logs, every bad link would page the owner (open
action "`ApiExceptionHandler` log levels" in the retros).

**Do:** 500s (anything unmapped) stay `Error` with the exception. 400, 403, 404 and 409 log at
`Information` without the stack trace, with the exception type, the status code and the request
path as structured fields (a separate `[LoggerMessage]`). The message text says what happened
("Request to {Path} ended with {StatusCode} ({ExceptionType})"), not "unhandled".

**Tests:** one per mapped status (400, 403, 404, 409 incl. the unique-violation `DbUpdateException`)
asserting level `Information`, the fields, and no exception attached; an unmapped exception logs
`Error` with the exception. Break-it-once: map 404 back to the error log; its test fails.

## Item 3: a few Croft metrics

**Do:** a `Meter` per app (`Croft.Api`, `Croft.Web`), registered in item 1's setup, with counters:
- API: `croft.page_views.recorded` (a page view stored; not counted when deduplicated), attribute
  `outcome` = `recorded` | `duplicate`; `croft.posts.published`, attribute `source` = `editor` |
  `scheduled`.
- Web: `croft.csp.reports`, attribute `directive` (the effective directive only; never the
  blocked URI or the document path, which can carry reader data); `croft.token.refreshes`,
  attribute `outcome` = `refreshed` | `transient_failure` | `failed`.

Follow the OpenTelemetry naming conventions (lower case, dots, units where they apply); keep
attribute values to the fixed sets above so the number of series stays small.

**Tests:** each counter increments once for one event with the right attribute (in-memory
exporter or `MeterListener`); a duplicate page view increments `duplicate`, not `recorded`.
Break-it-once: drop the scheduled publisher's increment; its test fails.

## Item 4: the k8s base passes the settings through

**Do:** in the Croft k8s base, both deployments get `OTEL_SERVICE_NAME` (`croft-web`,
`croft-api`) and `OTEL_RESOURCE_ATTRIBUTES` with `k8s.pod.name` and `k8s.namespace.name` from the
downward API. `OTEL_EXPORTER_OTLP_ENDPOINT` is not set in the base (sites add it; the base's
README says how, with an example value `http://otel-collector.observability:4317`). The k8s
README gains a short "Telemetry" section: what is exported, the variables, the privacy rule from
item 1.

**Tests:** the existing k8s manifest tests (or a new one in the same style) assert the two variables
on both deployments and that no OTLP endpoint is set in the base.

## Facts

- E2E: `dev/run-e2e.sh --instances 3 --first-instance 5` (check `ss -ltn` for other briefs'
  instances first and pick a free block if 5-7 are taken).
- The `test-runner` subagent may be on an exhausted model: run builds and suites yourself, output
  to a file, read the summary lines. Do not use `pattern-applier`.
- No collector is needed to run the tests; nothing in the tests may need network access beyond
  the local test hosts.

## Out of scope

The collector, VictoriaMetrics, VictoriaLogs, Grafana and dashboards (cluster work, done by the
orchestrator and the owner); tracing sampling policy beyond "always on" (sites can set
`OTEL_TRACES_SAMPLER`); new log messages beyond item 2's; any change to what the console logger
prints.
