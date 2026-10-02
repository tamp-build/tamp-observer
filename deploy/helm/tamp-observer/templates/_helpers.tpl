{{/* Chart name / fullname / labels */}}
{{- define "tamp-observer.name" -}}
{{- default .Chart.Name .Values.nameOverride | trunc 63 | trimSuffix "-" -}}
{{- end -}}

{{- define "tamp-observer.fullname" -}}
{{- if .Values.fullnameOverride -}}
{{- .Values.fullnameOverride | trunc 63 | trimSuffix "-" -}}
{{- else -}}
{{- printf "%s-%s" .Release.Name (include "tamp-observer.name" .) | trunc 63 | trimSuffix "-" -}}
{{- end -}}
{{- end -}}

{{- define "tamp-observer.labels" -}}
helm.sh/chart: {{ printf "%s-%s" .Chart.Name .Chart.Version | replace "+" "_" | trunc 63 | trimSuffix "-" }}
app.kubernetes.io/name: {{ include "tamp-observer.name" . }}
app.kubernetes.io/instance: {{ .Release.Name }}
app.kubernetes.io/version: {{ .Chart.AppVersion | quote }}
app.kubernetes.io/managed-by: {{ .Release.Service }}
{{- end -}}

{{/* Per-component selector labels: pass a dict {root, component} */}}
{{- define "tamp-observer.selectorLabels" -}}
app.kubernetes.io/name: {{ include "tamp-observer.name" .root }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/component: {{ .component }}
{{- end -}}

{{/* Name of the Secret to read (existing or generated) */}}
{{- define "tamp-observer.secretName" -}}
{{- if .Values.secrets.existingSecret -}}
{{- .Values.secrets.existingSecret -}}
{{- else -}}
{{- printf "%s-secrets" (include "tamp-observer.fullname" .) -}}
{{- end -}}
{{- end -}}

{{/* Fully-qualified first-party image ref: pass a dict {root, repository, tag} */}}
{{- define "tamp-observer.image" -}}
{{- $reg := .root.Values.image.registry -}}
{{- $tag := .tag | default .root.Values.image.tag | default .root.Chart.AppVersion -}}
{{- printf "%s%s:%s" $reg .repository $tag -}}
{{- end -}}

{{/* Component service DNS names */}}
{{- define "tamp-observer.postgresHost" -}}{{ printf "%s-postgres" (include "tamp-observer.fullname" .) }}{{- end -}}
{{- define "tamp-observer.valkeyHost" -}}{{ printf "%s-valkey" (include "tamp-observer.fullname" .) }}{{- end -}}
{{- define "tamp-observer.collectorHost" -}}{{ printf "%s-collector" (include "tamp-observer.fullname" .) }}{{- end -}}
