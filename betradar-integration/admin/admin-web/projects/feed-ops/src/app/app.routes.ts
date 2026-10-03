import { Routes } from '@angular/router';
import { feedOpsAuthGuard } from './core/auth';

export const routes: Routes = [
  {
    path: '',
    canActivate: [feedOpsAuthGuard],
    children: [
      { path: '', title: 'Overview · Feed Ops', loadComponent: () => import('./pages/overview/overview').then((m) => m.OverviewPage) },
      { path: 'events', title: 'Events · Feed Ops', loadComponent: () => import('./pages/events/events').then((m) => m.EventsPage) },
      {
        path: 'events/:id',
        title: 'Event · Feed Ops',
        loadComponent: () => import('./pages/event-detail/event-detail').then((m) => m.EventDetailPage),
      },
      { path: 'producers', title: 'Producers · Feed Ops', loadComponent: () => import('./pages/producers/producers').then((m) => m.ProducersPage) },
      { path: 'messages', title: 'Feed messages · Feed Ops', loadComponent: () => import('./pages/messages/messages').then((m) => m.MessagesPage) },
      { path: 'simulator', title: 'Simulator · Feed Ops', loadComponent: () => import('./pages/simulator/simulator').then((m) => m.SimulatorPage) },
    ],
  },
  { path: '**', redirectTo: '' },
];
