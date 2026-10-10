import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { catchError, from, switchMap, throwError } from 'rxjs';
import { Auth } from './auth';

/** Attaches the access token; on a 401 refreshes once (rotating cookie) and retries. */
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  const auth = inject(Auth);
  const isAuthCall = req.url.startsWith('/api/auth/login') || req.url.startsWith('/api/auth/refresh') || req.url.startsWith('/api/auth/logout');
  const withToken = () => {
    const token = auth.token();
    return token && !isAuthCall ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : req;
  };
  return next(withToken()).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse && err.status === 401 && !isAuthCall && err.error?.code !== 'no_tenant_selected') {
        return from(auth.refresh()).pipe(switchMap((ok) => (ok ? next(withToken()) : throwError(() => err))));
      }
      return throwError(() => err);
    }),
  );
};
