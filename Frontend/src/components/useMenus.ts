import { useEffect, useState } from 'react';
import { api } from '../api';

export interface NavLeaf {
  label: string;
  to: string;
  end?: boolean;
}
export interface NavGroup {
  label: string;
  icon?: string;
  to?: string;
  end?: boolean;
  children?: NavGroup[];
}

/** Fallback used when the menu API is unavailable (e.g. older backend). */
export const FALLBACK_NAV: NavGroup[] = [
  { label: 'Dashboard', icon: 'dashboard', to: '/', end: true },
  {
    label: 'CIBIL',
    icon: 'search',
    children: [
      { label: 'Checker', to: '/cibil' },
      { label: 'Analyzer', to: '/bsa-analyzer' },
    ],
  },
  {
    label: 'Masters',
    icon: 'database',
    children: [
      { label: 'Employee Master', to: '/employee-master' },
      { label: 'Employees DID', to: '/employees' },
      { label: 'Company Category', to: '/company-categories' },
      { label: 'OCR to Excel', to: '/ocr' },
      { label: 'Lead Master', to: '/lead-master' },
    ],
  },
  {
    label: 'Reports',
    icon: 'report',
    children: [
      { label: 'Dialer Live', to: '/dialer' },
      { label: 'Generic Reports', to: '/generic-reports' },
      { label: 'CIBIL Reports', to: '/reports' },
    ],
  },
  { label: 'Settings', icon: 'settings', to: '/settings' },
];

interface ApiMenuNode {
  menuId: number;
  label: string;
  path: string | null;
  icon: string | null;
  children: ApiMenuNode[];
}

/** Sidebar menus for the signed-in role. Falls back to the static NAV on error.
 *  Bump `reloadKey` to re-fetch (e.g. after saving the Menu Master). */
export function useMenus(isAdmin: boolean, reloadKey = 0): NavGroup[] {
  const [nav, setNav] = useState<NavGroup[]>(() => {
    if (isAdmin) {
      return [
        ...FALLBACK_NAV.slice(0, 4),
        {
          label: 'Administration',
          icon: 'shield',
          children: [
            { label: 'CIBIL Limit Requests', to: '/limit-requests' },
            { label: 'Access Control', to: '/access-control' },
            { label: 'Menu Master', to: '/menu-master' },
            { label: 'Role & Privilege Master', to: '/role-master' },
          ],
        },
        FALLBACK_NAV[4],
      ];
    }
    return FALLBACK_NAV;
  });

  useEffect(() => {
    let alive = true;
    api
      .get<ApiMenuNode[]>('/api/menus')
      .then((nodes) => {
        if (!alive || !Array.isArray(nodes) || nodes.length === 0) return;
        const map = (list: ApiMenuNode[]): NavGroup[] =>
          list.map((n) => ({
            label: n.label,
            icon: n.icon ?? undefined,
            to: n.path ?? undefined,
            children: n.children?.length ? map(n.children) : undefined,
          }));
        const translated = map(nodes);
        // Administration group is authoritative from the DB when present;
        // otherwise admins still get it so they can reach Menu Master.
        const hasAdmin = translated.some((g) => g.label === 'Administration');
        const finalNav = isAdmin && !hasAdmin
          ? [
              ...translated,
              {
                label: 'Administration',
                icon: 'shield',
                children: [
                  { label: 'CIBIL Limit Requests', to: '/limit-requests' },
                  { label: 'Access Control', to: '/access-control' },
                  { label: 'Menu Master', to: '/menu-master' },
                  { label: 'Role & Privilege Master', to: '/role-master' },
                ],
              },
            ]
          : translated;
        setNav(finalNav);
      })
      .catch(() => {
        /* keep fallback */
      });
    return () => {
      alive = false;
    };
  }, [isAdmin, reloadKey]);

  return nav;
}
