import { TestBed } from '@angular/core/testing';
import { StatusTag } from './status-tag';

describe('StatusTag', () => {
  it('renders the humanized status with its severity', async () => {
    const fixture = TestBed.createComponent(StatusTag);
    fixture.componentRef.setInput('status', 'not_started');
    await fixture.whenStable();
    const el: HTMLElement = fixture.nativeElement;
    expect(el.textContent?.trim()).toBe('not started');
    expect(el.querySelector('.tag-secondary')).not.toBeNull();
  });
});
