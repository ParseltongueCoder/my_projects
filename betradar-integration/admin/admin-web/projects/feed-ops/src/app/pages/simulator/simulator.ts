import { ChangeDetectionStrategy, Component, DestroyRef, inject, OnInit, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { StatusTag } from '@admin/ui';
import { interval, Observable, startWith, switchMap } from 'rxjs';
import { FeedOpsApi } from '../../core/feed-ops-api';
import { SimulatorStatus } from '../../core/models';
import { UserSession } from '../../core/session';

/** Demo controls for the UOF simulator (only shown when the API has a simulator configured). */
@Component({
  selector: 'app-simulator',
  imports: [MatButtonModule, MatIconModule, MatFormFieldModule, MatInputModule, MatProgressBarModule, FormsModule, StatusTag],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './simulator.html',
  styles: `
    .grid { display: grid; grid-template-columns: 1fr 1fr; gap: 1rem; }
    @media (max-width: 960px) { .grid { grid-template-columns: 1fr; } }
    .row { display: flex; align-items: center; justify-content: space-between; gap: .75rem; padding: .5rem 0;
           border-bottom: 1px solid var(--fo-border); }
    .row:last-child { border-bottom: 0; }
    .actions { display: flex; gap: .4rem; flex-wrap: wrap; }
    .note { display: flex; gap: .5rem; align-items: center; padding: .75rem 1rem; border-radius: 10px;
            background: var(--fo-info-bg); color: var(--fo-info); margin-bottom: 1rem; }
    mat-form-field { width: 9rem; }
  `,
})
export class SimulatorPage implements OnInit {
  private readonly api = inject(FeedOpsApi);
  private readonly snack = inject(MatSnackBar);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly session = inject(UserSession);

  protected readonly status = signal<SimulatorStatus | null>(null);
  protected readonly scenarios = signal<string[]>([]);
  protected speed = 2;
  protected downSeconds = 40;

  ngOnInit(): void {
    this.api.scenarios().subscribe((s) => this.scenarios.set(s));
    interval(2000)
      .pipe(startWith(0), switchMap(() => this.api.simulatorStatus()), takeUntilDestroyed(this.destroyRef))
      .subscribe((s) => this.status.set(s));
  }

  protected progress(r: { published: number; total: number }): number {
    return r.total ? Math.round((r.published / r.total) * 100) : 0;
  }

  protected start(name: string): void {
    this.run(this.api.startScenario(name, this.speed), `Scenario ${name} started`);
  }

  protected replay(): void {
    this.run(this.api.replayDemo(), 'Demo match replay started');
  }

  protected down(id: number, mode: 'silent' | 'unsubscribed'): void {
    this.run(this.api.producerDown(id, mode, this.downSeconds), `Producer ${id} → ${mode} for ${this.downSeconds}s`);
  }

  protected up(id: number): void {
    this.run(this.api.producerUp(id), `Producer ${id} back up`);
  }

  private run(call: Observable<unknown>, done: string): void {
    call.subscribe({
      next: () => this.snack.open(done, undefined, { duration: 2500 }),
      error: (e) => this.snack.open(`Simulator call failed: ${e?.message ?? e}`, 'Close', { duration: 5000 }),
    });
  }
}
