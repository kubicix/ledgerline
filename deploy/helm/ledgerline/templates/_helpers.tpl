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
