import { api } from "../api/client";

// Resolves a project's EnvironmentId -> name and VersionId -> version string (TOBS-28). Spans/logs carry the
// entity-id GUIDs; the trace header shows the human labels. Cached per project, loaded on demand, mirroring
// the services store. Failures are captured by the observability adapter; a miss leaves the tag hidden.

class EntityState {
  private envByProject = $state<Record<string, Record<string, string>>>({});
  private verByProject = $state<Record<string, Record<string, string>>>({});

  /** Load (and cache) the environment + version maps for a project. */
  async loadFor(projectId: string, force = false): Promise<void> {
    if (!force && this.envByProject[projectId] && this.verByProject[projectId]) return;
    const [envs, vers] = await Promise.all([
      api.GET("/api/projects/{projectId}/environments", { params: { path: { projectId } } }),
      api.GET("/api/projects/{projectId}/versions", { params: { path: { projectId } } }),
    ]);
    if (envs.data) {
      const m: Record<string, string> = {};
      for (const e of envs.data) m[e.id] = e.name;
      this.envByProject = { ...this.envByProject, [projectId]: m };
    }
    if (vers.data) {
      const m: Record<string, string> = {};
      for (const v of vers.data) m[v.id] = v.versionString;
      this.verByProject = { ...this.verByProject, [projectId]: m };
    }
  }

  /** The environment name for an EnvironmentId within a project, or null when unknown. */
  envName(projectId: string, environmentId: string | undefined | null): string | null {
    if (!environmentId) return null;
    return this.envByProject[projectId]?.[environmentId] ?? null;
  }

  /** The version string for a VersionId within a project, or null when unknown. */
  versionName(projectId: string, versionId: string | undefined | null): string | null {
    if (!versionId) return null;
    return this.verByProject[projectId]?.[versionId] ?? null;
  }
}

export const entities = new EntityState();
