import { inject } from '@angular/core';
import { CanActivateFn, Router, Routes } from '@angular/router';
import { Auth } from './core/auth';
import { Session } from './core/session';

/** Restores the session from the refresh cookie, then requires a selected workspace. */
const workspaceGuard: CanActivateFn = async () => {
  const auth = inject(Auth);
  const router = inject(Router);
  const session = inject(Session);
  if (!auth.signedIn() && !(await auth.restore())) return router.parseUrl('/login');
  if (!auth.inWorkspace()) return router.parseUrl('/workspaces');
  if (!session.app()) {
    try {
      await session.load();
    } catch {
      return router.parseUrl('/workspaces');
    }
  }
  return true;
};

/** Home: the first dashboard the person can see, otherwise their first list. */
const homeGuard: CanActivateFn = () => {
  const session = inject(Session);
  const router = inject(Router);
  const dashboard = session.app()?.dashboards?.[0];
  if (dashboard) return router.createUrlTree(['/dashboards', dashboard.view]);
  const first = session.app()?.navigation[0];
  return router.createUrlTree(first ? ['/records', first.entity] : ['/catalog']);
};

const signedInGuard: CanActivateFn = async () => {
  const auth = inject(Auth);
  if (auth.signedIn() || (await auth.restore())) return true;
  return inject(Router).parseUrl('/login');
};

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login.page').then((m) => m.LoginPage) },
  { path: 'workspaces', canActivate: [signedInGuard], loadComponent: () => import('./pages/workspaces.page').then((m) => m.WorkspacesPage) },
  { path: 'workspaces/new', canActivate: [signedInGuard], loadComponent: () => import('./pages/create-workspace.page').then((m) => m.CreateWorkspacePage) },
  {
    path: '',
    canActivate: [workspaceGuard],
    loadComponent: () => import('./shell/shell').then((m) => m.Shell),
    children: [
      { path: '', pathMatch: 'full', canActivate: [homeGuard], children: [] },
      { path: 'dashboards/:view', loadComponent: () => import('./pages/dashboard.page').then((m) => m.DashboardPage) },
      { path: 'records/:entity', loadComponent: () => import('./pages/list.page').then((m) => m.ListPage) },
      { path: 'records/:entity/new', loadComponent: () => import('./pages/create.page').then((m) => m.CreatePage) },
      { path: 'records/:entity/preview', loadComponent: () => import('./pages/record.page').then((m) => m.RecordPage), data: { preview: true } },
      { path: 'records/:entity/:id', loadComponent: () => import('./pages/record.page').then((m) => m.RecordPage) },
      { path: 'catalog', loadComponent: () => import('./pages/catalog.page').then((m) => m.CatalogPage) },
      { path: 'catalog/:item', loadComponent: () => import('./pages/request-form.page').then((m) => m.RequestFormPage) },
      { path: 'approvals', loadComponent: () => import('./pages/approvals.page').then((m) => m.ApprovalsPage) },
      { path: 'notifications', loadComponent: () => import('./pages/notifications.page').then((m) => m.NotificationsPage) },
      { path: 'alerts', loadComponent: () => import('./pages/alerts.page').then((m) => m.AlertsPage) },
      { path: 'admin/audit', loadComponent: () => import('./admin/audit.page').then((m) => m.AuditPage) },
      { path: 'admin/notifications', loadComponent: () => import('./admin/notifications.page').then((m) => m.NotificationsAdminPage) },
      { path: 'admin/people', loadComponent: () => import('./admin/people.page').then((m) => m.PeoplePage) },
      { path: 'admin/operations', loadComponent: () => import('./admin/operations.page').then((m) => m.OperationsPage) },
      { path: 'admin/configuration', loadComponent: () => import('./admin/configuration.page').then((m) => m.ConfigurationPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
