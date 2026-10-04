import { Routes } from '@angular/router';
import { boAuthGuard } from './core/auth';
import { permissionGuard } from './core/session';

const soon = (title: string, module: string, stage: string) => ({
  path: module.toLowerCase(),
  title: `${title} · Back Office`,
  data: { title, module, stage },
  loadComponent: () => import('./features/soon/soon').then((m) => m.SoonPage),
});

/** Sitemap of the operator back office (docs/08 §2). BO-0 builds the shell, CFG, ADM and platform pages. */
export const routes: Routes = [
  {
    path: '',
    canActivate: [boAuthGuard],
    children: [
      { path: '', title: 'Home · Back Office', loadComponent: () => import('./features/home/home').then((m) => m.HomePage) },
      {
        path: 'cfg',
        canActivate: [permissionGuard],
        data: { permission: 'cfg.view' },
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'scope' },
          { path: 'scope', title: 'Settings · Back Office', loadComponent: () => import('./features/cfg/scope-editor').then((m) => m.ScopeEditorPage) },
          { path: 'effective', title: 'Effective config · Back Office', data: { needsOperator: true }, canActivate: [permissionGuard], loadComponent: () => import('./features/cfg/effective').then((m) => m.EffectivePage) },
          { path: 'change-sets', title: 'Change sets · Back Office', loadComponent: () => import('./features/cfg/change-sets').then((m) => m.ChangeSetsPage) },
          { path: 'catalog', title: 'Settings catalog · Back Office', loadComponent: () => import('./features/cfg/catalog').then((m) => m.CatalogPage) },
        ],
      },
      {
        path: 'adm',
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'users' },
          { path: 'users', title: 'Users · Back Office', canActivate: [permissionGuard], data: { permission: 'adm.user.view' }, loadComponent: () => import('./features/adm/users').then((m) => m.UsersPage) },
          { path: 'roles', title: 'Roles · Back Office', canActivate: [permissionGuard], data: { permission: 'adm.user.view' }, loadComponent: () => import('./features/adm/roles').then((m) => m.RolesPage) },
          { path: 'audit', title: 'Audit log · Back Office', canActivate: [permissionGuard], data: { permission: 'adm.audit.view' }, loadComponent: () => import('./features/adm/audit').then((m) => m.AuditPage) },
        ],
      },
      {
        path: 'platform',
        canActivate: [permissionGuard],
        data: { platformOnly: true },
        children: [
          { path: '', pathMatch: 'full', redirectTo: 'operators' },
          { path: 'operators', title: 'Operators · Back Office', loadComponent: () => import('./features/platform/operators').then((m) => m.OperatorsPage) },
        ],
      },
      { path: 'sites', title: 'Brands & modules · Back Office', canActivate: [permissionGuard], data: { permission: 'adm.brand.view', needsOperator: true }, loadComponent: () => import('./features/platform/sites').then((m) => m.SitesPage) },
      soon('Catalog', 'CAT', 'BO-1'),
      soon('Translations', 'I18N', 'BO-1'),
      soon('Odds & margins', 'ODDS', 'BO-1'),
      soon('Messages (CMS)', 'CMS', 'BO-1'),
      soon('Tickets', 'BET', 'BO-2'),
      soon('Limits & liability', 'LIM', 'BO-2'),
      soon('Customers', 'CUS', 'BO-2'),
      soon('Bet monitoring', 'MON', 'BO-3'),
      soon('Cash-out', 'CASH', 'BO-3'),
      soon('Reports', 'REP', 'BO-3'),
      soon('Alerts', 'NOTIF', 'BO-3'),
      soon('Promotions', 'PROMO', 'BO-4'),
    ],
  },
  { path: '**', redirectTo: '' },
];
