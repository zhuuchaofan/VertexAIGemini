import { useCallback, useEffect, useState } from "react";
import type { RunApi, UserRole } from "../../api/types";

export type RoleApi = Pick<RunApi, "listRoles" | "createRole" | "updateRole" | "deleteRole">;

export interface Roles {
  roles: UserRole[];
  maxRoles: number;
  create: (role: { name: string; prompt: string }) => Promise<UserRole>;
  update: (id: string, role: { name: string; prompt: string }) => Promise<UserRole>;
  remove: (id: string) => Promise<void>;
}

const byName = (a: UserRole, b: UserRole) => a.name.localeCompare(b.name, "zh");

/**
 * The person's own prompt roles. A list that cannot be loaded is simply empty: the built-in presets keep working,
 * and the next open of the panel tries again.
 */
export function useRoles(api: RoleApi | undefined): Roles | undefined {
  const [roles, setRoles] = useState<UserRole[]>([]);
  const [maxRoles, setMaxRoles] = useState(30);

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    api
      .listRoles()
      .then(result => {
        if (cancelled) return;
        setRoles(result.roles);
        setMaxRoles(result.maxRoles);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [api]);

  const create = useCallback(
    async (role: { name: string; prompt: string }) => {
      const created = await api!.createRole(role);
      setRoles(current => [...current, created].sort(byName));
      return created;
    },
    [api]
  );
  const update = useCallback(
    async (id: string, role: { name: string; prompt: string }) => {
      const updated = await api!.updateRole(id, role);
      setRoles(current => current.map(item => (item.id === id ? updated : item)).sort(byName));
      return updated;
    },
    [api]
  );
  const remove = useCallback(
    async (id: string) => {
      await api!.deleteRole(id);
      setRoles(current => current.filter(item => item.id !== id));
    },
    [api]
  );

  return api ? { roles, maxRoles, create, update, remove } : undefined;
}
