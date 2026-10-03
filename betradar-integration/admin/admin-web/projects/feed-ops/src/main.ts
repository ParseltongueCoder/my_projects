import { bootstrapApplication } from '@angular/platform-browser';
import { buildAppConfig } from './app/app.config';
import { Shell } from './app/layout/shell';
import { loadConfig } from './app/core/config';

// Runtime config first (API base, Keycloak), so the same build runs in every environment.
loadConfig()
  .then((config) => bootstrapApplication(Shell, buildAppConfig(config)))
  .catch((err) => console.error(err));
