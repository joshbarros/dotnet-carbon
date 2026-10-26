import { Component, DestroyRef, OnInit, computed, inject, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { ApiService } from './core/api.service';
import { AuthService } from './core/auth.service';
import { Purchase, PurchaseStatus, LogEntry, Batch } from './core/models';

const BADGES: Record<PurchaseStatus, string> = {
  Requested: 'bg-slate-100 text-slate-700 ring-slate-300',
  Held: 'bg-amber-50 text-amber-800 ring-amber-300',
  Allocated: 'bg-emerald-50 text-emerald-800 ring-emerald-300',
  Reversed: 'bg-sky-50 text-sky-800 ring-sky-300',
  Rejected: 'bg-rose-50 text-rose-800 ring-rose-300',
};

@Component({
  selector: 'app-workspace',
  imports: [DatePipe],
  templateUrl: './workspace.component.html',
})
export class WorkspaceComponent implements OnInit {
  private readonly api = inject(ApiService);
  readonly auth = inject(AuthService);
  private readonly destroyRef = inject(DestroyRef);

  readonly batches = signal<Batch[]>([]);
  readonly selectedCode = signal('');
  readonly purchases = signal<Purchase[]>([]);
  readonly log = signal<LogEntry[]>([]);
  readonly logFor = signal<string | null>(null);
  readonly error = signal('');

  readonly code = signal('');
  readonly name = signal('Mangrove Delta');
  readonly standard = signal('VCS');
  readonly country = signal('Ecuador');
  readonly price = signal('1500');
  readonly capacity = signal('12');

  readonly reference = signal('');
  readonly tonnes = signal('1');

  readonly selected = computed(() => this.batches().find((s) => s.code === this.selectedCode()));

  ngOnInit(): void {
    void this.refresh();
    const timer = setInterval(() => void this.refresh(), 1000);
    this.destroyRef.onDestroy(() => clearInterval(timer));
  }

  badge(status: PurchaseStatus): string {
    return BADGES[status];
  }

  money(cents: number): string {
    return (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' });
  }

  async refresh(): Promise<void> {
    try {
      const batches = await this.api.batches();
      this.batches.set(batches);
      if (!this.selectedCode() && batches.length > 0) this.selectedCode.set(batches[0].code);
      if (this.selectedCode()) this.purchases.set(await this.api.purchases(this.selectedCode()));
      const id = this.logFor();
      if (id && this.auth.isStaff()) this.log.set(await this.api.events(id));
    } catch (e: unknown) {
      // A 401 means the session is gone; the interceptor has already signed the user out.
      if ((e as { status?: number }).status !== 401) this.error.set('The API is not reachable.');
      return;
    }
    if (this.error() === 'The API is not reachable.') this.error.set('');
  }

  async select(code: string): Promise<void> {
    this.selectedCode.set(code);
    this.logFor.set(null);
    this.log.set([]);
    await this.refresh();
  }

  async openBatch(): Promise<void> {
    await this.run(async () => {
      const code = this.code();
      const batch = await this.api.openBatch({
        code,
        name: this.name(),
        standard: this.standard(),
        country: this.country(),
        tonnesIssued: Number(this.capacity()),
        priceCents: Number(this.price()),
      });
      if (this.code() === code) this.code.set('');
      this.selectedCode.set(batch.code);
    });
  }

  async book(): Promise<void> {
    await this.run(async () => {
      const reference = this.reference();
      await this.api.book({
        batchCode: this.selectedCode(),
        reference,
        tonnes: Number(this.tonnes()),
      });
      if (this.reference() === reference) this.reference.set('');
    });
  }

  async reverse(purchase: Purchase): Promise<void> {
    await this.run(() => this.api.reverse(purchase.id));
  }

  async showLog(purchase: Purchase): Promise<void> {
    this.logFor.set(purchase.id);
    this.log.set([]);
    await this.refresh();
  }

  canReverse(purchase: Purchase): boolean {
    return purchase.status === 'Held' || purchase.status === 'Allocated';
  }

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.error.set('');
    try {
      await action();
    } catch (e: unknown) {
      const body = (e as { error?: { title?: string } }).error;
      this.error.set(body?.title ?? 'The request failed.');
    }
    await this.refresh();
  }

  field(signalRef: { set(value: string): void }, event: Event): void {
    signalRef.set((event.target as HTMLInputElement).value);
  }
}
