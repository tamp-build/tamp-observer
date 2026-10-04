using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

namespace Tamp.Observer.Api;

// Per-component runtime metrics from the in-cluster Kubernetes API (README 7.8, TOBS-33). Reads pod status
// (restarts, start time, readiness) from the core API and CPU/memory from the metrics.k8s.io API, keyed by pod.
// Best-effort by design: outside a cluster, without the RBAC to list pods, or with no metrics-server, every call
// fails fast and returns empty, so the health page falls back to the static component list rather than erroring.

public sealed record PodMetrics(
    string Pod, double? CpuMillicores, long? MemoryBytes, int Restarts, DateTimeOffset? StartedAt, bool Ready, bool Running);

public static class K8sMetrics
{
    private const string SaDir = "/var/run/secrets/kubernetes.io/serviceaccount";

    public static async Task<IReadOnlyList<PodMetrics>> TryReadAsync(CancellationToken ct)
    {
        try
        {
            if (!File.Exists($"{SaDir}/token"))
                return []; // not running in a cluster: skip without a slow timeout.

            var token = (await File.ReadAllTextAsync($"{SaDir}/token", ct)).Trim();
            var ca = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync($"{SaDir}/ca.crt", ct));
            var ns = Environment.GetEnvironmentVariable("OBSERVER_POD_NAMESPACE");
            if (string.IsNullOrWhiteSpace(ns) && File.Exists($"{SaDir}/namespace"))
                ns = (await File.ReadAllTextAsync($"{SaDir}/namespace", ct)).Trim();
            if (string.IsNullOrWhiteSpace(ns))
                return [];

            var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") ?? "kubernetes.default.svc";
            var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                {
                    if (cert is null) return false;
                    using var chain = new X509Chain();
                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    chain.ChainPolicy.CustomTrustStore.Add(ca);
                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    return chain.Build(cert);
                },
            };
            using var http = new HttpClient(handler) { BaseAddress = new Uri($"https://{host}:{port}"), Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Authorization = new("Bearer", token);

            var podsJson = await http.GetStringAsync($"/api/v1/namespaces/{ns}/pods", ct);
            string? metricsJson = null;
            try { metricsJson = await http.GetStringAsync($"/apis/metrics.k8s.io/v1beta1/namespaces/{ns}/pods", ct); }
            catch { /* metrics-server absent or no RBAC: status-only is still useful. */ }

            return Merge(podsJson, metricsJson);
        }
        catch
        {
            return [];
        }
    }

    private static IReadOnlyList<PodMetrics> Merge(string podsJson, string? metricsJson)
    {
        // CPU (millicores) and memory (bytes) per pod, summed across containers.
        var cpu = new Dictionary<string, double>(StringComparer.Ordinal);
        var mem = new Dictionary<string, long>(StringComparer.Ordinal);
        if (metricsJson is not null)
        {
            using var m = JsonDocument.Parse(metricsJson);
            foreach (var item in Items(m))
            {
                var name = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
                double c = 0;
                long b = 0;
                if (item.TryGetProperty("containers", out var containers))
                    foreach (var container in containers.EnumerateArray())
                        if (container.TryGetProperty("usage", out var usage))
                        {
                            c += Millicores(usage.TryGetProperty("cpu", out var cv) ? cv.GetString() : null);
                            b += MemoryBytes(usage.TryGetProperty("memory", out var mv) ? mv.GetString() : null);
                        }
                cpu[name] = c;
                mem[name] = b;
            }
        }

        var result = new List<PodMetrics>();
        using var p = JsonDocument.Parse(podsJson);
        foreach (var item in Items(p))
        {
            var name = item.GetProperty("metadata").GetProperty("name").GetString() ?? "";
            var status = item.TryGetProperty("status", out var st) ? st : default;
            var phase = status.ValueKind == JsonValueKind.Object && status.TryGetProperty("phase", out var ph) ? ph.GetString() : null;

            var restarts = 0;
            var ready = false;
            if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("containerStatuses", out var cs) && cs.ValueKind == JsonValueKind.Array)
            {
                ready = cs.GetArrayLength() > 0;
                foreach (var c in cs.EnumerateArray())
                {
                    if (c.TryGetProperty("restartCount", out var rc)) restarts += rc.GetInt32();
                    if (!(c.TryGetProperty("ready", out var rd) && rd.GetBoolean())) ready = false;
                }
            }

            DateTimeOffset? started = null;
            if (status.ValueKind == JsonValueKind.Object && status.TryGetProperty("startTime", out var stt)
                && stt.GetString() is { } s && DateTimeOffset.TryParse(s, out var dt))
                started = dt;

            result.Add(new PodMetrics(
                name,
                cpu.TryGetValue(name, out var cc) ? cc : null,
                mem.TryGetValue(name, out var bb) ? bb : null,
                restarts, started, ready, phase == "Running"));
        }
        return result;
    }

    private static IEnumerable<JsonElement> Items(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    // Kubernetes CPU quantity -> millicores. e.g. "3m" = 3, "123456n" = 0.123, "1" = 1000.
    private static double Millicores(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return 0;
        if (q.EndsWith('n')) return Num(q) / 1_000_000.0;
        if (q.EndsWith('u')) return Num(q) / 1_000.0;
        if (q.EndsWith('m')) return Num(q);
        return Num(q) * 1000.0; // whole cores
    }

    // Kubernetes memory quantity -> bytes. Binary (Ki/Mi/Gi/Ti) and decimal (K/M/G/T) suffixes, or plain bytes.
    private static long MemoryBytes(string? q)
    {
        if (string.IsNullOrWhiteSpace(q)) return 0;
        (string suffix, double mult)[] units =
        [
            ("Ki", 1024), ("Mi", 1024d * 1024), ("Gi", 1024d * 1024 * 1024), ("Ti", 1024d * 1024 * 1024 * 1024),
            ("K", 1000), ("M", 1_000_000), ("G", 1_000_000_000), ("T", 1_000_000_000_000),
        ];
        foreach (var (suffix, mult) in units)
            if (q.EndsWith(suffix, StringComparison.Ordinal))
                return (long)(Num(q[..^suffix.Length]) * mult);
        return (long)Num(q);
    }

    private static double Num(string q)
    {
        var span = q.AsSpan();
        var end = 0;
        while (end < span.Length && (char.IsDigit(span[end]) || span[end] == '.' || span[end] == '-' || span[end] == '+')) end++;
        return double.TryParse(span[..end], System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
    }
}
