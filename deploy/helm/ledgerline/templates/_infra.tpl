{{/*
Security contexts shared by the infrastructure workloads (templates/infra/*). Each image runs as a fixed
non-root uid; fsGroup makes the volumes writable for that uid.
Usage: {{ include "ledgerline.infra.podSecurityContext" $infra.runAsUser | nindent 6 }}
*/}}
{{- define "ledgerline.infra.podSecurityContext" -}}
runAsNonRoot: true
runAsUser: {{ . }}
runAsGroup: {{ . }}
fsGroup: {{ . }}
seccompProfile:
  type: RuntimeDefault
{{- end }}

{{- define "ledgerline.infra.containerSecurityContext" -}}
allowPrivilegeEscalation: false
readOnlyRootFilesystem: false
capabilities:
  drop:
    - ALL
{{- end }}
