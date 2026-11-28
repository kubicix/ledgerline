{{/*
Labels shared by every object the chart renders. The service name is the first argument's "name".
Usage: {{ include "ledgerline.labels" (dict "name" $name "root" $) }}
*/}}
{{- define "ledgerline.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: ledgerline
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ printf "%s-%s" .root.Chart.Name .root.Chart.Version | replace "+" "_" }}
{{- end }}

{{/*
Labels that select the pods of one service. They never change after the first install.
Usage: {{ include "ledgerline.selectorLabels" (dict "name" $name "root" $) }}
*/}}
{{- define "ledgerline.selectorLabels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end }}

{{/*
The services in effect, as YAML for fromYaml. An entry with phase2: true is included only when phase2.enabled; when
phase2.enabled, an entry's phase2Env is merged over its env (phase2Env wins). Neither key reaches the result, so with
phase2.enabled=false every service renders exactly as without them.
Usage: {{- range $name, $svc := include "ledgerline.services" . | fromYaml }}
*/}}
{{- define "ledgerline.services" -}}
{{- $on := .Values.phase2.enabled }}
{{- $services := dict }}
{{- range $name, $svc := .Values.services }}
{{- if or (not $svc.phase2) $on }}
{{- $effective := omit $svc "phase2" "phase2Env" }}
{{- if and $on $svc.phase2Env }}
{{- $_ := set $effective "env" (merge (deepCopy $svc.phase2Env) (deepCopy ($svc.env | default dict))) }}
{{- end }}
{{- $_ := set $services $name $effective }}
{{- end }}
{{- end }}
{{- toYaml $services }}
{{- end }}
