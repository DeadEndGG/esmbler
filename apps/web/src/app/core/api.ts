import { HttpClient, HttpErrorResponse, HttpHeaders } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Problem } from './models';

/** A failed API call with the server's problem details (code, target, suggestion, field errors). */
export class ApiError extends Error {
  constructor(public readonly problem: Problem) {
    super(problem.title);
  }

  get code(): string {
    return this.problem.code;
  }

  get status(): number {
    return this.problem.status;
  }
}

/** Thin promise-based client over HttpClient; auth and refresh are handled by the interceptor. */
@Injectable({ providedIn: 'root' })
export class Api {
  private readonly http = inject(HttpClient);

  get<T>(url: string, params?: Record<string, string | number | string[] | undefined>): Promise<T> {
    const clean: Record<string, string | string[]> = {};
    for (const [k, v] of Object.entries(params ?? {})) {
      if (v === undefined || v === '') continue;
      clean[k] = Array.isArray(v) ? v : String(v);
    }
    return this.run(this.http.get<T>(url, { params: clean }));
  }

  post<T>(url: string, body?: unknown, headers?: Record<string, string>): Promise<T> {
    return this.run(this.http.post<T>(url, body ?? {}, { headers: new HttpHeaders(headers ?? {}) }));
  }

  put<T>(url: string, body: unknown): Promise<T> {
    return this.run(this.http.put<T>(url, body));
  }

  patch<T>(url: string, body: unknown): Promise<T> {
    return this.run(this.http.patch<T>(url, body));
  }

  delete<T>(url: string, params?: Record<string, string | number>): Promise<T> {
    const clean: Record<string, string> = {};
    for (const [k, v] of Object.entries(params ?? {})) clean[k] = String(v);
    return this.run(this.http.delete<T>(url, { params: clean }));
  }

  private async run<T>(obs: import('rxjs').Observable<T>): Promise<T> {
    try {
      return await firstValueFrom(obs);
    } catch (e) {
      if (e instanceof HttpErrorResponse) {
        const body = e.error && typeof e.error === 'object' ? e.error : {};
        throw new ApiError({
          status: e.status,
          title: body.title ?? (e.status === 0 ? 'Cannot reach the server. Check your connection.' : 'Something went wrong.'),
          code: body.code ?? (e.status === 0 ? 'network' : 'error'),
          target: body.target,
          suggestion: body.suggestion,
          errors: body.errors,
          details: body.details,
          traceId: body.traceId,
        });
      }
      throw e;
    }
  }
}
