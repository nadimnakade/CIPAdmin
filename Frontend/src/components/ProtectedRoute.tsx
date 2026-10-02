import { Navigate } from 'react-router-dom';
import { ReactNode } from 'react';
import { getUser } from '../api';

export default function ProtectedRoute({ children }: { children: ReactNode }) {
  if (!getUser()) return <Navigate to="/login" replace />;
  return <>{children}</>;
}
