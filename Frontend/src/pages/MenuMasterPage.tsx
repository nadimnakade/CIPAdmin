import { useCallback, useEffect, useMemo, useState } from 'react';
import { api } from '../api';
import Icon from '../components/Icon';

interface MenuNode {
  menuId: number;
  label: string;
  path: string | null;
  icon: string | null;
  isActive: boolean;
  children: MenuNode[];
}
interface Role {
  roleId: number;
  roleName: string;
}

/** Flatten the menu tree to rows in display order. */
const flatten = (nodes: MenuNode[], depth = 0): { node: MenuNode; depth: number }[] =>
  nodes.flatMap((n) => [{ node: n, depth }, ...flatten(n.children || [], depth + 1)]);

export default function MenuMasterPage() {
  const [menus, setMenus] = useState<MenuNode[]>([]);
  const [roles, setRoles] = useState<Role[]>([]);
  const [selectedRole, setSelectedRole] = useState<number | null>(null);
  const [granted, setGranted] = useState<Set<number>>(new Set());
  const [dirty, setDirty] = useState(false);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [notice, setNotice] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    Promise.all([api.get<MenuNode[]>('/api/menus/all'), api.get<Role[]>('/api/menus/roles')])
      .then(([m, r]) => {
        setMenus(m);
        setRoles(r);
        const firstAdmin = r.find((x) => x.roleId === 1) ?? r[0];
        if (firstAdmin) setSelectedRole(firstAdmin.roleId);
      })
      .catch((e) => setError(e instanceof Error ? e.message : 'Failed to load'))
      .finally(() => setLoading(false));
  }, []);

  const loadGrants = useCallback((roleId: number) => {
    api
      .get<{ menuIds: number[] }>(`/api/menus/roles/${roleId}`)
      .then((d) => {
        setGranted(new Set(d.menuIds));
        setDirty(false);
      })
      .catch((e) => setError(e instanceof Error ? e.message : 'Failed to load grants'));
  }, []);

  useEffect(() => {
    if (selectedRole != null) loadGrants(selectedRole);
  }, [selectedRole, loadGrants]);

  const rows = useMemo(() => flatten(menus), [menus]);

  const toggle = (menuId: number) => {
    setGranted((prev) => {
      const next = new Set(prev);
      if (next.has(menuId)) next.delete(menuId);
      else next.add(menuId);
      return next;
    });
    setDirty(true);
  };

  const toggleGroup = (node: MenuNode) => {
    const ids = [node.menuId, ...(node.children || []).map((c) => c.menuId)];
    const allOn = ids.every((i) => granted.has(i));
    setGranted((prev) => {
      const next = new Set(prev);
      ids.forEach((i) => (allOn ? next.delete(i) : next.add(i)));
      return next;
    });
    setDirty(true);
  };

  const save = async () => {
    if (selectedRole == null) return;
    setSaving(true);
    setError('');
    try {
      await api.put(`/api/menus/roles/${selectedRole}`, { menuIds: [...granted] });
      setDirty(false);
      setNotice(`Saved — ${roles.find((r) => r.roleId === selectedRole)?.roleName ?? 'role'} updated`);
      setTimeout(() => setNotice(''), 4000);
      window.dispatchEvent(new CustomEvent('menus-updated'));
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Save failed');
    } finally {
      setSaving(false);
    }
  };

  if (loading) return <div className="page"><p className="muted">Loading…</p></div>;

  return (
    <div className="page">
      <div className="page-head">
        <div>
          <h1>Menu Master</h1>
          <p className="page-sub">Choose which sidebar menus each role can see</p>
        </div>
      </div>

      {error && <div className="alert error">{error}</div>}
      {notice && <div className="alert success">{notice}</div>}

      <div className="card menu-matrix-card">
        <div className="menu-matrix-toolbar">
          <label>
            Role
            <select
              value={selectedRole ?? ''}
              onChange={(e) => setSelectedRole(Number(e.target.value))}
            >
              {roles.map((r) => (
                <option key={r.roleId} value={r.roleId}>
                  {r.roleId} — {r.roleName}
                </option>
              ))}
            </select>
          </label>
          <button className="btn primary" onClick={save} disabled={saving || !dirty || selectedRole == null}>
            {saving ? 'Saving…' : dirty ? 'Save changes' : 'Saved'}
          </button>
        </div>

        <table className="table menu-matrix">
          <thead>
            <tr>
              <th>Menu</th>
              <th style={{ width: 110 }}>Visible</th>
            </tr>
          </thead>
          <tbody>
            {rows.map(({ node, depth }) => (
              <tr key={node.menuId} className={depth > 0 ? 'child-row' : 'group-row'}>
                <td>
                  <span style={{ paddingLeft: depth * 22, display: 'inline-flex', alignItems: 'center', gap: 8 }}>
                    {node.icon && <Icon name={node.icon} size={15} />}
                    <span className={depth === 0 && node.children?.length ? 'menu-group-label' : ''}>{node.label}</span>
                    {node.path && <code className="menu-path">{node.path}</code>}
                  </span>
                </td>
                <td>
                  <input
                    type="checkbox"
                    checked={granted.has(node.menuId)}
                    onChange={() =>
                      node.children?.length && depth === 0 ? toggleGroup(node) : toggle(node.menuId)
                    }
                    disabled={!node.isActive}
                    title={!node.isActive ? 'Menu is inactive' : depth === 0 && node.children?.length ? 'Toggle whole group' : ''}
                  />
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
