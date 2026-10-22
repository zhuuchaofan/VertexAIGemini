import { useCallback, useEffect, useState } from "react";
import type { RunApi, UserRole } from "../../api/types";

export type RoleApi = Pick<RunApi, "listRoles" | "createRole" | "updateRole" | "deleteRole" | "setDefaultRole">;

export interface Roles {
  roles: UserRole[];
  maxRoles: number;
  /** The role a new conversation starts with, if one is set and still exists. */
  defaultRoleId: string | null;
  /** True once the list has been read (or failed to be); callers need not wait on a failure. */
  loaded: boolean;
  create: (role: { name: string; prompt: string }) => Promise<UserRole>;
  update: (id: string, role: { name: string; prompt: string }) => Promise<UserRole>;
  remove: (id: string) => Promise<void>;
  setDefault: (id: string | null) => Promise<void>;
}

const byName = (a: UserRole, b: UserRole) => a.name.localeCompare(b.name, "zh");

/**
 * The person's own prompt roles. A list that cannot be loaded is simply empty: the built-in presets keep working,
 * and the next page load tries again.
 */
export function useRoles(api: RoleApi | undefined): Roles {
  const [roles, setRoles] = useState<UserRole[]>([]);
  const [maxRoles, setMaxRoles] = useState(30);
  const [defaultRoleId, setDefaultRoleId] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(!api);

  useEffect(() => {
    if (!api) return;
    let cancelled = false;
    api
      .listRoles()
      .then(result => {
        if (cancelled) return;
        setRoles(result.roles);
        setMaxRoles(result.maxRoles);
        setDefaultRoleId(result.defaultRoleId);
      })
      .catch(() => undefined)
      .finally(() => {
        if (!cancelled) setLoaded(true);
      });
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
      setDefaultRoleId(current => (current === id ? null : current));
    },
    [api]
  );
  const setDefault = useCallback(
    async (id: string | null) => {
      await api!.setDefaultRole(id);
      setDefaultRoleId(id);
    },
    [api]
  );

  return { roles, maxRoles, defaultRoleId, loaded, create, update, remove, setDefault };
}
