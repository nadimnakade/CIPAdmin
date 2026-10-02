import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom';
import { useEffect, useState } from 'react';
import { getUser, consumeSsoTicket } from './api';
import Layout from './components/Layout';
import ProtectedRoute from './components/ProtectedRoute';
import LoginPage from './pages/LoginPage';
import DashboardPage from './pages/DashboardPage';
import CibilCheckerPage from './pages/CibilCheckerPage';
import EmployeesPage from './pages/EmployeesPage';
import EmployeeMasterPage from './pages/EmployeeMasterPage';
import CompanyCategoryPage from './pages/CompanyCategoryPage';
import OcrPage from './pages/OcrPage';
import BsaAnalyzerPage from './pages/BsaAnalyzerPage';
import LeadMasterPage from './pages/LeadMasterPage';
import RoleMasterPage from './pages/RoleMasterPage';
import ReportsPage from './pages/ReportsPage';
import GenericReportsPage from './pages/GenericReportsPage';
import DialerLivePage from './pages/DialerLivePage';
import LimitRequestsPage from './pages/LimitRequestsPage';
import MenuMasterPage from './pages/MenuMasterPage';
import AccessControlPage from './pages/AccessControlPage';
import SettingsPage from './pages/SettingsPage';

export default function App() {
  // SSO hand-off from the legacy CIPPortal: exchange ?sso={ticket} for a JWT on boot.
  const [ssoState, setSsoState] = useState<'pending' | 'ok' | 'fail' | 'none'>('pending');
  useEffect(() => {
    consumeSsoTicket().then(setSsoState);
  }, []);

  if (ssoState === 'pending') return null;

  return (
    <BrowserRouter basename={(import.meta as any).env?.VITE_BASE || '/'}>
      <Routes>
        <Route
          path="/login"
          element={getUser() ? <Navigate to="/" replace /> : <LoginPage />}
        />
        <Route
          path="/"
          element={
            <ProtectedRoute>
              <Layout />
            </ProtectedRoute>
          }
        >
          <Route index element={<DashboardPage />} />
          <Route path="cibil" element={<CibilCheckerPage />} />
          <Route path="employees" element={<EmployeesPage />} />
          <Route path="employee-master" element={<EmployeeMasterPage />} />
          <Route path="company-categories" element={<CompanyCategoryPage />} />
          <Route path="ocr" element={<OcrPage />} />
          <Route path="bsa-analyzer" element={<BsaAnalyzerPage />} />
          <Route path="lead-master" element={<LeadMasterPage />} />
          <Route path="reports" element={<ReportsPage />} />
          <Route path="generic-reports" element={<GenericReportsPage />} />
          <Route path="dialer" element={<DialerLivePage />} />
          <Route path="limit-requests" element={<LimitRequestsPage />} />
          <Route path="menu-master" element={<MenuMasterPage />} />
          <Route path="access-control" element={<AccessControlPage />} />
          <Route path="role-master" element={<RoleMasterPage />} />
          <Route path="settings" element={<SettingsPage />} />
        </Route>
        <Route path="*" element={<Navigate to="/" replace />} />
      </Routes>
    </BrowserRouter>
  );
}
