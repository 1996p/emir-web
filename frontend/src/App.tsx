import { Navigate, Route, Routes } from 'react-router-dom';
import { AppShell } from './layout/AppShell';
import { RoleProvider } from './layout/role';
import { AbonentsPage } from './pages/AbonentsPage';
import { AdminPage } from './pages/AdminPage';
import { CashPage } from './pages/CashPage';
import { CorrectionsPage } from './pages/CorrectionsPage';
import { DirectoriesPage } from './pages/DirectoriesPage';
import { ExchangePage } from './pages/ExchangePage';
import { PeriodPage } from './pages/PeriodPage';
import { ReadingsPage } from './pages/ReadingsPage';
import { RegisterPage } from './pages/RegisterPage';
import { AccountPage } from './pages/AccountPage';
import { CancelChargesPage } from './pages/CancelChargesPage';
import { ChecksPage } from './pages/ChecksPage';
import { DocumentsPage } from './pages/DocumentsPage';
import { PrintDocumentsPage } from './pages/PrintDocumentsPage';
import { PrintStatementPage } from './pages/PrintStatementPage';
import { ReadingImportPage } from './pages/ReadingImportPage';
import { ReportsPage } from './pages/ReportsPage';
import { TodayPage } from './pages/TodayPage';

export function App() {
  return (
    <RoleProvider>
      <Routes>
        <Route path="print/documents" element={<PrintDocumentsPage />} />
        <Route path="print/abonent/:id" element={<PrintStatementPage />} />
        <Route element={<AppShell />}>
          <Route index element={<TodayPage />} />
          <Route path="abonents" element={<AbonentsPage />} />
          <Route path="abonents/new" element={<RegisterPage />} />
          <Route path="cash" element={<CashPage />} />
          <Route path="readings" element={<ReadingsPage />} />
          <Route path="readings/import" element={<ReadingImportPage />} />
          <Route path="period" element={<PeriodPage />} />
          <Route path="period/cancel" element={<CancelChargesPage />} />
          <Route path="corrections" element={<CorrectionsPage />} />
          <Route path="reports" element={<ReportsPage />} />
          <Route path="account" element={<AccountPage />} />
          <Route path="checks" element={<ChecksPage />} />
          <Route path="documents" element={<DocumentsPage />} />
          <Route path="exchange" element={<ExchangePage />} />
          <Route path="directories" element={<DirectoriesPage />} />
          <Route path="admin" element={<AdminPage />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Route>
      </Routes>
    </RoleProvider>
  );
}
