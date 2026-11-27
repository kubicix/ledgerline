# M4 implementation decisions

These decisions keep their rationale and what each costs if wrong. Entries that a
later task reversed say so; the current state is stated in the entry. The M3
equivalent is [M3 decisions](m3-decisions.md).

## Rulings

Pre-flight:
- Setup: Ruling: Each task got a brief built from the task text, the global constraints, the task protocol, the traps that apply, and the spec excerpts. Implementers never commit. The orchestrator stages the exact files and commits once per task with the plan's message after review is clean. Cost if wrong: none; the history matches the M3 shape.
- Setup: Ruling: One combined reviewer per task gives both the spec-compliance and the quality verdict. Cost if wrong: weaker separation of the two review stages.

Task 1 (kind cluster):
- Task 1: Ruling: The node image stays at the kind v0.33.0 default (`kindest/node:v1.37.0`, which kind pins by digest). Cost if wrong: a kind upgrade silently moves the Kubernetes version.
- Task 1: Ruling: A process from another installed product held host port 8080. The implementer first proved the cluster on a temporary host port 18080 and restored the definition. The literal Prove on 8080 was re-run after the owner freed the port. Cost if wrong: none; the 8080 proof passed.
- Task 1: Ruling: The orchestrator fixed three review nits directly: the exec bit at commit, an exit-code check after `kubectl config use-context`, and verified install URLs. Cost if wrong: none material.

Task 2 (readiness and shutdown):
- Task 2: Ruling: The owner chose option (b): `ConsumerHost` starts its existing drain on `ApplicationStopping`, so the drain runs alongside the Kestrel stop. The timings stay at 5 s preStop, 25 s `ShutdownTimeout`, 30 s grace and at most 20 s drain. This is a timing change only; ack, commit and publish order are unchanged. Cost if wrong: in-flight HTTP-triggered work could race consumer cancellation.
- Task 2: Ruling: The owner approved the new package `Microsoft.Extensions.Diagnostics.HealthChecks` 10.0.15 in Messaging, with an ADR 0000 row. Cost if wrong: one extra `Microsoft.Extensions` dependency.
- Task 2: Ruling: The broker readiness check is `RabbitMqHealthCheck` in Messaging, named "broker". Each service's `ReadinessCheck` checks only its database ("database"). Cost if wrong: the check names in the `/health/ready` output change.
- Task 2: Ruling: Readiness returns 503 from `ApplicationStopping` through a short-circuit in the `/health/ready` endpoint pipeline, not through another health check. Cost if wrong: the pipeline differs from the `MapHealthChecks` defaults.
- Task 2: Ruling: `ShutdownTimeout` is 25 s in `AddLedgerlineWebDefaults` for all six hosts. Cost if wrong: none for hosts without consumers.
- Task 2: Ruling: `OutboxRelay` is unchanged and still cancels an in-progress batch on shutdown (see the spec deviations). Cost if wrong: a batch publish repeats after a restart, and the inbox deduplicates it.
- Task 2: Ruling: The drain test gates the host before `ConsumerHost.StopAsync`, then waits with `Eventually` for `ExecuteTask` to complete with one ack. Cost if wrong: the test relies on host stop internals.
- Task 2: Ruling: The 503-during-drain behaviour is asserted on `TestServer` only, not on Kestrel. Cost if wrong: the last spans of a stopping pod can be lost.

Task 3 (chart and service templates):
- Task 3: Ruling: The C1 infrastructure-only switch is the top-level `infraOnly` boolean. It skips the service Deployments, Services, ConfigMaps, PDBs and migration Jobs, but `templates/secrets.yaml` always renders. Cost if wrong: a rename in `kind-deploy`.
- Task 3: Ruling: Resource names equal service names, with no release prefix, so there is one release per namespace. Cost if wrong: two releases in one namespace would collide.
- Task 3: Ruling: `imagePullPolicy` is `IfNotPresent`. The preStop hook is the native `lifecycle.preStop.sleep` of 5 s (GA in Kubernetes 1.34; the node runs v1.37). Cost if wrong: clusters older than 1.30 reject the hook.
- Task 3: Ruling: A PodDisruptionBudget (`minAvailable: 1`) renders only when replicas are above 1. Cost if wrong: no disruption budget at one replica, where it would protect nothing anyway.
- Task 3: Ruling: Each service requests 100m CPU and 192Mi memory, has a 512Mi memory limit and no CPU limit. Cost if wrong: OOM under heavy load; retune the values.
- Task 3: Ruling: Containers run as non-root uid and gid 1654 with seccomp `RuntimeDefault`, no privilege escalation and all capabilities dropped. `readOnlyRootFilesystem` is false. Cost if wrong: weaker hardening.
- Task 3: Ruling: Probe timings beyond the spec: liveness every 10 s (3 failures, 2 s timeout), readiness every 5 s (3 failures, 3 s timeout), startup 30 tries 2 s apart. Cost if wrong: retune for slow CI cold starts.
- Task 3: Ruling: A per-service Secret `<svc>-secrets` holds the connection strings, the RabbitMQ URI and `Bank__ApiKey`, with the Compose development values. gitleaks is clean. Cost if wrong: a future scanner rule may flag them.

Task 4 (migration hook Jobs):
- Task 4: Ruling: C1 applied. The migration Jobs are `pre-install,pre-upgrade` hooks with the same weight, guarded by `infraOnly` being false and `migrate: true`. They read only `ConnectionStrings__Database` from `<svc>-secrets`, which the infrastructure-only pass already renders. Cost if wrong: a first install without the infrastructure pass deadlocks, which `docs/kubernetes.md` documents.
- Task 4: Ruling: The `pg_isready` init container runs as non-root uid 1654 with `-U postgres`. Without `-U` it loops forever. Cost if wrong: the wait never ends.
- Task 4: Ruling: No `activeDeadlineSeconds`; the Helm `--timeout` bounds the wait. Cost if wrong: a slower, less specific failure when the database is unreachable.
- Task 4: Ruling: `migrations.waitForDatabase` values were added to support an external database. Task 5 deduplicated the image. Cost if wrong: none.

Task 5 (infrastructure templates):
- Task 5: Ruling: A fourth file, `dashboards.yaml` (Grafana provisioning), is synced alongside the three copies the plan lists. The ruling not to carry `local-events.json` was reversed in Task 6. Cost if wrong: none for the dashboards file.
- Task 5: Ruling: `infra.postgres.image` is one value, shared by the StatefulSet and the migration wait. Cost if wrong: none.
- Task 5: Ruling: The values key is `infra.otelLgtm` for the Service `otel-lgtm`. Cost if wrong: naming inconsistency only.
- Task 5: Ruling: The Mailpit SMTP port and the otel-lgtm OTLP ports sit on the same NodePort Services and get random node ports, which kind does not publish. Cost if wrong: a random port can take one of the fixed ports first. See deferred finding 4.
- Task 5: Ruling: All six infrastructure images run non-root. Redis, Mailpit and Grafana data use `emptyDir`, infrastructure Deployments use the `Recreate` strategy, and Keycloak's startup budget is 300 s. Cost if wrong: none observed on kind.
- Task 5: Ruling: The helper file `templates/_infra.tpl` (security-context helpers) was added outside the file list. Cost if wrong: none.
- Task 5: Ruling: The orchestrator fixed Redis `command` to `args`, so the image entrypoint runs as it does in Compose. Cost if wrong: none.

Task 6 (one-script deploy):
- Task 6: Ruling: The full pass uses `--reset-values`, because Helm otherwise reuses `infraOnly=true` from pass 1. Extra arguments come after it, so callers repeat their `--set` values on every call. Cost if wrong: a later plain `kind-deploy` drops earlier `--set` values; `docs/kubernetes.md` documents this.
- Task 6: Ruling: This reverses the Task 5 ruling on `local-events.json`. Without the Compose `rabbitmq-init` step, `journal-entry-posted` was unroutable. A `rabbitmq-definitions` `pre-install,pre-upgrade` hook Job (weight -1, not rendered under `infraOnly`) now imports `files/local-events.json`, mirroring Compose. Cost if wrong: one extra hook Job per upgrade.
- Task 6: Ruling: Release presence is detected with `helm list --filter '^ledgerline$'`, and the script aborts on Helm errors (fix round 1). Cost if wrong: see deferred finding 12.
- Task 6: Ruling: C2 verified. A token from `localhost:8081` carries the issuer `http://localhost:8081/realms/ledgerline`, and the gateway accepts it inside the cluster. Cost if wrong: not applicable.

Task 7 (resilience on Kubernetes):
- Task 7: Ruling: The k6 workload reaches the cluster through the kind Docker network, at `http://<node>:<NodePort>` with values looked up through kind, docker and kubectl. The script's own API checks use the host mappings. Cost if wrong: none seen; this works on Linux runners without `host.docker.internal`.
- Task 7: Ruling: Pod deletion times count from the start of the load phase (k6's first progress line), not from the k6 launch, because `setup()` takes 15 to 90 s. Cost if wrong: none; earlier kills would abort setup instead of testing the load.
- Task 7: Ruling: The rolling restart starts 10 s into the second run, so the rollout (7 to 18 s measured) falls inside the 60 s load. Cost if wrong: none.
- Task 7: Ruling: k6 exit codes 0 and 99 (thresholds breached) are accepted, and every other code fails the run. Latency is not judged (trap 18). Guards against a vacuous pass: completed sagas above zero, deletes reported, and gateway requests above zero. Cost if wrong: a latency regression on Kubernetes is not caught by this script.
- Task 7: Ruling: Payments are checked through the wallets of the k6 VU users, because the API has no admin listing of every account. The script also requires k6 teardown's drain check over all 200 wallets, and it treats `Compensating` as in flight alongside `Processing`. Cost if wrong: if `smoke.js` changed its VU-to-user mapping, the per-wallet check would sample the wrong wallets; the teardown drain check still covers `Processing`.
- Task 7: Ruling: Failed requests during the restart are read from k6's own per-request CSV. Every failed `/api/` request must be a 503 between `rollout restart` and 15 s after `rollout status` returns; window times use the Docker engine clock. Cost if wrong: the 15 s tail could hide a late 503. No request failed in any rollout.
- Task 7: Ruling: Nothing is left in `docs/results/raw`. A later `--summary-export` keeps the summary inside the container, and the CSV is deleted in the EXIT trap. `scripts/k6.sh` is unchanged. Cost if wrong: see deferred T07 finding 6.
- Task 7: Ruling: Run 1 resets Payments to 1 replica when needed and run 2 sets 2, so repeated runs are the same test. This reset is the documented reset between the three consecutive runs. Cost if wrong: one extra `kind-deploy` (2 to 3 minutes) per repeat run.
- Task 7: Ruling: The script unsets the k6 pool and duration overrides, so every run uses the 60 s default workload with new idempotency keys (`RUN_ID` defaults to the time). Cost if wrong: none.
- Task 7: Ruling: There is no per-wallet balance reconciliation, and a zero trial balance cannot reveal a double posting. `docs/kubernetes.md` states this limit. In all 8 workloads the journal-entry count grew by exactly the number of completed sagas, checked by hand. Cost if wrong: a duplicate posting under two Payments replicas would not fail the script.

Task 8 (CI and README):
- Task 8: Ruling: C3 applied. The `compose-validate` job runs `docker compose config --quiet` and `render_test.sh` on push and pull request, and the "Validate Compose file" step left `build-test`. Cost if wrong: none.
- Task 8: Ruling: `compose-validate` skips the nightly schedule (`if: github.event_name != 'schedule'`), because `docs/testing.md` section 4 lists push and pull request. Cost if wrong: the nightly run would not re-check the chart, which only changes through a push or a pull request.
- Task 8: Ruling: The `kind` job creates the cluster with `helm/kind-action` from `deploy/kind/cluster.yaml` (cluster `ledgerline`) instead of running `kind-up.sh`. Cost if wrong: `kind-up.sh` itself is not exercised in CI.
- Task 8: Ruling: The README M4 status row was updated in Task 8, because Task 8 owns the README. The final fix wave reworded the M3 and M4 rows so they stay true after the merge. Cost if wrong: one line.

Final review:
- Final: Ruling: The fix wave took Minor findings 1, 9, 10, 11 and 13, none of which edits `kind-resilience.sh`, so no cluster re-run was needed. The other findings are deferred below. Finding 6 of the Task 7 review (a `.gitignore` line for the resilience CSV) is deferred because `.gitignore` is outside the files the M4 tasks list. Cost if wrong: a stray CSV left by a killed run could be committed by a careless `git add -A`.

Final audit (the starred tasks T02 and T07):
- Final: Ruling: The audit found T02 clean and T07 not clean. In I1, the restart-failure check passed when a CSV column was missing. In I2, the anti-vacuity guards were not tied to the faults. Both were fixed in `kind-resilience.sh`, and the three consecutive runs were repeated on a fresh cluster. A1 (amounts are not reconciled) stays accepted under the Task 7 ruling. Cost if wrong: none; the re-audit was clean.
- Final: Ruling: The `running (` progress line stays the marker for the start of the load. It was verified on k6 2.4.0: during `setup()` k6 prints only setup progress lines. Cost if wrong: only if the k6 image changes; run 2's guard against requests before the restart would still catch a restart issued during setup.
- Final: Ruling: "During the rollout" means strictly between `rollout restart` and the return of `rollout status`, without the 15 s tail. Cost if wrong: a rollout shorter than about 2 s would fail the guard. Measured rollouts took 7 to 26 s.
- Final: Ruling: Every `smoke` row must have a readable timestamp, a status, and an `expected_response` of `true` or `false`. Cost if wrong: a future k6 row with an empty status fails the run, which is the intended direction.
- Final: Ruling: A 4xx in the load phase counts as failed even when `expected_response` is true. `lib/api.js` marks a 404 as expected only for setup requests, which carry no `smoke` scenario. Cost if wrong: none for this workload.
- Final: Ruling: During the rebuild of the cluster for the re-proof, the first `kind-deploy` timed out in pass 1. The Postgres startup probe failed because of slow host disk: a checkpoint fsync took 58 s, and the image pulls took 7 minutes. The first retry failed on a transient NuGet network error, and the second retry succeeded as a single upgrade over the failed release (see deferred finding 12). Cost if wrong: none; these were host and network conditions, not chart defects.

## Owner decisions

- Shutdown timing: the consumer drain starts on `ApplicationStopping` (option b), so it runs alongside the Kestrel stop.
- New package `Microsoft.Extensions.Diagnostics.HealthChecks` 10.0.15 in Messaging.
- The owner stopped the other product's web server that held host port 8080, twice, and set its start type to manual.
- Docker disk cleanup, asked for by the owner during the final gate: the unused volumes of this project were deleted. That covered the M3 proof projects (`ledgerline-m3-k6-*`), the system-test project (`ledgerline-systest_*`) and the default `ledgerline_*` development volumes, 39 in all. Volumes of other projects were not touched. This overrides the handoff rule that no volume is deleted; it happened after every proof that used Compose data had run. The owner also approved removing the whole Docker build cache at the end of the run, after the last cluster proof.
- The run was paused before Task 7 at the owner's request and resumed later on a freshly created cluster.

## Spec deviations recorded, spec unchanged

`docs/design.md`, `docs/specs/*`, `docs/testing.md` and the ADRs were left as written, except for ADR 0000 version rows.

- C1, two-pass deploy: the first install runs with `--set infraOnly=true --wait`, then the full release follows. The migration Jobs are `pre-install,pre-upgrade` hooks that wait with `pg_isready`. A single install would deadlock, because the hook Jobs run before the infrastructure they wait for exists.
- C2, Keycloak: a fourth kind port mapping (30081 to host 8081) with `KC_HOSTNAME=http://localhost:8081`, so the token issuer is the same inside and outside the cluster. The plan listed three mappings.
- C3, `compose-validate`: the job that `docs/testing.md` section 4 specifies did not exist in `ci.yml`. It was created with `docker compose config --quiet` and `render_test.sh`, and the Compose step left `build-test`.
- `OutboxRelay` still cancels an in-progress batch on shutdown. `building-blocks.md` section 8 and ADR 0011 say the relay finishes its batch. This is safe under at-least-once delivery, because a repeated publish is deduplicated by the inbox.
- CI budgets: the `kind` job has a 55 minute timeout (steps of 20, 5 and 25 minutes) against the 15 minutes in `docs/testing.md` section 4. A warm local deploy took 556 to 615 s and one resilience run took 651 to 689 s, and a cold runner also builds every image. `compose-validate` has 5 minutes against 1 minute, as headroom for the Helm download.

## Deferred minor findings

Final whole-branch review (`d0bb564..f882232`):
- 2: The 20 minute deploy step has little margin on a cold runner, where every image is built from scratch.
- 3: The first run of the `kind` job on a Linux runner happens after the merge, so checklist row 4 and the Task 8 Prove stay open until then.
- 4: The random NodePorts of Mailpit SMTP and the otel-lgtm OTLP ports can take a fixed NodePort first. 30300 (Grafana) lies outside the static band that random allocation avoids.
- 5: The C1 rule that hook Jobs read only what exists before the upgrade is not explained for users in `docs/kubernetes.md`.
- 6: Coupled values (shutdown timings in `values.yaml` and `WebDefaults.cs`) are not cross-referenced where they are set.
- 7: kind binds the host ports on every interface (no `listenAddress`), so the Keycloak admin console with its development password is reachable from the local network while the cluster runs.
- 8: Optional pod hardening: no `automountServiceAccountToken: false` and no `enableServiceLinks: false`.
- 12: Release presence counts a release in any state, and recovery from an interrupted pass is not documented.

Task 7 review:
- 1 and 2: When the release is missing, or a rollout fails, the script exits through `set -e` without a `FAILED:` line.
- 3: Only `Processing` and `Compensating` count as in flight. `Authorized`, `Capturing` and `Voiding` are non-final too, but `smoke.js` does not produce them.
- 4: The wallet id is extracted with a grep that depends on JSON property order. It fails loudly if the order changes.
- 5: On a failure mid-run, the EXIT trap waits for k6 up to the teardown limit (about 17 minutes).
- 6: A run that is killed hard can leave `kind-resilience-*.csv` in `docs/results/raw`; there is no ignore rule.
- 7: Duplicate postings are not asserted by the script (see the Task 7 ruling).

Final re-audit of the resilience fix:
- A CSV row that a line break inside a quoted field splits is skipped instead of failed. This cannot happen with k6 2.4.0 today, because its error texts are short and unquoted.
- Run 2 has no "saga completed after the restart" guard. If every saga after the restart ended `Failed` or `Compensated`, the run would pass (the A1 class).
- Run 1's guard assumes whole-second CSV timestamps. A millisecond time format would need a change to the script.
- Run 1 has no guard requiring a request before the first deletion (accepted by the marker ruling).
- When the script is sourced instead of run, it only defines its functions and returns 0. CI runs it directly.
- In one run the first five sagas timed out on the client side before the first deletion, on the slow host. Later sagas completed. The script prints the timeout count but does not assert it.

## Open owner items

- Closed: the branch was merged through pull request #30. Its checks passed, and the first `kind` job on `main` was green (merge commit bff7de0), so checklist row 4 is ticked. The GitGuardian check on the pull request flagged the development Keycloak password that `render_test.sh` asserts; the owner closed it as a test credential.
- Decide on deferred finding 7 (bind kind to `127.0.0.1`) before running the cluster on an untrusted network.
