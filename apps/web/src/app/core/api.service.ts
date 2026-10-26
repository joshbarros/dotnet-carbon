import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Purchase, LogEntry, Batch } from './models';

export interface NewBatch {
  code: string;
  name: string;
  standard: string;
  country: string;
  tonnesIssued: number;
  priceCents: number;
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly http = inject(HttpClient);

  batches = () => firstValueFrom(this.http.get<Batch[]>('/api/batches'));
  openBatch = (body: NewBatch) => firstValueFrom(this.http.post<Batch>('/api/batches', body));

  purchases = (batchCode: string) =>
    firstValueFrom(this.http.get<Purchase[]>('/api/purchases', { params: { batchCode } }));
  book = (body: { batchCode: string; reference: string; tonnes: number }) =>
    firstValueFrom(this.http.post<Purchase>('/api/purchases', body));
  reverse = (id: string) =>
    firstValueFrom(this.http.post<Purchase>(`/api/purchases/${id}/reversal`, {}));
  events = (id: string) => firstValueFrom(this.http.get<LogEntry[]>(`/api/purchases/${id}/events`));
}
