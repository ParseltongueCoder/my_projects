import { inject, Injectable, signal } from '@angular/core';
import { BoApi } from './bo-api';

const KEY = 'bo-content-lang';

/** Language in which catalogue names are shown and edited (the operator's languages; default Georgian). */
@Injectable({ providedIn: 'root' })
export class ContentLanguage {
  private readonly api = inject(BoApi);
  readonly lang = signal(read() ?? 'ka');
  readonly options = signal<{ code: string; name: string }[]>([]);

  load(): void {
    this.api.languages().subscribe((l) => {
      const enabled = l.operatorLanguages ?? l.all.map((x) => x.code);
      // In the operator's order (its first language is its main one).
      this.options.set(enabled.map((code) => l.all.find((x) => x.code === code)).filter((x) => !!x).map((x) => ({ code: x!.code, name: x!.nativeName })));
      if (!enabled.includes(this.lang())) {
        this.set(enabled[0] ?? 'en');
      }
    });
  }

  set(lang: string): void {
    this.lang.set(lang);
    try {
      localStorage.setItem(KEY, lang);
    } catch {
      // not remembered
    }
  }
}

function read(): string | null {
  try {
    return localStorage.getItem(KEY);
  } catch {
    return null;
  }
}
