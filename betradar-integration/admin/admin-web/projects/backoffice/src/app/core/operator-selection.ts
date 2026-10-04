import { Injectable, signal } from '@angular/core';

const KEY = 'bo-operator';

function read(): number | null {
  try {
    const v = sessionStorage.getItem(KEY);
    return v ? Number(v) : null;
  } catch {
    return null;
  }
}

/** The operator platform staff act for (per browser tab, sent as X-Operator-Id). Operator users never set it. */
@Injectable({ providedIn: 'root' })
export class OperatorSelection {
  readonly selected = signal<number | null>(read());

  set(id: number | null): void {
    this.selected.set(id);
    try {
      if (id === null) {
        sessionStorage.removeItem(KEY);
      } else {
        sessionStorage.setItem(KEY, String(id));
      }
    } catch {
      // storage unavailable: the choice lasts until reload
    }
  }
}
