import { api } from "../api/client";
import type { components } from "../api/schema";

// Instance-level data loaded once after sign-in: the projects the viewer can access (top-bar switcher, README
// section 4) and the enforcement posture (mode badge on every screen, README section 6). Kept in one store so
// the shell has them without each surface refetching.

export type ProjectSummary = components["schemas"]["ProjectSummary"];
export type EnforcementView = components["schemas"]["EnforcementView"];

class InstanceState {
  projects = $state<ProjectSummary[]>([]);
  enforcement = $state<EnforcementView | null>(null);
  loaded = $state(false);

  async load(): Promise<void> {
    const [projects, enforcement] = await Promise.all([
      api.GET("/api/projects"),
      api.GET("/api/enforcement"),
    ]);
    if (projects.data) this.projects = projects.data;
    if (enforcement.data) this.enforcement = enforcement.data;
    this.loaded = true;
  }

  project(id: string): ProjectSummary | undefined {
    return this.projects.find((p) => p.id === id);
  }
}

export const instance = new InstanceState();
