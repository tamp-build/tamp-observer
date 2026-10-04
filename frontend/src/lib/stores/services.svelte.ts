import { api } from "../api/client";

// Resolves a project's ServiceId -> service name (GET /api/projects/{id}/services). Logs, traces and
// operations carry a ServiceId GUID; the UI shows the human name. Cached per project; refreshed on demand.

class ServiceState {
  private byProject = $state<Record<string, Record<string, string>>>({});

  /** Load (and cache) the service map for a project. Pass force to refresh after new services appear. Failures
      are captured by the observability adapter; a miss just leaves names unresolved (shown as a dash). */
  async loadFor(projectId: string, force = false): Promise<void> {
    if (!force && this.byProject[projectId]) return;
    const { data } = await api.GET("/api/projects/{projectId}/services", {
      params: { path: { projectId } },
    });
    if (!data) return;
    const map: Record<string, string> = {};
    for (const s of data) map[s.id] = s.serviceName;
    this.byProject = { ...this.byProject, [projectId]: map };
  }

  /** The service name for a ServiceId within a project, or a dash when unknown. */
  name(projectId: string, serviceId: string | undefined | null): string {
    if (!serviceId) return "-";
    return this.byProject[projectId]?.[serviceId] ?? "-";
  }

  /** The {id, name} pairs for a project, name-sorted, for a filter dropdown. Empty until loadFor resolves. */
  list(projectId: string): { id: string; name: string }[] {
    const map = this.byProject[projectId];
    if (!map) return [];
    return Object.entries(map)
      .map(([id, name]) => ({ id, name }))
      .sort((a, b) => a.name.localeCompare(b.name));
  }
}

export const services = new ServiceState();
