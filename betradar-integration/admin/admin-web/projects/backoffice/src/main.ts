import { bootstrapApplication } from '@angular/platform-browser';
import { buildAppConfig } from './app/app.config';
import { loadConfig } from './app/core/config';
import { Shell } from './app/shell/shell';

// Runtime config first (API base, Keycloak), so the same build runs in every environment.
loadConfig()
  .then((config) => bootstrapApplication(Shell, buildAppConfig(config)))
  .catch((err) => console.error(err));
