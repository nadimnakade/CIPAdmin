import { useEffect, useMemo, useRef, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { getUser, clearAuth, setLastSignOutReason } from '../api';
import Icon from './Icon';
import IdleWarningDialog from './IdleWarning';
import { useIdleTimeout } from '../hooks/useIdleTimeout';
import { useMenus, type NavGroup } from './useMenus';

const isAdminUser = () => {
  const u = getUser();
  return u?.roleId === 1 || u?.roleId === 2;
};

/** Breadcrumb segments per route (shown in the topbar, per the reference design). */
const BREADCRUMBS: Record<string, string[]> = {
  '/': ['Dashboard'],
  '/cibil': ['CIBIL', 'Checker'],
  '/bsa-analyzer': ['CIBIL', 'Bank Statement Analyzer'],
  '/financial-analysis': ['CIBIL', 'Financial Analysis'],
  '/employees': ['Employees', 'DID Management'],
  '/employee-master': ['Masters', 'Employee Master'],
  '/company-categories': ['Masters', 'Company Category'],
  '/ocr': ['Masters', 'OCR to Excel'],
  '/lead-master': ['Masters', 'Lead Master'],
  '/dialer': ['Reports', 'Dialer Live'],
  '/reports': ['Reports', 'CIBIL Reports'],
  '/generic-reports': ['Reports', 'Generic Reports'],
  '/limit-requests': ['Administration', 'CIBIL Limit Requests'],
  '/menu-master': ['Administration', 'Menu Master'],
  '/role-master': ['Administration', 'Role & Privilege Master'],
  '/settings': ['Settings'],
};

export default function Layout() {
  const navigate = useNavigate();
  const location = useLocation();
  const user = getUser();

  // Re-fetch menus when an admin saves the Menu Master
  const [menuVersion, setMenuVersion] = useState(0);
  useEffect(() => {
    const handler = () => setMenuVersion((v) => v + 1);
    window.addEventListener('menus-updated', handler);
    return () => window.removeEventListener('menus-updated', handler);
  }, []);
  const nav: NavGroup[] = useMenus(isAdminUser(), menuVersion);

  // Desktop: collapse to icon rail. Mobile: slide-in drawer.
  const [collapsed, setCollapsed] = useState(false);
  const [open, setOpen] = useState(false);
  // Sections start open so the sidebar shows the whole menu on arrival, matching the
  // reference design. Each heading is then a toggle. Persisted because a user who
  // deliberately folds a section away should not find it re-opened on every reload.
  const GROUPS_KEY = 'cip_nav_open_groups';
  const [openGroups, setOpenGroups] = useState<string[]>(() => {
    try {
      const saved = localStorage.getItem(GROUPS_KEY);
      if (saved) {
        const parsed = JSON.parse(saved);
        if (Array.isArray(parsed)) return parsed as string[];
      }
    } catch {
      /* corrupt or unavailable storage - fall through to open-everything */
    }
    return nav.filter((g) => g.children?.length).map((g) => g.label);
  });

  useEffect(() => {
    try {
      localStorage.setItem(GROUPS_KEY, JSON.stringify(openGroups));
    } catch {
      /* storage full or blocked - collapsing just won't persist */
    }
  }, [openGroups]);

  // Navigating into a section that the user has folded away must reveal it, or the
  // page they just opened would be invisible in the menu.
  useEffect(() => {
    const group = nav.find((g) => g.children?.some((c) => c.to === location.pathname));
    if (!group) return;
    setOpenGroups((prev) => (prev.includes(group.label) ? prev : [...prev, group.label]));
  }, [location.pathname, nav]);

  // Close the mobile drawer on navigation + keep the active group open
  useEffect(() => {
    setOpen(false);
    const active = nav.find((g) => g.children?.some((c) => c.to === location.pathname));
    if (active) setOpenGroups((prev) => (prev.includes(active.label) ? prev : [...prev, active.label]));
  }, [location.pathname]);

  const toggleGroup = (label: string) =>
    setOpenGroups((prev) => (prev.includes(label) ? prev.filter((l) => l !== label) : [...prev, label]));

  const logout = () => {
    clearAuth();
    navigate('/login', { replace: true });
  };

  // 15-minute idle sign-out. The server drops the session row at the same deadline;
  // this stops an untouched tab from continuing to display data that is already invalid.
  const idleWarning = useIdleTimeout(() => {
    // Order matters: record the reason BEFORE clearing the session. clearAuth() notifies
    // subscribers, which can mount the login screen, and it reads this reason on mount.
    setLastSignOutReason('idle');
    clearAuth();
    navigate('/login', { replace: true });
  });

  // Topbar user-chip dropdown (Sign out lives directly on screen).
  const [userMenuOpen, setUserMenuOpen] = useState(false);
  const userMenuRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!userMenuOpen) return;
    const close = (e: MouseEvent) => {
      if (!userMenuRef.current?.contains(e.target as Node)) setUserMenuOpen(false);
    };
    document.addEventListener('mousedown', close);
    return () => document.removeEventListener('mousedown', close);
  }, [userMenuOpen]);

  // Topbar quick search — matches page names and navigates on Enter.
  // External links are excluded: navigate() would treat their URL as an in-app route.
  const searchIndex = useMemo(
    () =>
      nav.flatMap((g) =>
        g.to
          ? g.external
            ? []
            : [{ label: g.label, to: g.to }]
          : (g.children || [])
              .filter((c) => !!c.to && !c.external)
              .map((c) => ({ label: c.label, to: c.to! }))
      ),
    [nav]
  );
  const [query, setQuery] = useState('');
  const searchRef = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        searchRef.current?.focus();
      }
    };
    window.addEventListener('keydown', handler);
    return () => window.removeEventListener('keydown', handler);
  }, []);

  const submitSearch = () => {
    const q = query.trim().toLowerCase();
    if (!q) return;
    const hit = searchIndex.find((s) => s.label.toLowerCase().includes(q));
    if (hit) {
      navigate(hit.to);
      setQuery('');
      (document.activeElement as HTMLElement)?.blur();
    }
  };

  const crumbs = BREADCRUMBS[location.pathname] || ['Dashboard'];

  // Section headings are always expanded — the reference design shows every group open,
  // so the group label is a heading rather than a disclosure toggle. `openGroups` is kept
  // for the rarer third level (a leaf that itself has children), which does collapse.
  const leafTitle = (label: string) => (collapsed ? label : undefined);

  const renderLeaf = (c: NavGroup): JSX.Element => {
    const kids = c.children?.filter((x) => !!x.to) ?? [];
    if (kids.length) {
      const isOpen = openGroups.includes(c.label);
      return (
        <div key={c.label} className={'nav-group nav-nested' + (isOpen ? ' expanded' : '')}>
          <button
            className="nav-item nav-group-toggle"
            onClick={() => toggleGroup(c.label)}
            title={leafTitle(c.label)}
            aria-expanded={isOpen}
          >
            <Icon name={c.icon ?? 'layers'} size={14} />
            <span className="nav-item-label">{c.label}</span>
            <span className="nav-caret"><Icon name={isOpen ? 'chevronDown' : 'chevronRight'} size={11} /></span>
          </button>
          {isOpen && <div className="nav-submenu nav-submenu-open">{kids.map(renderLeaf)}</div>}
        </div>
      );
    }
    if (c.external) {
      return (
        <a
          key={c.to}
          className="nav-subitem"
          href={c.to!}
          target="_blank"
          rel="noopener noreferrer"
          title={leafTitle(c.label)}
        >
          <Icon name={c.icon ?? 'fileText'} size={14} />
          <span className="nav-item-label">{c.label}</span>
        </a>
      );
    }
    return (
      <NavLink
        key={c.to}
        to={c.to!}
        end={c.end}
        className={({ isActive }) => 'nav-subitem' + (isActive ? ' active' : '')}
        title={leafTitle(c.label)}
      >
        <Icon name={c.icon ?? 'fileText'} size={14} />
        <span className="nav-item-label">{c.label}</span>
      </NavLink>
    );
  };

  return (
    <div className={'layout' + (collapsed ? ' sidebar-collapsed' : '')}>
      {open && <div className="sidebar-backdrop" onClick={() => setOpen(false)} />}

      <aside className={'sidebar' + (open ? ' open' : '')}>
        <div className="brand">
          <span className="brand-tile">
            <img src={`${import.meta.env.BASE_URL}assets/logo.png`} alt="" className="brand-img" />
          </span>
          <span className="brand-text">
            <span className="brand-name">CIBIL Analyzer</span>
            <span className="brand-sub">Bank &amp; Credit Analysis</span>
          </span>
          <button
            className="icon-btn sidebar-close"
            onClick={() => setOpen(false)}
            aria-label="Close menu"
          >
            <Icon name="close" size={18} />
          </button>
          <button
            className="icon-btn brand-collapse"
            onClick={() => setCollapsed(!collapsed)}
            aria-label={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
            title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
          >
            <Icon name={collapsed ? 'chevronsRight' : 'chevronsLeft'} size={15} />
          </button>
        </div>

        <nav className="side-nav">
          {nav.map((item) =>
            item.to ? (
              item.external ? (
                // Off-site link (e.g. the ClearTax calculator): a real anchor in a new
                // tab, never a react-router NavLink — that would treat the URL as an
                // in-app route and render a blank page.
                <a
                  key={item.label}
                  className="nav-item"
                  href={item.to}
                  target="_blank"
                  rel="noopener noreferrer"
                  title={leafTitle(item.label)}
                >
                  <Icon name={item.icon ?? 'fileText'} /> <span className="nav-item-label">{item.label}</span>
                </a>
              ) : (
              <NavLink
                key={item.label}
                to={item.to}
                end={item.end}
                className={({ isActive }) => 'nav-item' + (isActive ? ' active' : '')}
                title={leafTitle(item.label)}
              >
                <Icon name={item.icon ?? 'fileText'} /> <span className="nav-item-label">{item.label}</span>
              </NavLink>
              )
            ) : (
              // Group label is a plain section heading; its children always render.
              <div key={item.label} className={'nav-section' + (openGroups.includes(item.label) ? ' open' : '')}>
                <button
                  className="nav-section-label"
                  onClick={() => toggleGroup(item.label)}
                  aria-expanded={openGroups.includes(item.label)}
                >
                  <Icon name={item.icon ?? 'layers'} size={13} />
                  <span className="nav-section-text">{item.label}</span>
                  <span className="nav-section-caret"><Icon name="chevronDown" size={12} /></span>
                </button>
                {openGroups.includes(item.label) && (
                  <div className="nav-submenu nav-submenu-open">
                    {item.children!.filter((c) => !!c.to).map(renderLeaf)}
                  </div>
                )}
              </div>
            )
          )}
        </nav>

        <div className="sidebar-footer">
          <div className="need-help">
            <div className="need-help-icon">
              <Icon name="help" size={18} />
            </div>
            <div className="need-help-text">
              <div className="need-help-title">Need Help?</div>
              <div className="need-help-sub">Contact support team</div>
            </div>
            <span className="need-help-arrow"><Icon name="chevronRight" size={14} /></span>
          </div>
          <div className="side-user">
            <div className="avatar small">{user?.empName?.[0] || 'A'}</div>
            <div className="side-user-info">
              <div className="side-user-name">{user?.empName || 'Admin'}</div>
              <div className="side-user-mail">
                {(user?.empName || 'admin').toLowerCase()}@1up.com
              </div>
            </div>
            <button className="icon-btn" onClick={logout} title="Logout">
              <Icon name="logout" />
            </button>
          </div>
        </div>
      </aside>

      <main className="main-area">
        <header className="topbar">
          <div className="topbar-left">
            <button
              className="icon-btn hamburger"
              onClick={() => setOpen(true)}
              aria-label="Open menu"
            >
              <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
                <line x1="3" y1="6" x2="21" y2="6" />
                <line x1="3" y1="12" x2="21" y2="12" />
                <line x1="3" y1="18" x2="21" y2="18" />
              </svg>
            </button>
            <div className="topbar-search">
              <Icon name="search" size={16} />
              <input
                ref={searchRef}
                placeholder="Search here..."
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                onKeyDown={(e) => e.key === 'Enter' && submitSearch()}
              />
              <kbd>Ctrl + K</kbd>
            </div>
          </div>
          <div className="topbar-right">
            <button className="icon-btn has-dot" title="Notifications">
              <Icon name="bell" />
              <span className="notif-dot" />
            </button>
            <div className="topbar-user-wrap" ref={userMenuRef}>
              <button
                type="button"
                className={'topbar-user' + (userMenuOpen ? ' open' : '')}
                onClick={() => setUserMenuOpen((o) => !o)}
                aria-haspopup="menu"
                aria-expanded={userMenuOpen}
              >
                <div className="avatar">{user?.empName?.[0] || 'A'}</div>
                <span className="topbar-username">{user?.empName || 'Admin'}</span>
                <span className="chevron"><Icon name="chevronDown" size={11} /></span>
              </button>
              {userMenuOpen && (
                <div className="user-menu" role="menu">
                  <div className="user-menu-head">
                    <div className="user-menu-name">{user?.empName || 'Admin'}</div>
                    {(user?.roleName && user.roleName.toUpperCase() !== 'CONNECTOR') && <div className="user-menu-role">{user.roleName}</div>}
                  </div>
                  <button type="button" className="user-menu-item" role="menuitem" onClick={logout}>
                    <Icon name="logout" size={15} /> Sign out
                  </button>
                </div>
              )}
            </div>
          </div>
        </header>
        <div className="breadcrumb-bar">
          {crumbs.map((c, i) => (
            <span key={i} className={'crumb' + (i === crumbs.length - 1 ? ' current' : '')}>
              {c}
              {i < crumbs.length - 1 && <span className="crumb-sep"><Icon name="chevronRight" size={11} /></span>}
            </span>
          ))}
        </div>
        <div className="page-content">
          <Outlet />
        </div>
      </main>
      <IdleWarningDialog warning={idleWarning} />
    </div>
  );
}
