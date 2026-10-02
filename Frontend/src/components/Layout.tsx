import { useEffect, useMemo, useRef, useState } from 'react';
import { NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { getUser, clearAuth } from '../api';
import Icon from './Icon';
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
  const [openGroups, setOpenGroups] = useState<string[]>(() =>
    nav.filter((g) => g.children?.some((c) => c.to === location.pathname)).map((g) => g.label)
  );

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
  const searchIndex = useMemo(
    () =>
      nav.flatMap((g) =>
        g.to
          ? [{ label: g.label, to: g.to }]
          : (g.children || []).filter((c) => !!c.to).map((c) => ({ label: c.label, to: c.to! }))
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

  return (
    <div className={'layout' + (collapsed ? ' sidebar-collapsed' : '')}>
      {open && <div className="sidebar-backdrop" onClick={() => setOpen(false)} />}

      <aside className={'sidebar' + (open ? ' open' : '')}>
        <div className="brand">
          <img src={`${import.meta.env.BASE_URL}assets/logo.png`} alt="1UP logo" className="brand-img" />
          <span className="brand-name">UP</span>
          <button
            className="icon-btn sidebar-close"
            onClick={() => setOpen(false)}
            aria-label="Close menu"
          >
            <Icon name="close" size={18} />
          </button>
        </div>

        <nav className="side-nav">
          {nav.map((item) =>
            item.to ? (
              <NavLink
                key={item.label}
                to={item.to}
                end={item.end}
                className={({ isActive }) => 'nav-item' + (isActive ? ' active' : '')}
                title={collapsed ? item.label : undefined}
              >
                <Icon name={item.icon ?? 'fileText'} /> <span>{item.label}</span>
              </NavLink>
            ) : (
              <div key={item.label} className={'nav-group' + (openGroups.includes(item.label) ? ' expanded' : '')}>
                <button
                  className="nav-item nav-group-toggle"
                  onClick={() => toggleGroup(item.label)}
                  title={collapsed ? item.label : undefined}
                >
                  <Icon name={item.icon ?? 'layers'} />
                  <span>{item.label}</span>
                  <span className="nav-caret"><Icon name="chevronDown" size={10} /></span>
                </button>
                <div className="nav-submenu">
                  {item.children!.filter((c) => !!c.to).map((c) => (
                    <NavLink
                      key={c.to}
                      to={c.to!}
                      className={({ isActive }) => 'nav-subitem' + (isActive ? ' active' : '')}
                      title={collapsed ? c.label : undefined}
                    >
                      <span className="nav-dot" />
                      <span>{c.label}</span>
                    </NavLink>
                  ))}
                </div>
              </div>
            )
          )}
        </nav>

        <button
          className="collapse-btn"
          onClick={() => setCollapsed(!collapsed)}
          aria-label="Toggle sidebar"
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          <Icon name={collapsed ? 'chevronsRight' : 'chevronsLeft'} size={15} />
        </button>

        <div className="sidebar-footer">
          <div className="need-help">
            <div className="need-help-icon">
              <Icon name="help" size={18} />
            </div>
            <div className="need-help-text">
              <div className="need-help-title">Need Help?</div>
              <div className="need-help-sub">Contact support team</div>
            </div>
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
    </div>
  );
}
